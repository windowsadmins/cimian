// pkg/logging/events.go - Modern structured logging for external monitoring tools

package logging

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"time"
)

// StructuredLogger provides structured logging with timestamped directories
type StructuredLogger struct {
	baseDir         string
	currentSession  string
	sessionFile     *os.File
	eventsFile      *os.File
	retentionConfig RetentionConfig
}

// RetentionConfig defines log retention policies
type RetentionConfig struct {
	DailyRetentionDays   int  `yaml:"daily_retention_days"`
	HourlyRetentionHours int  `yaml:"hourly_retention_hours"`
	EnableCleanup        bool `yaml:"enable_cleanup"`
}

// LogSession represents a complete installation/update session
type LogSession struct {
	SessionID   string                 `json:"session_id"`
	StartTime   time.Time              `json:"start_time"`
	EndTime     *time.Time             `json:"end_time,omitempty"`
	RunType     string                 `json:"run_type"` // auto, manual, bootstrap, ondemand
	Status      string                 `json:"status"`   // running, completed, failed, interrupted
	Summary     SessionSummary         `json:"summary"`
	Environment map[string]interface{} `json:"environment"`
	Metadata    map[string]interface{} `json:"metadata"`
}

// SessionSummary provides high-level session metrics
type SessionSummary struct {
	TotalActions    int           `json:"total_actions"`
	Installs        int           `json:"installs"`
	Updates         int           `json:"updates"`
	Removals        int           `json:"removals"`
	Successes       int           `json:"successes"`
	Failures        int           `json:"failures"`
	Duration        time.Duration `json:"duration"`
	PackagesHandled []string      `json:"packages_handled"`
}

// LogEvent represents individual actions within a session
type LogEvent struct {
	EventID   string                 `json:"event_id"`
	SessionID string                 `json:"session_id"`
	Timestamp time.Time              `json:"timestamp"`
	Level     string                 `json:"level"`
	EventType string                 `json:"event_type"` // install, remove, update, status_check, error
	Package   string                 `json:"package,omitempty"`
	Version   string                 `json:"version,omitempty"`
	Action    string                 `json:"action"`
	Status    string                 `json:"status"` // started, progress, completed, failed
	Message   string                 `json:"message"`
	Duration  *time.Duration         `json:"duration,omitempty"`
	Progress  *int                   `json:"progress,omitempty"` // 0-100 or -1 for indeterminate
	Error     string                 `json:"error,omitempty"`
	Context   map[string]interface{} `json:"context,omitempty"`
	Source    SourceInfo             `json:"source"`
}

// SourceInfo tracks where actions originated for debugging
type SourceInfo struct {
	File     string `json:"file"`
	Function string `json:"function"`
	Line     int    `json:"line"`
	Caller   string `json:"caller,omitempty"`
}

// NewStructuredLogger creates a new structured logger instance
func NewStructuredLogger(baseDir string, config RetentionConfig) (*StructuredLogger, error) {
	if err := os.MkdirAll(baseDir, 0755); err != nil {
		return nil, fmt.Errorf("failed to create base log directory: %w", err)
	}

	logger := &StructuredLogger{
		baseDir:         baseDir,
		retentionConfig: config,
	}

	// Perform cleanup if enabled
	if config.EnableCleanup {
		if err := logger.performRetentionCleanup(); err != nil {
			// Don't fail initialization on cleanup errors, just log
			fmt.Printf("Warning: failed to perform log cleanup: %v\n", err)
		}
	}

	return logger, nil
}

