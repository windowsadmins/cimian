// pkg/logging/reporting.go - Data reporting functionality for external monitoring tools

package logging

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

// DataTables defines the table schemas for external monitoring tool integration
type DataTables struct {
	CimianSessions []SessionRecord `json:"sessions"`
	CimianEvents   []EventRecord   `json:"events"`
	CimianPackages []PackageRecord `json:"packages"`
}

// SessionRecord represents a row in the sessions table
type SessionRecord struct {
	SessionID    string `json:"session_id"`
	StartTime    string `json:"start_time"`
	EndTime      string `json:"end_time,omitempty"`
	RunType      string `json:"run_type"`
	Status       string `json:"status"`
	Duration     int64  `json:"duration_seconds"`
	TotalActions int    `json:"total_actions"`
	Installs     int    `json:"installs"`
	Updates      int    `json:"updates"`
	Removals     int    `json:"removals"`
	Successes    int    `json:"successes"`
	Failures     int    `json:"failures"`
	Hostname     string `json:"hostname"`
	User         string `json:"user"`
	ProcessID    int    `json:"process_id"`
	LogVersion   string `json:"log_version"`
}

// EventRecord represents a row in the events table
type EventRecord struct {
	EventID    string `json:"event_id"`
	SessionID  string `json:"session_id"`
	Timestamp  string `json:"timestamp"`
	Level      string `json:"level"`
	EventType  string `json:"event_type"`
	Package    string `json:"package,omitempty"`
	Version    string `json:"version,omitempty"`
	Action     string `json:"action"`
	Status     string `json:"status"`
	Message    string `json:"message"`
	Duration   int64  `json:"duration_ms,omitempty"`
	Progress   int    `json:"progress,omitempty"`
	Error      string `json:"error,omitempty"`
	SourceFile string `json:"source_file"`
	SourceFunc string `json:"source_function"`
	SourceLine int    `json:"source_line"`
}

// PackageRecord represents a row in the packages table (aggregated view)
type PackageRecord struct {
	PackageName       string `json:"package_name"`
	LatestVersion     string `json:"latest_version"`
	LastInstallTime   string `json:"last_install_time"`
	LastUpdateTime    string `json:"last_update_time"`
	InstallCount      int    `json:"install_count"`
	UpdateCount       int    `json:"update_count"`
	RemovalCount      int    `json:"removal_count"`
	LastInstallStatus string `json:"last_install_status"`
	TotalSessions     int    `json:"total_sessions"`
}

// DataExporter provides methods to export Cimian logs for external monitoring tool consumption
type DataExporter struct {
	baseDir string
}

// NewDataExporter creates a new data exporter
func NewDataExporter(baseDir string) *DataExporter {
	return &DataExporter{baseDir: baseDir}
}

// GenerateSessionsTable creates external tool-compatible session records
func (exp *DataExporter) GenerateSessionsTable(limitDays int) ([]SessionRecord, error) {
	sessions, err := exp.getRecentSessions(limitDays)
	if err != nil {
		return nil, fmt.Errorf("failed to get recent sessions: %w", err)
	}

	var records []SessionRecord
	for _, sessionDir := range sessions {
		sessionPath := filepath.Join(exp.baseDir, sessionDir, "session.json")

		var session LogSession
		if err := exp.readJSONFile(sessionPath, &session); err != nil {
			continue // Skip corrupted sessions
		}

		record := SessionRecord{
			SessionID:    session.SessionID,
			StartTime:    session.StartTime.Format(time.RFC3339),
			RunType:      session.RunType,
			Status:       session.Status,
			TotalActions: session.Summary.TotalActions,
			Installs:     session.Summary.Installs,
			Updates:      session.Summary.Updates,
			Removals:     session.Summary.Removals,
			Successes:    session.Summary.Successes,
			Failures:     session.Summary.Failures,
		}

		if session.EndTime != nil {
			record.EndTime = session.EndTime.Format(time.RFC3339)
			record.Duration = int64(session.Summary.Duration.Seconds())
		}

		// Extract environment info
		if hostname, ok := session.Environment["hostname"].(string); ok {
			record.Hostname = hostname
		}
		if user, ok := session.Environment["user"].(string); ok {
			record.User = user
		}
		if pid, ok := session.Environment["process_id"].(float64); ok {
			record.ProcessID = int(pid)
		}
		if version, ok := session.Environment["log_version"].(string); ok {
			record.LogVersion = version
		}

		records = append(records, record)
	}

	return records, nil
}

