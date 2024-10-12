
package logging

import (
    "log"
    "os"
    "path/filepath"
    "fmt"
)

const (
    LogsPath = `C:\ProgramData\ManagedInstalls\Logs`
)

var logFile *os.File

// InitLogger initializes the logger and creates the log file
func InitLogger() {
    os.MkdirAll(LogsPath, 0755)
    var err error
    logFile, err = os.OpenFile(filepath.Join(LogsPath, "ManagedSoftwareUpdate.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        log.Fatalf("Failed to create log file: %v", err)
    }

    log.SetOutput(logFile)
    log.Println("Logger initialized.")
}

// LogDownloadStart logs the beginning of a download
func LogDownloadStart(url string) {
    log.Printf("[DOWNLOAD START] URL: %s", url)
}

// LogDownloadComplete logs the completion of a download
func LogDownloadComplete(dest string) {
    log.Printf("[DOWNLOAD COMPLETE] File saved to: %s", dest)
}

// LogVerification logs the verification process of a package
func LogVerification(filePath string, status string) {
    log.Printf("[VERIFICATION] File: %s Status: %s", filePath, status)
}

// LogInstallStart logs the beginning of an installation
func LogInstallStart(packageName, version string) {
    log.Printf("[INSTALL START] Package: %s Version: %s", packageName, version)
}

// LogInstallComplete logs the completion of an installation
func LogInstallComplete(packageName, version string, status string) {
    log.Printf("[INSTALL COMPLETE] Package: %s Version: %s Status: %s", packageName, version, status)
}

// LogError logs errors during the installation process
func LogError(err error, context string) {
    log.Printf("[ERROR] %s: %v", context, err)
}

// CloseLogger closes the log file
func CloseLogger() {
    if logFile != nil {
        err := logFile.Close()
        if err != nil {
            fmt.Printf("Failed to close log file: %v\n", err)
        }
    }
}
