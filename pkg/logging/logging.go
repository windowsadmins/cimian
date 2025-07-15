// pkg/logging/logging.go - Enhanced timestamped logging package for Cimian
//
// This package provides structured logging with timestamped directories
// compatible with external monitoring and reporting tools. Features include:
// - Timestamped subdirectories (YYYY-MM-DD-HHMMss format)
// - Automatic log rotation and retention policies
// - Structured data formats for external tool integration
// - Multiple output formats (JSON, YAML, plain text)
// - Background cleanup of old log directories

package logging

import (
	"encoding/json"
	"fmt"
	"io"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"golang.org/x/sys/windows"
	"gopkg.in/yaml.v3"
)

// LogLevel represents the severity of the log message.
type LogLevel int

const (
	// Define log levels.
	LevelError LogLevel = iota
	LevelWarn
	LevelInfo
	LevelDebug
)

// String returns the string representation of the LogLevel.
func (ll LogLevel) String() string {
	switch ll {
	case LevelError:
		return "ERROR"
	case LevelWarn:
		return "WARN"
	case LevelInfo:
		return "INFO"
	case LevelDebug:
		return "DEBUG"
	default:
		return "UNKNOWN"
	}
}

// LogEntry represents a structured log entry compatible with external monitoring tools
type LogEntry struct {
	Time       int64                  `json:"time" yaml:"time"`                                 // Unix timestamp (BIGINT)
	Timestamp  string                 `json:"timestamp" yaml:"timestamp"`                       // ISO 8601 formatted time
	Level      string                 `json:"level" yaml:"level"`                               // Log level (TEXT)
	Message    string                 `json:"message" yaml:"message"`                           // Log message (TEXT)
	Component  string                 `json:"component" yaml:"component"`                       // Component/module name (TEXT)
	Process    string                 `json:"process" yaml:"process"`                           // Process name (TEXT)
	PID        int64                  `json:"pid" yaml:"pid"`                                   // Process ID (BIGINT)
	Thread     string                 `json:"thread" yaml:"thread"`                             // Thread identifier (TEXT)
	Hostname   string                 `json:"hostname" yaml:"hostname"`                         // System hostname (TEXT)
	Version    string                 `json:"version" yaml:"version"`                           // Cimian version (TEXT)
	SessionID  string                 `json:"session_id" yaml:"session_id"`                     // Unique session identifier (TEXT)
	RunType    string                 `json:"run_type" yaml:"run_type"`                         // manual, scheduled, auto (TEXT)
	Properties map[string]interface{} `json:"properties,omitempty" yaml:"properties,omitempty"` // Additional structured data
}

// RetentionPolicy defines log retention rules
type RetentionPolicy struct {
	DailyRuns  int // Keep last N daily runs (default: 10)
	HourlyRuns int // Keep last N hourly runs (default: 24)
	MaxAgeDays int // Maximum age in days before deletion (default: 30)
}

// LoggerConfig holds configuration for the enhanced logger
type LoggerConfig struct {
	BaseDir          string          // Base logging directory
	RunType          string          // Type of run: manual, scheduled, auto
	SessionID        string          // Unique session identifier
	Component        string          // Component/module name
	Retention        RetentionPolicy // Retention policy
	EnableStructured bool            // Enable structured JSON output
	EnableJSON       bool            // Enable JSON output
	EnableYAML       bool            // Enable YAML output
	EnableConsole    bool            // Enable console output
}

// Logger encapsulates the enhanced logging functionality with timestamped directories.
type Logger struct {
	mu           sync.RWMutex
	logger       *log.Logger
	logLevel     LogLevel
	logFile      *os.File
	jsonFile     *os.File
	yamlFile     *os.File
	config       LoggerConfig
	sessionStart time.Time
	logDir       string // Current timestamped log directory
	hostname     string
	version      string

	// Structured logging integration
	structuredLogger *StructuredLogger
	currentSessionID string
}

// singleton instance and sync.Once for thread-safe initialization
var (
	instance *Logger
	once     sync.Once
)