// StartSession begins a new logging session with timestamped directory
func (sl *StructuredLogger) StartSession(runType string, metadata map[string]interface{}) (string, error) {
	now := time.Now()
	sessionID := now.Format("20060102-150405") // YYYYMMDD-HHMMSS

	// Create session directory
	sessionDir := filepath.Join(sl.baseDir, sessionID)
	if err := os.MkdirAll(sessionDir, 0755); err != nil {
		return "", fmt.Errorf("failed to create session directory: %w", err)
	}

	sl.currentSession = sessionID

	// Create session.json file
	sessionPath := filepath.Join(sessionDir, "session.json")
	sessionFile, err := os.Create(sessionPath)
	if err != nil {
		return "", fmt.Errorf("failed to create session file: %w", err)
	}
	sl.sessionFile = sessionFile

	// Create events.jsonl file (JSON Lines format for streaming)
	eventsPath := filepath.Join(sessionDir, "events.jsonl")
	eventsFile, err := os.Create(eventsPath)
	if err != nil {
		sessionFile.Close()
		return "", fmt.Errorf("failed to create events file: %w", err)
	}
	sl.eventsFile = eventsFile

	// Initialize session
	session := LogSession{
		SessionID:   sessionID,
		StartTime:   now,
		RunType:     runType,
		Status:      "running",
		Environment: sl.gatherEnvironmentInfo(),
		Metadata:    metadata,
		Summary:     SessionSummary{PackagesHandled: make([]string, 0)},
	}

	// Write initial session data
	if err := sl.writeSession(session); err != nil {
		return "", fmt.Errorf("failed to write initial session: %w", err)
	}

	return sessionID, nil
}

// LogEvent writes an event to the current session
func (sl *StructuredLogger) LogEvent(event LogEvent) error {
	if sl.eventsFile == nil {
		return fmt.Errorf("no active session for logging event")
	}

	event.SessionID = sl.currentSession
	if event.Timestamp.IsZero() {
		event.Timestamp = time.Now()
	}

	// Generate event ID if not provided
	if event.EventID == "" {
		event.EventID = fmt.Sprintf("%s-%d", sl.currentSession, time.Now().UnixNano())
	}

	// Write event as JSON line
	eventJSON, err := json.MarshalIndent(event, "", "  ")
	if err != nil {
		return fmt.Errorf("failed to marshal event: %w", err)
	}

	if _, err := sl.eventsFile.WriteString(string(eventJSON) + "\n"); err != nil {
		return fmt.Errorf("failed to write event: %w", err)
	}

	// Force flush to disk
	if err := sl.eventsFile.Sync(); err != nil {
		return fmt.Errorf("failed to sync events file: %w", err)
	}

	return nil
}

// EndSession completes the current session and updates summary
func (sl *StructuredLogger) EndSession(status string, summary SessionSummary, startTime time.Time) error {
	if sl.sessionFile == nil {
		return fmt.Errorf("no active session to end")
	}

	now := time.Now()
	summary.Duration = now.Sub(startTime)

	session := LogSession{
		SessionID: sl.currentSession,
		EndTime:   &now,
		Status:    status,
		Summary:   summary,
	}

	if err := sl.writeSession(session); err != nil {
		return fmt.Errorf("failed to write final session: %w", err)
	}

	// Close files
	if err := sl.sessionFile.Close(); err != nil {
		return fmt.Errorf("failed to close session file: %w", err)
	}
	if err := sl.eventsFile.Close(); err != nil {
		return fmt.Errorf("failed to close events file: %w", err)
	}

	sl.sessionFile = nil
	sl.eventsFile = nil
	sl.currentSession = ""

	return nil
}

// writeSession updates the session.json file
func (sl *StructuredLogger) writeSession(session LogSession) error {
	if sl.sessionFile == nil {
		return fmt.Errorf("no session file open")
	}

	// Seek to beginning and truncate
	if _, err := sl.sessionFile.Seek(0, 0); err != nil {
		return err
	}
	if err := sl.sessionFile.Truncate(0); err != nil {
		return err
	}

	encoder := json.NewEncoder(sl.sessionFile)
	encoder.SetIndent("", "  ")
	return encoder.Encode(session)
}

// gatherEnvironmentInfo collects system environment for session context
func (sl *StructuredLogger) gatherEnvironmentInfo() map[string]interface{} {
	env := make(map[string]interface{})

	// System information
	if hostname, err := os.Hostname(); err == nil {
		env["hostname"] = hostname
	}

	env["platform"] = "windows"
	env["log_version"] = "2.0"
	env["process_id"] = os.Getpid()

	// User context
	if user, exists := os.LookupEnv("USERNAME"); exists {
		env["user"] = user
	}
	if domain, exists := os.LookupEnv("USERDOMAIN"); exists {
		env["domain"] = domain
	}

	return env
}

