// pkg/reporting/reporting.go - Data reporting functionality for external monitoring tools

package reporting

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
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
}

// NewDataExporter creates a new data exporter
func NewDataExporter(baseDir string) *DataExporter {
	return &DataExporter{baseDir: baseDir}
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
			summary := &SessionSummary{
				TotalPackagesManaged: len(record.PackagesHandled),
				PackagesInstalled:    record.Successes,
				PackagesPending:      record.TotalActions - record.Successes - record.Failures,
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
					record.Summary = &SessionSummary{
						TotalPackagesManaged: len(record.PackagesHandled),
						PackagesInstalled:    record.Successes,
						PackagesPending:      record.TotalActions - record.Successes - record.Failures,
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

					// Extract package name and version from properties and message
					packageName := ""
					packageID := ""
					version := ""
					action := ""
					status := ""
					errorMsg := ""
					installerType := ""

					if props, ok := logEvent["properties"].(map[string]interface{}); ok {
						if item, ok := props["item"].(string); ok {
							packageName = item
							// Generate standardized package ID
							packageID = exp.generatePackageID(item)
						}
						// Check multiple version field names used in Cimian logs
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
						// Extract installer type from installer path or method
						if installerPath, ok := props["installer_path"].(string); ok {
							installerType = exp.determineInstallerTypeFromPath(installerPath)
						}
					}

					// Get message and extract additional version info if not already present
					message := ""
					eventType := "general"
					if msg, ok := logEvent["message"].(string); ok {
						message = msg

						// Extract version from message if not found in properties
						if version == "" {
							version = exp.extractVersionFromMessage(message)
						}

						// Extract package name from message if not found in properties
						if packageName == "" {
							extractedName := exp.extractPackageFromMessage(message)
							if extractedName != "" {
								packageName = extractedName
								packageID = exp.generatePackageID(extractedName)
							}
						}

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
						Status:     exp.normalizeStatus(status, level, errorMsg), // Apply status normalization
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

	// Export enhanced items.json for external reporting tool integration
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

// getInstalledVersionFromRegistry attempts to get the installed version of a package from Windows registry
func (exp *DataExporter) getInstalledVersionFromRegistry(packageName string) string {
	// Try Cimian's managed registry first (most reliable)
	if cimianVersion := exp.getCimianManagedVersion(packageName); cimianVersion != "" {
		return cimianVersion
	}

	// Try Windows uninstall registry
	if uninstallVersion := exp.getUninstallRegistryVersion(packageName); uninstallVersion != "" {
		return uninstallVersion
	}

	return ""
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

	// Detect loop: 3+ install attempts in recent history with less than 50% success rate
	if installAttempts >= 3 {
		successRate := float64(successCount) / float64(installAttempts)
		if successRate < 0.5 {
			// Create enhanced loop details
			loopDetails := &InstallLoopDetail{
				DetectionCriteria: "same_version_reinstalled",
				LoopStartSession:  firstAttemptSession,
				SuspectedCause:    exp.analyzeSuspectedCause(attempts, packageName),
				Recommendation:    exp.getLoopRecommendation(attempts, packageName),
			}
			return true, loopDetails
		}
	}

	return false, nil
}

// analyzeSuspectedCause determines the likely cause of install loops
func (exp *DataExporter) analyzeSuspectedCause(attempts []ItemAttempt, packageName string) string {
	// Check for consistent version reinstalls
	versionCounts := make(map[string]int)
	for _, attempt := range attempts {
		if attempt.Version != "" {
			versionCounts[attempt.Version]++
		}
	}
	
	for version, count := range versionCounts {
		if count >= 2 {
			return fmt.Sprintf("installer_exit_code_success_but_not_installed_%s", version)
		}
	}
	
	// Check for permission/access issues
	hasFailures := false
	for _, attempt := range attempts {
		if attempt.Status == "failed" {
			hasFailures = true
			break
		}
	}
	
	if hasFailures {
		return "installer_permission_or_dependency_issues"
	}
	
	return "unknown_loop_cause"
}

// getLoopRecommendation provides specific recommendations for resolving install loops
func (exp *DataExporter) getLoopRecommendation(attempts []ItemAttempt, packageName string) string {
	// Analyze the pattern to provide specific recommendations
	if strings.Contains(strings.ToLower(packageName), "msi") {
		return "check_msi_installer_silent_flags_and_admin_rights"
	}
	
	if strings.Contains(strings.ToLower(packageName), "exe") {
		return "verify_exe_installer_exit_codes_and_silent_install_parameters"
	}
	
	// Generic recommendations based on attempt patterns
	failureCount := 0
	for _, attempt := range attempts {
		if attempt.Status == "failed" {
			failureCount++
		}
	}
	
	if failureCount > 0 {
		return "check_installer_logs_and_resolve_permission_issues"
	}
	
	return "verify_installer_configuration_and_check_registry_state"
}