// DefaultRetentionPolicy returns sensible defaults for log retention
func DefaultRetentionPolicy() RetentionPolicy {
	return RetentionPolicy{
		DailyRuns:  10, // Keep last 10 daily runs
		HourlyRuns: 24, // Keep last 24 hourly runs
		MaxAgeDays: 30, // Delete logs older than 30 days
	}
}

// Init initializes the singleton Logger based on the provided configuration.
// It must be called before any logging functions are used.
func Init(cfg *config.Configuration) error {
	var initErr error
	once.Do(func() {
		instance, initErr = newLogger(cfg)
	})
	return initErr
}

// InitWithConfig initializes the logger with explicit LoggerConfig
func InitWithConfig(logCfg LoggerConfig) error {
	var initErr error
	once.Do(func() {
		instance, initErr = newLoggerWithConfig(logCfg)
	})
	return initErr
}

// generateSessionID creates a unique session identifier
func generateSessionID() string {
	return fmt.Sprintf("cimian-%d-%s", time.Now().Unix(),
		time.Now().Format("2006-01-02-150405"))
}

// createTimestampedLogDir creates a timestamped log directory
func createTimestampedLogDir(baseDir string, sessionStart time.Time) (string, error) {
	// Format: YYYY-MM-DD-HHMMss
	timestamp := sessionStart.Format("2006-01-02-150405")
	logDir := filepath.Join(baseDir, timestamp)

	if err := os.MkdirAll(logDir, 0755); err != nil {
		return "", fmt.Errorf("failed to create timestamped log directory %s: %w", logDir, err)
	}

	return logDir, nil
}

// newLogger creates a new Logger instance based on the configuration.
func newLogger(cfg *config.Configuration) (*Logger, error) {
	// Set up default logging config
	logCfg := LoggerConfig{
		BaseDir:          filepath.Join(`C:\ProgramData\ManagedInstalls`, `logs`),
		RunType:          "auto", // Default run type
		SessionID:        generateSessionID(),
		Component:        "cimian",
		Retention:        DefaultRetentionPolicy(),
		EnableStructured: true,
		EnableJSON:       true,
		EnableYAML:       true,
		EnableConsole:    true,
	}

	// Override based on configuration flags
	if cfg.Debug {
		logCfg.RunType = "manual"
	}

	return newLoggerWithConfig(logCfg)
}

// newLoggerWithConfig creates a new Logger instance with explicit configuration.
func newLoggerWithConfig(cfg LoggerConfig) (*Logger, error) {
	sessionStart := time.Now()

	// Create base directory
	if err := os.MkdirAll(cfg.BaseDir, 0755); err != nil {
		return nil, fmt.Errorf("failed to create base log directory: %w", err)
	}

	// Create timestamped log directory
	logDir, err := createTimestampedLogDir(cfg.BaseDir, sessionStart)
	if err != nil {
		return nil, err
	}

	// Get system information
	hostname, _ := os.Hostname()
	if hostname == "" {
		hostname = "unknown"
	}

	// Create logger instance
	logger := &Logger{
		config:       cfg,
		sessionStart: sessionStart,
		logDir:       logDir,
		hostname:     hostname,
		version:      "2025.07.12", // TODO: Get from build info
	}

	// Set up log level
	var level LogLevel = LevelInfo
	// TODO: Read from cfg when available
	logger.logLevel = level

	// Initialize log files
	if err := logger.initializeLogFiles(); err != nil {
		return nil, err
	}

	// Initialize structured logger
	retentionConfig := RetentionConfig{
		DailyRetentionDays:   cfg.Retention.DailyRuns,
		HourlyRetentionHours: cfg.Retention.HourlyRuns,
		EnableCleanup:        true,
	}

	logger.structuredLogger, err = NewStructuredLogger(cfg.BaseDir, retentionConfig)
	if err != nil {
		return nil, fmt.Errorf("failed to initialize structured logger: %w", err)
	}

	// Set up console output
	if cfg.EnableConsole {
		multiWriter := io.MultiWriter(os.Stdout, logger.logFile)
		logger.logger = log.New(multiWriter, "", 0)
	} else {
		logger.logger = log.New(logger.logFile, "", 0)
	}

	// Start background cleanup routine
	go logger.cleanupOldLogs()

	return logger, nil
}

