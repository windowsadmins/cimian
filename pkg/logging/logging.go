package logging

import (
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/windowsadmins/gorilla/pkg/config"
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

	logFilePath := filepath.Join(logDir, "gorilla.log")
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

// logMessage logs a message at the specified level with optional key-value pairs.
func (l *Logger) logMessage(level LogLevel, message string, keyValues ...interface{}) {
	l.mu.RLock()
	defer l.mu.RUnlock()

	if l.logger == nil {
		// Fallback to stdout if logger is not initialized.
		fmt.Printf("LOGGING NOT INITIALIZED: %s %s %v\n", level.String(), message, keyValues)
		return
	}

	// If the message's level is higher (less severe) than the configured level, skip it.
	if level > l.logLevel {
		return
	}

	// Build a timestamp prefix like "[2025-01-15 09:28:54] INFO"
	ts := time.Now().Format("2006-01-02 15:04:05")
	prefix := fmt.Sprintf("[%s] %-5s", ts, level.String())

	// If not debug, we won't print the keyValues. If debug, we parse them.
	var kvPairs string
	if level == LevelDebug && len(keyValues) > 0 {
		// Ensure even number of keyValues.
		if len(keyValues)%2 != 0 {
			keyValues = append(keyValues, "MISSING_VALUE")
		}
		for i := 0; i < len(keyValues); i += 2 {
			key, _ := keyValues[i].(string)
			val := keyValues[i+1]
			kvPairs += fmt.Sprintf(" %s=%v", key, val)
		}
	}

	// Construct final log line
	logLine := prefix
	if message != "" {
		logLine += " " + message
	}
	if kvPairs != "" {
		logLine += kvPairs
	}

	l.logger.Println(logLine)

	// Force flush to disk (in case of crash)
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
