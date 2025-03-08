// pkg/logging/logging.go - Logging package for Cimian

package logging

import (
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"runtime"
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
	logger := log.New(multiWriter, "", 0)

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

	// Log initialization details.
	logger.Printf("Logger initialized log_level=%s verbose=%v debug=%v\n", cfg.LogLevel, cfg.Verbose, cfg.Debug)

	return &Logger{
		logger:   logger,
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

	// Check if this is a special message that should have the entire line colored
	isSuccessfulInstall := level == LevelInfo && strings.HasPrefix(message, "Installed item successfully")
	isVersionWarning := level == LevelWarn && strings.Contains(message, "Refusing to install older version")

	// Pick a color based on log level.
	prefixColor := ""
	switch level {
	case LevelError:
		prefixColor = colorRed
	case LevelWarn:
		prefixColor = colorYellow
	case LevelDebug:
		prefixColor = colorBlue
	case LevelInfo:
		prefixColor = "" // default for INFO
	default:
		prefixColor = ""
	}

	ts := time.Now().Format("2006-01-02 15:04:05")

	// Format the prefix differently based on whether this is a special message
	var prefix string
	if isSuccessfulInstall || isVersionWarning {
		// For special messages, we'll add color at the start but not reset until the end
		prefix = fmt.Sprintf("%s[%s] %-5s", prefixColor, ts, level.String())
	} else {
		// For other messages, reset the color after the prefix
		prefix = fmt.Sprintf("%s[%s] %-5s%s", prefixColor, ts, level.String(), colorReset)
	}

	// If there are many key–value pairs, use multiline formatting.
	var kvPairs string
	threshold := 4 // adjust threshold as needed
	if len(keyValues) > 0 {
		if len(keyValues)/2 > threshold {
			// Multiline formatting for readability.
			for i := 0; i < len(keyValues); i += 2 {
				key, ok := keyValues[i].(string)
				if !ok {
					key = fmt.Sprintf("%v", keyValues[i])
				}
				val := keyValues[i+1]
				kvPairs += fmt.Sprintf("\n        %s: %v", key, val)
			}
		} else {
			// Inline formatting.
			for i := 0; i < len(keyValues); i += 2 {
				key, ok := keyValues[i].(string)
				if !ok {
					key = fmt.Sprintf("%v", keyValues[i])
				}
				val := keyValues[i+1]
				kvPairs += fmt.Sprintf(" %s=%v", key, val)
			}
		}
	}

	// Prepend a separator for errors.
	separator := ""
	if level == LevelError {
		separator = "\n----------------------------------------\n"
	}

	logLine := separator + prefix
	if message != "" {
		logLine += " " + message
	}
	if kvPairs != "" {
		logLine += kvPairs
	}

	// For special messages, add the color reset at the end of the line
	if isSuccessfulInstall || isVersionWarning {
		logLine += colorReset
	}

	l.logger.Println(logLine)

	// Force flush to disk.
	if l.logFile != nil {
		_ = l.logFile.Sync()
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

// New creates a new Logger instance
func New(verbose bool) *Logger {
	enableColors()
	flags := log.Ldate | log.Ltime
	if verbose {
		flags |= log.Lshortfile
	}

	output := os.Stdout
	if !verbose {
		output = os.Stderr
	}

	return &Logger{
		logger: log.New(output, "", flags),
	}
}

// SetOutput changes the output destination
func (l *Logger) SetOutput(w io.Writer) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.logger.SetOutput(w)
}

// colorPrintf prints a colored message
func (l *Logger) colorPrintf(color, format string, v ...interface{}) {
	l.mu.RLock()
	defer l.mu.RUnlock()

	ts := time.Now().Format("2006-01-02 15:04:05")
	msg := fmt.Sprintf(format, v...)
	l.logger.Printf("%s[%s] %s%s", color, ts, msg, colorReset)
}

// Printf prints a regular message
func (l *Logger) Printf(format string, v ...interface{}) {
	l.mu.RLock()
	defer l.mu.RUnlock()
	ts := time.Now().Format("2006-01-02 15:04:05")
	msg := fmt.Sprintf(format, v...)
	l.logger.Printf("[%s] %s", ts, msg)
}

// Info prints an informational message (instance method counterpart to the package-level Info)
func (l *Logger) Info(format string, v ...interface{}) {
	l.Printf(format, v...)
}

// Success prints a success message in green
func (l *Logger) Success(format string, v ...interface{}) {
	l.colorPrintf(colorGreen, format, v...)
}

// Error prints an error message in red
func (l *Logger) Error(format string, v ...interface{}) {
	l.colorPrintf(colorRed, format, v...)
}

// Warning prints a warning message in yellow
func (l *Logger) Warning(format string, v ...interface{}) {
	l.colorPrintf(colorYellow, format, v...)
}

// Debug prints a debug message in blue
func (l *Logger) Debug(format string, v ...interface{}) {
	l.colorPrintf(colorBlue, format, v...)
}

// Fatal prints an error message in red and exits
func (l *Logger) Fatal(format string, v ...interface{}) {
	l.Error(format, v...)
	os.Exit(1)
}
