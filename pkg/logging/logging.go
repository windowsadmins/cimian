// pkg/logging/logging.go - Logging package for Cimian

package logging

import (
	"fmt"
	"io"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"golang.org/x/sys/windows"
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

// Logger encapsulates the logging functionality.
type Logger struct {
	mu       sync.RWMutex
	logger   *log.Logger
	logLevel LogLevel
	logFile  *os.File
}

// singleton instance and sync.Once for thread-safe initialization
var (
	instance *Logger
	once     sync.Once
)

// Init initializes the singleton Logger based on the provided configuration.
// It must be called before any logging functions are used.
func Init(cfg *config.Configuration) error {
	var initErr error
	once.Do(func() {
		instance, initErr = newLogger(cfg)
	})
	return initErr
}

// newLogger creates a new Logger instance based on the configuration.
func newLogger(cfg *config.Configuration) (*Logger, error) {
	logDir := filepath.Join(`C:\ProgramData\ManagedInstalls`, `Logs`)
	if err := os.MkdirAll(logDir, 0755); err != nil {
		return nil, fmt.Errorf("failed to create log directory: %w", err)
	}

	logFilePath := filepath.Join(logDir, "install.log")
	file, err := os.OpenFile(logFilePath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
	if err != nil {
		return nil, fmt.Errorf("failed to open log file: %w", err)
	}

	multiWriter := io.MultiWriter(os.Stdout, file)
	l := log.New(multiWriter, "", 0)

	// Determine log level based on configuration.
	var level LogLevel
	switch cfg.LogLevel {
	case "ERROR":
		level = LevelError
	case "WARN":
		level = LevelWarn
	case "DEBUG":
		level = LevelDebug
	default:
		level = LevelInfo
	}

	// Override log level based on verbose and debug flags.
	if cfg.Debug {
		level = LevelDebug
	} else if cfg.Verbose {
		level = LevelInfo
	}

	return &Logger{
		logger:   l,
		logLevel: level,
		logFile:  file,
	}, nil
}

// CloseLogger closes the log file if it's open.
func CloseLogger() {
	if instance == nil {
		return
	}
	instance.mu.Lock()
	defer instance.mu.Unlock()

	if instance.logFile != nil {
		if err := instance.logFile.Close(); err != nil {
			fmt.Printf("Failed to close log file: %v\n", err)
		}
		instance.logFile = nil
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

func (l *Logger) logMessage(level LogLevel, message string, keyValues ...interface{}) {
	l.mu.RLock()
	defer l.mu.RUnlock()

	if l.logger == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: %s %s %v\n", level.String(), message, keyValues)
		return
	}

	if level > l.logLevel {
		return
	}

	// Ensure even number of keyValues.
	if len(keyValues)%2 != 0 {
		keyValues = append(keyValues, "MISSING_VALUE")
	}

	// Determine the color for the entire line based on log level.
	// For INFO, we leave it uncolored (set to empty) or you can choose a specific color.
	var lineColor string
	switch level {
	case LevelError:
		lineColor = colorRed
	case LevelWarn:
		lineColor = colorYellow
	case LevelDebug:
		lineColor = colorBlue
	case LevelInfo:
		lineColor = "" // No forced color for INFO; change to e.g. "\033[37m" for white if desired.
	default:
		lineColor = ""
	}

	ts := time.Now().Format("2006-01-02 15:04:05")
	var levelStr string
	if level == LevelInfo {
		levelStr = "INFO"
	} else {
		levelStr = level.String()
	}

	// Build the base log line.
	baseLine := fmt.Sprintf("[%s] %-5s %s", ts, levelStr, message)

	// Append key-value pairs.
	if len(keyValues) > 0 {
		// Use multiline formatting if there are many pairs.
		if len(keyValues)/2 > 4 {
			for i := 0; i < len(keyValues); i += 2 {
				key, ok := keyValues[i].(string)
				if !ok {
					key = fmt.Sprintf("%v", keyValues[i])
				}
				val := keyValues[i+1]
				baseLine += fmt.Sprintf("\n        %s: %v", key, val)
			}
		} else {
			for i := 0; i < len(keyValues); i += 2 {
				key, ok := keyValues[i].(string)
				if !ok {
					key = fmt.Sprintf("%v", keyValues[i])
				}
				val := keyValues[i+1]
				baseLine += fmt.Sprintf(" %s=%v", key, val)
			}
		}
	}

	// Prepend a separator for errors.
	if level == LevelError {
		baseLine = "\n----------------------------------------\n" + baseLine
	}

	// If a color is set, wrap the entire line in it.
	if lineColor != "" {
		baseLine = lineColor + baseLine + colorReset
	}

	l.logger.Println(baseLine)

	// Force flush to disk.
	if l.logFile != nil {
		_ = l.logFile.Sync()
	}
}

// StructuredEntry represents a single structured log entry for ManagedReport compatibility.
type StructuredEntry struct {
	Timestamp string `yaml:"timestamp"`
	Level     string `yaml:"level"`
	Message   string `yaml:"message"`
}

// structuredLog writes a structured log entry to CimianReport.yaml.
func (l *Logger) structuredLog(level LogLevel, message string) {
	l.mu.Lock()
	defer l.mu.Unlock()

	// Build structured entry
	entry := StructuredEntry{
		Timestamp: time.Now().Format(time.RFC3339),
		Level:     level.String(),
		Message:   message,
	}

	// Convert to YAML format
	yamlEntry := fmt.Sprintf("- timestamp: \"%s\"\n  level: \"%s\"\n  message: \"%s\"\n",
		entry.Timestamp, entry.Level, entry.Message)

	// Determine CimianReport YAML log path
	cimianReportPath := filepath.Join(`C:\ProgramData\ManagedInstalls\Logs`, "CimianReport.yaml")

	// Append structured log to CimianReport.yaml
	f, err := os.OpenFile(cimianReportPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		l.logger.Printf("Failed to write structured log: %v", err)
		return
	}
	defer f.Close()

	if _, err := f.WriteString(yamlEntry); err != nil {
		l.logger.Printf("Failed to write structured log entry: %v", err)
	}
}

// Info logs informational messages.
func Info(message string, keyValues ...interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: INFO %s %v\n", message, keyValues)
		return
	}
	instance.logMessage(LevelInfo, message, keyValues...)
	instance.structuredLog(LevelInfo, message)
}

// Debug logs debug messages.
func Debug(message string, keyValues ...interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: DEBUG %s %v\n", message, keyValues)
		return
	}
	instance.logMessage(LevelDebug, message, keyValues...)
	instance.structuredLog(LevelDebug, message)
}

// Warn logs warning messages.
func Warn(message string, keyValues ...interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: WARN %s %v\n", message, keyValues)
		return
	}
	instance.logMessage(LevelWarn, message, keyValues...)
	instance.structuredLog(LevelWarn, message)
}