// initializeLogFiles creates and opens all log files
func (l *Logger) initializeLogFiles() error {
	var err error

	// Main log file (install.log for backward compatibility)
	logFilePath := filepath.Join(l.logDir, "install.log")
	l.logFile, err = os.OpenFile(logFilePath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
	if err != nil {
		return fmt.Errorf("failed to open main log file: %w", err)
	}

	// JSON log file for structured logging
	if l.config.EnableJSON {
		jsonPath := filepath.Join(l.logDir, "events.jsonl")
		l.jsonFile, err = os.OpenFile(jsonPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
		if err != nil {
			return fmt.Errorf("failed to open JSON log file: %w", err)
		}
	}

	// YAML log file for structured logging
	if l.config.EnableYAML {
		yamlPath := filepath.Join(l.logDir, "cimian.yaml")
		l.yamlFile, err = os.OpenFile(yamlPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
		if err != nil {
			return fmt.Errorf("failed to open YAML log file: %w", err)
		}
	}

	return nil
}

// cleanupOldLogs removes old log directories based on retention policy
func (l *Logger) cleanupOldLogs() {
	ticker := time.NewTicker(1 * time.Hour) // Check every hour
	defer ticker.Stop()

	for range ticker.C {
		l.performCleanup()
	}
}

// performCleanup actually performs the log cleanup
func (l *Logger) performCleanup() {
	baseDir := l.config.BaseDir
	entries, err := os.ReadDir(baseDir)
	if err != nil {
		return // Silently fail cleanup
	}

	var logDirs []os.DirEntry
	now := time.Now()

	// Filter for log directories (timestamped format)
	for _, entry := range entries {
		if entry.IsDir() {
			// Check if directory name matches timestamp format YYYY-MM-DD-HHMMss
			if len(entry.Name()) == 17 && strings.Count(entry.Name(), "-") == 3 {
				logDirs = append(logDirs, entry)
			}
		}
	}

	// Sort directories by name (which sorts by timestamp due to format)
	sort.Slice(logDirs, func(i, j int) bool {
		return logDirs[i].Name() > logDirs[j].Name() // Newest first
	})

	// Apply retention policy
	retention := l.config.Retention
	toDelete := []string{}

	// Keep recent directories based on retention counts
	keepCount := retention.DailyRuns + retention.HourlyRuns
	if len(logDirs) > keepCount {
		for i := keepCount; i < len(logDirs); i++ {
			toDelete = append(toDelete, logDirs[i].Name())
		}
	}

	// Also delete directories older than MaxAgeDays
	maxAge := time.Duration(retention.MaxAgeDays) * 24 * time.Hour
	for _, dir := range logDirs {
		dirPath := filepath.Join(baseDir, dir.Name())
		if info, err := os.Stat(dirPath); err == nil {
			if now.Sub(info.ModTime()) > maxAge {
				toDelete = append(toDelete, dir.Name())
			}
		}
	}

	// Remove duplicates and delete directories
	deletedDirs := make(map[string]bool)
	for _, dirName := range toDelete {
		if !deletedDirs[dirName] {
			dirPath := filepath.Join(baseDir, dirName)
			os.RemoveAll(dirPath) // Best effort, ignore errors
			deletedDirs[dirName] = true
		}
	}
}

// createLogEntry creates a structured log entry
func (l *Logger) createLogEntry(level LogLevel, message string, properties map[string]interface{}) LogEntry {
	now := time.Now()

	entry := LogEntry{
		Time:       now.Unix(),
		Timestamp:  now.Format(time.RFC3339),
		Level:      level.String(),
		Message:    message,
		Component:  l.config.Component,
		Process:    "cimian", // TODO: Get actual process name
		PID:        int64(os.Getpid()),
		Thread:     fmt.Sprintf("%d", runtime.NumGoroutine()), // Use goroutine count as thread info
		Hostname:   l.hostname,
		Version:    l.version,
		SessionID:  l.config.SessionID,
		RunType:    l.config.RunType,
		Properties: properties,
	}

	return entry
}

// CloseLogger closes all log files if they're open.
func CloseLogger() {
	if instance == nil {
		return
	}
	instance.mu.Lock()
	defer instance.mu.Unlock()

	// Close main log file
	if instance.logFile != nil {
		if err := instance.logFile.Close(); err != nil {
			fmt.Printf("Failed to close main log file: %v\n", err)
		}
		instance.logFile = nil
	}

	// Close JSON log file
	if instance.jsonFile != nil {
		if err := instance.jsonFile.Close(); err != nil {
			fmt.Printf("Failed to close JSON log file: %v\n", err)
		}
		instance.jsonFile = nil
	}

	// Close YAML log file
	if instance.yamlFile != nil {
		if err := instance.yamlFile.Close(); err != nil {
			fmt.Printf("Failed to close YAML log file: %v\n", err)
		}
		instance.yamlFile = nil
	}
}

// logMessage is the core logging method that writes to all configured outputs
func (l *Logger) logMessage(level LogLevel, message string, keyValues ...interface{}) {
	l.mu.Lock()
	defer l.mu.Unlock()

	if l.logger == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: %s %s %v\n", level.String(), message, keyValues)
		return
	}

	if level > l.logLevel {
		return
	}

	// Convert keyValues to properties map
	properties := make(map[string]interface{})
	for i := 0; i < len(keyValues); i += 2 {
		if i+1 < len(keyValues) {
			key := fmt.Sprintf("%v", keyValues[i])
			properties[key] = keyValues[i+1]
		}
	}

	// Create structured log entry
	entry := l.createLogEntry(level, message, properties)

	// Write to main log file (backward compatible format)
	l.writeMainLog(entry, keyValues)

	// Write to structured formats if enabled
	if l.config.EnableJSON && l.jsonFile != nil {
		l.writeJSONLog(entry)
	}

	if l.config.EnableYAML && l.yamlFile != nil {
		l.writeYAMLLog(entry)
	}

	// Force sync all files
	l.syncFiles()
}

// writeMainLog writes to the main install.log file in traditional format
func (l *Logger) writeMainLog(entry LogEntry, keyValues []interface{}) {
	// Traditional format for backward compatibility
	ts := time.Unix(entry.Time, 0).Format("2006-01-02 15:04:05")
	baseLine := fmt.Sprintf("[%s] %-5s %s", ts, entry.Level, entry.Message)

	// Append key-value pairs in traditional format
	if len(keyValues) > 0 {
		if len(keyValues)/2 > 4 {
			for i := 0; i < len(keyValues); i += 2 {
				if i+1 < len(keyValues) {
					key := fmt.Sprintf("%v", keyValues[i])
					val := keyValues[i+1]
					baseLine += fmt.Sprintf("\n        %s: %v", key, val)
				}
			}
		} else {
			for i := 0; i < len(keyValues); i += 2 {
				if i+1 < len(keyValues) {
					key := fmt.Sprintf("%v", keyValues[i])
					val := keyValues[i+1]
					baseLine += fmt.Sprintf(" %s=%v", key, val)
				}
			}
		}
	}

	// Add error separator
	if entry.Level == "ERROR" {
		baseLine = "\n----------------------------------------\n" + baseLine
	}

	l.logger.Println(baseLine)
}

// writeJSONLog writes structured JSON log entry
func (l *Logger) writeJSONLog(entry LogEntry) {
	if data, err := json.MarshalIndent(entry, "", "  "); err == nil {
		l.jsonFile.WriteString(string(data) + "\n")
	}
}

// writeYAMLLog writes structured YAML log entry
func (l *Logger) writeYAMLLog(entry LogEntry) {
	if data, err := yaml.Marshal(entry); err == nil {
		l.yamlFile.WriteString("---\n" + string(data))
	}
}

// syncFiles forces sync on all open log files
func (l *Logger) syncFiles() {
	if l.logFile != nil {
		l.logFile.Sync()
	}
	if l.jsonFile != nil {
		l.jsonFile.Sync()
	}
	if l.yamlFile != nil {
		l.yamlFile.Sync()
	}
}

// ANSI color codes
const (
	colorReset  = "\033[0m"
	colorRed    = "\033[31m"
	colorYellow = "\033[33m"
	colorBlue   = "\033[34m"
	colorGreen  = "\033[32m"
)

// enableColors enables ANSI colors for Windows console
func enableColors() {
	if runtime.GOOS == "windows" {
		// Use windows package instead of syscall
		handle := windows.Handle(windows.STD_OUTPUT_HANDLE)
		var mode uint32
		err := windows.GetConsoleMode(handle, &mode)
		if err == nil {
			// Enable virtual terminal processing (0x0004)
			mode |= 0x0004
			_ = windows.SetConsoleMode(handle, mode)
		}
	}
}

// Info logs informational messages.
func Info(message string, keyValues ...interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: INFO %s %v\n", message, keyValues)
		return
	}
	instance.logMessage(LevelInfo, message, keyValues...)
}

// Debug logs debug messages.
func Debug(message string, keyValues ...interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: DEBUG %s %v\n", message, keyValues)
		return
	}
	instance.logMessage(LevelDebug, message, keyValues...)
}

