package logging

import (
    "fmt"
    "io"
    "log"
    "os"
    "path/filepath"

    "github.com/windowsadmins/gorilla/pkg/config"
)

const (
    DefaultLogsPath = `C:\ProgramData\ManagedInstalls\Logs`
)

var (
    // File handles
    managedSoftwareUpdateLog *os.File
    installLog               *os.File
    warningsLog              *os.File
    singleLogFile            *os.File

    // Configuration flags
    debug     bool
    verbose   bool
    checkonly bool

    // Mode flags
    multiLogMode bool
)

// InitLogger initializes the logger. It supports both multi-log mode (ManagedSoftwareUpdate.log, Install.log, Warnings.log)
// and single-log mode (gorilla.log), depending on configuration.
// If cfg.AppDataPath is set, it will use a single gorilla.log file under that path.
// Otherwise, it uses the multi-log structure under DefaultLogsPath.
func InitLogger(cfg config.Configuration) {
    debug = cfg.Debug
    verbose = cfg.Verbose
    checkonly = cfg.CheckOnly

    // If AppDataPath is provided, assume single log mode. Otherwise, use multi-log mode.
    if cfg.AppDataPath != "" {
        initSingleLog(cfg)
    } else {
        initMultiLogs(cfg)
    }

    // Set default logger flags and a default file output.
    if multiLogMode {
        // Default to ManagedSoftwareUpdate.log
        log.SetOutput(managedSoftwareUpdateLog)
    } else {
        // Default to single log file
        log.SetOutput(singleLogFile)
    }
    log.SetFlags(log.Ldate | log.Lmicroseconds)
    log.Println("Logger initialized.")
}

func initSingleLog(cfg config.Configuration) {
    multiLogMode = false
    logPath := filepath.Join(cfg.AppDataPath, "gorilla.log")

    err := os.MkdirAll(filepath.Dir(logPath), 0755)
    if err != nil {
        panic(fmt.Sprintf("Unable to create directory: %s - %v", filepath.Dir(logPath), err))
    }

    singleLogFile, err = os.OpenFile(logPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        panic(fmt.Sprintf("Unable to open file: %s - %v", logPath, err))
    }
}

func initMultiLogs(cfg config.Configuration) {
    multiLogMode = true

    // Ensure the default logs directory
    err := os.MkdirAll(DefaultLogsPath, 0755)
    if err != nil {
        log.Fatalf("Failed to create logs directory: %v", err)
    }

    managedSoftwareUpdateLog, err = os.OpenFile(filepath.Join(DefaultLogsPath, "ManagedSoftwareUpdate.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        log.Fatalf("Failed to create ManagedSoftwareUpdate.log: %v", err)
    }

    installLog, err = os.OpenFile(filepath.Join(DefaultLogsPath, "Install.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        log.Fatalf("Failed to create Install.log: %v", err)
    }

    warningsLog, err = os.OpenFile(filepath.Join(DefaultLogsPath, "Warnings.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        log.Fatalf("Failed to create Warnings.log: %v", err)
    }
}

// Debug logs a debug message if debug is enabled.
func Debug(logStrings ...interface{}) {
    if !debug {
        return
    }

    log.SetPrefix("DEBUG: ")
    if verbose {
        fmt.Println(logStrings...)
    }

    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(managedSoftwareUpdateLog)
    } else {
        log.SetOutput(singleLogFile)
    }
    log.Println(logStrings...)
}

// Info logs an informational message if verbose is enabled.
func Info(logStrings ...interface{}) {
    log.SetPrefix("INFO: ")
    if verbose {
        fmt.Println(logStrings...)
    }

    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(managedSoftwareUpdateLog)
    } else {
        log.SetOutput(singleLogFile)
    }
    log.Println(logStrings...)
}

// Warn logs a warning message. Always logged to disk. In multi-log mode, goes to Warnings.log.
func Warn(logStrings ...interface{}) {
    log.SetPrefix("WARNING: ")
    if verbose {
        fmt.Println(logStrings...)
    }

    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(warningsLog)
    } else {
        log.SetOutput(singleLogFile)
    }
    log.Println(logStrings...)
}

// Error logs an error message. In multi-log mode, goes to Warnings.log; in single mode, goes to single log.
func Error(logStrings ...interface{}) {
    log.SetPrefix("ERROR: ")
    if verbose {
        fmt.Println(logStrings...)
    }

    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(warningsLog)
    } else {
        log.SetOutput(singleLogFile)
    }
    log.Println(logStrings...)
}

// LogDownloadStart logs the beginning of a download.
func LogDownloadStart(url string) {
    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(managedSoftwareUpdateLog)
    } else {
        log.SetOutput(singleLogFile)
    }

    log.Printf("[DOWNLOAD START] URL: %s", url)
}

// LogDownloadComplete logs the completion of a download.
func LogDownloadComplete(dest string) {
    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(managedSoftwareUpdateLog)
    } else {
        log.SetOutput(singleLogFile)
    }

    log.Printf("[DOWNLOAD COMPLETE] File saved to: %s", dest)
}

// LogVerification logs file verification status.
func LogVerification(filePath string, status string) {
    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(managedSoftwareUpdateLog)
    } else {
        log.SetOutput(singleLogFile)
    }
    log.Printf("[VERIFICATION] File: %s Status: %s", filePath, status)
}

// LogInstallStart logs the beginning of an installation (if multiLogMode).
func LogInstallStart(packageName, version string) {
    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(installLog)
    } else {
        log.SetOutput(singleLogFile)
    }
    log.Printf("[INSTALL START] Package: %s Version: %s", packageName, version)
}

// LogInstallComplete logs the completion of an installation (if multiLogMode).
func LogInstallComplete(packageName, version string, status string) {
    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(installLog)
    } else {
        log.SetOutput(singleLogFile)
    }
    log.Printf("[INSTALL COMPLETE] Package: %s Version: %s Status: %s", packageName, version, status)
}

// LogError logs errors during the installation process to warnings (in multi mode) or single log.
func LogError(err error, context string) {
    if checkonly {
        return
    }

    if multiLogMode {
        log.SetOutput(warningsLog)
    } else {
        log.SetOutput(singleLogFile)
    }
    log.Printf("[ERROR] %s: %v", context, err)
}

// CloseLogger closes all open log files.
func CloseLogger() {
    // If in single log mode
    if singleLogFile != nil {
        if err := singleLogFile.Close(); err != nil {
            fmt.Printf("Failed to close gorilla.log: %v\n", err)
        }
    }

    // If in multi log mode
    if managedSoftwareUpdateLog != nil {
        if err := managedSoftwareUpdateLog.Close(); err != nil {
            fmt.Printf("Failed to close ManagedSoftwareUpdate.log: %v\n", err)
        }
    }
    if installLog != nil {
        if err := installLog.Close(); err != nil {
            fmt.Printf("Failed to close Install.log: %v\n", err)
        }
    }
    if warningsLog != nil {
        if err := warningsLog.Close(); err != nil {
            fmt.Printf("Failed to close Warnings.log: %v\n", err)
        }
    }
}