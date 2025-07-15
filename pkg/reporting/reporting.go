// pkg/reporting/reporting.go - Data reporting functionality for external monitoring tools

package reporting

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/logging"
)

// DataTables defines the table schemas for external monitoring tool integration
type DataTables struct {
	CimianSessions []SessionRecord `json:"sessions"`
	CimianEvents   []EventRecord   `json:"events"`
	CimianItems    []ItemRecord    `json:"items"`
}

// SessionRecord represents a row in the sessions table
type SessionRecord struct {
	SessionID       string   `json:"session_id"`
	StartTime       string   `json:"start_time"`
	EndTime         string   `json:"end_time,omitempty"`
	RunType         string   `json:"run_type"`
	Status          string   `json:"status"`
	Duration        int64    `json:"duration_seconds"`
	TotalActions    int      `json:"total_actions"`
	Installs        int      `json:"installs"`
	Updates         int      `json:"updates"`
	Removals        int      `json:"removals"`
	Successes       int      `json:"successes"`
	Failures        int      `json:"failures"`
	Hostname        string   `json:"hostname"`
	User            string   `json:"user"`
	ProcessID       int      `json:"process_id"`
	LogVersion      string   `json:"log_version"`
	PackagesHandled []string `json:"packages_handled,omitempty"`
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

// ItemRecord represents a row in the items table (comprehensive device status)
type ItemRecord struct {
	ItemName            string        `json:"item_name"`
	ItemType            string        `json:"item_type"`      // managed_installs, managed_updates, optional_installs, etc.
	CurrentStatus       string        `json:"current_status"` // "installed", "failed", "warning", "install_loop", "not_installed"
	LatestVersion       string        `json:"latest_version"`
	InstalledVersion    string        `json:"installed_version,omitempty"`
	LastSeenInSession   string        `json:"last_seen_in_session"`
	LastSuccessfulTime  string        `json:"last_successful_time"`
	LastAttemptTime     string        `json:"last_attempt_time"`
	LastAttemptStatus   string        `json:"last_attempt_status"` // "success", "failed", "warning"
	InstallCount        int           `json:"install_count"`
	UpdateCount         int           `json:"update_count"`
	RemovalCount        int           `json:"removal_count"`
	FailureCount        int           `json:"failure_count"`
	WarningCount        int           `json:"warning_count"`
	TotalSessions       int           `json:"total_sessions"`
	InstallLoopDetected bool          `json:"install_loop_detected"`
	LastError           string        `json:"last_error,omitempty"`
	LastWarning         string        `json:"last_warning,omitempty"`
	RecentAttempts      []ItemAttempt `json:"recent_attempts,omitempty"` // Last 5 attempts for loop detection
}

// ItemAttempt represents a single install/update attempt for loop detection
type ItemAttempt struct {
	SessionID string `json:"session_id"`
	Timestamp string `json:"timestamp"`
	Action    string `json:"action"` // "install", "update", "remove"
	Status    string `json:"status"` // "success", "failed", "warning"
	Version   string `json:"version,omitempty"`
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

		// Try new format first (with session.json)
		var session logging.LogSession
		if err := exp.readJSONFile(sessionPath, &session); err == nil {
			// New format with session.json
			record := SessionRecord{
				SessionID:       session.SessionID,
				StartTime:       session.StartTime.Format(time.RFC3339),
				RunType:         session.RunType,
				Status:          session.Status,
				TotalActions:    session.Summary.TotalActions,
				Installs:        session.Summary.Installs,
				Updates:         session.Summary.Updates,
				Removals:        session.Summary.Removals,
				Successes:       session.Summary.Successes,
				Failures:        session.Summary.Failures,
				PackagesHandled: session.Summary.PackagesHandled,
			}

			// Calculate duration
			if session.EndTime != nil {
				record.EndTime = session.EndTime.Format(time.RFC3339)
				if !session.StartTime.IsZero() {
					record.Duration = int64(session.EndTime.Sub(session.StartTime).Seconds())
				} else {
					record.Duration = int64(session.Summary.Duration.Seconds())
				}
			}

			// Extract environment info - handle both map types
			if session.Environment != nil {
				if hostname, ok := session.Environment["hostname"].(string); ok {
					record.Hostname = hostname
				}
				if user, ok := session.Environment["user"].(string); ok {
					record.User = user
				}
				if logVersion, ok := session.Environment["log_version"].(string); ok {
					record.LogVersion = logVersion
				}
				if processID, ok := session.Environment["process_id"].(float64); ok {
					record.ProcessID = int(processID)
				}
			}

			// If start_time is missing or zero, try to get it from first event
			if session.StartTime.IsZero() || record.StartTime == "0001-01-01T00:00:00Z" {
				if startTime := exp.getSessionStartTimeFromEvents(sessionDir); !startTime.IsZero() {
					record.StartTime = startTime.Format(time.RFC3339)
					// Recalculate duration with correct start time
					if session.EndTime != nil {
						record.Duration = int64(session.EndTime.Sub(startTime).Seconds())
					}
				}
			}

			// Fill missing environment data from events if needed
			if record.Hostname == "" || record.User == "" || record.LogVersion == "" || record.ProcessID == 0 || record.RunType == "" {
				exp.fillMissingSessionData(&record, sessionDir)
			}

			records = append(records, record)
		} else {
			// Old format - extract info from events.jsonl
			record := exp.generateSessionFromEvents(sessionDir)
			if record != nil {
				records = append(records, *record)
			}
		}
	}

	return records, nil
}

