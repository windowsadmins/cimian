// cmd/managedsoftwareupdate/main.go

package main

import (
    "flag"
    "fmt"
    "os"
    "os/signal"
    "path/filepath"
    "syscall"
    "unsafe"

    "github.com/rodchristiansen/gorilla/pkg/catalog"
    "github.com/rodchristiansen/gorilla/pkg/config"
    "github.com/rodchristiansen/gorilla/pkg/installer"
    "github.com/rodchristiansen/gorilla/pkg/logging"
    "github.com/rodchristiansen/gorilla/pkg/manifest"
    "github.com/rodchristiansen/gorilla/pkg/preflight"
    "github.com/rodchristiansen/gorilla/pkg/process"
    "github.com/rodchristiansen/gorilla/pkg/status"

    "golang.org/x/sys/windows"
    "gopkg.in/yaml.v3"
)

var verbosity int

func main() {
    // Define the --show-config and -v flags
    showConfig := flag.Bool("show-config", false, "Display the current configuration and exit.")
    flag.IntVar(&verbosity, "v", 0, "Increase verbosity with multiple -v flags.")
    flag.Parse()

    // Load configuration
    cfg, err := config.LoadConfig()
    if err != nil {
        logError("Failed to load configuration: %v", err)
        os.Exit(1)
    }

    if *showConfig {
        // Pretty-print the configuration as YAML
        cfgYaml, err := yaml.Marshal(cfg)
        if err != nil {
            logError("Failed to marshal configuration: %v", err)
            os.Exit(1)
        }
        fmt.Printf("Current Configuration:\n%s\n", cfgYaml)
        os.Exit(0)
    }

    // Initialize logger
    logging.InitLogger(*cfg)
    defer logging.CloseLogger()

    logInfo("Initializing...")

    // Handle system signals for cleanup
    signalChan := make(chan os.Signal, 1)
    signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
    go func() {
        <-signalChan
        logInfo("Signal received, exiting gracefully...")
        os.Exit(1)
    }()

    // Check for admin privileges
    admin, err := adminCheck()
    if err != nil || !admin {
        logError("Administrative access is required. Please run as an administrator.")
        os.Exit(1)
    }

    // Run the preflight script
    err = preflight.RunPreflight(verbosity, logInfo, logError)
    if err != nil {
        logError("Preflight script failed: %v", err)
        os.Exit(1)
    }

    // Reload configuration in case the preflight script modified it
    cfg, err = config.LoadConfig()
    if err != nil {
        logError("Failed to reload configuration after preflight: %v", err)
        os.Exit(1)
    }

    // Create the cache directory if needed
    cachePath := cfg.CachePath
    err = os.MkdirAll(filepath.Clean(cachePath), 0755)
    if err != nil {
        logError("Failed to create cache directory: %v", err)
        os.Exit(1)
    }

    // Check system idle time
    idleTime := getIdleSeconds()
    if idleTime < 0 {
        logInfo("Running updates immediately, ignoring idle time.")
    }

    // Run the update process
    manifestItems, _ := manifest.Get(*cfg)
    for _, item := range manifestItems {
        logInfo("Checking for updates: %s", item.Name)
        if needsUpdate(item, cfg) {
            logInfo("Installing update for %s...", item.Name)
            installUpdate(item, cfg)
        }
    }

    logInfo("Cleaning up old cache...")
    process.CleanUp(cachePath)

    logInfo("Software updates completed.")
}

func logError(message string, args ...interface{}) {
    fmt.Fprintf(os.Stderr, message+"\n", args...)
}

func logInfo(message string, args ...interface{}) {
    if verbosity >= 1 {
        fmt.Printf(message+"\n", args...)
    }
}

func logVerbose(message string, args ...interface{}) {
    if verbosity >= 2 {
        fmt.Printf(message+"\n", args...)
    }
}

func logVeryVerbose(message string, args ...interface{}) {
    if verbosity >= 3 {
        fmt.Printf(message+"\n", args...)
    }
}

// adminCheck checks if the program is running with admin privileges.
func adminCheck() (bool, error) {
    // Skip the check if this is test
    if flag.Lookup("test.v") != nil {
        return false, nil
    }

    var adminSid *windows.SID

    // Allocate and initialize SID
    err := windows.AllocateAndInitializeSid(
        &windows.SECURITY_NT_AUTHORITY,
        2,
        windows.SECURITY_BUILTIN_DOMAIN_RID,
        windows.DOMAIN_ALIAS_RID_ADMINS,
        0, 0, 0, 0, 0, 0,
        &adminSid)
    if err != nil {
        return false, fmt.Errorf("SID Error: %v", err)
    }
    defer windows.FreeSid(adminSid)

    token := windows.Token(0)

    admin, err := token.IsMember(adminSid)
    if err != nil {
        return false, fmt.Errorf("Token Membership Error: %v", err)
    }

    return admin, nil
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

func needsUpdate(item manifest.Item, cfg *config.Configuration) bool {
    catalogItem := catalog.Item{
        Name:    item.Name,
        Version: item.Version,
    }
    cachePath := cfg.CachePath
    actionNeeded, err := status.CheckStatus(catalogItem, "install", cachePath)
    return err != nil || actionNeeded
}

func installUpdate(item manifest.Item, cfg *config.Configuration) {
    catalogItem := catalog.Item{
        DisplayName: item.Name,
        Version:     item.Version,
        Installer: catalog.InstallerItem{
            Type:     getInstallerType(item.InstallerLocation),
            Location: item.InstallerLocation,
        },
    }

    result := installer.Install(catalogItem, "install", cfg.URLPkgsInfo, cfg.CachePath, false)

    if result != "" && result != "Item not needed" {
        fmt.Printf("Failed to install %s: %s\n", item.Name, result)
    } else {
        fmt.Printf("Successfully installed %s\n", item.Name)
    }
}

func getInstallerType(installerLocation string) string {
    switch filepath.Ext(installerLocation) {
    case ".msi":
        return "msi"
    case ".exe":
        return "exe"
    case ".ps1":
        return "ps1"
    case ".nupkg":
        return "nupkg"
    default:
        return ""
    }
}
