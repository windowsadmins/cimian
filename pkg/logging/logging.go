// pkg/logging/logging.go

package logging

import (
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"

	"github.com/windowsadmins/gorilla/pkg/config"
)

// Logger is the centralized logger instance.
var (
	logger  *log.Logger
	debug   bool
	verbose bool
	logFile *os.File
)

// Init initializes the logging based on the provided configuration.
// It sets up the logger with appropriate prefixes and outputs based on the log level.
func Init(cfg *config.Configuration) error {
	logLevel := cfg.LogLevel
	verbose = cfg.Verbose
	debug = cfg.Debug

	// Ensure log directory exists
	logDir := filepath.Join("C:\\ProgramData\\ManagedInstalls", "Logs")
	err := os.MkdirAll(logDir, 0755)
	if err != nil {
		return fmt.Errorf("failed to create log directory: %w", err)
	}

	// Open or create the log file
	logFilePath := filepath.Join(logDir, "gorilla.log")
	logFile, err = os.OpenFile(logFilePath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
	if err != nil {
		return fmt.Errorf("failed to open log file: %w", err)
	}

	// Create a multi-writer to write to both terminal and log file
	multiWriter := io.MultiWriter(os.Stdout, logFile)

	// Set logger based on log level
	switch logLevel {
	case "DEBUG":
		logger = log.New(multiWriter, "DEBUG: ", log.Ldate|log.Ltime|log.Lshortfile)
	case "INFO":
		logger = log.New(multiWriter, "INFO: ", log.Ldate|log.Ltime)
	case "WARN":
		logger = log.New(multiWriter, "WARN: ", log.Ldate|log.Ltime)
	case "ERROR":
		logger = log.New(multiWriter, "ERROR: ", log.Ldate|log.Ltime)
	default:
		logger = log.New(multiWriter, "INFO: ", log.Ldate|log.Ltime)
	}

	logger.Println("Logger initialized", "log_level", logLevel, "verbose", verbose, "debug", debug)
	return nil
}

// Info logs informational messages.
func Info(message string, keyValues ...interface{}) {
	logStructured("INFO", message, keyValues...)
}

// Debug logs debug messages.
func Debug(message string, keyValues ...interface{}) {
	if debug {
		logStructured("DEBUG", message, keyValues...)
	}
}

// Warn logs warning messages.
func Warn(message string, keyValues ...interface{}) {
	logStructured("WARN", message, keyValues...)
}

// Error logs error messages.
func Error(message string, keyValues ...interface{}) {
	logStructured("ERROR", message, keyValues...)
}

// LogDownloadStart logs the start of a download.
func LogDownloadStart(url string) {
	Info("Starting download", "url", url)
}

// LogDownloadComplete logs the completion of a download.
func LogDownloadComplete(dest string) {
	Info("Download complete", "destination", dest)
}

// LogVerification logs the verification status of a file.
func LogVerification(filePath, status string) {
	Info("Verification status", "file", filePath, "status", status)
}

// LogInstallStart logs the start of an installation.
func LogInstallStart(packageName, version string) {
	Info("Starting installation", "package", packageName, "version", version)
}

// LogInstallComplete logs the completion of an installation.
func LogInstallComplete(packageName, version, status string) {
	if status != "" {
		Info("Installation complete", "package", packageName, "version", version, "status", status)
	} else {
		Info("Installation complete", "package", packageName, "version", version)
	}
}

// LogErrorDuringInstall logs errors that occur during the installation process.
func LogErrorDuringInstall(err error, context string) {
	Error("Installation error", "context", context, "error", err.Error())
}

// logStructured formats and logs the message with key-value pairs.
func logStructured(level, message string, keyValues ...interface{}) {
	// Ensure even number of keyValues
	if len(keyValues)%2 != 0 {
		// Append a placeholder for the missing value
		keyValues = append(keyValues, "MISSING_VALUE")
	}

	// Build the key-value string
	kvPairs := ""
	for i := 0; i < len(keyValues); i += 2 {
		key, ok := keyValues[i].(string)
		if !ok {
			// If the key is not a string, use a placeholder
			key = fmt.Sprintf("NON_STRING_KEY_%d", i)
		}
		value := keyValues[i+1]
		kvPairs += fmt.Sprintf("%s=%v ", key, value)
	}

	// Trim the trailing space
	if len(kvPairs) > 0 {
		kvPairs = kvPairs[:len(kvPairs)-1]
	}

	// Log the structured message
	logger.Println(fmt.Sprintf("%s: %s %s", level, message, kvPairs))
}

// CloseLogger performs necessary cleanup for the logger.
// Closes the log file if it was opened.
func CloseLogger() {
	if logFile != nil {
		err := logFile.Close()
		if err != nil {
			fmt.Printf("Failed to close log file: %v\n", err)
		}
	}
}
