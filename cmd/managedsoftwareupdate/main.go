// cmd/managedsoftwareupdate/main.go
//
// This is the main entry point for the Gorilla "managedsoftwareupdate" tool, which checks for and installs updates
// based on a managed configuration similar to Munki. It supports command-line flags to control its behavior and
// integrates a "preflight" step as well as logging and configuration loading.
//
// Key Steps:
// 1. Parse command-line flags for various run modes (check-only, install-only, auto, show-config).
// 2. Load and configure logging and configuration settings.
// 3. Run the preflight script if available to perform any preliminary setup tasks.
// 4. Re-load configuration after preflight to capture any changes made by the script.
// 5. (Re-initialize logging if needed after the config reload.)
// 6. Depending on command-line flags and conditions, check for updates, install them, or simply show configuration.
// 7. Handle graceful shutdown signals and perform cleanup as needed.
//
// The script uses verbosity flags (-v) to control logging output levels and relies on external modules and packages
// for configuration, logging, manifests, and installation logic.

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
	// Define command-line flags using pflag, a more advanced alternative to the standard flag package.
	// These flags determine the mode the tool runs in: showing config, checking for updates only,
	// installing pending updates only, or doing everything automatically.
	showConfig := pflag.Bool("show-config", false, "Display the current configuration and exit.")
	checkOnly := pflag.Bool("checkonly", false, "Check for updates, but don't install them.")
	installOnly := pflag.Bool("installonly", false, "Install pending updates without checking for new ones.")
	auto := pflag.Bool("auto", false, "Perform automatic updates.")

	var verbosity int
	// The CountVarP call increments verbosity each time -v is used. So:
	// -v sets verbosity to 1, -vv to 2, etc.
	pflag.CountVarP(&verbosity, "verbose", "v", "Increase verbosity by adding more 'v' (e.g. -v, -vv, -vvv)")

	// Customize usage output to explain common options.
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

	// Parse command-line flags.
	pflag.Parse()

	// Setup logging callbacks based on verbosity level. Higher verbosity enables more detailed logging.
	// By default, we only log Info-level messages. With increasing verbosity, we add more verbosity levels.
	var (
		logInfo        = logging.Info
		logVerbose     = func(string, ...interface{}) {}
		logVeryVerbose = func(string, ...interface{}) {}
		logDebug       = func(string, ...interface{}) {}
	)

	switch verbosity {
	case 0:
		// Only Info logging is enabled
	case 1:
		// Verbose logging
		logVerbose = logging.Info
	case 2:
		// VeryVerbose logging includes verbose logging as well
		logVerbose = logging.Info
		logVeryVerbose = logging.Info
	default:
		// verbosity >= 3 enables debug logs too
		logVerbose = logging.Info
		logVeryVerbose = logging.Info
		logDebug = logging.Debug
	}

	// Load the configuration from the Config.yaml file.
	// If loading fails, we cannot proceed, so exit.
	cfg, err := config.LoadConfig()
	if err != nil {
		fmt.Printf("Failed to load configuration: %v\n", err)
		os.Exit(1)
	}

	// Override verbosity settings in the configuration based on command-line flags.
	// If verbosity > 0, turn on verbose logging in config. If >= 3, enable debug.
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}

	// Initialize logging now that we have a configuration.
	// The logging system uses the config to determine log level, log file paths, etc.
	err = logging.Init(cfg)
	if err != nil {
		fmt.Printf("Error initializing logging: %v\n", err)
		os.Exit(1)
	}
	defer logging.CloseLogger() // Ensure we flush and close logs on exit.

	// Handle system signals for graceful shutdown (e.g., Ctrl+C or SIGTERM).
	// If we receive a signal, log it and exit cleanly.
	signalChan := make(chan os.Signal, 1)
	signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		sig := <-signalChan
		logInfo("Signal received, exiting gracefully", "signal", sig.String())
		os.Exit(1)
	}()

	// Run the preflight script if it exists. This allows for any preliminary actions
	// like adjusting configuration or setting up environment before updates.
	err = preflight.RunPreflight(verbosity, logInfo, logging.Error)
	if err != nil {
		logging.Error("Preflight script failed", "error", err)
		os.Exit(1)
	}

	// If the preflight script modifies the configuration, re-load it here:
	cfg, err = config.LoadConfig()
	if err != nil {
		logging.Error("Failed to reload configuration after preflight", "error", err)
		os.Exit(1)
	}

	// Re-apply verbosity overrides after config reload (in case preflight changed config):
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}

	// Re-initialize logging after config reload, in case something changed that affects logging.
	err = logging.Init(cfg)
	if err != nil {
		fmt.Printf("Error re-initializing logging after preflight: %v\n", err)
		os.Exit(1)
	}
	defer logging.CloseLogger()

	// If verbosity >= 4, dump detailed configuration and environment variables for troubleshooting.
	if verbosity >= 4 {
		cfgYaml, err := yaml.Marshal(cfg)
		if err != nil {
			logging.Error("Failed to marshal configuration", "error", err)
		} else {
			logDebug("Loaded configuration", "config_yaml", string(cfgYaml))
		}
		logDebug("Environment Variables", "env", os.Environ())
	}

	logInfo("Initializing", "run_type", "custom")

	// Ensure that conflicting flags aren't used together.
	// e.g., --checkonly and --installonly cannot be used at the same time.
	if *checkOnly && *installOnly {
		logging.Warn("Conflicting flags", "flags", "--checkonly and --installonly are mutually exclusive")
		pflag.Usage()
		os.Exit(1)
	}

	// Check for administrative privileges since updating software typically requires admin rights.
	admin, err := adminCheck()
	if err != nil || !admin {
		logging.Error("Administrative access required", "error", err, "admin", admin)
		os.Exit(1)
	}

	// Ensure the cache directory exists, creating it if necessary. This directory is used to
	// store downloaded installers and other temporary data.
	cachePath := cfg.CachePath
	err = os.MkdirAll(filepath.Clean(cachePath), 0755)
	if err != nil {
		logging.Error("Failed to create cache directory", "cache_path", cachePath, "error", err)
		os.Exit(1)
	}

	// If the user requested to just show the current configuration, do so and exit.
	if *showConfig {
		cfgYaml, err := yaml.Marshal(cfg)
		if err != nil {
			logging.Error("Failed to marshal configuration", "error", err)
			os.Exit(1)
		}
		fmt.Printf("Current Configuration:\n%s\n", cfgYaml)
		os.Exit(0)
	}

	// Determine the run type based on flags. If --auto is used, we run in "auto" mode.
	// Otherwise, it's a "custom" run. "auto" mode might skip updates if the user is active.
	runType := "custom"
	if *auto {
		runType = "auto"
		*checkOnly = false
		*installOnly = false
	}

	logInfo("Run type", "run_type", runType)

	// If --installonly is used, we skip checking for updates and just install whatever is pending.
	if *installOnly {
		logInfo("Running in install-only mode")
		installPendingUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
		os.Exit(0)
	}

	// If --checkonly is used, we check for updates but do not install them.
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

	// Default behavior: check for updates and install them if available.
	// If running in auto mode, first check if the user is active. If so, skip updates to avoid disruption.
	if *auto {
		if isUserActive() {
			logInfo("User is active. Skipping automatic updates", "idle_seconds", getIdleSeconds())
			os.Exit(0)
		}
	}

	// Check for updates and then install them if any are found.
	updatesAvailable := checkForUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
	if updatesAvailable {
		installPendingUpdates(cfg, verbosity, logInfo, logVerbose, logVeryVerbose, logDebug)
	} else {
		logInfo("No updates available")
	}

	// Print a final message depending on verbosity.
	if verbosity == 0 {
		// Minimal output
		fmt.Println("Updates completed.")
	} else {
		// More detailed output at higher verbosity
		logInfo("Software updates completed")
	}

	// Exit successfully.
	os.Exit(0)
}