// performRetentionCleanup removes old log directories based on retention policy
func (sl *StructuredLogger) performRetentionCleanup() error {
	entries, err := os.ReadDir(sl.baseDir)
	if err != nil {
		return fmt.Errorf("failed to read log directory: %w", err)
	}

	now := time.Now()
	var dirsToRemove []string

	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}

		// Parse directory name as timestamp (YYYYMMDD-HHMMSS)
		dirName := entry.Name()
		if len(dirName) != 15 || dirName[8] != '-' {
			continue // Skip non-timestamped directories
		}

		timestamp, err := time.Parse("20060102-150405", dirName)
		if err != nil {
			continue // Skip directories that don't match our format
		}

		age := now.Sub(timestamp)

		// Determine if directory should be removed based on retention policy
		shouldRemove := false

		// For directories older than 24 hours, keep only daily (remove hourly)
		if age > time.Duration(sl.retentionConfig.HourlyRetentionHours)*time.Hour {
			// Check if this is a "daily keeper" (first session of the day)
			if !sl.isDailyKeeper(timestamp, entries) {
				shouldRemove = true
			}
		}

		// For directories older than daily retention, remove all
		if age > time.Duration(sl.retentionConfig.DailyRetentionDays)*24*time.Hour {
			shouldRemove = true
		}

		if shouldRemove {
			dirsToRemove = append(dirsToRemove, filepath.Join(sl.baseDir, dirName))
		}
	}

	// Remove directories
	for _, dir := range dirsToRemove {
		if err := os.RemoveAll(dir); err != nil {
			fmt.Printf("Warning: failed to remove old log directory %s: %v\n", dir, err)
		}
	}

	return nil
}

// isDailyKeeper determines if a timestamp represents the "daily keeper" session
func (sl *StructuredLogger) isDailyKeeper(timestamp time.Time, entries []os.DirEntry) bool {
	dayStart := time.Date(timestamp.Year(), timestamp.Month(), timestamp.Day(), 0, 0, 0, 0, timestamp.Location())
	dayEnd := dayStart.Add(24 * time.Hour)

	// Find the first session of this day
	var firstSession *time.Time
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}

		if entryTime, err := time.Parse("20060102-150405", entry.Name()); err == nil {
			if entryTime.After(dayStart) && entryTime.Before(dayEnd) {
				if firstSession == nil || entryTime.Before(*firstSession) {
					firstSession = &entryTime
				}
			}
		}
	}

	return firstSession != nil && timestamp.Equal(*firstSession)
}

// GetSessionDirs returns all session directories sorted by timestamp
func (sl *StructuredLogger) GetSessionDirs() ([]string, error) {
	entries, err := os.ReadDir(sl.baseDir)
	if err != nil {
		return nil, fmt.Errorf("failed to read log directory: %w", err)
	}

	var sessions []string
	for _, entry := range entries {
		if entry.IsDir() && len(entry.Name()) == 15 && entry.Name()[8] == '-' {
			if _, err := time.Parse("20060102-150405", entry.Name()); err == nil {
				sessions = append(sessions, entry.Name())
			}
		}
	}

	sort.Strings(sessions) // Chronological order due to timestamp format
	return sessions, nil
}

// QueryEvents provides a simple query interface for external monitoring tools
func (sl *StructuredLogger) QueryEvents(sessionID string, filters map[string]interface{}) ([]LogEvent, error) {
	eventsPath := filepath.Join(sl.baseDir, sessionID, "events.jsonl")

	file, err := os.Open(eventsPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open events file: %w", err)
	}
	defer file.Close()

	var events []LogEvent
	decoder := json.NewDecoder(file)

	for decoder.More() {
		var event LogEvent
		if err := decoder.Decode(&event); err != nil {
			continue // Skip malformed events
		}

		// Apply filters
		if sl.matchesFilters(event, filters) {
			events = append(events, event)
		}
	}

	return events, nil
}

// matchesFilters checks if an event matches the provided filters
func (sl *StructuredLogger) matchesFilters(event LogEvent, filters map[string]interface{}) bool {
	for key, value := range filters {
		switch key {
		case "level":
			if event.Level != value.(string) {
				return false
			}
		case "event_type":
			if event.EventType != value.(string) {
				return false
			}
		case "package":
			if event.Package != value.(string) {
				return false
			}
		case "status":
			if event.Status != value.(string) {
				return false
			}
		}
	}
	return true
}
