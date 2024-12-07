// cmd/managedsoftwareupdate/main.go

package main

import (
    "flag"
    "fmt"
    "os"
    "os/signal"
    "path/filepath"
    "strings"
    "syscall"
    "unsafe"

    "github.com/windowsadmins/gorilla/pkg/catalog"
    "github.com/windowsadmins/gorilla/pkg/config"
    "github.com/windowsadmins/gorilla/pkg/installer"
    "github.com/windowsadmins/gorilla/pkg/logging"
    "github.com/windowsadmins/gorilla/pkg/manifest"
    "github.com/windowsadmins/gorilla/pkg/preflight"
    "github.com/windowsadmins/gorilla/pkg/process"
    "github.com/windowsadmins/gorilla/pkg/report"
    "github.com/windowsadmins/gorilla/pkg/status"

    "golang.org/x/sys/windows"
    "gopkg.in/yaml.v3"
)

type verbosityFlag struct {
    level *int
}

func (vf *verbosityFlag) String() string {
    if vf.level == nil {
        return "0"
    }
    return fmt.Sprintf("%d", *vf.level)
}

func (vf *verbosityFlag) Set(val string) error {
    count := 0
    for _, c := range val {
        if c == 'v' {
            count++
        } else {
            return fmt.Errorf("invalid verbosity flag: %s (only 'v' is allowed)", val)
        }
    }
    *vf.level = count
    return nil
}

var verbosity int