// Error logs error messages.
func Error(message string, keyValues ...interface{}) {
	if instance == nil {
		fmt.Printf("LOGGING NOT INITIALIZED: ERROR %s %v\n", message, keyValues)
		return
	}
	instance.logMessage(LevelError, message, keyValues...)
	instance.structuredLog(LevelError, message)
}

// ReInit allows re-initializing the logger (e.g., after configuration reload).
// It closes the existing logger and creates a new one.
func ReInit(cfg *config.Configuration) error {
	instance.mu.Lock()
	defer instance.mu.Unlock()

	if instance.logFile != nil {
		if err := instance.logFile.Close(); err != nil {
			return fmt.Errorf("failed to close existing log file: %w", err)
		}
		instance.logFile = nil
	}

	newLogger, err := newLogger(cfg)
	if err != nil {
		return err
	}

	instance.logger = newLogger.logger
	instance.logLevel = newLogger.logLevel
	instance.logFile = newLogger.logFile

	return nil
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
	fmt.Printf("Running preflight script with verbosity level: %d\n", verbosity)
	scriptPath := `C:\Program Files\Cimian\preflight.ps1`
	return runScript(scriptPath, "Preflight", verbosity, logError)
}

// RunPostflight calls runScript for postflight.
func RunPostflight(verbosity int, logError func(string, ...interface{})) error {
	fmt.Printf("Running postflight script with verbosity level: %d\n", verbosity)
	scriptPath := `C:\Program Files\Cimian\postflight.ps1`
	return runScript(scriptPath, "Postflight", verbosity, logError)
}
