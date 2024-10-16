package main

import (
    "flag"
    "fmt"
    "os"
    "os/signal"
    "path/filepath"
    "unsafe"
    "syscall"
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
    logging.InitLogger(*cfg)
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
    cachePath := cfg.CachePath
    err = os.MkdirAll(filepath.Clean(cachePath), 0755)
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
    manifestItems, catalogsMap := manifest.Get(*cfg)
    for _, item := range manifestItems {
        fmt.Printf("Checking for updates: %s\n", item.Name)
        if needsUpdate(item) {
            fmt.Printf("Installing update for %s...\n", item.Name)
            installUpdate(item)
        }
    }

    fmt.Println("Cleaning up old cache...")
    process.CleanUp(cachePath)

    fmt.Println("Software updates completed.")
}

// adminCheck checks if the program is running with admin privileges.
func adminCheck() (bool, error) {
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
type LASTINPUTINFO struct {
    CbSize uint32
    DwTime uint32
}

func getIdleSeconds() int {
    lastInput := LASTINPUTINFO{
        CbSize: uint32(unsafe.Sizeof(LASTINPUTINFO{})),
    }
    ret, _, err := syscall.NewLazyDLL("user32.dll").NewProc("GetLastInputInfo").Call(uintptr(unsafe.Pointer(&lastInput)))
    if ret == 0 {
        fmt.Printf("Error getting last input info: %v\n", err)
        return 0
    }

    tickCount, _, err := syscall.NewLazyDLL("kernel32.dll").NewProc("GetTickCount").Call()
    if tickCount == 0 {
        fmt.Printf("Error getting tick count: %v\n", err)
        return 0
    }

    idleTime := (uint32(tickCount) - lastInput.DwTime) / 1000
    return int(idleTime)
}

// needsUpdate determines if the item needs to be updated.
func needsUpdate(item manifest.Item) bool {
    installedVersion, err := pkginfo.GetInstalledVersion(item.Name)
    if err != nil {
        // Not installed or error occurred; assume update is needed
        return true
    }
    return installedVersion != item.Version
}

// installUpdate installs a package based on its type.
func installUpdate(item manifest.Item) {
    var err error
    switch filepath.Ext(item.InstallerLocation) {
    case ".msi":
        fmt.Printf("Installing MSI: %s\n", item.InstallerLocation)
        err = process.InstallMSI(item.InstallerLocation)
    case ".exe":
        fmt.Printf("Running EXE: %s\n", item.InstallerLocation)
        err = process.RunEXE(item.InstallerLocation)
    case ".ps1":
        fmt.Printf("Executing PowerShell script: %s\n", item.InstallerLocation)
        err = process.RunPowerShellScript(item.InstallerLocation)
    case ".nupkg":
        fmt.Printf("Installing NuGet package: %s\n", item.InstallerLocation)
        err = process.InstallNuGetPackage(item.InstallerLocation)
    default:
        fmt.Printf("Unsupported installer type for %s\n", item.InstallerLocation)
        return
    }

    if err != nil {
        fmt.Printf("Failed to install %s: %v\n", item.Name, err)
    } else {
        fmt.Printf("Successfully installed %s\n", item.Name)
    }
}