// generateSessionFromEvents creates a session record from events.jsonl for old format
func (exp *DataExporter) generateSessionFromEvents(sessionDir string) *SessionRecord {
	eventsPath := filepath.Join(exp.baseDir, sessionDir, "events.jsonl")

	file, err := os.Open(eventsPath)
	if err != nil {
		return nil
	}
	defer file.Close()

	scanner := bufio.NewScanner(file)
	var firstEvent, lastEvent map[string]interface{}
	var hostname, user, logVersion, runType string
	var processID int

	// Read through events to gather session info
	for scanner.Scan() {
		line := scanner.Text()
		if strings.TrimSpace(line) == "" {
			continue
		}

		var event map[string]interface{}
		if err := json.Unmarshal([]byte(line), &event); err != nil {
			continue
		}

		if firstEvent == nil {
			firstEvent = event
			// Extract session-level info from first event
			if h, ok := event["hostname"].(string); ok {
				hostname = h
			}
			if u, ok := event["user"].(string); ok {
				user = u
			}
			if rt, ok := event["run_type"].(string); ok {
				runType = rt
			}
			if v, ok := event["version"].(string); ok {
				logVersion = v
			}
			if pid, ok := event["pid"].(float64); ok {
				processID = int(pid)
			}
		}
		lastEvent = event
	}

	if firstEvent == nil {
		return nil
	}

	// Parse timestamps
	var startTime, endTime time.Time
	if ts, ok := firstEvent["timestamp"].(string); ok {
		if t, err := time.Parse(time.RFC3339, ts); err == nil {
			startTime = t
		}
	}
	if lastEvent != nil {
		if ts, ok := lastEvent["timestamp"].(string); ok {
			if t, err := time.Parse(time.RFC3339, ts); err == nil {
				endTime = t
			}
		}
	}

	record := &SessionRecord{
		SessionID:       sessionDir,
		StartTime:       startTime.Format(time.RFC3339),
		RunType:         runType,
		Status:          "completed", // Assume completed for old format
		TotalActions:    0,           // Could be calculated by counting events
		Installs:        0,
		Updates:         0,
		Removals:        0,
		Successes:       0,
		Failures:        0,
		Hostname:        hostname,
		User:            user,
		ProcessID:       processID,
		LogVersion:      logVersion,
		PackagesHandled: []string{}, // Old format doesn't have this data
	}

	if !endTime.IsZero() {
		record.EndTime = endTime.Format(time.RFC3339)
		record.Duration = int64(endTime.Sub(startTime).Seconds())
	}

	return record
}