// GenerateEventsTable creates external tool-compatible event records
func (exp *DataExporter) GenerateEventsTable(sessionID string, limitHours int) ([]EventRecord, error) {
	eventsPath := filepath.Join(exp.baseDir, sessionID, "events.jsonl")

	file, err := os.Open(eventsPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open events file: %w", err)
	}
	defer file.Close()

	var records []EventRecord
	cutoffTime := time.Now().Add(-time.Duration(limitHours) * time.Hour)

	decoder := json.NewDecoder(file)
	for decoder.More() {
		var event LogEvent
		if err := decoder.Decode(&event); err != nil {
			continue // Skip malformed events
		}

		// Apply time filter
		if limitHours > 0 && event.Timestamp.Before(cutoffTime) {
			continue
		}

		record := EventRecord{
			EventID:    event.EventID,
			SessionID:  event.SessionID,
			Timestamp:  event.Timestamp.Format(time.RFC3339),
			Level:      event.Level,
			EventType:  event.EventType,
			Package:    event.Package,
			Version:    event.Version,
			Action:     event.Action,
			Status:     event.Status,
			Message:    event.Message,
			Error:      event.Error,
			SourceFile: event.Source.File,
			SourceFunc: event.Source.Function,
			SourceLine: event.Source.Line,
		}

		if event.Duration != nil {
			record.Duration = int64(event.Duration.Nanoseconds() / 1000000) // Convert to milliseconds
		}
		if event.Progress != nil {
			record.Progress = *event.Progress
		}

		records = append(records, record)
	}

	return records, nil
}

// GeneratePackagesTable creates an aggregated view of package operations
func (exp *DataExporter) GeneratePackagesTable(limitDays int) ([]PackageRecord, error) {
	sessions, err := exp.getRecentSessions(limitDays)
	if err != nil {
		return nil, fmt.Errorf("failed to get recent sessions: %w", err)
	}

	packageStats := make(map[string]*packageStat)

	// Aggregate data from all sessions
	for _, sessionDir := range sessions {
		events, err := exp.GenerateEventsTable(sessionDir, 0) // Get all events for session
		if err != nil {
			continue
		}

		for _, event := range events {
			if event.Package == "" {
				continue
			}

			if _, exists := packageStats[event.Package]; !exists {
				packageStats[event.Package] = &packageStat{
					Name:     event.Package,
					Sessions: make(map[string]bool),
				}
			}

			stats := packageStats[event.Package]
			stats.Sessions[event.SessionID] = true

			// Update latest version
			if event.Version != "" {
				stats.LatestVersion = event.Version
			}

			// Count operations
			switch event.EventType {
			case "install":
				if event.Status == "completed" {
					stats.InstallCount++
					stats.LastInstallStatus = "success"
					if eventTime, err := time.Parse(time.RFC3339, event.Timestamp); err == nil {
						if stats.LastInstallTime.IsZero() || eventTime.After(stats.LastInstallTime) {
							stats.LastInstallTime = eventTime
						}
					}
				} else if event.Status == "failed" {
					stats.LastInstallStatus = "failed"
				}
			case "update":
				if event.Status == "completed" {
					stats.UpdateCount++
					if eventTime, err := time.Parse(time.RFC3339, event.Timestamp); err == nil {
						if stats.LastUpdateTime.IsZero() || eventTime.After(stats.LastUpdateTime) {
							stats.LastUpdateTime = eventTime
						}
					}
				}
			case "remove":
				if event.Status == "completed" {
					stats.RemovalCount++
				}
			}
		}
	}

	// Convert to records
	var records []PackageRecord
	for _, stats := range packageStats {
		record := PackageRecord{
			PackageName:       stats.Name,
			LatestVersion:     stats.LatestVersion,
			InstallCount:      stats.InstallCount,
			UpdateCount:       stats.UpdateCount,
			RemovalCount:      stats.RemovalCount,
			LastInstallStatus: stats.LastInstallStatus,
			TotalSessions:     len(stats.Sessions),
		}

		if !stats.LastInstallTime.IsZero() {
			record.LastInstallTime = stats.LastInstallTime.Format(time.RFC3339)
		}
		if !stats.LastUpdateTime.IsZero() {
			record.LastUpdateTime = stats.LastUpdateTime.Format(time.RFC3339)
		}

		records = append(records, record)
	}

	return records, nil
}