func main() {
    // Define command-line flags
    var (
        showConfig  = flag.Bool("show-config", false, "Display the current configuration and exit.")
        checkOnly   = flag.Bool("checkonly", false, "Check for updates, but don't install them.")
        installOnly = flag.Bool("installonly", false, "Install pending updates without checking for new ones.")
        auto        = flag.Bool("auto", false, "Perform automatic updates.")
    )

    // Replace integer-based verbosity with custom verbosityFlag
    vf := &verbosityFlag{&verbosity}
    flag.Var(vf, "v", "Increase verbosity by adding more 'v' (e.g. -v, -vv, -vvv, -vvvv)")

    // Custom usage function
    flag.Usage = func() {
        fmt.Printf("Usage: %s [options]\n\n", os.Args[0])
        fmt.Println("Options:")
        flag.PrintDefaults()
        fmt.Println("\nCommon Options:")
        fmt.Println("  -v, --verbose       Increase verbosity. Can be used multiple times.")
        fmt.Println("  --checkonly         Check for updates, but don't install them.")
        fmt.Println("  --installonly       Install pending updates without checking for new ones.")
        fmt.Println("  --auto              Perform automatic updates.")
        fmt.Println("  --show-config       Display the current configuration and exit.")
    }

    // Parse flags early
    flag.Parse()

    // Initialize logging functions after parsing flags
    logError := func(message string, args ...interface{}) {
        // Errors always print regardless of verbosity
        fmt.Fprintf(os.Stderr, message+"\n", args...)
    }

    logInfo := func(message string, args ...interface{}) {
        // Basic info shown if verbosity >= 1
        if verbosity >= 1 {
            fmt.Printf(message+"\n", args...)
        }
    }

    logVerbose := func(message string, args ...interface{}) {
        // More detailed info if verbosity >= 2
        if verbosity >= 2 {
            fmt.Printf(message+"\n", args...)
        }
    }

    logVeryVerbose := func(message string, args ...interface{}) {
        // Even more detailed if verbosity >= 3
        if verbosity >= 3 {
            fmt.Printf(message+"\n", args...)
        }
    }

    logDebug := func(message string, args ...interface{}) {
        // Full debug if verbosity >= 4
        if verbosity >= 4 {
            fmt.Printf("[DEBUG] "+message+"\n", args...)
        }
    }

    // Handle system signals for cleanup
    signalChan := make(chan os.Signal, 1)
    signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
    go func() {
        <-signalChan
        if verbosity >= 1 {
            fmt.Println("Signal received, exiting gracefully...")
        }
        os.Exit(1)
    }()

    // Run the preflight script regardless of runType or flags
    err := preflight.RunPreflight(verbosity, logInfo, logError)
    if err != nil {
        logError("Preflight script failed: %v", err)
        os.Exit(1)
    }

    // Load configuration (in case preflight modified it)
    cfg, err := config.LoadConfig()
    if err != nil {
        logError("Failed to load configuration: %v", err)
        os.Exit(1)
    }

    // Initialize logger with loaded configuration
    logging.InitLogger(*cfg)
    defer logging.CloseLogger()

    if verbosity >= 4 {
        logDebug("Loaded configuration:")
        cfgYaml, _ := yaml.Marshal(cfg)
        logDebug("%s", cfgYaml)
        logDebug("Environment Variables:")
        for _, e := range os.Environ() {
            logDebug("%s", e)
        }
    }

    if verbosity >= 1 {
        fmt.Println("Initializing...")
    }

    // Check for conflicting flags
    if *checkOnly && *installOnly {
        fmt.Fprintln(os.Stderr, "--checkonly and --installonly options are mutually exclusive!")
        flag.Usage()
        os.Exit(1)
    }

    // Check for admin privileges
    admin, err := adminCheck()
    if err != nil || !admin {
        logError("Administrative access is required. Please run as an administrator.")
        os.Exit(1)
    }

    // Create the cache directory if needed
    cachePath := cfg.CachePath
    err = os.MkdirAll(filepath.Clean(cachePath), 0755)
    if err != nil {
        logError("Failed to create cache directory: %v", err)
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

    // Determine run type based on flags
    runType := "custom"
    if *auto {
        runType = "auto"
        *checkOnly = false
        *installOnly = false
    }

    if verbosity >= 1 {
        fmt.Printf("Run type: %s\n", runType)
    }

    if *installOnly {
        if verbosity >= 1 {
            fmt.Println("Running in install-only mode.")
        }
        installPendingUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
        os.Exit(0)
    }

    if *checkOnly {
        if verbosity >= 1 {
            fmt.Println("Running in check-only mode.")
        }
        checkForUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
        os.Exit(0)
    }

    // Default behavior: check for updates and install them
    if *auto {
        // For automatic updates, we might want to check for user activity
        if isUserActive() {
            if verbosity >= 1 {
                fmt.Println("User is active. Skipping automatic updates.")
            }
            os.Exit(0)
        }
    }

    // Check for updates
    updatesAvailable := checkForUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
    if updatesAvailable {
        // Install updates
        installPendingUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
    } else {
        if verbosity >= 1 {
            fmt.Println("No updates available.")
        }
    }

    if verbosity == 0 {
        // Minimal info at no verbosity: just a final success message.
        fmt.Println("Updates completed.")
    } else {
        // Higher verbosity: more detailed final message.
        fmt.Println("Software updates completed.")
    }
    os.Exit(0)
}

func logVerbose(message string, args ...interface{}) {
    // This placeholder is required to prevent a compile error if needed
    // since we redefined above, remove the duplicate definition if it occurs
}

func logVeryVerbose(message string, args ...interface{}) {
    // Same note as above
}

func logDebug(message string, args ...interface{}) {
    // Same note as above
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

// isUserActive checks if the user is active based on idle time.
func isUserActive() bool {
    idleSeconds := getIdleSeconds()
    // Consider user active if idle time is less than 300 seconds (5 minutes)
    return idleSeconds < 300
}

// checkForUpdates checks for available updates and returns true if updates are available.
func checkForUpdates(cfg *config.Configuration, verbosity int,
    logInfo func(string, ...interface{}),
    logVerbose func(string, ...interface{}),
    logVeryVerbose func(string, ...interface{}),
    logDebug func(string, ...interface{})) bool {

    if verbosity >= 1 {
        logInfo("Checking for updates...")
    }

    // Fetch manifest items
    updatesAvailable := false
    manifestItems, err := manifest.Get(*cfg)
    if err != nil {
        fmt.Fprintf(os.Stderr, "Failed to get manifest items: %v\n", err)
        return false
    }

    // Check each item for updates
    for _, item := range manifestItems {
        if verbosity >= 2 {
            logVerbose("Checking item: %s (Version: %s)", item.Name, item.Version)
        }
        if verbosity >= 3 {
            logVeryVerbose("Installer location: %s", item.InstallerLocation)
            logVeryVerbose("Catalogs: %v", item.Catalogs)
        }

        if needsUpdate(item, cfg) {
            if verbosity >= 1 {
                logInfo("Update available for %s", item.Name)
            }
            updatesAvailable = true
        } else if verbosity >= 2 {
            logVerbose("No update needed for %s", item.Name)
        }
    }

    return updatesAvailable
}

// installPendingUpdates installs updates for all items that need updating.
func installPendingUpdates(cfg *config.Configuration, verbosity int,
    logInfo func(string, ...interface{}),
    logVerbose func(string, ...interface{}),
    logVeryVerbose func(string, ...interface{}),
    logDebug func(string, ...interface{})) {

    if verbosity >= 1 {
        logInfo("Installing updates...")
    }

    // Fetch manifest items
    manifestItems, err := manifest.Get(*cfg)
    if err != nil {
        fmt.Fprintf(os.Stderr, "Failed to get manifest items: %v\n", err)
        return
    }

    // Install updates for each item
    for _, item := range manifestItems {
        if verbosity >= 2 {
            logVerbose("Checking if %s needs an update...", item.Name)
        }
        if needsUpdate(item, cfg) {
            if verbosity >= 2 {
                logVerbose("Installing update for %s...", item.Name)
            }
            installUpdate(item, cfg, verbosity, logVeryVerbose, logDebug)
        } else if verbosity >= 2 {
            logVerbose("Skipping %s, no update needed.", item.Name)
        }
    }

    // Clean up cache
    cachePath := cfg.CachePath
    if verbosity >= 2 {
        logVerbose("Cleaning up old cache...")
    }
    process.CleanUp(cachePath)
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

func installUpdate(item manifest.Item, cfg *config.Configuration, verbosity int,
    logVeryVerbose func(string, ...interface{}),
    logDebug func(string, ...interface{})) {

    catalogItem := catalog.Item{
        DisplayName: item.Name,
        Version:     item.Version,
        Installer: catalog.InstallerItem{
            Type:     getInstallerType(item.InstallerLocation),
            Location: item.InstallerLocation,
        },
    }

    if verbosity >= 3 {
        logVeryVerbose("Updating %s: Running installer type %s at %s", item.Name, catalogItem.Installer.Type, catalogItem.Installer.Location)
    }

    result := installer.Install(catalogItem, "install", cfg.URLPkgsInfo, cfg.CachePath, false)

    if result != "" && result != "Item not needed" {
        fmt.Printf("Failed to install %s: %s\n", item.Name, result)
    } else {
        fmt.Printf("Successfully installed %s\n", item.Name)
    }

    if verbosity >= 4 {
        logDebug("Install update completed for %s", item.Name)
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