// Warn logs warning messages.
func Warn(message string, keyValues ...interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: WARN %s %v\n", message, keyValues)
		return
	}
	instance.logMessage(LevelWarn, message, keyValues...)
}

// Error logs error messages.
func Error(message string, keyValues ...interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: ERROR %s %v\n", message, keyValues)
		return
	}
	instance.logMessage(LevelError, message, keyValues...)
}

// New creates a new Logger instance.
func New(verbose bool) *Logger {
	enableColors()

	flags := 0

	output := os.Stdout
	if !verbose {
		output = os.Stderr
	}
	l := log.New(output, "", flags)
	return &Logger{
		logger:   l,
		logLevel: LevelInfo, // default log level
		logFile:  nil,       // no file logging for this instance
	}
}

// SetOutput changes the output destination.
func (l *Logger) SetOutput(w io.Writer) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.logger.SetOutput(w)
}

// colorPrintf prints a colored message.
func (l *Logger) colorPrintf(color, format string, v ...interface{}) {
	l.mu.RLock()
	defer l.mu.RUnlock()

	ts := time.Now().Format("2006-01-02 15:04:05")
	msg := fmt.Sprintf(format, v...)
	l.logger.Printf("%s[%s] %s%s", color, ts, msg, colorReset)
}

// Printf prints a regular message.
func (l *Logger) Printf(format string, v ...interface{}) {
	l.mu.RLock()
	defer l.mu.RUnlock()
	ts := time.Now().Format("2006-01-02 15:04:05")
	msg := fmt.Sprintf(format, v...)
	l.logger.Printf("[%s] %s", ts, msg)
}

