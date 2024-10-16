package main

import (
    "flag"
    "fmt"
    "os"
    "os/signal"
    "syscall"
    "path/filepath"
    "time"
    "unsafe"

    "github.com/rodchristiansen/gorilla/pkg/config"
    "github.com/rodchristiansen/gorilla/pkg/logging"
    "github.com/rodchristiansen/gorilla/pkg/manifest"
    "github.com/rodchristiansen/gorilla/pkg/pkginfo"
    "github.com/rodchristiansen/gorilla/pkg/process"
    "golang.org/x/sys/windows"
)

func main() {
    // Load configuration
    cfg, err := config.LoadConfig()
    if err != nil {
        fmt.Println("Failed to load configuration:", err)
        os.Exit(1)
    }

    // Initialize logger
    logging.InitLogger(cfg)
    defer logging.CloseLogger()

    // Handle system signals for cleanup
    signalChan := make(chan os.Signal, 1)
    signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
    go func() {
        <-signalChan
        fmt.Println("Signal received, exiting gracefully...")
        os.Exit(1)
    }()

    // Check for admin privileges
    admin, err := adminCheck()
    if err != nil || !admin {
        fmt.Println("Administrative access is required. Please run as an administrator.")
        os.Exit(1)
    }

    // Create the cache directory if needed
    err = os.MkdirAll(filepath.Clean(cfg.CachePath), 0755)
    if err != nil {
        logging.LogError(err, "Failed to create cache directory")
        os.Exit(1)
    }

    // Check system idle time
    idleTime := getIdleSeconds()
    if idleTime < 300 {
        fmt.Println("System has not been idle long enough, deferring updates.")
        os.Exit(0)
    }

    // Run the update process
    manifestItems, err := manifest.Get(*cfg)
    if err != nil {
        logging.Error("Failed to retrieve manifest:", err)
        os.Exit(1)
    }

    for _, item := range manifestItems {
        fmt.Printf("Checking for updates: %s (%s)\n", item.Name, item.Version)
        if item.NeedsUpdate {
            fmt.Printf("Installing update for %s...\n", item.Name)
            installUpdate(item)
        }
    }

    fmt.Println("Software updates completed.")
}

// adminCheck checks if the program is running with admin privileges.
func adminCheck() (bool, error) {
    if flag.Lookup("test.v") != nil {
        return false, nil
    }

    var adminSid *windows.SID
    adminSid, err := windows.CreateWellKnownSid(windows.WinBuiltinAdministratorsSid, nil)
    if err != nil {
        return false, err
    }

    token := windows.Token(0)
    isAdmin, err := token.IsMember(adminSid)
    if err != nil {
        return false, err
    }

    return isAdmin, nil
}

// getIdleSeconds uses the Windows API to get the system's idle time in seconds.
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
func runUpdates(cfg *config.Configuration) {
    manifest, err := manifest.Get(cfg)
    if err != nil {
        logging.Error("Failed to retrieve manifest:", err)
        os.Exit(1)
    }

    for _, item := range manifest.Items {
        fmt.Printf("Checking for updates: %s (%s)\n", item.Name, item.Version)

        if item.NeedsUpdate {
            fmt.Printf("Installing update for %s...\n", item.Name)
            installUpdate(item)
        }
    }
}

// installUpdate installs a package based on its type.
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
