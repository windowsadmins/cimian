package main

import (
    "fmt"
    "os"
    "os/signal"
    "syscall"
    "path/filepath"
    "time"
    "github.com/rodchristiansen/gorilla/pkg/catalog"
    "github.com/rodchristiansen/gorilla/pkg/config"
    "github.com/rodchristiansen/gorilla/pkg/download"
    "github.com/rodchristiansen/gorilla/pkg/pkginfo"
    "github.com/rodchristiansen/gorilla/pkg/logging"
    "github.com/rodchristiansen/gorilla/pkg/manifest"
    "github.com/rodchristiansen/gorilla/pkg/process"
    "github.com/rodchristiansen/gorilla/pkg/report"
    "golang.org/x/sys/windows"
)

func main() {
    logging.InitLogger()
    defer logging.CloseLogger()

    // Handle signals for clean up
    signalChan := make(chan os.Signal, 1)
    signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
    go func() {
        <-signalChan
        fmt.Println("Signal received, exiting gracefully...")
        os.Exit(1)
    }()

    // Get the configuration
    cfg := config.Get()

    // Check for admin privileges
    admin, err := adminCheck()
    if err != nil || !admin {
        fmt.Println("Administrative access is required to run updates. Please run as an administrator.")
        os.Exit(1)
    }

    // Create the cache directory if needed
    err = os.MkdirAll(filepath.Clean(cfg.CachePath), 0755)
    if err != nil {
        logging.LogError(err, "Failed to create cache directory")
        os.Exit(1)
    }

    // Detect idle time to decide when to perform updates
    idleTime := getIdleSeconds()
    if idleTime < 300 {
        fmt.Println("System has not been idle for long enough, deferring updates.")
        os.Exit(0)
    }

    // Run the update process
    fmt.Println("Running software update checks...")
    runUpdates(cfg)

    fmt.Println("Software updates completed.")
}

// getIdleSeconds uses the Windows API to get the system's idle time in seconds
func getIdleSeconds() int {
    var lastInput windows.LastInputInfo
    lastInput.Size = uint32(unsafe.Sizeof(lastInput))
    err := windows.GetLastInputInfo(&lastInput)
    if err != nil {
        return 0
    }
    currentTime := windows.GetTickCount()
    return int((currentTime - lastInput.Time) / 1000) // Convert milliseconds to seconds
}

// runUpdates checks for updates and processes .msi, .exe, .ps1, and .nupkg installations
func runUpdates(cfg *config.Config) {
    // Placeholder to retrieve manifest and available updates
    manifest := manifest.Get() // Fetch the manifest

    for _, item := range manifest.Items {
        fmt.Printf("Checking for updates: %s (%s)\n", item.Name, item.Version)

        if item.NeedsUpdate {
            fmt.Printf("Installing update for %s...\n", item.Name)
            installUpdate(item)
        }
    }
}

// installUpdate handles the installation of different types of packages
func installUpdate(item pkginfo.PkgInfo) {
    switch filepath.Ext(item.Installer) {
    case ".msi":
        fmt.Printf("Installing MSI: %s\n", item.Installer)
        process.InstallMSI(item.Installer)
    case ".exe":
        fmt.Printf("Running EXE: %s\n", item.Installer)
        process.RunEXE(item.Installer)
    case ".ps1":
        fmt.Printf("Executing PowerShell script: %s\n", item.Installer)
        process.RunPowerShellScript(item.Installer)
    case ".nupkg":
        fmt.Printf("Installing NuGet package: %s\n", item.Installer)
        process.InstallNuGetPackage(item.Installer)
    default:
        fmt.Printf("Unsupported installer type for %s\n", item.Name)
    }
}