// Info prints an informational message (instance method counterpart to the package-level Info).
func (l *Logger) Info(format string, v ...interface{}) {
	l.Printf(format, v...)
}

// Success prints a success message in green.
func (l *Logger) Success(format string, v ...interface{}) {
	l.colorPrintf(colorGreen, format, v...)
}

// Error prints an error message in red.
func (l *Logger) Error(format string, v ...interface{}) {
	l.colorPrintf(colorRed, format, v...)
}

// Warning prints a warning message in yellow.
func (l *Logger) Warning(format string, v ...interface{}) {
	l.colorPrintf(colorYellow, format, v...)
}

// Debug prints a debug message in blue.
func (l *Logger) Debug(format string, v ...interface{}) {
	l.colorPrintf(colorBlue, format, v...)
}

// Fatal prints an error message in red and exits.
func (l *Logger) Fatal(format string, v ...interface{}) {
	l.Error(format, v...)
	os.Exit(1)
}

// DebugRaw prints a debug message without forcing a blue color.
// This preserves any ANSI escape codes already present in the message.
func (l *Logger) DebugRaw(format string, v ...interface{}) {
	l.mu.RLock()
	defer l.mu.RUnlock()
	ts := time.Now().Format("2006-01-02 15:04:05")
	msg := fmt.Sprintf(format, v...)
	l.logger.Printf("[%s] %s", ts, msg)
}

