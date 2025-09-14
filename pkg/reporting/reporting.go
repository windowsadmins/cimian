// pkg/reporting/reporting.go - Data reporting functionality for external monitoring tools

package reporting

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/status"
	"golang.org/x/sys/windows/registry"
	"gopkg.in/yaml.v3"
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

	// Enhanced fields for external reporting tools
	Config  *SessionConfig  `json:"config,omitempty"`
	Summary *SessionSummary `json:"summary,omitempty"`
}

// SessionConfig represents configuration data for external reporting tool integration
type SessionConfig struct {
	Manifest         string `json:"manifest,omitempty"`
	SoftwareRepoURL  string `json:"software_repo_url,omitempty"`
	ClientIdentifier string `json:"client_identifier,omitempty"`
	BootstrapMode    bool   `json:"bootstrap_mode,omitempty"`
	CachePath        string `json:"cache_path,omitempty"`
	DefaultCatalog   string `json:"default_catalog,omitempty"`
	LogLevel         string `json:"log_level,omitempty"`
}

// SessionSummary represents enhanced summary data for external reporting tool integration
type SessionSummary struct {
	TotalPackagesManaged int     `json:"total_packages_managed"`
	PackagesInstalled    int     `json:"packages_installed"`
	PackagesPending      int     `json:"packages_pending"`
	PackagesFailed       int     `json:"packages_failed"`
	CacheSizeMB          float64 `json:"cache_size_mb,omitempty"`
	
	// ENHANCEMENT 4: Failed package details for ReportMate
	FailedPackages       []FailedPackageInfo `json:"failed_packages,omitempty"`
}

// FailedPackageInfo provides details about failed packages for ReportMate
type FailedPackageInfo struct {
	PackageID    string `json:"package_id"`
	PackageName  string `json:"package_name"`
	ErrorType    string `json:"error_type"`
	LastAttempt  string `json:"last_attempt"`
	ErrorMessage string `json:"error_message,omitempty"`
}

// EventRecord represents a row in the events table
type EventRecord struct {
	EventID    string `json:"event_id"`
	SessionID  string `json:"session_id"`
	Timestamp  string `json:"timestamp"`
	Level      string `json:"level"`
	EventType  string `json:"event_type"`
	
	// ENHANCEMENT 1: Enhanced package context for ReportMate
	PackageID      string `json:"package_id,omitempty"`      // Package identifier for correlation
	PackageName    string `json:"package_name,omitempty"`    // Human readable name
	PackageVersion string `json:"package_version,omitempty"` // Version being processed
	
	// Legacy fields (maintained for compatibility)
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

	// ENHANCEMENT 3: Enhanced error information
	ErrorDetails   *ErrorDetails `json:"error_details,omitempty"`
	
	// ENHANCEMENT 5: Installation method context
	InstallerType  string `json:"installer_type,omitempty"`  // "chocolatey", "nupkg", "msi", "exe", "zip"
	
	// Enhanced fields for external reporting tools
	Details string        `json:"details,omitempty"`
	Context *EventContext `json:"context,omitempty"`
	LogFile string        `json:"log_file,omitempty"`
}

// ErrorDetails provides structured error information for troubleshooting
type ErrorDetails struct {
	ErrorCode      int    `json:"error_code,omitempty"`
	ErrorType      string `json:"error_type,omitempty"`
	Command        string `json:"command,omitempty"`
	Stderr         string `json:"stderr,omitempty"`
	RetryCount     int    `json:"retry_count,omitempty"`
	ResolutionHint string `json:"resolution_hint,omitempty"`
}

// EventContext represents context information for events
type EventContext struct {
	RunType   string `json:"run_type,omitempty"`
	User      string `json:"user,omitempty"`
	Hostname  string `json:"hostname,omitempty"`
	ProcessID int    `json:"process_id,omitempty"`
}

// ItemRecord represents a row in the items table (comprehensive device status)
type ItemRecord struct {
	// Core identification
	ID          string `json:"id"`                     // Short identifier for external tools
	ItemName    string `json:"item_name"`              // Full package name
	DisplayName string `json:"display_name,omitempty"` // User-friendly display name
	ItemType    string `json:"item_type"`              // managed_installs, managed_updates, optional_installs, etc.

	// Version information
	CurrentStatus    string `json:"current_status"` // "installed", "failed", "warning", "install_loop", "not_installed", "pending_install"
	LatestVersion    string `json:"latest_version"`
	InstalledVersion string `json:"installed_version,omitempty"`

	// Status and timing
	LastSeenInSession  string `json:"last_seen_in_session"`
	LastSuccessfulTime string `json:"last_successful_time"`
	LastAttemptTime    string `json:"last_attempt_time"`
	LastAttemptStatus  string `json:"last_attempt_status"` // "success", "failed", "warning"
	LastUpdate         string `json:"last_update"`         // Most recent activity timestamp

	// Statistics
	InstallCount        int  `json:"install_count"`
	UpdateCount         int  `json:"update_count"`
	RemovalCount        int  `json:"removal_count"`
	FailureCount        int  `json:"failure_count"`
	WarningCount        int  `json:"warning_count"`
	TotalSessions       int  `json:"total_sessions"`
	// ENHANCEMENT 6: Enhanced install loop detection
	InstallLoopDetected bool               `json:"install_loop_detected"`
	LoopDetails         *InstallLoopDetail `json:"loop_details,omitempty"`

	// Enhanced metadata for external reporting tools
	InstallMethod string `json:"install_method,omitempty"` // "nupkg", "msi", "exe", "pwsh"
	Type          string `json:"type"`                     // Package manager type identifier

	// Error information
	LastError      string        `json:"last_error,omitempty"`
	LastWarning    string        `json:"last_warning,omitempty"`
	RecentAttempts []ItemAttempt `json:"recent_attempts,omitempty"` // Last 5 attempts for loop detection
}

// InstallLoopDetail provides enhanced information about install loops
type InstallLoopDetail struct {
	DetectionCriteria  string `json:"detection_criteria"`
	LoopStartSession   string `json:"loop_start_session"`
	SuspectedCause     string `json:"suspected_cause"`
	Recommendation     string `json:"recommendation"`
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
	manifestPackageCache map[string]int // Cache for manifest package counts to avoid repetitive parsing
}

// NewDataExporter creates a new data exporter
func NewDataExporter(baseDir string) *DataExporter {
	return &DataExporter{
		baseDir: baseDir,
		manifestPackageCache: make(map[string]int),
	}
}

// loadCimianConfiguration loads the current Cimian configuration for session enhancement
func (exp *DataExporter) loadCimianConfiguration() *SessionConfig {
	cfg, err := config.LoadConfig()
	if err != nil {
		return nil
	}

	sessionConfig := &SessionConfig{
		SoftwareRepoURL:  cfg.SoftwareRepoURL,
		ClientIdentifier: cfg.ClientIdentifier,
		CachePath:        cfg.CachePath,
		DefaultCatalog:   cfg.DefaultCatalog,
		LogLevel:         cfg.LogLevel,
	}

	// Determine current manifest path (simplified version)
	if cfg.LocalOnlyManifest != "" {
		sessionConfig.Manifest = cfg.LocalOnlyManifest
	} else if cfg.ClientIdentifier != "" {
		sessionConfig.Manifest = cfg.ClientIdentifier
	}

	return sessionConfig
}

// calculateCacheSize estimates the cache directory size in MB
func (exp *DataExporter) calculateCacheSize(cachePath string) float64 {
	if cachePath == "" {
		return 0.0
	}

	var totalSize int64
	err := filepath.Walk(cachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil // Continue on errors
		}
		if !info.IsDir() {
			totalSize += info.Size()
		}
		return nil
	})

	if err != nil {
		return 0.0
	}

	return float64(totalSize) / (1024 * 1024) // Convert to MB
}

// determineInstallMethod attempts to determine the install method from cache files and package name
// ENHANCEMENT 5: Track specific installation methods for better troubleshooting
func (exp *DataExporter) determineInstallMethod(packageName string, sessionConfig *SessionConfig) string {
	if sessionConfig == nil || sessionConfig.CachePath == "" {
		return "unknown"
	}

	nameLower := strings.ToLower(packageName)

	// Check cache directory for package files
	installMethods := map[string]string{
		".nupkg":  "nupkg",
		".msi":    "msi", 
		".exe":    "exe",
		".msix":   "msix",
		".appx":   "appx",
		".zip":    "zip",
		".7z":     "archive",
		".cab":    "cab",
	}

	for ext, method := range installMethods {
		// Try various naming patterns
		patterns := []string{
			fmt.Sprintf("%s*%s", nameLower, ext),
			fmt.Sprintf("*%s*%s", nameLower, ext),
		}

		for _, pattern := range patterns {
			matches, _ := filepath.Glob(filepath.Join(sessionConfig.CachePath, "**", pattern))
			if len(matches) > 0 {
				return method
			}
		}
	}

	// Check for PowerShell scripts
	psPatterns := []string{
		fmt.Sprintf("%s*.ps1", nameLower),
		fmt.Sprintf("*%s*.ps1", nameLower),
	}
	for _, pattern := range psPatterns {
		matches, _ := filepath.Glob(filepath.Join(sessionConfig.CachePath, "**", pattern))
		if len(matches) > 0 {
			return "powershell"
		}
	}

	// Check for Chocolatey packages (common naming patterns)
	if strings.Contains(nameLower, "chocolatey") || strings.Contains(nameLower, "choco") {
		return "chocolatey"
	}

	return "unknown"
}

