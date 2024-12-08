package logging

import (
	"fmt"
	"log"
	"os"

	"github.com/windowsadmins/gorilla/pkg/config"
)

// logger is the centralized logger instance.
var (
	logger  *log.Logger
	debug   bool
	verbose bool
)

// Init initializes the logging based on the provided configuration.
// It sets up the logger with appropriate prefixes and outputs based on the log level.
func Init(cfg *config.Configuration) error {
	logLevel := cfg.LogLevel
	verbose = cfg.Verbose
	debug = cfg.Debug

	switch logLevel {
	case "DEBUG":
		logger = log.New(os.Stdout, "DEBUG: ", log.Ldate|log.Ltime|log.Lshortfile)
	case "INFO":
		logger = log.New(os.Stdout, "INFO: ", log.Ldate|log.Ltime)
	case "WARN":
		logger = log.New(os.Stdout, "WARN: ", log.Ldate|log.Ltime)
	case "ERROR":
		logger = log.New(os.Stderr, "ERROR: ", log.Ldate|log.Ltime)
	default:
		logger = log.New(os.Stdout, "INFO: ", log.Ldate|log.Ltime)
	}

	return nil
}

// Info logs informational messages based on verbosity.
// It accepts a message and an optional list of key-value pairs.
func Info(message string, keyValues ...interface{}) {
	if verbose || debug {
		logStructured("INFO", message, keyValues...)
	}
}

// Debug logs debug messages.
// It accepts a message and an optional list of key-value pairs.
func Debug(message string, keyValues ...interface{}) {
	if debug {
		logStructured("DEBUG", message, keyValues...)
	}
}

// Warn logs warning messages.
// It accepts a message and an optional list of key-value pairs.
func Warn(message string, keyValues ...interface{}) {
	logStructured("WARN", message, keyValues...)
}

// Error logs error messages.
// It accepts a message and an optional list of key-value pairs.
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
// It ensures that keyValues are in key-value pair format.
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
	kvPairs = kvPairs[:len(kvPairs)-1]

	// Log the structured message
	logger.Println(fmt.Sprintf("%s: %s %s", level, message, kvPairs))
}

// CloseLogger performs any necessary cleanup for the logger.
// Currently, there are no resources to clean up, but this function is provided for future enhancements.
func CloseLogger() {
	// Placeholder for future cleanup logic, such as closing file handles if logging to files.
}