// ReInit allows re-initializing the logger (e.g., after configuration reload).
// It closes the existing logger and creates a new one.
func ReInit(cfg *config.Configuration) error {
	if instance == nil {
		return Init(cfg)
	}

	instance.mu.Lock()
	defer instance.mu.Unlock()

	// Close all existing files
	if instance.logFile != nil {
		instance.logFile.Close()
		instance.logFile = nil
	}
	if instance.jsonFile != nil {
		instance.jsonFile.Close()
		instance.jsonFile = nil
	}
	if instance.yamlFile != nil {
		instance.yamlFile.Close()
		instance.yamlFile = nil
	}

	// Create new logger
	newLogger, err := newLogger(cfg)
	if err != nil {
		return err
	}

	// Replace instance fields
	instance.logger = newLogger.logger
	instance.logLevel = newLogger.logLevel
	instance.logFile = newLogger.logFile
	instance.jsonFile = newLogger.jsonFile
	instance.yamlFile = newLogger.yamlFile
	instance.config = newLogger.config
	instance.sessionStart = newLogger.sessionStart
	instance.logDir = newLogger.logDir
	instance.hostname = newLogger.hostname
	instance.version = newLogger.version

	return nil
}

// GetCurrentLogDir returns the current timestamped log directory
func GetCurrentLogDir() string {
	if instance == nil {
		return ""
	}
	instance.mu.RLock()
	defer instance.mu.RUnlock()
	return instance.logDir
}

// GetSessionID returns the current session ID
func GetSessionID() string {
	if instance == nil {
		return ""
	}
	instance.mu.RLock()
	defer instance.mu.RUnlock()
	return instance.config.SessionID
}

// SetRunType updates the run type for the current session
func SetRunType(runType string) {
	if instance == nil {
		return
	}
	instance.mu.Lock()
	defer instance.mu.Unlock()
	instance.config.RunType = runType
}

// StartSession begins a new structured logging session (package-level function)
func StartSession(runType string, metadata map[string]interface{}) error {
	if instance == nil {
		return fmt.Errorf("logging not initialized")
	}
	return instance.StartSession(runType, metadata)
}

// EndSession completes the current structured logging session (package-level function)
func EndSession(status string, summary SessionSummary) error {
	if instance == nil {
		return fmt.Errorf("logging not initialized")
	}
	return instance.EndSession(status, summary)
}

// LogStructured logs a structured message with explicit properties (useful for osquery compatibility)
func LogStructured(level LogLevel, message string, properties map[string]interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: %s %s %v\n", level.String(), message, properties)
		return
	}

	// Convert properties to keyValues slice
	keyValues := make([]interface{}, 0, len(properties)*2)
	for k, v := range properties {
		keyValues = append(keyValues, k, v)
	}

	instance.logMessage(level, message, keyValues...)
}