// GenerateSessionsTable creates external tool-compatible session records
func (exp *DataExporter) GenerateSessionsTable(limitDays int) ([]SessionRecord, error) {
	sessions, err := exp.getRecentSessions(limitDays)
	if err != nil {
		return nil, fmt.Errorf("failed to get recent sessions: %w", err)
	}

	// Load current configuration for session enhancement
	sessionConfig := exp.loadCimianConfiguration()
	var cacheSize float64
	if sessionConfig != nil && sessionConfig.CachePath != "" {
		cacheSize = exp.calculateCacheSize(sessionConfig.CachePath)
	}

	// PERFORMANCE OPTIMIZATION: Calculate total managed packages once, not per session
	// Since all sessions for this system use the same manifest, we only need to calculate this once
	var totalManagedPackages int
	if sessionConfig != nil {
		totalManagedPackages = exp.getTotalManagedPackagesFromManifest(sessionConfig)
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
				Config:          sessionConfig, // Add configuration data
			}

			// Calculate duration - prioritize session-level duration_seconds
			if session.DurationSeconds != nil {
				// Use the pre-calculated duration_seconds from session
				record.Duration = *session.DurationSeconds
			} else if session.EndTime != nil {
				// Fallback to calculating duration from timestamps
				record.EndTime = session.EndTime.Format(time.RFC3339)
				if !session.StartTime.IsZero() {
					record.Duration = int64(session.EndTime.Sub(session.StartTime).Seconds())
				}
			} else if session.Summary.Duration > 0 {
				// Last fallback to summary duration (convert from nanoseconds)
				record.Duration = int64(session.Summary.Duration.Seconds())
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

			// Create enhanced summary for external reporting tools
			// Use pre-calculated total managed packages (calculated once outside the loop)
			finalTotalManagedPackages := totalManagedPackages
			if finalTotalManagedPackages == 0 {
				// Fallback to packages handled if manifest reading fails
				finalTotalManagedPackages = len(record.PackagesHandled)
			}
			
			summary := &SessionSummary{
				TotalPackagesManaged: finalTotalManagedPackages,
				PackagesInstalled:    record.Successes,
				PackagesPending:      finalTotalManagedPackages - record.Successes - record.Failures,
				PackagesFailed:       record.Failures,
				CacheSizeMB:          cacheSize,
			}

			// ENHANCEMENT 4: Add failed package details to sessions
			if record.Failures > 0 {
				summary.FailedPackages = exp.getFailedPackagesForSession(sessionDir)
			}
			
			record.Summary = summary

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
				// Add configuration and summary data to old format records too
				record.Config = sessionConfig
				if record.Summary == nil {
					// Use pre-calculated total managed packages (calculated once outside the loop)
					finalTotalManagedPackages := totalManagedPackages
					if finalTotalManagedPackages == 0 {
						finalTotalManagedPackages = len(record.PackagesHandled)
					}
					
					record.Summary = &SessionSummary{
						TotalPackagesManaged: finalTotalManagedPackages,
						PackagesInstalled:    record.Successes,
						PackagesPending:      finalTotalManagedPackages - record.Successes - record.Failures,
						PackagesFailed:       record.Failures,
						CacheSizeMB:          cacheSize,
					}
				}
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

	file, err := os.Open(eventsPath)
	if err != nil {
		return nil, fmt.Errorf("failed to read events file: %w", err)
	}
	defer file.Close()

	var records []EventRecord
	cutoffTime := time.Now().Add(-time.Duration(limitHours) * time.Hour)

	// ENHANCED FIX: Use robust JSON parsing to handle both JSONL and pretty-printed JSON
	events, err := exp.parseEventsWithRecovery(file)
	if err != nil {
		return nil, fmt.Errorf("failed to parse events: %w", err)
	}
	
	for _, logEvent := range events {
		
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
			continue
		}

		// Extract package name and version from properties and message
		packageName := ""
		packageID := ""
		version := ""
		action := ""
		status := ""
		errorMsg := ""
		installerType := ""
		eventType := ""

		// ENHANCED FIX: Handle both new event format (context.item) and legacy format (properties.item)
		
		// New event format: check context.item first
		if context, ok := logEvent["context"].(map[string]interface{}); ok {
			if item, ok := context["item"].(string); ok {
				packageName = item
				packageID = exp.generatePackageID(item)
			}
			if ver, ok := context["version"].(string); ok {
				version = ver
			}
		}
		
		// Event-level fields in new format
		if eventTypeVal, ok := logEvent["event_type"].(string); ok {
			eventType = eventTypeVal
		}
		if actionVal, ok := logEvent["action"].(string); ok {
			action = actionVal
		}
		if statusVal, ok := logEvent["status"].(string); ok {
			status = statusVal
		}
		
		// Legacy format: check properties (fallback)
		if packageName == "" {
			if props, ok := logEvent["properties"].(map[string]interface{}); ok {
				if item, ok := props["item"].(string); ok {
					packageName = item
					packageID = exp.generatePackageID(item)
				}
				// Check multiple version field names used in Cimian logs
				if version == "" {
					if ver, ok := props["version"].(string); ok {
						version = ver
					} else if ver, ok := props["registryVersion"].(string); ok {
						version = ver
					} else if ver, ok := props["localVersion"].(string); ok {
						version = ver
					} else if ver, ok := props["repoVersion"].(string); ok {
						version = ver
					} else if ver, ok := props["targetVersion"].(string); ok {
						version = ver
					}
				}
				if action == "" {
					if act, ok := props["action"].(string); ok {
						action = act
					}
				}
				if status == "" {
					if stat, ok := props["status"].(string); ok {
						status = stat
					}
				}
				if errVal, ok := props["error"]; ok && errVal != nil {
					if errStr, ok := errVal.(string); ok {
						errorMsg = errStr
					}
				}
				// Extract installer type from installer path or method
				if installerPath, ok := props["installer_path"].(string); ok {
					installerType = exp.determineInstallerTypeFromPath(installerPath)
				}
			}
		}

		// Get message and extract additional version info if not already present
		message := ""
		if msg, ok := logEvent["message"].(string); ok {
			message = msg

			// Extract version from message if not found in properties/context
			if version == "" {
				version = exp.extractVersionFromMessage(message)
			}

			// Extract package name from message if not found in properties/context
			if packageName == "" {
				extractedName := exp.extractPackageFromMessage(message)
				if extractedName != "" {
					packageName = extractedName
					packageID = exp.generatePackageID(extractedName)
				}
			}

			// Only infer event type from message if not already set from new format
			if eventType == "" {
				eventType = "general"
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
		}

		// Generate enhanced event ID with package information
		eventID := fmt.Sprintf("%s-%s-%v", sessionID, packageID, logEvent["time"])
		if packageID == "" {
			eventID = fmt.Sprintf("%s-%v", sessionID, logEvent["time"])
		}

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

		// Enhanced error details for external reporting tools
		var errorDetails *ErrorDetails
		details := ""
		if errorMsg != "" && level == "ERROR" {
			errorDetails = exp.createErrorDetails(errorMsg, message, action)
			details = fmt.Sprintf("Error Details: %s", errorMsg)
			if strings.Contains(message, "timeout") {
				details += ". This may be due to network connectivity issues."
			} else if strings.Contains(message, "exit code") {
				details += ". Check installer logs for more information."
			}
		}

		// Create event context for external reporting tool integration
		eventContext := &EventContext{}
		if props, ok := logEvent["properties"].(map[string]interface{}); ok {
			if runType, ok := props["run_type"].(string); ok {
				eventContext.RunType = runType
			}
			if user, ok := props["user"].(string); ok {
				eventContext.User = user
			}
			if hostname, ok := props["hostname"].(string); ok {
				eventContext.Hostname = hostname
			}
			if processID, ok := props["process_id"].(float64); ok {
				eventContext.ProcessID = int(processID)
			}
		}

		// Extract duration and progress if available
		var duration int64
		var progress int
		if props, ok := logEvent["properties"].(map[string]interface{}); ok {
			if dur, ok := props["duration_ms"].(float64); ok {
				duration = int64(dur)
			}
			if prog, ok := props["progress"].(float64); ok {
				progress = int(prog)
			}
		}

		// Generate log file path for this session
		logFilePath := filepath.Join(exp.baseDir, sessionID, "events.jsonl")

		record := EventRecord{
			EventID:    eventID,
			SessionID:  sessionID,
			Timestamp:  timestamp,
			Level:      level,
			EventType:  eventType,
			
			// Enhanced package context for ReportMate
			PackageID:      packageID,
			PackageName:    packageName,
			PackageVersion: version,
			
			// Legacy fields (maintained for compatibility)
			Package:    packageName,
			Version:    version,
			
			Action:     action,
			Status:     exp.normalizeStatus(status, level, errorMsg),
			Message:    message,
			Duration:   duration,
			Progress:   progress,
			Error:      errorMsg,
			SourceFile: sourceFile,
			SourceFunc: sourceFunc,
			SourceLine: 0,

			// Enhanced fields
			ErrorDetails:  errorDetails,
			InstallerType: installerType,
			Details:       details,
			Context:       eventContext,
			LogFile:       logFilePath,
		}

		records = append(records, record)
	}

	return records, nil
}

// ManifestFile represents a parsed manifest YAML file
type ManifestFile struct {
	ManagedInstalls   []interface{} `yaml:"managed_installs"`
	ManagedUninstalls []interface{} `yaml:"managed_uninstalls"`
	ManagedUpdates    []interface{} `yaml:"managed_updates"`
	OptionalInstalls  []interface{} `yaml:"optional_installs"`
}

// parseManifestFile parses a YAML manifest file
func (exp *DataExporter) parseManifestFile(filepath string) (*ManifestFile, error) {
	data, err := os.ReadFile(filepath)
	if err != nil {
		return nil, err
	}
	
	var manifest ManifestFile
	err = yaml.Unmarshal(data, &manifest)
	if err != nil {
		return nil, err
	}
	
	return &manifest, nil
}

// getSystemArchitecture returns the current system architecture
func (exp *DataExporter) getSystemArchitecture() string {
	// This mimics the logic from pkg/status but simplified for reporting
	switch runtime.GOARCH {
	case "amd64":
		return "x64"
	case "arm64":
		return "arm64"
	case "386":
		return "x86"
	default:
		return runtime.GOARCH
	}
}

// checkArchitectureCompatibility checks if a package supports the current system architecture
func (exp *DataExporter) checkArchitectureCompatibility(packageName, systemArch string) (bool, []string) {
	// Load catalog to get supported architectures
	catalogsPath := `C:\ProgramData\ManagedInstalls\catalogs`
	
	var foundSupportedArchs []string
	compatible := false
	
	// Try to find the package in catalogs
	filepath.Walk(catalogsPath, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || compatible {
			return nil
		}
		
		if !strings.HasSuffix(strings.ToLower(path), ".yaml") {
			return nil
		}
		
		// Parse catalog file
		data, err := os.ReadFile(path)
		if err != nil {
			return nil
		}
		
		var catalog struct {
			Items []struct {
				Name                   string   `yaml:"name"`
				SupportedArchitectures []string `yaml:"supported_architectures"`
			} `yaml:"items"`
		}
		
		if err := yaml.Unmarshal(data, &catalog); err != nil {
			return nil
		}
		
		// Look for the package in the items array
		for _, item := range catalog.Items {
			if strings.EqualFold(item.Name, packageName) {
				foundSupportedArchs = item.SupportedArchitectures
				for _, arch := range item.SupportedArchitectures {
					if arch == systemArch {
						compatible = true
						return nil
					}
				}
				return nil // Found package but not compatible
			}
		}
		return nil
	})
	
	return compatible, foundSupportedArchs
}