// adminCheck verifies if the current user has administrative privileges.
// On Windows, this checks the token membership for the Administrators group SID.
func adminCheck() (bool, error) {
	// Skip the check if this is a test environment.
	if pflag.Lookup("test.v") != nil {
		return false, nil
	}

	var adminSid *windows.SID
	// Allocate and initialize the SID for the Administrators group.
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

// LASTINPUTINFO struct is used by the Windows API to determine the last time there was user input.
// We use this to determine if the user is "idle" or "active".
type LASTINPUTINFO struct {
	CbSize uint32
	DwTime uint32
}

// getIdleSeconds uses the Windows API (GetLastInputInfo and GetTickCount) to find out how long (in seconds)
// the system has been idle. This can help us decide if it's safe to do automatic updates without interfering
// with an active user.
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

// isUserActive checks if the user is currently active by determining if the idle time is less than 5 minutes.
func isUserActive() bool {
	idleSeconds := getIdleSeconds()
	// Consider user active if idle time is less than 300 seconds (5 minutes).
	return idleSeconds < 300
}

// checkForUpdates checks the configured manifests for available updates.
// Returns true if updates are available, false otherwise.
func checkForUpdates(cfg *config.Configuration, verbosity int,
	logInfo func(string, ...interface{}),
	logVerbose func(string, ...interface{}),
	logVeryVerbose func(string, ...interface{}),
	logDebug func(string, ...interface{})) bool {

	logInfo("Checking for updates...")

	// Retrieve items from the manifest.
	manifestItems, err := manifest.Get(cfg)
	if err != nil {
		// If we fail to get the manifest, we cannot determine if updates are needed.
		fmt.Fprintf(os.Stderr, "Failed to get manifest items: %v\n", err)
		return false
	}

	updatesAvailable := false

	// Iterate over each item in the manifest, checking if it needs updating.
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
// It will re-check the manifest, and for each item that needs an update, it downloads and applies it.
func installPendingUpdates(cfg *config.Configuration, verbosity int,
	logInfo func(string, ...interface{}),
	logVerbose func(string, ...interface{}),
	logVeryVerbose func(string, ...interface{}),
	logDebug func(string, ...interface{})) {

	logInfo("Installing updates...")

	// Fetch the manifest items again to ensure we have the latest data.
	manifestItems, err := manifest.Get(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to get manifest items: %v\n", err)
		return
	}

	// For each item, determine if we need to install an update.
	for _, item := range manifestItems {
		logVerbose("Checking if package needs update", "package", item.Name)
		if needsUpdate(item, cfg) {
			logVerbose("Installing update for package", "package", item.Name)
			installUpdate(item, cfg, verbosity, logVeryVerbose, logDebug)
		} else {
			logVerbose("Skipping package, no update needed", "package", item.Name)
		}
	}

	// After installing updates, clean up old cached installers.
	cachePath := cfg.CachePath
	logVerbose("Cleaning up old cache", "cache_path", cachePath)
	process.CleanUp(cachePath, cfg)
}

// needsUpdate checks if a given package item requires an update by comparing its status and version.
func needsUpdate(item manifest.Item, cfg *config.Configuration) bool {
	catalogItem := catalog.Item{
		Name:    item.Name,
		Version: item.Version,
	}
	cachePath := cfg.CachePath
	actionNeeded, err := status.CheckStatus(catalogItem, "install", cachePath)
	// If there's an error or action is needed, return true.
	return err != nil || actionNeeded
}

// installUpdate installs a single package update based on the manifest item.
// It identifies the installer type, downloads and installs it, and logs results.
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

	// Log detailed info for very verbose modes
	if verbosity >= 2 {
		logVeryVerbose("Updating package", "package", item.Name, "installer_type", catalogItem.Installer.Type, "installer_location", catalogItem.Installer.Location)
	}

	// Attempt installation
	result := installer.Install(catalogItem, "install", cfg.URLPkgsInfo, cfg.CachePath, cfg.ForceBasicAuth, cfg)

	// If the result is not empty or "Item not needed", it might indicate a failure.
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

// getInstallerType returns the installer type based on the file extension.
// This helps determine how to install the package (e.g., MSI, EXE, PS1 scripts, etc.)
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