// GenerateEventsTable creates external tool-compatible event records
func (exp *DataExporter) GenerateEventsTable(sessionID string, limitHours int) ([]EventRecord, error) {
	eventsPath := filepath.Join(exp.baseDir, sessionID, "events.jsonl")

	content, err := os.ReadFile(eventsPath)
	if err != nil {
		return nil, fmt.Errorf("failed to read events file: %w", err)
	}

	var records []EventRecord
	cutoffTime := time.Now().Add(-time.Duration(limitHours) * time.Hour)

	// Split content into individual JSON objects by finding complete JSON blocks
	var currentJSON strings.Builder
	var braceCount int
	var inString bool
	var escapeNext bool

	for _, char := range string(content) {
		if escapeNext {
			escapeNext = false
			currentJSON.WriteRune(char)
			continue
		}

		switch char {
		case '\\':
			escapeNext = true
			currentJSON.WriteRune(char)
		case '"':
			if !escapeNext {
				inString = !inString
			}
			currentJSON.WriteRune(char)
		case '{':
			if !inString {
				braceCount++
			}
			currentJSON.WriteRune(char)
		case '}':
			if !inString {
				braceCount--
			}
			currentJSON.WriteRune(char)
		default:
			currentJSON.WriteRune(char)
		}

		// If braces are balanced and we're not in a string, we have a complete JSON object
		if braceCount == 0 && currentJSON.Len() > 0 && !inString {
			jsonStr := strings.TrimSpace(currentJSON.String())
			if jsonStr != "" {
				var logEvent map[string]interface{}
				if err := json.Unmarshal([]byte(jsonStr), &logEvent); err == nil {
					// Parse timestamp
					timestamp := ""
					eventTime := time.Time{}
					if ts, ok := logEvent["timestamp"].(string); ok {
						timestamp = ts
						if t, err := time.Parse(time.RFC3339, timestamp); err == nil {
							eventTime = t
						}
					}

					// Apply time filter
					if limitHours > 0 && !eventTime.IsZero() && eventTime.Before(cutoffTime) {
						currentJSON.Reset()
						continue
					}

					// Extract package name and other details from properties
					packageName := ""
					version := ""
					action := ""
					status := ""
					errorMsg := ""

					if props, ok := logEvent["properties"].(map[string]interface{}); ok {
						if item, ok := props["item"].(string); ok {
							packageName = item
						}
						if ver, ok := props["version"].(string); ok {
							version = ver
						}
						if act, ok := props["action"].(string); ok {
							action = act
						}
						if stat, ok := props["status"].(string); ok {
							status = stat
						}
						if errVal, ok := props["error"]; ok && errVal != nil {
							if errStr, ok := errVal.(string); ok {
								errorMsg = errStr
							}
						}
					}

					// Infer event type from message content
					message := ""
					eventType := "general"
					if msg, ok := logEvent["message"].(string); ok {
						message = msg
						msgLower := strings.ToLower(message)
						if strings.Contains(msgLower, "install") {
							eventType = "install"
							if action == "" {
								action = "install_package"
							}
						} else if strings.Contains(msgLower, "update") || strings.Contains(msgLower, "upgrade") {
							eventType = "update"
							if action == "" {
								action = "update_package"
							}
						} else if strings.Contains(msgLower, "remove") || strings.Contains(msgLower, "uninstall") {
							eventType = "remove"
							if action == "" {
								action = "remove_package"
							}
						} else if strings.Contains(msgLower, "download") {
							eventType = "download"
							if action == "" {
								action = "download_file"
							}
						}
					}

					// Generate event ID
					eventID := fmt.Sprintf("%s-%v", sessionID, logEvent["time"])

					// Get source information
					level := ""
					if l, ok := logEvent["level"].(string); ok {
						level = l
					}

					sourceFile := ""
					if sf, ok := logEvent["component"].(string); ok {
						sourceFile = sf
					}

					sourceFunc := ""
					if sf, ok := logEvent["process"].(string); ok {
						sourceFunc = sf
					}

					record := EventRecord{
						EventID:    eventID,
						SessionID:  sessionID,
						Timestamp:  timestamp,
						Level:      level,
						EventType:  eventType,
						Package:    packageName,
						Version:    version,
						Action:     action,
						Status:     status,
						Message:    message,
						Error:      errorMsg,
						SourceFile: sourceFile,
						SourceFunc: sourceFunc,
						SourceLine: 0,
					}

					records = append(records, record)
				}
			}
			currentJSON.Reset()
		}
	}

	return records, nil
}