// getRegistryVersion checks for installed version using comprehensive detection
func (exp *DataExporter) getRegistryVersion(packageName string) string {
	// First try the basic Cimian-managed registry check
	regPath := `Software\ManagedInstalls\` + packageName
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, regPath, registry.QUERY_VALUE)
	if err == nil {
		defer key.Close()
		if version, _, err := key.GetStringValue("Version"); err == nil && version != "" {
			return version
		}
	}
	
	// If basic registry check fails, this might be a package installed through other means
	// or the registry tracking is incomplete. For reporting purposes, we should return
	// empty string and let the calling code handle it properly.
	return ""
}

// populateFromCurrentManifests loads current manifest state to ensure all managed packages are represented
func (exp *DataExporter) populateFromCurrentManifests(itemStats map[string]*comprehensiveItemStat) error {
	// Load catalog data to get version information
	catalogVersions := exp.loadCatalogVersions()
	catalogDisplayNames := exp.loadCatalogDisplayNames()
	
	// Get system architecture for compatibility checking
	systemArch := exp.getSystemArchitecture()
	
	// Get the root manifest path - this should contain the current managed items
	manifestsDir := filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests")
	
	// Try to find the main manifest file(s)
	err := filepath.Walk(manifestsDir, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil // Continue on errors
		}
		
		// Skip directories
		if info.IsDir() {
			return nil
		}
		
		// Only process YAML files
		if !strings.HasSuffix(strings.ToLower(path), ".yaml") && !strings.HasSuffix(strings.ToLower(path), ".yml") {
			return nil
		}
		
		// Parse the manifest file
		manifest, err := exp.parseManifestFile(path)
		if err != nil {
			return nil // Continue on parse errors
		}
		
		// Extract items from all managed install categories
		allItems := []interface{}{}
		allItems = append(allItems, manifest.ManagedInstalls...)
		allItems = append(allItems, manifest.ManagedUninstalls...)
		allItems = append(allItems, manifest.ManagedUpdates...)
		allItems = append(allItems, manifest.OptionalInstalls...)
		
		// Process each item
		for _, item := range allItems {
			var packageName string
			
			// Handle different item formats (string or object)
			switch v := item.(type) {
			case string:
				packageName = v
			case map[string]interface{}:
				if name, ok := v["name"].(string); ok {
					packageName = name
				}
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
			
			// Set version information from catalog
			if catalogVersion, hasCatalogVersion := catalogVersions[strings.ToLower(packageName)]; hasCatalogVersion && catalogVersion != "" {
				stats.LatestVersion = catalogVersion
			}
			
			// Set display name from catalog
			if displayName, hasDisplayName := catalogDisplayNames[strings.ToLower(packageName)]; hasDisplayName && displayName != "" {
				stats.DisplayName = displayName
			} else {
				stats.DisplayName = packageName // Fallback to package name
			}
			
			// Check architecture compatibility first
			compatible, supportedArchs := exp.checkArchitectureCompatibility(packageName, systemArch)
			
			// Check installed status from registry
			registryVersion := exp.getRegistryVersion(packageName)
			if registryVersion != "" {
				stats.InstalledVersion = registryVersion
				
			if !compatible && len(supportedArchs) > 0 {
				// Package is installed but not compatible with current architecture
				stats.CurrentStatus = "Warning"
				stats.WarningCount = 1
				archList := strings.Join(supportedArchs, ", ")
				stats.LastWarning = fmt.Sprintf("Architecture mismatch: package supports %s, system is %s", archList, systemArch)
			} else {
				stats.CurrentStatus = "Installed"
			}
		} else {
			if !compatible && len(supportedArchs) > 0 {
				// Package is not installed and not compatible
				stats.CurrentStatus = "Not Available"
				stats.WarningCount = 1
				archList := strings.Join(supportedArchs, ", ")
				stats.LastWarning = fmt.Sprintf("Architecture mismatch: package supports %s, system is %s", archList, systemArch)
			} else {
				stats.CurrentStatus = "Pending"
			}
		}			// Set item type based on which array it came from
			if stats.ItemType == "" {
				stats.ItemType = "managed_installs" // Default
			}
		}
		
		return nil
	})
	
	return err
}

// GenerateItemsTable creates a comprehensive view of all items ever managed by Cimian
func (exp *DataExporter) GenerateItemsTable(limitDays int) ([]ItemRecord, error) {
	itemStats := make(map[string]*comprehensiveItemStat)

	// CRITICAL FIX: First populate from current manifest state
	// This ensures we always have data even without historical events
	err := exp.populateFromCurrentManifests(itemStats)
	if err != nil {
		// Log but don't fail - continue with event-based data
		fmt.Printf("Warning: Could not load current manifest data: %v\n", err)
	}

	// Get ALL sessions (not just recent ones) to build comprehensive history
	allSessions, err := exp.getAllSessions()
	if err != nil {
		return nil, fmt.Errorf("failed to get all sessions: %w", err)
	}

	// Load catalog data to get authoritative version information
	catalogVersions := exp.loadCatalogVersions()
	
	// Load catalog display names for proper display name information
	catalogDisplayNames := exp.loadCatalogDisplayNames()

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

			// Update latest version - prioritize catalog version over event version
			if catalogVersion, hasCatalogVersion := catalogVersions[strings.ToLower(packageName)]; hasCatalogVersion && catalogVersion != "" {
				stats.LatestVersion = catalogVersion
			} else if event.Version != "" {
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
				case "Success":
					stats.InstallCount++
					stats.CurrentStatus = "Installed"
					if timeErr == nil {
						stats.LastSuccessfulTime = eventTime
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "Success"
					stats.InstalledVersion = event.Version
				case "Failed":
					stats.FailureCount++
					stats.CurrentStatus = "Failed"
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "Failed"
					if event.Error != "" {
						stats.LastError = event.Error
					} else {
						stats.LastError = event.Message
					}
				case "Warning":
					stats.WarningCount++
					if stats.CurrentStatus != "Failed" { // Don't override failed status
						stats.CurrentStatus = "Warning"
					}
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "warning"
					stats.LastWarning = event.Message
				}
			case "update":
				switch attempt.Status {
				case "Success":
					stats.UpdateCount++
					stats.CurrentStatus = "Installed"
					if timeErr == nil {
						stats.LastSuccessfulTime = eventTime
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "Success"
					stats.InstalledVersion = event.Version
				case "Failed":
					stats.FailureCount++
					stats.CurrentStatus = "Failed"
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "Failed"
					if event.Error != "" {
						stats.LastError = event.Error
					} else {
						stats.LastError = event.Message
					}
				case "Warning":
					stats.WarningCount++
					if stats.CurrentStatus != "Failed" {
						stats.CurrentStatus = "Warning"
					}
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "Warning"
					stats.LastWarning = event.Message
				}
			case "remove":
				if attempt.Status == "Success" {
					stats.RemovalCount++
					stats.CurrentStatus = "Not Installed"
					stats.InstalledVersion = ""
					if timeErr == nil {
						stats.LastAttemptTime = eventTime
					}
					stats.LastAttemptStatus = "Success"
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
		loopDetected, loopDetails := exp.detectInstallLoopEnhanced(stats.RecentAttempts, stats.Name)
		stats.InstallLoopDetected = loopDetected
		if loopDetected {
			stats.CurrentStatus = "Install Loop"
			stats.LoopDetails = loopDetails
			// Generate warning for install loop detection
			stats.LastWarning = fmt.Sprintf("Install loop detected: %s - %s", loopDetails.SuspectedCause, loopDetails.Recommendation)
			stats.WarningCount++
		}
	}

	// CRITICAL FIX: Add items from current manifest state (even if no events exist)
	// This ensures ReportMate gets data for all currently managed packages
	currentManifestItems := exp.getCurrentManagedItems()
	for _, item := range currentManifestItems {
		packageName := item.Name
		
		// If we don't have this item from events, create it
		if _, exists := itemStats[packageName]; !exists {
			itemStats[packageName] = &comprehensiveItemStat{
				Name:           packageName,
				Sessions:       make(map[string]bool),
				RecentAttempts: []ItemAttempt{},
				CurrentStatus:  "Installed", // Assume installed since it's in manifest
				ItemType:       item.Type,
				LastSeenTime:   time.Now(), // Mark as current
			}
		}
		
		// Update with current manifest data
		stats := itemStats[packageName]
		
		// Set version information from catalog and registry
		if catalogVersion, hasCatalogVersion := catalogVersions[strings.ToLower(packageName)]; hasCatalogVersion && catalogVersion != "" {
			stats.LatestVersion = catalogVersion
		}
		
		// Get installed version from registry
		if registryVersion := exp.getInstalledVersionFromRegistry(packageName); registryVersion != "" {
			stats.InstalledVersion = registryVersion
			// If current status is unknown, set to installed since we found registry data
			if stats.CurrentStatus == "" {
				stats.CurrentStatus = "Installed"
			}
		}
		
		// Ensure item type is set
		if stats.ItemType == "" {
			stats.ItemType = item.Type
		}
	}

	// Enhanced version detection - fill in missing version information
	for _, stats := range itemStats {
		// If we don't have latest version from catalog or events, try registry
		if stats.LatestVersion == "" {
			if registryVersion := exp.getInstalledVersionFromRegistry(stats.Name); registryVersion != "" {
				stats.LatestVersion = registryVersion
				// If we found it in registry and no installed version is set, use this
				if stats.InstalledVersion == "" {
					stats.InstalledVersion = registryVersion
				}
			}
		}

		// If we still don't have installed version, try to get it from registry
		if stats.InstalledVersion == "" {
			if registryVersion := exp.getInstalledVersionFromRegistry(stats.Name); registryVersion != "" {
				stats.InstalledVersion = registryVersion
			}
		}
	}

	// Convert to records
	var records []ItemRecord
	sessionConfig := exp.loadCimianConfiguration() // Load config for cache path info

	for _, stats := range itemStats {
		// Generate standard reporting ID (lowercase, no spaces)
		itemID := strings.ToLower(strings.ReplaceAll(stats.Name, " ", "-"))

		// Determine display name - prioritize catalog display_name over generated title case
		displayName := stats.Name
		if catalogDisplayName, hasCatalogDisplayName := catalogDisplayNames[strings.ToLower(stats.Name)]; hasCatalogDisplayName && catalogDisplayName != "" {
			// Use the display_name from catalog (authoritative source)
			displayName = catalogDisplayName
		} else if strings.Contains(stats.Name, "-") || strings.Contains(stats.Name, "_") {
			// Convert package-name or package_name to Package Name as fallback
			parts := strings.FieldsFunc(stats.Name, func(c rune) bool {
				return c == '-' || c == '_'
			})
			var titleParts []string
			for _, part := range parts {
				if len(part) > 0 {
					// Capitalize first letter of each part
					if len(part) == 1 {
						titleParts = append(titleParts, strings.ToUpper(part))
					} else {
						titleParts = append(titleParts, strings.ToUpper(part[:1])+strings.ToLower(part[1:]))
					}
				}
			}
			displayName = strings.Join(titleParts, " ")
		}

		// Map current status to standard reporting format
		standardStatus := stats.CurrentStatus
		if stats.CurrentStatus == "Not Installed" && stats.LatestVersion != "" {
			standardStatus = "Pending Install"
		}

		// If version is unknown, set status to Error
		if stats.LatestVersion == "" || stats.LatestVersion == "Unknown" {
			standardStatus = "Error"
		}

		// Determine last update timestamp
		lastUpdate := ""
		if !stats.LastAttemptTime.IsZero() {
			lastUpdate = stats.LastAttemptTime.Format(time.RFC3339)
		} else if !stats.LastSuccessfulTime.IsZero() {
			lastUpdate = stats.LastSuccessfulTime.Format(time.RFC3339)
		}

		// Determine install method from package data and cache
		installMethod := exp.determineInstallMethod(stats.Name, sessionConfig)

		record := ItemRecord{
			// Core identification (enhanced for external reporting tools)
			ID:          itemID,
			ItemName:    stats.Name,
			DisplayName: displayName,
			ItemType:    stats.ItemType,

			// Version information
			CurrentStatus:    standardStatus,
			LatestVersion:    stats.LatestVersion,
			InstalledVersion: stats.InstalledVersion,

			// Status and timing
			LastSeenInSession: stats.LastSeenSession,
			LastAttemptStatus: stats.LastAttemptStatus,
			LastUpdate:        lastUpdate,

			// Statistics
			InstallCount:        stats.InstallCount,
			UpdateCount:         stats.UpdateCount,
			RemovalCount:        stats.RemovalCount,
			FailureCount:        stats.FailureCount,
			WarningCount:        stats.WarningCount,
			TotalSessions:       len(stats.Sessions),
			// ENHANCEMENT 6: Enhanced install loop detection
			InstallLoopDetected: stats.InstallLoopDetected,
			LoopDetails:         stats.LoopDetails,

			// Enhanced metadata for external reporting tools
			InstallMethod: installMethod,
			Type:          "cimian",

			// Error information
			LastError:      stats.LastError,
			LastWarning:    stats.LastWarning,
			RecentAttempts: stats.RecentAttempts,
		}

		if !stats.LastSuccessfulTime.IsZero() {
			record.LastSuccessfulTime = stats.LastSuccessfulTime.Format(time.RFC3339)
		}
		if !stats.LastAttemptTime.IsZero() {
			record.LastAttemptTime = stats.LastAttemptTime.Format(time.RFC3339)
		}

		// CRITICAL FIX: Correct status for packages successfully installed but not tracked in registry
		// This addresses the core issue where packages like FortiClient-VPN show "Pending" despite successful installation
		if record.CurrentStatus == "Pending Install" || record.CurrentStatus == "Not Installed" || record.CurrentStatus == "Pending" {
			// Use direct Windows registry check for installed software
			if installedVersion := exp.getInstalledVersionFromWindowsRegistry(stats.Name); installedVersion != "" {
				record.CurrentStatus = "Installed"
				record.InstalledVersion = installedVersion
			}
		}

		records = append(records, record)
	}

	return records, nil
}

// Helper function to check Windows registry for installed software version
func (exp *DataExporter) getInstalledVersionFromWindowsRegistry(itemName string) string {
	// Map package names to their Windows display names
	displayNameMap := map[string][]string{
		"FortiClient-VPN": {"FortiClient VPN"},
		"Chrome":          {"Google Chrome"},
		"Git":             {"Git"},
		"PowerShell":      {"PowerShell"},
		"AzureCLI":        {"Microsoft Azure CLI"},
		// Add more mappings as needed
	}
	
	// Get possible display names for this item
	var displayNames []string
	if mappedNames, ok := displayNameMap[itemName]; ok {
		displayNames = mappedNames
	} else {
		// Default: use the item name itself and common variations
		displayNames = []string{itemName}
	}
	
	// Check Windows uninstall registry
	uninstallKey, err := registry.OpenKey(registry.LOCAL_MACHINE, `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, registry.ENUMERATE_SUB_KEYS)
	if err != nil {
		return ""
	}
	defer uninstallKey.Close()
	
	subKeys, err := uninstallKey.ReadSubKeyNames(-1)
	if err != nil {
		return ""
	}
	
	for _, subKey := range subKeys {
		subKeyPath := `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\` + subKey
		key, err := registry.OpenKey(registry.LOCAL_MACHINE, subKeyPath, registry.QUERY_VALUE)
		if err != nil {
			continue
		}
		
		displayName, _, err := key.GetStringValue("DisplayName")
		if err != nil {
			key.Close()
			continue
		}
		
		// Check if this matches any of our expected display names
		for _, expectedName := range displayNames {
			if strings.Contains(displayName, expectedName) || strings.Contains(expectedName, displayName) {
				if version, _, err := key.GetStringValue("DisplayVersion"); err == nil {
					key.Close()
					return version
				}
			}
		}
		key.Close()
	}
	
	return ""
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

	// Export enhanced items.json for external reporting tool integration
	if err := exp.writeJSONFile(filepath.Join(reportsDir, "items.json"), packages); err != nil {
		return fmt.Errorf("failed to export items: %w", err)
	}

	return nil
}

// Helper types and methods

// parseEventsWithRecovery provides robust JSON parsing for events.jsonl files
// Handles both single-line JSONL format and legacy pretty-printed JSON format
func (exp *DataExporter) parseEventsWithRecovery(file *os.File) ([]map[string]interface{}, error) {
	var events []map[string]interface{}
	
	// Read entire file content
	file.Seek(0, 0) // Reset to beginning
	content, err := io.ReadAll(file)
	if err != nil {
		return nil, fmt.Errorf("failed to read events file: %w", err)
	}
	
	contentStr := string(content)
	
	// Try parsing as JSONL first (preferred format)
	scanner := bufio.NewScanner(strings.NewReader(contentStr))
	jsonlSuccess := true
	var jsonlEvents []map[string]interface{}
	
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		
		var event map[string]interface{}
		if err := json.Unmarshal([]byte(line), &event); err != nil {
			jsonlSuccess = false
			break
		}
		jsonlEvents = append(jsonlEvents, event)
	}
	
	if jsonlSuccess && len(jsonlEvents) > 0 {
		return jsonlEvents, nil
	}
	
	// Fallback: Try parsing as array of pretty-printed JSON objects
	// This handles legacy format with indented JSON separated by newlines
	
	// Split content by lines that start with "}" (end of pretty-printed JSON objects)
	var jsonStrings []string
	lines := strings.Split(contentStr, "\n")
	var currentJson strings.Builder
	
	for _, line := range lines {
		currentJson.WriteString(line + "\n")
		
		if strings.TrimSpace(line) == "}" {
			// This might be the end of a JSON object
			jsonStr := strings.TrimSpace(currentJson.String())
			if strings.HasPrefix(jsonStr, "{") && strings.HasSuffix(jsonStr, "}") {
				jsonStrings = append(jsonStrings, jsonStr)
				currentJson.Reset()
			}
		}
	}
	
	// Try to parse each extracted JSON string
	for _, jsonStr := range jsonStrings {
		var event map[string]interface{}
		if err := json.Unmarshal([]byte(jsonStr), &event); err == nil {
			events = append(events, event)
		}
		// Silently skip malformed JSON objects
	}
	
	return events, nil
}

// comprehensiveItemStat tracks detailed statistics for an item across all sessions
type comprehensiveItemStat struct {
	Name                string
	DisplayName         string // Added for user-friendly display
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
	LoopDetails         *InstallLoopDetail // Enhanced loop information
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
	SessionID       string     `json:"session_id"`
	StartTime       string     `json:"start_time"`
	EndTime         *string    `json:"end_time,omitempty"`
	RunType         string     `json:"run_type"`
	Status          string     `json:"status"`
	DurationSeconds *int64     `json:"duration_seconds,omitempty"`
	Summary         struct {
		TotalActions    int      `json:"total_actions"`
		Installs        int      `json:"installs"`
		Updates         int      `json:"updates"`
		Removals        int      `json:"removals"`
		Successes       int      `json:"successes"`
		Failures        int      `json:"failures"`
		Duration        int64    `json:"duration"`
		PackagesHandled []string `json:"packages_handled"`
	} `json:"summary"`
	Environment map[string]interface{} `json:"environment"`
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

// extractVersionFromMessage attempts to extract version information from log messages
func (exp *DataExporter) extractVersionFromMessage(message string) string {
	// Common version patterns in Cimian messages
	versionPatterns := []string{
		// Standard version patterns
		`version\s+([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`v([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)\s+to\s+`,
		`-([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)\.(exe|msi|nupkg)`,
		`([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)-x64`,
		`([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)-arm64`,

		// Cimian-specific patterns
		`registry version.*?([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`local version.*?([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`repo version.*?([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`installed version.*?([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`target version.*?([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`version\s+is\s+([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)\s+(?:installed|found|detected)`,

		// Patterns for version comparisons
		`from\s+([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)\s+to`,
		`updating\s+.*?([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
		`installing\s+.*?([0-9]+\.[0-9]+(?:\.[0-9]+)*(?:\.[0-9]+)*)`,
	}

	for _, pattern := range versionPatterns {
		re := regexp.MustCompile(`(?i)` + pattern) // Case insensitive
		if matches := re.FindStringSubmatch(message); len(matches) > 1 {
			return matches[1]
		}
	}

	return ""
}

// inferItemType attempts to determine the item type from session data or context
func (exp *DataExporter) inferItemType(packageName, sessionDir string, event EventRecord) string {
	switch event.EventType {
	case "install":
		return "managed_installs"
	case "update":
		return "managed_updates"
	case "remove":
		return "managed_uninstalls"
	case "profile":
		return "managed_profiles"
	case "app":
		return "managed_apps"
	default:
		return "unknown"
	}
}

// normalizeStatus converts various status formats to standard form with proper capitalization
// ENHANCEMENT 2: Consistent status vocabulary for ReportMate
func (exp *DataExporter) normalizeStatus(status, level, errorMsg string) string {
	// Convert to lowercase for comparison
	statusLower := strings.ToLower(status)
	levelLower := strings.ToLower(level)

	// Check for explicit error conditions
	if errorMsg != "" || levelLower == "error" || strings.Contains(statusLower, "fail") {
		return "Failed"
	}

	// Check for warning conditions
	if levelLower == "warn" || levelLower == "warning" || strings.Contains(statusLower, "warn") {
		return "Warning"
	}

	// Check for success conditions
	if statusLower == "completed" || statusLower == "success" || statusLower == "ok" ||
		statusLower == "installed" || strings.Contains(statusLower, "success") || 
		strings.Contains(statusLower, "complete") {
		return "Success"
	}

	// Check for pending conditions
	if strings.Contains(statusLower, "pending") || strings.Contains(statusLower, "waiting") ||
		strings.Contains(statusLower, "blocked") || strings.Contains(statusLower, "queued") {
		return "Pending"
	}

	// Check for skipped conditions
	if strings.Contains(statusLower, "skip") || strings.Contains(statusLower, "bypass") {
		return "Skipped"
	}

	// Default based on log level
	switch levelLower {
	case "info":
		return "Success"
	case "debug":
		return "Success"
	case "error":
		return "Failed"
	case "warn", "warning":
		return "Warning"
	default:
		return "Unknown"
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

	file, err := os.Open(eventsPath)
	if err != nil {
		return time.Time{}
	}
	defer file.Close()

	// Use robust JSON parsing to get the first event
	events, err := exp.parseEventsWithRecovery(file)
	if err != nil || len(events) == 0 {
		return time.Time{}
	}

	// Get timestamp from first event
	firstEvent := events[0]
	if timestampStr, ok := firstEvent["timestamp"].(string); ok {
		if timestamp, err := time.Parse(time.RFC3339, timestampStr); err == nil {
			return timestamp
		}
	}

	return time.Time{}
}

// fillMissingSessionData fills in missing environment data from events.jsonl
func (exp *DataExporter) fillMissingSessionData(record *SessionRecord, sessionDir string) {
	eventsPath := filepath.Join(exp.baseDir, sessionDir, "events.jsonl")

	file, err := os.Open(eventsPath)
	if err != nil {
		return
	}
	defer file.Close()

	// Use robust JSON parsing to get events
	events, err := exp.parseEventsWithRecovery(file)
	if err != nil || len(events) == 0 {
		return
	}

	// Check events for missing environment data
	for _, event := range events {
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

// loadCatalogVersions reads catalog files to extract authoritative version information
func (exp *DataExporter) loadCatalogVersions() map[string]string {
	versions := make(map[string]string)

	// Try to load from catalogs directory
	catalogsPath := `C:\ProgramData\ManagedInstalls\catalogs`

	entries, err := os.ReadDir(catalogsPath)
	if err != nil {
		logging.Debug("Could not read catalogs directory for version data", "path", catalogsPath, "error", err)
		return versions
	}

	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".yaml") {
			continue
		}

		catalogPath := filepath.Join(catalogsPath, entry.Name())
		data, err := os.ReadFile(catalogPath)
		if err != nil {
			continue
		}

		// Parse catalog file using the same structure as main code
		var wrapper struct {
			Items []struct {
				Name    string `yaml:"name"`
				Version string `yaml:"version"`
			} `yaml:"items"`
		}

		if err := yaml.Unmarshal(data, &wrapper); err != nil {
			continue
		}

		// Extract version information for each item
		for _, item := range wrapper.Items {
			if item.Name != "" && item.Version != "" {
				// Store with lowercase key for case-insensitive lookup
				versions[strings.ToLower(item.Name)] = item.Version
			}
		}
	}

	logging.Debug("Loaded catalog versions for reporting", "count", len(versions))
	return versions
}

// loadCatalogDisplayNames reads catalog files to extract authoritative display name information
func (exp *DataExporter) loadCatalogDisplayNames() map[string]string {
	displayNames := make(map[string]string)

	// Try to load from catalogs directory
	catalogsPath := `C:\ProgramData\ManagedInstalls\catalogs`

	entries, err := os.ReadDir(catalogsPath)
	if err != nil {
		logging.Debug("Could not read catalogs directory for display name data", "path", catalogsPath, "error", err)
		return displayNames
	}

	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".yaml") {
			continue
		}

		catalogPath := filepath.Join(catalogsPath, entry.Name())
		data, err := os.ReadFile(catalogPath)
		if err != nil {
			continue
		}

		// Parse catalog file using the same structure as main code
		var wrapper struct {
			Items []struct {
				Name        string `yaml:"name"`
				DisplayName string `yaml:"display_name"`
			} `yaml:"items"`
		}

		if err := yaml.Unmarshal(data, &wrapper); err != nil {
			continue
		}

		// Extract display name information for each item
		for _, item := range wrapper.Items {
			if item.Name != "" && item.DisplayName != "" {
				// Store with lowercase key for case-insensitive lookup
				displayNames[strings.ToLower(item.Name)] = item.DisplayName
			}
		}
	}

	logging.Debug("Loaded catalog display names for reporting", "count", len(displayNames))
	return displayNames
}

// getInstalledVersionFromRegistry attempts to get the installed version of a package from multiple sources
func (exp *DataExporter) getInstalledVersionFromRegistry(packageName string) string {
	// Use the comprehensive multi-source detection system
	version, source, err := status.GetAuthoritativeInstalledVersion(packageName)
	if err != nil {
		logging.Debug("Multi-source version detection failed", 
			"package", packageName, 
			"error", err)
		return ""
	}
	
	if version != "" {
		logging.Debug("Multi-source version detection succeeded", 
			"package", packageName, 
			"version", version, 
			"source", source)
	}
	
	return version
}

// getCimianManagedVersion gets version from Cimian's managed registry
func (exp *DataExporter) getCimianManagedVersion(packageName string) string {
	// Read from HKLM\Software\ManagedInstalls\<packageName>\Version
	regPath := `Software\ManagedInstalls\` + packageName
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, regPath, registry.QUERY_VALUE)
	if err != nil {
		return ""
	}
	defer k.Close()

	ver, _, err := k.GetStringValue("Version")
	if err != nil {
		return ""
	}
	return ver
}

// getUninstallRegistryVersion gets version from Windows uninstall registry
func (exp *DataExporter) getUninstallRegistryVersion(packageName string) string {
	regPaths := []string{
		`Software\Microsoft\Windows\CurrentVersion\Uninstall`,
		`Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall`,
	}

	for _, rPath := range regPaths {
		key, err := registry.OpenKey(registry.LOCAL_MACHINE, rPath, registry.READ)
		if err != nil {
			continue
		}
		defer key.Close()

		subKeys, err := key.ReadSubKeyNames(0)
		if err != nil {
			continue
		}

		for _, subKey := range subKeys {
			fullPath := rPath + `\` + subKey
			subKeyObj, err := registry.OpenKey(registry.LOCAL_MACHINE, fullPath, registry.READ)
			if err != nil {
				continue
			}
			defer subKeyObj.Close()

			// Get DisplayName
			if name, _, err := subKeyObj.GetStringValue("DisplayName"); err == nil {
				// Check for exact match or partial match
				if strings.EqualFold(name, packageName) || strings.Contains(strings.ToLower(name), strings.ToLower(packageName)) {
					// Get DisplayVersion
					if version, _, err := subKeyObj.GetStringValue("DisplayVersion"); err == nil {
						return version
					}
				}
			}
		}
	}

	return ""
}

// generatePackageID creates a standardized package ID for correlation
func (exp *DataExporter) generatePackageID(packageName string) string {
	if packageName == "" {
		return ""
	}
	// Convert to lowercase and replace spaces/special chars with hyphens
	id := strings.ToLower(packageName)
	id = regexp.MustCompile(`[^a-z0-9]+`).ReplaceAllString(id, "-")
	id = strings.Trim(id, "-")
	return id
}

// determineInstallerTypeFromPath extracts installer type from file path
func (exp *DataExporter) determineInstallerTypeFromPath(path string) string {
	if path == "" {
		return ""
	}
	
	path = strings.ToLower(path)
	switch {
	case strings.Contains(path, ".nupkg"):
		return "nupkg"
	case strings.Contains(path, ".msi"):
		return "msi"
	case strings.Contains(path, ".exe"):
		return "exe"
	case strings.Contains(path, ".msix"):
		return "msix"
	case strings.Contains(path, ".appx"):
		return "appx"
	case strings.Contains(path, ".zip"):
		return "zip"
	case strings.Contains(path, ".ps1"):
		return "powershell"
	case strings.Contains(path, "chocolatey") || strings.Contains(path, "choco"):
		return "chocolatey"
	default:
		return "unknown"
	}
}

// createErrorDetails creates structured error information for troubleshooting
func (exp *DataExporter) createErrorDetails(errorMsg, message, action string) *ErrorDetails {
	details := &ErrorDetails{
		ErrorType: exp.categorizeError(errorMsg, message),
	}
	
	// Extract error code from common patterns
	if matches := regexp.MustCompile(`exit code[:\s]*(\d+)`).FindStringSubmatch(errorMsg); len(matches) > 1 {
		if code, err := fmt.Sscanf(matches[1], "%d", &details.ErrorCode); err == nil && code == 1 {
			// Successfully parsed error code
		}
	}
	
	// Extract command if present
	if strings.Contains(message, "command") {
		if matches := regexp.MustCompile(`command[:\s]+"?([^"]+)"?`).FindStringSubmatch(message); len(matches) > 1 {
			details.Command = strings.TrimSpace(matches[1])
		}
	}
	
	// Add resolution hints based on error type
	details.ResolutionHint = exp.getResolutionHint(details.ErrorType, errorMsg)
	
	return details
}

// categorizeError determines the error type from error message
func (exp *DataExporter) categorizeError(errorMsg, message string) string {
	lower := strings.ToLower(errorMsg + " " + message)
	
	switch {
	case strings.Contains(lower, "access denied") || strings.Contains(lower, "permission"):
		return "permission_denied"
	case strings.Contains(lower, "exit code") || strings.Contains(lower, "exit status"):
		return "installer_failure"
	case strings.Contains(lower, "timeout"):
		return "timeout"
	case strings.Contains(lower, "network") || strings.Contains(lower, "download"):
		return "network_failure"
	case strings.Contains(lower, "dependency") || strings.Contains(lower, "missing"):
		return "dependency_missing"
	case strings.Contains(lower, "registry"):
		return "registry_error"
	case strings.Contains(lower, "file not found"):
		return "file_not_found"
	default:
		return "unknown_error"
	}
}

// getResolutionHint provides troubleshooting suggestions based on error type
func (exp *DataExporter) getResolutionHint(errorType, errorMsg string) string {
	switch errorType {
	case "permission_denied":
		return "Run as administrator or check file permissions"
	case "installer_failure":
		return "Check installer logs and verify package integrity"
	case "timeout":
		return "Check network connectivity and retry"
	case "network_failure":
		return "Verify internet connection and proxy settings"
	case "dependency_missing":
		return "Install required dependencies or check manifest"
	case "registry_error":
		return "Check registry permissions and integrity"
	case "file_not_found":
		return "Verify file paths and cache integrity"
	default:
		return "Check logs for more details"
	}
}

// getFailedPackagesForSession extracts failed package information from session events
func (exp *DataExporter) getFailedPackagesForSession(sessionDir string) []FailedPackageInfo {
	var failedPackages []FailedPackageInfo
	
	// Get events for this session
	events, err := exp.GenerateEventsTable(sessionDir, 0)
	if err != nil {
		return failedPackages
	}
	
	// Track failed packages by ID to avoid duplicates
	failedMap := make(map[string]*FailedPackageInfo)
	
	for _, event := range events {
		if event.Status == "Failed" && event.PackageID != "" {
			packageID := event.PackageID
			if packageID == "" && event.PackageName != "" {
				packageID = exp.generatePackageID(event.PackageName)
			}
			
			if packageID != "" {
				// Update or create failed package entry
				if existing, exists := failedMap[packageID]; exists {
					// Update with latest attempt time
					if laterTime, err := time.Parse(time.RFC3339, event.Timestamp); err == nil {
						if existingTime, err := time.Parse(time.RFC3339, existing.LastAttempt); err == nil {
							if laterTime.After(existingTime) {
								existing.LastAttempt = event.Timestamp
								if event.Error != "" {
									existing.ErrorMessage = event.Error
								}
							}
						}
					}
				} else {
					errorType := "unknown_error"
					if event.ErrorDetails != nil {
						errorType = event.ErrorDetails.ErrorType
					}
					
					packageName := event.PackageName
					if packageName == "" {
						packageName = event.Package
					}
					
					failedMap[packageID] = &FailedPackageInfo{
						PackageID:    packageID,
						PackageName:  packageName,
						ErrorType:    errorType,
						LastAttempt:  event.Timestamp,
						ErrorMessage: event.Error,
					}
				}
			}
		}
	}
	
	// Convert map to slice
	for _, failedPkg := range failedMap {
		failedPackages = append(failedPackages, *failedPkg)
	}
	
	return failedPackages
}

// detectInstallLoopEnhanced analyzes recent attempts with enhanced loop detection
func (exp *DataExporter) detectInstallLoopEnhanced(attempts []ItemAttempt, packageName string) (bool, *InstallLoopDetail) {
	if len(attempts) < 3 {
		return false, nil // Need at least 3 attempts to detect a loop
	}

	// Count recent install attempts (last 5 attempts)
	recentCount := len(attempts)
	if recentCount > 5 {
		attempts = attempts[recentCount-5:] // Look at last 5 attempts
	}

	installAttempts := 0
	successCount := 0
	var firstAttemptSession string

	// Parse timestamps to check if attempts are recent (within last 7 days)
	cutoffTime := time.Now().Add(-7 * 24 * time.Hour)

	for i, attempt := range attempts {
		if attemptTime, err := time.Parse(time.RFC3339, attempt.Timestamp); err == nil {
			if attemptTime.After(cutoffTime) {
				if attempt.Action == "install" || attempt.Action == "update" {
					installAttempts++
					if attempt.Status == "success" {
						successCount++
					}
					if i == 0 {
						firstAttemptSession = attempt.SessionID
					}
				}
			}
		}
	}

	// Detect loop: Multiple scenarios
	var detectionCriteria string
	isLoop := false
	
	// Scenario 1: 3+ install attempts with less than 50% success rate
	if installAttempts >= 3 {
		successRate := float64(successCount) / float64(installAttempts)
		if successRate < 0.5 {
			detectionCriteria = fmt.Sprintf("repeated_failures_%d_attempts_%.0f%%_success", installAttempts, successRate*100)
			isLoop = true
		}
	}
	
	// Scenario 2: Same version reinstalled multiple times
	if !isLoop {
		versionCounts := make(map[string]int)
		for _, attempt := range attempts {
			if attempt.Version != "" && (attempt.Action == "install" || attempt.Action == "update") {
				versionCounts[attempt.Version]++
			}
		}
		
		for version, count := range versionCounts {
			if count >= 3 {
				detectionCriteria = fmt.Sprintf("same_version_reinstalled_%s_%d_times", version, count)
				isLoop = true
				break
			}
		}
	}
	
	// Scenario 3: Rapid consecutive attempts (within short time window)
	if !isLoop && len(attempts) >= 3 {
		recentAttempts := attempts[len(attempts)-3:]
		var timestamps []time.Time
		for _, attempt := range recentAttempts {
			if t, err := time.Parse(time.RFC3339, attempt.Timestamp); err == nil {
				timestamps = append(timestamps, t)
			}
		}
		
		if len(timestamps) >= 3 {
			// Check if all 3 attempts happened within 1 hour
			timeSpan := timestamps[len(timestamps)-1].Sub(timestamps[0])
			if timeSpan < time.Hour {
				detectionCriteria = fmt.Sprintf("rapid_consecutive_attempts_%d_in_%v", len(recentAttempts), timeSpan.Round(time.Minute))
				isLoop = true
			}
		}
	}
	
	if isLoop {
		// Create enhanced loop details
		loopDetails := &InstallLoopDetail{
			DetectionCriteria: detectionCriteria,
			LoopStartSession:  firstAttemptSession,
			SuspectedCause:    exp.analyzeSuspectedCause(attempts, packageName),
			Recommendation:    exp.getLoopRecommendation(attempts, packageName),
		}
		return true, loopDetails
	}

	return false, nil
}

// analyzeSuspectedCause determines the likely cause of install loops
func (exp *DataExporter) analyzeSuspectedCause(attempts []ItemAttempt, packageName string) string {
	// Pattern 1: Check for consistent version reinstalls (installer reports success but app not detected)
	versionCounts := make(map[string]int)
	successfulReinstalls := 0
	for _, attempt := range attempts {
		if attempt.Version != "" && attempt.Action == "install" {
			versionCounts[attempt.Version]++
			if attempt.Status == "success" {
				successfulReinstalls++
			}
		}
	}
	
	for version, count := range versionCounts {
		if count >= 2 && successfulReinstalls >= 2 {
			return fmt.Sprintf("installer_reports_success_but_app_not_detected_v%s", version)
		}
	}
	
	// Pattern 2: Repeated failures - permission/dependency issues
	failureCount := 0
	successCount := 0
	for _, attempt := range attempts {
		if attempt.Status == "failed" {
			failureCount++
		} else if attempt.Status == "success" {
			successCount++
		}
	}
	
	if failureCount >= 2 && successCount == 0 {
		// Check for common failure patterns
		if strings.Contains(strings.ToLower(packageName), "adobe") {
			return "adobe_licensing_or_creative_cloud_conflict"
		}
		if strings.Contains(strings.ToLower(packageName), "office") || strings.Contains(strings.ToLower(packageName), "microsoft") {
			return "microsoft_installer_service_or_office_conflict"
		}
		if strings.Contains(strings.ToLower(packageName), "java") {
			return "java_version_conflict_or_registry_corruption"
		}
		return "installer_permission_dependency_or_conflict_issues"
	}
	
	// Pattern 3: Rapid reinstallation attempts
	if len(attempts) >= 3 {
		recentAttempts := attempts[len(attempts)-3:]
		var timestamps []time.Time
		for _, attempt := range recentAttempts {
			if t, err := time.Parse(time.RFC3339, attempt.Timestamp); err == nil {
				timestamps = append(timestamps, t)
			}
		}
		
		if len(timestamps) >= 3 {
			timeSpan := timestamps[len(timestamps)-1].Sub(timestamps[0])
			if timeSpan < time.Hour {
				return "system_instability_or_automated_retry_loop"
			}
		}
	}
	
	// Pattern 4: Mixed success/failure pattern
	if failureCount > 0 && successCount > 0 {
		return "intermittent_system_conditions_or_timing_issues"
	}
	
	return "unknown_loop_cause_requires_manual_investigation"
}

// getLoopRecommendation provides specific recommendations for resolving install loops
func (exp *DataExporter) getLoopRecommendation(attempts []ItemAttempt, packageName string) string {
	// Get the suspected cause to provide targeted recommendations
	suspectedCause := exp.analyzeSuspectedCause(attempts, packageName)
	
	// Cause-specific recommendations
	switch {
	case strings.Contains(suspectedCause, "installer_reports_success_but_app_not_detected"):
		return "Verify installer exit codes and app detection logic in pkginfo; check if silent install parameters are correct"
		
	case strings.Contains(suspectedCause, "adobe_licensing_or_creative_cloud"):
		return "Clear Adobe licensing cache, restart Creative Cloud services, or temporarily disable real-time AV scanning"
		
	case strings.Contains(suspectedCause, "microsoft_installer_service"):
		return "Restart Windows Installer service, clear MSI cache, or run system in safe mode for troubleshooting"
		
	case strings.Contains(suspectedCause, "java_version_conflict"):
		return "Clean Java registry entries, remove conflicting Java versions, or use Java offline installer"
		
	case strings.Contains(suspectedCause, "system_instability_or_automated_retry"):
		return "Increase installation delays, check system resources, or implement exponential backoff retry logic"
		
	case strings.Contains(suspectedCause, "intermittent_system_conditions"):
		return "Schedule installations during maintenance windows or implement pre-flight system health checks"
		
	case strings.Contains(suspectedCause, "installer_permission_dependency"):
		return "Run as SYSTEM account, verify installer dependencies, or check antivirus exclusions"
	}
	
	// Package-type specific recommendations
	packageLower := strings.ToLower(packageName)
	switch {
	case strings.Contains(packageLower, "msi"):
		return "Verify MSI installer silent flags (/quiet /norestart), check admin rights and MSI log files"
		
	case strings.Contains(packageLower, "exe"):
		return "Validate EXE installer exit codes, silent parameters, and ensure proper privilege elevation"
		
	case strings.Contains(packageLower, "nupkg") || strings.Contains(packageLower, "chocolatey"):
		return "Check Chocolatey package dependencies, verify package source accessibility, clear Chocolatey cache"
		
	case strings.Contains(packageLower, "powershell") || strings.Contains(packageLower, "ps1"):
		return "Verify PowerShell execution policy, check script signing requirements, validate module dependencies"
	}
	
	// Pattern-based recommendations
	failureCount := 0
	successCount := 0
	for _, attempt := range attempts {
		if attempt.Status == "failed" {
			failureCount++
		} else if attempt.Status == "success" {
			successCount++
		}
	}
	
	if failureCount > successCount {
		return "Primary issue: Consistent failures - Check installer logs, system requirements, and resolve permission/dependency issues"
	}
	
	if successCount > 0 && failureCount > 0 {
		return "Intermittent issue detected - Monitor system resources during installation and implement retry logic with delays"
	}
	
	// Default comprehensive recommendation
	return "Review installer logs, verify system requirements, check for conflicts with AV/security software, and consider manual installation test"
}

// getTotalManagedPackagesFromManifest gets the total number of managed packages from the current manifest
// This fixes the critical bug where sessions show 0 managed packages when no actions were performed
// CRITICAL FIX: Handle hierarchical manifest paths (e.g., "Shared/Curriculum/Animation/C3234/CintiqLab16")
func (exp *DataExporter) getTotalManagedPackagesFromManifest(sessionConfig *SessionConfig) int {
	if sessionConfig == nil || sessionConfig.Manifest == "" {
		return 0
	}
	
	// Check cache first to avoid repeated parsing of the same manifest
	if cachedCount, exists := exp.manifestPackageCache[sessionConfig.Manifest]; exists {
		// Cache hit - return silently (no logging needed for reporting)
		return cachedCount
	}
	
	// Handle hierarchical paths (e.g., "Shared/Curriculum/Animation/C3234/CintiqLab16" or "Shared\Curriculum\Animation\C3234\CintiqLab16")
	manifestName := sessionConfig.Manifest
	
	// Normalize path separators - handle both forward slashes and backslashes
	// This ensures we work regardless of how the path was constructed (preflight vs manual config)
	hierarchicalPath := strings.ReplaceAll(manifestName, "/", string(filepath.Separator))
	hierarchicalPath = strings.ReplaceAll(hierarchicalPath, "\\", string(filepath.Separator))
	
	// Also create a version with forward slashes for potential Unix-style path matching
	unixStylePath := strings.ReplaceAll(manifestName, "\\", "/")
	
	// Try to load the current manifest using hierarchical path resolution
	manifestPath := ""
	
	// Look for manifest files in common locations - prioritize hierarchical paths
	possiblePaths := []string{
		// PRIORITY 1: Try normalized hierarchical path first (most common for complex deployments)
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", hierarchicalPath+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", hierarchicalPath),
		
		// PRIORITY 2: Try direct name as-is (backward compatibility)
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", manifestName+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", manifestName),
		
		// PRIORITY 3: Try Unix-style path (additional fallback)
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", unixStylePath+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", unixStylePath),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", manifestName),
		
		// PRIORITY 4: Try with catalogs directory as well (all variations)
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "catalogs", hierarchicalPath+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "catalogs", hierarchicalPath),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "catalogs", manifestName+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "catalogs", manifestName),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "catalogs", unixStylePath+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "catalogs", unixStylePath),
	}
	
	for _, path := range possiblePaths {
		if _, err := os.Stat(path); err == nil {
			manifestPath = path
			// Found the manifest file - no need for verbose logging during reporting
			break
		}
	}
	
	if manifestPath == "" {
		logging.Debug("Could not find manifest file for package counting", "manifest", sessionConfig.Manifest, "attempted_paths", possiblePaths)
		return 0
	}
	
	// Read and parse the manifest file
	data, err := os.ReadFile(manifestPath)
	if err != nil {
		logging.Debug("Could not read manifest file", "path", manifestPath, "error", err)
		return 0
	}
	
	// Parse manifest YAML to count packages from all relevant arrays
	var manifest struct {
		ManagedInstalls   []interface{} `yaml:"managed_installs"`
		ManagedUninstalls []interface{} `yaml:"managed_uninstalls"`
		ManagedUpdates    []interface{} `yaml:"managed_updates"`
		OptionalInstalls  []interface{} `yaml:"optional_installs"`
		IncludedManifests []interface{} `yaml:"included_manifests"`
		Items             []interface{} `yaml:"items"` // Legacy support
	}
	
	if err := yaml.Unmarshal(data, &manifest); err != nil {
		logging.Debug("Could not parse manifest YAML", "path", manifestPath, "error", err)
		return 0
	}
	
	// Count packages from all relevant manifest arrays (direct packages in this manifest)
	directPackages := len(manifest.ManagedInstalls) + 
	                len(manifest.ManagedUninstalls) + 
	                len(manifest.ManagedUpdates) + 
	                len(manifest.OptionalInstalls) +
	                len(manifest.Items) // Legacy support for old format
	
	totalPackages := directPackages
	
	// RECURSIVE FIX: Process included_manifests to get total package count from hierarchy
	if len(manifest.IncludedManifests) > 0 {
		// Process included manifests silently - no verbose logging during reporting
		for _, includedManifest := range manifest.IncludedManifests {
			if includedManifestName, ok := includedManifest.(string); ok {
				// Create a temporary session config for the included manifest
				tempConfig := &SessionConfig{
					Manifest: includedManifestName,
				}
				
				// Recursively get package count from included manifest
				// Note: This will use the cache if we've already processed this manifest
				includedPackages := exp.getTotalManagedPackagesFromManifest(tempConfig)
				totalPackages += includedPackages
			}
		}
	}
	
	// Cache the result to avoid repeated parsing
	exp.manifestPackageCache[sessionConfig.Manifest] = totalPackages
	
	// Single clean summary line for reporting
	if sessionConfig.Manifest == exp.getRootManifest() {
		logging.Debug("Added managed packages from manifest hierarchy for reporting", "total_packages", totalPackages, "root_manifest", sessionConfig.Manifest)
	}
	
	return totalPackages
}