// ExportDataJSON exports all tables to a JSON file for external tool consumption
func (exp *DataExporter) ExportDataJSON(outputPath string, limitDays int) error {
	sessions, err := exp.GenerateSessionsTable(limitDays)
	if err != nil {
		return fmt.Errorf("failed to generate sessions table: %w", err)
	}

	packages, err := exp.GeneratePackagesTable(limitDays)
	if err != nil {
		return fmt.Errorf("failed to generate packages table: %w", err)
	}

	// Collect events from recent sessions
	var allEvents []EventRecord
	recentSessions, err := exp.getRecentSessions(1) // Last day for events
	if err == nil {
		for _, sessionDir := range recentSessions {
			events, err := exp.GenerateEventsTable(sessionDir, 24) // Last 24 hours
			if err == nil {
				allEvents = append(allEvents, events...)
			}
		}
	}

	tables := DataTables{
		CimianSessions: sessions,
		CimianEvents:   allEvents,
		CimianPackages: packages,
	}

	return exp.writeJSONFile(outputPath, tables)
}

// ExportToReportsDirectory exports all data tables to the reports directory
func (exp *DataExporter) ExportToReportsDirectory(limitDays int) error {
	// Reports go to C:\ProgramData\ManagedInstalls\reports (separate from logs)
	reportsDir := filepath.Join(filepath.Dir(exp.baseDir), "reports")
	if err := os.MkdirAll(reportsDir, 0755); err != nil {
		return fmt.Errorf("failed to create reports directory: %w", err)
	}

	// Generate sessions table
	sessions, err := exp.GenerateSessionsTable(limitDays)
	if err != nil {
		return fmt.Errorf("failed to generate sessions table: %w", err)
	}

	// Generate packages table
	packages, err := exp.GeneratePackagesTable(limitDays)
	if err != nil {
		return fmt.Errorf("failed to generate packages table: %w", err)
	}

	// Generate events table (last 24 hours for performance)
	var allEvents []EventRecord
	recentSessions, err := exp.getRecentSessions(1) // Last day for events
	if err == nil {
		for _, sessionDir := range recentSessions {
			events, err := exp.GenerateEventsTable(sessionDir, 24) // Last 24 hours
			if err == nil {
				allEvents = append(allEvents, events...)
			}
		}
	}

	// Export individual tables for external tool consumption
	if err := exp.writeJSONFile(filepath.Join(reportsDir, "sessions.json"), sessions); err != nil {
		return fmt.Errorf("failed to export sessions: %w", err)
	}

	if err := exp.writeJSONFile(filepath.Join(reportsDir, "events.json"), allEvents); err != nil {
		return fmt.Errorf("failed to export events: %w", err)
	}

	if err := exp.writeJSONFile(filepath.Join(reportsDir, "packages.json"), packages); err != nil {
		return fmt.Errorf("failed to export packages: %w", err)
	}

	return nil
}

// Helper types and methods

type packageStat struct {
	Name              string
	LatestVersion     string
	LastInstallTime   time.Time
	LastUpdateTime    time.Time
	InstallCount      int
	UpdateCount       int
	RemovalCount      int
	LastInstallStatus string
	Sessions          map[string]bool
}

func (exp *DataExporter) getRecentSessions(limitDays int) ([]string, error) {
	entries, err := os.ReadDir(exp.baseDir)
	if err != nil {
		return nil, err
	}

	cutoffTime := time.Now().AddDate(0, 0, -limitDays)
	var recentSessions []string

	for _, entry := range entries {
		if !entry.IsDir() || len(entry.Name()) != 15 {
			continue
		}

		if sessionTime, err := time.Parse("20060102-150405", entry.Name()); err == nil {
			if limitDays == 0 || sessionTime.After(cutoffTime) {
				recentSessions = append(recentSessions, entry.Name())
			}
		}
	}

	return recentSessions, nil
}

func (exp *DataExporter) readJSONFile(path string, v interface{}) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, v)
}

func (exp *DataExporter) writeJSONFile(path string, v interface{}) error {
	file, err := os.Create(path)
	if err != nil {
		return err
	}
	defer file.Close()

	encoder := json.NewEncoder(file)
	encoder.SetIndent("", "  ")
	return encoder.Encode(v)
}