// GenerateItemsTable creates a comprehensive view of all items ever managed by Cimian
func (exp *DataExporter) GenerateItemsTable(limitDays int) ([]ItemRecord, error) {
	// Get ALL sessions (not just recent ones) to build comprehensive history
	allSessions, err := exp.getAllSessions()
	if err != nil {
		return nil, fmt.Errorf("failed to get all sessions: %w", err)
	}

	itemStats := make(map[string]*comprehensiveItemStat)

	// Process all sessions to build complete history
	for _, sessionDir := range allSessions {
		// Get events for this session
		events, err := exp.GenerateEventsTable(sessionDir, 0) // Get all events
		if err != nil {
			continue
		}

		// Process each event
		for _, event := range events {
			packageName := event.Package
			if packageName == "" {
				packageName = exp.extractPackageFromMessage(event.Message)
			}
			if packageName == "" {
				continue
			}

			// Initialize item stats if not exists
			if _, exists := itemStats[packageName]; !exists {
				itemStats[packageName] = &comprehensiveItemStat{
					Name:           packageName,
					Sessions:       make(map[string]bool),
					RecentAttempts: []ItemAttempt{},
				}
			}

			stats := itemStats[packageName]
			stats.Sessions[event.SessionID] = true

			// Parse event timestamp
			eventTime, timeErr := time.Parse(time.RFC3339, event.Timestamp)

			// Update latest version
			if event.Version != "" {
				stats.LatestVersion = event.Version
			}

			// Determine item type from session data or event context
			if stats.ItemType == "" {
				stats.ItemType = exp.inferItemType(packageName, sessionDir, event)
			}

			// Create attempt record
			attempt := ItemAttempt{
				SessionID: event.SessionID,
				Timestamp: event.Timestamp,
				Action:    event.EventType,
				Status:    exp.normalizeStatus(event.Status, event.Level, event.Error),
				Version:   event.Version,
			}

			// Add to recent attempts (keep last 10 for loop detection)
			stats.RecentAttempts = append(stats.RecentAttempts, attempt)
			if len(stats.RecentAttempts) > 10 {
				stats.RecentAttempts = stats.RecentAttempts[1:]
			}

			// Update counts and status
			switch event.EventType {
			case "install":
				switch attempt.Status {
				case "success":
					stats.InstallCount++
					stats.CurrentStatus = "installed"
					if timeErr == nil {
						stats.LastSuccessfulTime = eventTime
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "success"
					stats.InstalledVersion = event.Version
				case "failed":
					stats.FailureCount++
					stats.CurrentStatus = "failed"
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "failed"
					if event.Error != "" {
						stats.LastError = event.Error
					} else {
						stats.LastError = event.Message
					}
				case "warning":
					stats.WarningCount++
					if stats.CurrentStatus != "failed" { // Don't override failed status
						stats.CurrentStatus = "warning"
					}
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "warning"
					stats.LastWarning = event.Message
				}
			case "update":
				switch attempt.Status {
				case "success":
					stats.UpdateCount++
					stats.CurrentStatus = "installed"
					if timeErr == nil {
						stats.LastSuccessfulTime = eventTime
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "success"
					stats.InstalledVersion = event.Version
				case "failed":
					stats.FailureCount++
					stats.CurrentStatus = "failed"
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "failed"
					if event.Error != "" {
						stats.LastError = event.Error
					} else {
						stats.LastError = event.Message
					}
				case "warning":
					stats.WarningCount++
					if stats.CurrentStatus != "failed" {
						stats.CurrentStatus = "warning"
					}
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "warning"
					stats.LastWarning = event.Message
				}
			case "remove":
				if attempt.Status == "success" {
					stats.RemovalCount++
					stats.CurrentStatus = "not_installed"
					stats.InstalledVersion = ""
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "success"
				}
			}

			// Update last seen session
			if timeErr == nil {
				if stats.LastSeenTime.IsZero() || eventTime.After(stats.LastSeenTime) {
					stats.LastSeenTime = eventTime
					stats.LastSeenSession = event.SessionID
				}
			}
		}
	}

	// Detect install loops and finalize status
	for _, stats := range itemStats {
		stats.InstallLoopDetected = exp.detectInstallLoop(stats.RecentAttempts)
		if stats.InstallLoopDetected {
			stats.CurrentStatus = "install_loop"
		}
	}

	// Convert to records
	var records []ItemRecord
	for _, stats := range itemStats {
		record := ItemRecord{
			ItemName:            stats.Name,
			ItemType:            stats.ItemType,
			CurrentStatus:       stats.CurrentStatus,
			LatestVersion:       stats.LatestVersion,
			InstalledVersion:    stats.InstalledVersion,
			LastSeenInSession:   stats.LastSeenSession,
			LastAttemptStatus:   stats.LastAttemptStatus,
			InstallCount:        stats.InstallCount,
			UpdateCount:         stats.UpdateCount,
			RemovalCount:        stats.RemovalCount,
			FailureCount:        stats.FailureCount,
			WarningCount:        stats.WarningCount,
			TotalSessions:       len(stats.Sessions),
			InstallLoopDetected: stats.InstallLoopDetected,
			LastError:           stats.LastError,
			LastWarning:         stats.LastWarning,
			RecentAttempts:      stats.RecentAttempts,
		}

		if !stats.LastSuccessfulTime.IsZero() {
			record.LastSuccessfulTime = stats.LastSuccessfulTime.Format(time.RFC3339)
		}
		if !stats.LastAttemptTime.IsZero() {
			record.LastAttemptTime = stats.LastAttemptTime.Format(time.RFC3339)
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

	packages, err := exp.GenerateItemsTable(limitDays)
	if err != nil {
		return fmt.Errorf("failed to generate items table: %w", err)
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
		CimianItems:    packages,
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

	// Generate items table
	packages, err := exp.GenerateItemsTable(limitDays)
	if err != nil {
		return fmt.Errorf("failed to generate items table: %w", err)
	}

	// Generate events table (last 48 hours for performance)
	var allEvents []EventRecord
	recentSessions, err := exp.getRecentSessions(3) // Last 3 days for session coverage
	if err == nil {
		for _, sessionDir := range recentSessions {
			// Limit events to 48 hours to prevent huge files
			events, err := exp.GenerateEventsTable(sessionDir, 48)
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

	if err := exp.writeJSONFile(filepath.Join(reportsDir, "items.json"), packages); err != nil {
		return fmt.Errorf("failed to export items: %w", err)
	}

	return nil
}

// Helper types and methods

// comprehensiveItemStat tracks detailed statistics for an item across all sessions
type comprehensiveItemStat struct {
	Name                string
	ItemType            string
	CurrentStatus       string
	LatestVersion       string
	InstalledVersion    string
	LastSeenSession     string
	LastSeenTime        time.Time
	LastSuccessfulTime  time.Time
	LastAttemptTime     time.Time
	LastAttemptStatus   string
	InstallCount        int
	UpdateCount         int
	RemovalCount        int
	FailureCount        int
	WarningCount        int
	Sessions            map[string]bool
	RecentAttempts      []ItemAttempt
	LastError           string
	LastWarning         string
	InstallLoopDetected bool
}

func (exp *DataExporter) getRecentSessions(limitDays int) ([]string, error) {
	entries, err := os.ReadDir(exp.baseDir)
	if err != nil {
		return nil, err
	}

	cutoffTime := time.Now().AddDate(0, 0, -limitDays)
	var recentSessions []string

	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}

		dirName := entry.Name()
		var sessionTime time.Time
		var parseErr error

		// Try new format first: 2025-07-15-015253 (17 chars)
		if len(dirName) == 17 && strings.Count(dirName, "-") == 3 {
			sessionTime, parseErr = time.Parse("2006-01-02-150405", dirName)
		} else if len(dirName) == 15 && strings.Contains(dirName, "-") {
			// Try old format: 20250715-015253 (15 chars) - deprecated
			sessionTime, parseErr = time.Parse("20060102-150405", dirName)
		} else {
			continue // Skip directories that don't match either format
		}

		if parseErr == nil {
			if limitDays == 0 || sessionTime.After(cutoffTime) {
				recentSessions = append(recentSessions, dirName)
			}
		}
	}

	return recentSessions, nil
}

func (exp *DataExporter) getAllSessions() ([]string, error) {
	entries, err := os.ReadDir(exp.baseDir)
	if err != nil {
		return nil, fmt.Errorf("failed to read logs directory: %w", err)
	}

	var sessions []string
	for _, entry := range entries {
		if entry.IsDir() {
			// Check if this looks like a session directory (has timestamp format)
			sessionPath := filepath.Join(exp.baseDir, entry.Name())
			if exp.isValidSessionDir(sessionPath) {
				sessions = append(sessions, entry.Name())
			}
		}
	}

	return sessions, nil
}

// isValidSessionDir checks if a directory contains valid session files
func (exp *DataExporter) isValidSessionDir(sessionPath string) bool {
	// Check for either events.jsonl or session.json
	eventsPath := filepath.Join(sessionPath, "events.jsonl")
	sessionJsonPath := filepath.Join(sessionPath, "session.json")

	if _, err := os.Stat(eventsPath); err == nil {
		return true
	}
	if _, err := os.Stat(sessionJsonPath); err == nil {
		return true
	}

	return false
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

// Helper session data structure for reading session.json
type SessionData struct {
	SessionID string `json:"session_id"`
	StartTime string `json:"start_time"`
	RunType   string `json:"run_type"`
	Status    string `json:"status"`
	Summary   struct {
		TotalActions    int      `json:"total_actions"`
		Installs        int      `json:"installs"`
		Updates         int      `json:"updates"`
		Removals        int      `json:"removals"`
		Successes       int      `json:"successes"`
		Failures        int      `json:"failures"`
		Duration        int64    `json:"duration"`
		PackagesHandled []string `json:"packages_handled"`
	} `json:"summary"`
}

func (exp *DataExporter) extractPackageFromMessage(message string) string {
	// Try to extract package name from common message patterns
	lowerMsg := strings.ToLower(message)

	// Look for specific patterns that indicate actual package names
	// Be more strict to avoid false positives
	patterns := []struct {
		prefix    string
		minLength int
	}{
		{"installing package ", 10},
		{"updating package ", 10},
		{"removing package ", 10},
		{"processing item ", 8},
		{"handling item ", 8},
	}

	for _, pattern := range patterns {
		if idx := strings.Index(lowerMsg, pattern.prefix); idx != -1 {
			remaining := message[idx+len(pattern.prefix):]
			// Extract the first word after the pattern
			words := strings.Fields(remaining)
			if len(words) > 0 {
				// Clean up the package name
				packageName := words[0]
				packageName = strings.Trim(packageName, ".,;:!?\"'")
				// Only return if it's long enough and doesn't contain common false positive words
				if len(packageName) >= pattern.minLength &&
					!strings.Contains(packageName, "manifest") &&
					!strings.Contains(packageName, "catalog") &&
					!strings.Contains(packageName, "check") {
					return packageName
				}
			}
		}
	}

	return ""
}

// inferItemType attempts to determine the item type from session data or context
func (exp *DataExporter) inferItemType(packageName, sessionDir string, event EventRecord) string {
	// Try to read session manifest data if available
	sessionPath := filepath.Join(exp.baseDir, sessionDir, "cimian.yaml")
	if data, err := os.ReadFile(sessionPath); err == nil {
		content := string(data)
		if strings.Contains(content, "managed_installs:") && strings.Contains(content, packageName) {
			return "managed_installs"
		}
		if strings.Contains(content, "managed_updates:") && strings.Contains(content, packageName) {
			return "managed_updates"
		}
		if strings.Contains(content, "optional_installs:") && strings.Contains(content, packageName) {
			return "optional_installs"
		}
		if strings.Contains(content, "managed_uninstalls:") && strings.Contains(content, packageName) {
			return "managed_uninstalls"
		}
	}

	// Fallback to inferring from event type
	switch event.EventType {
	case "install":
		return "managed_installs"
	case "update":
		return "managed_updates"
	case "remove":
		return "managed_uninstalls"
	default:
		return "unknown"
	}
}

// normalizeStatus converts various status formats to standard form
func (exp *DataExporter) normalizeStatus(status, level, errorMsg string) string {
	// Convert to lowercase for comparison
	statusLower := strings.ToLower(status)
	levelLower := strings.ToLower(level)

	// Check for explicit error conditions
	if errorMsg != "" || levelLower == "error" || strings.Contains(statusLower, "fail") {
		return "failed"
	}

	// Check for warning conditions
	if levelLower == "warn" || levelLower == "warning" || strings.Contains(statusLower, "warn") {
		return "warning"
	}

	// Check for success conditions
	if statusLower == "completed" || statusLower == "success" || statusLower == "ok" ||
		strings.Contains(statusLower, "success") || strings.Contains(statusLower, "complete") {
		return "success"
	}

	// Default based on log level
	switch levelLower {
	case "info":
		return "success"
	case "debug":
		return "success"
	default:
		return "unknown"
	}
}

// detectInstallLoop analyzes recent attempts to detect if an item is stuck in an install loop
func (exp *DataExporter) detectInstallLoop(attempts []ItemAttempt) bool {
	if len(attempts) < 3 {
		return false // Need at least 3 attempts to detect a loop
	}

	// Count recent install attempts (last 5 attempts)
	recentCount := len(attempts)
	if recentCount > 5 {
		attempts = attempts[recentCount-5:] // Look at last 5 attempts
	}

	installAttempts := 0
	successCount := 0

	// Parse timestamps to check if attempts are recent (within last 7 days)
	cutoffTime := time.Now().Add(-7 * 24 * time.Hour)

	for _, attempt := range attempts {
		if attemptTime, err := time.Parse(time.RFC3339, attempt.Timestamp); err == nil {
			if attemptTime.After(cutoffTime) {
				if attempt.Action == "install" || attempt.Action == "update" {
					installAttempts++
					if attempt.Status == "success" {
						successCount++
					}
				}
			}
		}
	}

	// Detect loop: 3+ install attempts in recent history with less than 50% success rate
	// This indicates the item keeps trying to install but isn't staying installed
	if installAttempts >= 3 {
		successRate := float64(successCount) / float64(installAttempts)
		return successRate < 0.5 // Less than 50% success rate indicates a loop
	}

	return false
}

// getSessionStartTimeFromEvents extracts the start time from the first event in events.jsonl
func (exp *DataExporter) getSessionStartTimeFromEvents(sessionDir string) time.Time {
	eventsPath := filepath.Join(exp.baseDir, sessionDir, "events.jsonl")

	content, err := os.ReadFile(eventsPath)
	if err != nil {
		return time.Time{}
	}

	// Handle multi-line JSON format - get the first complete JSON object
	var currentJSON strings.Builder
	var braceCount int
	var inString bool
	var escapeNext bool

	for _, char := range string(content) {
		if escapeNext {
			escapeNext = false
			currentJSON.WriteRune(char)
			continue
		}

		switch char {
		case '\\':
			escapeNext = true
			currentJSON.WriteRune(char)
		case '"':
			if !escapeNext {
				inString = !inString
			}
			currentJSON.WriteRune(char)
		case '{':
			if !inString {
				braceCount++
			}
			currentJSON.WriteRune(char)
		case '}':
			if !inString {
				braceCount--
			}
			currentJSON.WriteRune(char)
		default:
			currentJSON.WriteRune(char)
		}

		// If braces are balanced and we have the first complete JSON object
		if braceCount == 0 && currentJSON.Len() > 0 && !inString {
			jsonStr := strings.TrimSpace(currentJSON.String())
			if jsonStr != "" {
				var event map[string]interface{}
				if err := json.Unmarshal([]byte(jsonStr), &event); err == nil {
					if timestampStr, ok := event["timestamp"].(string); ok {
						if timestamp, err := time.Parse(time.RFC3339, timestampStr); err == nil {
							return timestamp
						}
					}
				}
			}
			// Return after first event
			break
		}
	}

	return time.Time{}
}

// fillMissingSessionData fills in missing environment data from events.jsonl
func (exp *DataExporter) fillMissingSessionData(record *SessionRecord, sessionDir string) {
	eventsPath := filepath.Join(exp.baseDir, sessionDir, "events.jsonl")

	content, err := os.ReadFile(eventsPath)
	if err != nil {
		return
	}

	// Handle multi-line JSON format (pretty-printed JSON objects)
	var currentJSON strings.Builder
	var braceCount int
	var inString bool
	var escapeNext bool

	for _, char := range string(content) {
		if escapeNext {
			escapeNext = false
			currentJSON.WriteRune(char)
			continue
		}

		switch char {
		case '\\':
			escapeNext = true
			currentJSON.WriteRune(char)
		case '"':
			if !escapeNext {
				inString = !inString
			}
			currentJSON.WriteRune(char)
		case '{':
			if !inString {
				braceCount++
			}
			currentJSON.WriteRune(char)
		case '}':
			if !inString {
				braceCount--
			}
			currentJSON.WriteRune(char)
		default:
			currentJSON.WriteRune(char)
		}

		// If braces are balanced and we have a complete JSON object
		if braceCount == 0 && currentJSON.Len() > 0 && !inString {
			jsonStr := strings.TrimSpace(currentJSON.String())
			if jsonStr != "" {
				var event map[string]interface{}
				if err := json.Unmarshal([]byte(jsonStr), &event); err == nil {
					// Extract environment data from event properties
					if props, ok := event["properties"].(map[string]interface{}); ok {
						if record.Hostname == "" {
							if hostname, ok := props["hostname"].(string); ok && hostname != "" {
								record.Hostname = hostname
							}
						}
						if record.User == "" {
							if user, ok := props["user"].(string); ok && user != "" {
								record.User = user
							}
						}
						if record.LogVersion == "" {
							if version, ok := props["log_version"].(string); ok && version != "" {
								record.LogVersion = version
							}
						}
						if record.ProcessID == 0 {
							if processID, ok := props["process_id"].(float64); ok && processID > 0 {
								record.ProcessID = int(processID)
							}
						}
					}

					// Also check direct event fields (correct field names)
					if record.Hostname == "" {
						if hostname, ok := event["hostname"].(string); ok && hostname != "" {
							record.Hostname = hostname
						}
					}
					if record.User == "" {
						if user, ok := event["user"].(string); ok && user != "" {
							record.User = user
						}
					}
					if record.LogVersion == "" {
						if version, ok := event["version"].(string); ok && version != "" {
							record.LogVersion = version
						}
					}
					if record.ProcessID == 0 {
						if processID, ok := event["pid"].(float64); ok && processID > 0 {
							record.ProcessID = int(processID)
						}
					}
					if record.RunType == "" {
						if runType, ok := event["run_type"].(string); ok && runType != "" {
							record.RunType = runType
						}
					}

					// Stop once we have all the data we need
					if record.Hostname != "" && record.LogVersion != "" && record.ProcessID != 0 && record.RunType != "" {
						// If user is still missing, try to get it from the environment
						if record.User == "" {
							if user := os.Getenv("USERNAME"); user != "" {
								record.User = user
							}
						}
						return
					}
				}
			}
			currentJSON.Reset()
		}
	}
}