// getRootManifest returns the root manifest name for this system (used for clean logging)
func (exp *DataExporter) getRootManifest() string {
	// Try to get the root manifest from the current config
	if config := exp.loadCimianConfiguration(); config != nil && config.Manifest != "" {
		return config.Manifest
	}
	return "" // Unknown root manifest
}

// ManagedItem represents a managed package item
type ManagedItem struct {
	Name string
	Type string // "managed_installs", "managed_updates", etc.
}

// getCurrentManagedItems retrieves all currently managed items from manifest files
func (exp *DataExporter) getCurrentManagedItems() []ManagedItem {
	var items []ManagedItem
	
	// Get the main manifest path from configuration
	config := exp.loadCimianConfiguration()
	if config.ClientIdentifier == "" {
		return items // No client identifier, can't determine manifest
	}
	
	// Try to get items from hierarchical manifest path
	manifestItems := exp.getItemsFromManifest(config.ClientIdentifier)
	return manifestItems
}

// getItemsFromManifest recursively extracts all managed items from a manifest and its includes
func (exp *DataExporter) getItemsFromManifest(manifestName string) []ManagedItem {
	var items []ManagedItem
	
	// Skip cache entries and prevent infinite loops
	if exp.manifestPackageCache == nil {
		exp.manifestPackageCache = make(map[string]int)
	}
	if _, processed := exp.manifestPackageCache[manifestName]; processed {
		return items
	}
	exp.manifestPackageCache[manifestName] = 1 // Mark as processing
	
	// Construct potential manifest file paths (reuse existing logic)
	hierarchicalPath := strings.ReplaceAll(manifestName, "/", "\\")
	unixStylePath := strings.ReplaceAll(manifestName, "\\", "/")
	
	candidatePaths := []string{
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", hierarchicalPath+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", hierarchicalPath),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", manifestName+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", manifestName),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", unixStylePath+".yaml"),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", unixStylePath),
		filepath.Join("C:\\", "ProgramData", "ManagedInstalls", "manifests", manifestName),
	}
	
	var manifestPath string
	for _, path := range candidatePaths {
		if _, err := os.Stat(path); err == nil {
			manifestPath = path
			break
		}
	}
	
	if manifestPath == "" {
		return items
	}
	
	// Read and parse manifest
	data, err := os.ReadFile(manifestPath)
	if err != nil {
		return items
	}
	
	var manifest struct {
		ManagedInstalls   []interface{} `yaml:"managed_installs"`
		ManagedUninstalls []interface{} `yaml:"managed_uninstalls"`
		ManagedUpdates    []interface{} `yaml:"managed_updates"`
		OptionalInstalls  []interface{} `yaml:"optional_installs"`
		IncludedManifests []interface{} `yaml:"included_manifests"`
		Items             []interface{} `yaml:"items"`
	}
	
	if err := yaml.Unmarshal(data, &manifest); err != nil {
		return items
	}
	
	// Extract items from each category
	for _, item := range manifest.ManagedInstalls {
		if itemName, ok := item.(string); ok {
			items = append(items, ManagedItem{Name: itemName, Type: "managed_installs"})
		}
	}
	
	for _, item := range manifest.ManagedUninstalls {
		if itemName, ok := item.(string); ok {
			items = append(items, ManagedItem{Name: itemName, Type: "managed_uninstalls"})
		}
	}
	
	for _, item := range manifest.ManagedUpdates {
		if itemName, ok := item.(string); ok {
			items = append(items, ManagedItem{Name: itemName, Type: "managed_updates"})
		}
	}
	
	for _, item := range manifest.OptionalInstalls {
		if itemName, ok := item.(string); ok {
			items = append(items, ManagedItem{Name: itemName, Type: "optional_installs"})
		}
	}
	
	for _, item := range manifest.Items {
		if itemName, ok := item.(string); ok {
			items = append(items, ManagedItem{Name: itemName, Type: "items"})
		}
	}
	
	// Process included manifests recursively
	for _, includedManifest := range manifest.IncludedManifests {
		if manifestName, ok := includedManifest.(string); ok {
			includedItems := exp.getItemsFromManifest(manifestName)
			items = append(items, includedItems...)
		}
	}
	
	return items
}