// runScript executes the PowerShell script with the specified verbosity and returns its output.
func runScript(
	scriptPath string,
	displayName string,
	verbosity int,
	logError func(string, ...interface{}),
) error {
	psExe, err := exec.LookPath("pwsh.exe")
	if err != nil {
		fmt.Printf("pwsh.exe not found; falling back to Windows PowerShell (v5)\n")
		psExe, err = exec.LookPath("powershell.exe")
		if err != nil {
			return fmt.Errorf("neither pwsh.exe nor powershell.exe were found: %v", err)
		}
	} else {
		fmt.Printf("Using PowerShell Core (pwsh) for %s\n", displayName)
	}

	if versionBytes, verErr := exec.Command(psExe, "--version").CombinedOutput(); verErr == nil {
		fmt.Printf("%s version: %s\n", psExe, strings.TrimSpace(string(versionBytes)))
	}

	cmd := exec.Command(
		psExe,
		"-NoLogo",
		"-NoProfile",
		"-ExecutionPolicy", "Bypass",
		"-File", scriptPath,
		"-Verbosity", strconv.Itoa(verbosity),
	)
	cmd.Dir = filepath.Dir(scriptPath)
	cmd.Env = append(cmd.Env, "TERM=xterm-256color")
	cmd.Env = append(cmd.Env, os.Environ()...)

	outputBytes, execErr := cmd.CombinedOutput()
	fmt.Print(string(outputBytes))
	if execErr != nil {
		logError("%s script error: %v", displayName, execErr)
		return fmt.Errorf("%s script error: %w", displayName, execErr)
	}

	fmt.Printf("%s script completed successfully\n", displayName)
	return nil
}

// RunPreflight calls runScript for preflight.
func RunPreflight(verbosity int, logError func(string, ...interface{})) error {
	scriptPath := `C:\Program Files\Cimian\preflight.ps1`

	// Check if the script exists before trying to run it
	if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
		if verbosity >= 3 {
			fmt.Printf("Preflight script not found at %s, skipping\n", scriptPath)
		}
		return nil // Not an error - script is optional
	}

	fmt.Printf("Running preflight script with verbosity level: %d\n", verbosity)
	return runScript(scriptPath, "Preflight", verbosity, logError)
}

// RunPostflight calls runScript for postflight.
func RunPostflight(verbosity int, logError func(string, ...interface{})) error {
	scriptPath := `C:\Program Files\Cimian\postflight.ps1`

	// Check if the script exists before trying to run it
	if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
		if verbosity >= 3 {
			fmt.Printf("Postflight script not found at %s, skipping\n", scriptPath)
		}
		return nil // Not an error - script is optional
	}

	fmt.Printf("Running postflight script with verbosity level: %d\n", verbosity)
	return runScript(scriptPath, "Postflight", verbosity, logError)
}

// Structured Logging Integration Methods

// StartSession begins a new structured logging session
func (l *Logger) StartSession(runType string, metadata map[string]interface{}) error {
	if l.structuredLogger == nil {
		return nil // Gracefully handle when structured logging is disabled
	}

	sessionID, err := l.structuredLogger.StartSession(runType, metadata)
	if err != nil {
		return fmt.Errorf("failed to start structured session: %w", err)
	}

	l.currentSessionID = sessionID
	return nil
}

// LogEvent writes a structured event to the current session
func (l *Logger) LogEvent(eventType, action, status, message string, opts ...EventOption) error {
	if l.structuredLogger == nil || l.currentSessionID == "" {
		return nil // Gracefully handle when structured logging is not available
	}

	// Get caller information
	pc, file, line, ok := runtime.Caller(1)
	sourceInfo := SourceInfo{}
	if ok {
		sourceInfo.File = filepath.Base(file)
		sourceInfo.Line = line
		if fn := runtime.FuncForPC(pc); fn != nil {
			sourceInfo.Function = filepath.Base(fn.Name())
		}
	}

	event := LogEvent{
		EventType: eventType,
		Action:    action,
		Status:    status,
		Message:   message,
		Source:    sourceInfo,
		Context:   make(map[string]interface{}),
	}

	// Apply options
	for _, opt := range opts {
		opt(&event)
	}

	return l.structuredLogger.LogEvent(event)
}

// EndSession completes the current structured logging session
func (l *Logger) EndSession(status string, summary SessionSummary) error {
	if l.structuredLogger == nil || l.currentSessionID == "" {
		return nil // Gracefully handle when structured logging is disabled
	}

	err := l.structuredLogger.EndSession(status, summary, l.sessionStart)
	l.currentSessionID = ""
	return err
}

