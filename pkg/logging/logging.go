
package logging

import (
    "fmt"
    "log"
    "os"
    "path/filepath"

    "github.com/rodchristiansen/gorilla/pkg/config"
)

const (
    LogsPath = `C:\ProgramData\ManagedInstalls\Logs`
)

var (
    managedSoftwareUpdateLog *os.File
    installLog               *os.File
    warningsLog              *os.File
    debug                    bool
    verbose                  bool
    checkonly                bool
)

// InitLogger initializes the logger and creates the log files
func InitLogger(cfg config.Configuration) {
    os.MkdirAll(LogsPath, 0755)

    // Open ManagedSoftwareUpdate.log
    var err error
    managedSoftwareUpdateLog, err = os.OpenFile(filepath.Join(LogsPath, "ManagedSoftwareUpdate.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        log.Fatalf("Failed to create ManagedSoftwareUpdate.log: %v", err)
    }

    // Open Install.log
    installLog, err = os.OpenFile(filepath.Join(LogsPath, "Install.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        log.Fatalf("Failed to create Install.log: %v", err)
    }

    // Open Warnings.log
    warningsLog, err = os.OpenFile(filepath.Join(LogsPath, "Warnings.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        log.Fatalf("Failed to create Warnings.log: %v", err)
    }

    log.SetOutput(managedSoftwareUpdateLog)
    log.SetFlags(log.Ldate | log.Lmicroseconds)
    log.Println("Logger initialized.")

    // Set logging verbosity levels
    debug = cfg.Debug
    verbose = cfg.Verbose
    checkonly = cfg.CheckOnly
}

// Debug logs a message with a DEBUG prefix if debug is enabled
func Debug(logStrings ...interface{}) {
    log.SetPrefix("DEBUG: ")
    if debug {
        fmt.Println(logStrings...)
        if checkonly {
            return
        }
        log.SetOutput(managedSoftwareUpdateLog)
        log.Println(logStrings...)
    }
}

// Info logs a message with an INFO prefix if verbose is enabled
func Info(logStrings ...interface{}) {
    log.SetPrefix("INFO: ")
    if verbose {
        fmt.Println(logStrings...)
    }
    if checkonly {
        return
    }
    log.SetOutput(managedSoftwareUpdateLog)
    log.Println(logStrings...)
}

// Error logs a message with an ERROR prefix and writes to Warnings.log
func Error(logStrings ...interface{}) {
    log.SetPrefix("ERROR: ")
    if checkonly {
        return
    }
    log.SetOutput(warningsLog)
    log.Println(logStrings...)
}

// LogDownloadStart logs the beginning of a download to ManagedSoftwareUpdate.log
func LogDownloadStart(url string) {
    log.SetOutput(managedSoftwareUpdateLog)
    log.Printf("[DOWNLOAD START] URL: %s", url)
}

// LogDownloadComplete logs the completion of a download to ManagedSoftwareUpdate.log
func LogDownloadComplete(dest string) {
    log.SetOutput(managedSoftwareUpdateLog)
    log.Printf("[DOWNLOAD COMPLETE] File saved to: %s", dest)
}

// LogVerification logs the verification process of a package to ManagedSoftwareUpdate.log
func LogVerification(filePath string, status string) {
    log.SetOutput(managedSoftwareUpdateLog)
    log.Printf("[VERIFICATION] File: %s Status: %s", filePath, status)
}

// LogInstallStart logs the beginning of an installation to Install.log
func LogInstallStart(packageName, version string) {
    log.SetOutput(installLog)
    log.Printf("[INSTALL START] Package: %s Version: %s", packageName, version)
}

// LogInstallComplete logs the completion of an installation to Install.log
func LogInstallComplete(packageName, version string, status string) {
    log.SetOutput(installLog)
    log.Printf("[INSTALL COMPLETE] Package: %s Version: %s Status: %s", packageName, version, status)
}

// LogError logs errors during the installation process to Warnings.log
func LogError(err error, context string) {
    log.SetOutput(warningsLog)
    log.Printf("[ERROR] %s: %v", context, err)
}

// CloseLogger closes all log files
func CloseLogger() {
    if managedSoftwareUpdateLog != nil {
        err := managedSoftwareUpdateLog.Close()
        if err != nil {
            fmt.Printf("Failed to close ManagedSoftwareUpdate.log: %v\n", err)
        }
    }
    if installLog != nil {
        err := installLog.Close()
        if err != nil {
            fmt.Printf("Failed to close Install.log: %v\n", err)
        }
    }
    if warningsLog != nil {
        err := warningsLog.Close()
        if err != nil {
            fmt.Printf("Failed to close Warnings.log: %v\n", err)
        }
    }
}
