// cmd/managedsoftwareupdate/main.go

package main

import (
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"unsafe"

	"github.com/spf13/pflag"

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/installer"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/manifest"
	"github.com/windowsadmins/gorilla/pkg/preflight"
	"github.com/windowsadmins/gorilla/pkg/process"
	"github.com/windowsadmins/gorilla/pkg/status"

	"golang.org/x/sys/windows"
	"gopkg.in/yaml.v3"
)

var verbosity int

func main() {
	// Define command-line flags using pflag
	showConfig := pflag.Bool("show-config", false, "Display the current configuration and exit.")
	checkOnly := pflag.Bool("checkonly", false, "Check for updates, but don't install them.")
	installOnly := pflag.Bool("installonly", false, "Install pending updates without checking for new ones.")
	auto := pflag.Bool("auto", false, "Perform automatic updates.")

	// `CountVarP` increments `verbosity` each time `-v` is used.
	// So:
	// no -v: verbosity = 0
	// -v: verbosity = 1
	// -vv: verbosity = 2
	// -vvv: verbosity = 3
	pflag.CountVarP(&verbosity, "verbose", "v", "Increase verbosity by adding more 'v' (e.g. -v, -vv, -vvv)")

	pflag.Usage = func() {
		fmt.Printf("Usage: %s [options]\n\n", os.Args[0])
		fmt.Println("Options:")
		pflag.PrintDefaults()
		fmt.Println("\nCommon Options:")
		fmt.Println("  -v, --verbose       Increase verbosity. Can be used multiple times.")
		fmt.Println("  --checkonly         Check for updates, but don't install them.")
		fmt.Println("  --installonly       Install pending updates without checking for new ones.")
		fmt.Println("  --auto              Perform automatic updates.")
		fmt.Println("  --show-config       Display the current configuration and exit.")
	}

	// Parse flags
	pflag.Parse()

	// Setup logging callbacks based on verbosity
	// verbosity 0: only Info
	// verbosity 1: Info + Verbose
	// verbosity 2: Info + Verbose + VeryVerbose
	// verbosity >=3: Info + Verbose + VeryVerbose + Debug
	var (
		logInfo        = logging.Info
		logVerbose     = func(string, ...interface{}) {}
		logVeryVerbose = func(string, ...interface{}) {}
		logDebug       = func(string, ...interface{}) {}
	)

	switch verbosity {
	case 0:
		// Only Info
	case 1:
		logVerbose = logging.Info
	case 2:
		logVerbose = logging.Info
		logVeryVerbose = logging.Info
	default:
		logVerbose = logging.Info
		logVeryVerbose = logging.Info
		logDebug = logging.Debug
	}

	// Handle system signals for cleanup
	signalChan := make(chan os.Signal, 1)
	signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		sig := <-signalChan
		logInfo("Signal received, exiting gracefully", "signal", sig.String())
		os.Exit(1)
	}()

	// Run preflight
	err := preflight.RunPreflight(verbosity, logInfo, logging.Error)
	if err != nil {
		logging.Error("Preflight script failed", "error", err)
		os.Exit(1)
	}

	// Load configuration (in case preflight modified it)
	cfg, err := config.LoadConfig()
	if err != nil {
		logging.Error("Failed to load configuration", "error", err)
		os.Exit(1)
	}

	// Override config.Verbose and config.Debug based on verbosity
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}

	// Initialize logger with updated configuration
	err = logging.Init(cfg)
	if err != nil {
		fmt.Printf("Error initializing logging: %v\n", err)
		os.Exit(1)
	}
	defer logging.CloseLogger()

	// If verbosity >= 4, log detailed config and environment variables
	if verbosity >= 4 {
		cfgYaml, err := yaml.Marshal(cfg)
		if err != nil {
			logging.Error("Failed to marshal configuration", "error", err)
		} else {
			logDebug("Loaded configuration", "config_yaml", string(cfgYaml))
		}
		logDebug("Environment Variables", "env", os.Environ())
	}

	// Initialize log with run type
	logInfo("Initializing", "run_type", "custom")

	// Check for conflicting flags
	if *checkOnly && *installOnly {
		logging.Warn("Conflicting flags", "flags", "--checkonly and --installonly are mutually exclusive")
		pflag.Usage()
		os.Exit(1)
	}

	// Check for admin privileges
	admin, err := adminCheck()
	if err != nil || !admin {
		logging.Error("Administrative access required", "error", err, "admin", admin)
		os.Exit(1)
	}

	// Create the cache directory if needed
	cachePath := cfg.CachePath
	err = os.MkdirAll(filepath.Clean(cachePath), 0755)
	if err != nil {
		logging.Error("Failed to create cache directory", "cache_path", cachePath, "error", err)
		os.Exit(1)
	}

	// Show configuration if requested
	if *showConfig {
		cfgYaml, err := yaml.Marshal(cfg)
		if err != nil {
			logging.Error("Failed to marshal configuration", "error", err)
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

	logInfo("Run type", "run_type", runType)

	// Execute based on run type and flags
	if *installOnly {
		logInfo("Running in install-only mode")
		installPendingUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
		os.Exit(0)
	}

	if *checkOnly {
		logInfo("Running in check-only mode")
		updatesAvailable := checkForUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
		if updatesAvailable {
			logInfo("Updates are available")
		} else {
			logInfo("No updates available")
		}
		os.Exit(0)
	}

	// Default behavior: check for updates and install them
	if *auto {
		// For automatic updates, check for user activity
		if isUserActive() {
			logInfo("User is active. Skipping automatic updates", "idle_seconds", getIdleSeconds())
			os.Exit(0)
		}
	}

	// Check for updates
	updatesAvailable := checkForUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
	if updatesAvailable {
		// Install updates
		installPendingUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
	} else {
		logInfo("No updates available")
	}

	// Final messages based on verbosity
	if verbosity == 0 {
		// Minimal info at no verbosity: just a final success message to stdout.
		fmt.Println("Updates completed.")
	} else {
		// Higher verbosity: more detailed final message.
		logInfo("Software updates completed")
	}

	os.Exit(0)
}

// adminCheck verifies if the current user has administrative privileges.
func adminCheck() (bool, error) {
	// Skip the check if this is test
	if pflag.Lookup("test.v") != nil {
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

// LASTINPUTINFO struct for GetLastInputInfo
type LASTINPUTINFO struct {
	CbSize uint32
	DwTime uint32
}

// getIdleSeconds uses the Windows API to get the system's idle time in seconds.
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

	logInfo("Checking for updates...")

	// Fetch manifest items
	updatesAvailable := false
	manifestItems, err := manifest.Get(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to get manifest items: %v\n", err)
		return false
	}

	// Check each item for updates
	for _, item := range manifestItems {
		if verbosity >= 1 {
			logVerbose("Checking item", "name", item.Name, "version", item.Version)
		}
		if verbosity >= 2 {
			logVeryVerbose("Installer location", "installer_location", item.InstallerLocation)
			logVeryVerbose("Catalogs", "catalogs", item.Catalogs)
		}

		if needsUpdate(item, cfg) {
			logInfo("Update available for package", "package", item.Name)
			updatesAvailable = true
		} else {
			logVerbose("No update needed for package", "package", item.Name)
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

	logInfo("Installing updates...")

	// Fetch manifest items
	manifestItems, err := manifest.Get(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to get manifest items: %v\n", err)
		return
	}

	// Install updates for each item
	for _, item := range manifestItems {
		logVerbose("Checking if package needs update", "package", item.Name)
		if needsUpdate(item, cfg) {
			logVerbose("Installing update for package", "package", item.Name)
			installUpdate(item, cfg, verbosity, logVeryVerbose, logDebug)
		} else {
			logVerbose("Skipping package, no update needed", "package", item.Name)
		}
	}

	// Clean up cache
	cachePath := cfg.CachePath
	logVerbose("Cleaning up old cache", "cache_path", cachePath)
	process.CleanUp(cachePath, cfg)
}

// needsUpdate determines if a package needs an update based on its status.
func needsUpdate(item manifest.Item, cfg *config.Configuration) bool {
	catalogItem := catalog.Item{
		Name:    item.Name,
		Version: item.Version,
	}
	cachePath := cfg.CachePath
	actionNeeded, err := status.CheckStatus(catalogItem, "install", cachePath)
	return err != nil || actionNeeded
}

// installUpdate installs a single package update.
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

	if verbosity >= 2 {
		logVeryVerbose("Updating package", "package", item.Name, "installer_type", catalogItem.Installer.Type, "installer_location", catalogItem.Installer.Location)
	}

	// Pass cfg.ForceBasicAuth to installer.Install if needed
	result := installer.Install(catalogItem, "install", cfg.URLPkgsInfo, cfg.CachePath, cfg.ForceBasicAuth, cfg)

	if result != "" && result != "Item not needed" {
		fmt.Printf("Failed to install %s: %s\n", item.Name, result)
		logging.Error("Failed to install package", "package", item.Name, "result", result)
	} else {
		fmt.Printf("Successfully installed %s\n", item.Name)
		logging.Info("Successfully installed package", "package", item.Name)
	}

	if verbosity >= 3 {
		logDebug("Install update completed for package", "package", item.Name)
	}
}

// getInstallerType determines the installer type based on the file extension.
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