// ExportForOSQuery generates osquery-compatible JSON export
// Note: This function is deprecated. Use the reporting package directly.
func (l *Logger) ExportForOSQuery(outputPath string, limitDays int) error {
	return fmt.Errorf("ExportForOSQuery is deprecated - use github.com/windowsadmins/cimian/pkg/reporting.NewDataExporter() directly")
}

// GetSessionDirs returns all available session directories
func (l *Logger) GetSessionDirs() ([]string, error) {
	if l.structuredLogger == nil {
		return nil, fmt.Errorf("structured logger not initialized")
	}

	return l.structuredLogger.GetSessionDirs()
}

// QueryEvents provides a simple query interface for events
func (l *Logger) QueryEvents(sessionID string, filters map[string]interface{}) ([]LogEvent, error) {
	if l.structuredLogger == nil {
		return nil, fmt.Errorf("structured logger not initialized")
	}

	return l.structuredLogger.QueryEvents(sessionID, filters)
}

// EventOption allows customizing log events
type EventOption func(*LogEvent)

// WithPackage sets the package name for the event
func WithPackage(name, version string) EventOption {
	return func(e *LogEvent) {
		e.Package = name
		e.Version = version
	}
}

// WithProgress sets the progress percentage for the event
func WithProgress(progress int) EventOption {
	return func(e *LogEvent) {
		e.Progress = &progress
	}
}

// WithDuration sets the duration for the event
func WithDuration(duration time.Duration) EventOption {
	return func(e *LogEvent) {
		e.Duration = &duration
	}
}

// WithError sets the error message for the event
func WithError(err error) EventOption {
	return func(e *LogEvent) {
		if err != nil {
			e.Error = err.Error()
		}
	}
}

// WithContext adds context information to the event
func WithContext(key string, value interface{}) EventOption {
	return func(e *LogEvent) {
		if e.Context == nil {
			e.Context = make(map[string]interface{})
		}
		e.Context[key] = value
	}
}

// WithLevel sets the log level for the event
func WithLevel(level string) EventOption {
	return func(e *LogEvent) {
		e.Level = level
	}
}

// Helper methods for common event patterns

// LogInstallStart logs the start of a package installation
func (l *Logger) LogInstallStart(packageName, version string) error {
	return l.LogEvent("install", "start", "started",
		fmt.Sprintf("Starting installation of %s %s", packageName, version),
		WithPackage(packageName, version),
		WithLevel("INFO"))
}

// LogInstallProgress logs installation progress
func (l *Logger) LogInstallProgress(packageName string, progress int, message string) error {
	return l.LogEvent("install", "progress", "running", message,
		WithPackage(packageName, ""),
		WithProgress(progress),
		WithLevel("INFO"))
}

// LogInstallComplete logs successful completion of installation
func (l *Logger) LogInstallComplete(packageName, version string, duration time.Duration) error {
	return l.LogEvent("install", "complete", "completed",
		fmt.Sprintf("Successfully installed %s %s", packageName, version),
		WithPackage(packageName, version),
		WithDuration(duration),
		WithLevel("INFO"))
}

// LogInstallFailed logs failed installation
func (l *Logger) LogInstallFailed(packageName, version string, err error) error {
	return l.LogEvent("install", "complete", "failed",
		fmt.Sprintf("Failed to install %s %s", packageName, version),
		WithPackage(packageName, version),
		WithError(err),
		WithLevel("ERROR"))
}

// LogStatusCheck logs a status check operation
func (l *Logger) LogStatusCheck(packageName string, installed bool, version string) error {
	status := "not_installed"
	if installed {
		status = "installed"
	}

	return l.LogEvent("status_check", "check", status,
		fmt.Sprintf("Status check: %s %s is %s", packageName, version, status),
		WithPackage(packageName, version),
		WithLevel("DEBUG"))
}

// LogSystemEvent logs system-level events
func (l *Logger) LogSystemEvent(eventType, message string, context map[string]interface{}) error {
	opts := []EventOption{WithLevel("INFO")}
	for k, v := range context {
		opts = append(opts, WithContext(k, v))
	}

	return l.LogEvent("system", eventType, "completed", message, opts...)
}
