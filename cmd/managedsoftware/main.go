package main

import (
	"fmt"
	"log"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"unsafe"

	"github.com/spf13/pflag"

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/download"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/manifest"
	"github.com/windowsadmins/gorilla/pkg/preflight"
	"github.com/windowsadmins/gorilla/pkg/status"
	"github.com/windowsadmins/gorilla/pkg/version"

	"golang.org/x/sys/windows"
	"gopkg.in/yaml.v3"
)

func main() {
	// Define command-line flags
	showConfig := pflag.Bool("show-config", false, "Display the current configuration and exit.")
	checkOnly := pflag.Bool("checkonly", false, "Check for updates, but don't install them.")
	installOnly := pflag.Bool("installonly", false, "Install pending updates without checking for new ones.")
	auto := pflag.Bool("auto", false, "Perform automatic updates.")
	versionFlag := pflag.Bool("version", false, "Print the version and exit.")

	var verbosity int
	pflag.CountVarP(&verbosity, "verbose", "v", "Increase verbosity by adding more 'v' (e.g. -v, -vv, -vvv)")

	// Custom usage
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
		fmt.Println("  --version           Print the version and exit.")
	}

	pflag.Parse()

	// Handle --version
	if *versionFlag {
		version.Print()
		os.Exit(0)
	}

	// Load configuration
	cfg, err := config.LoadConfig()
	if err != nil {
		fmt.Printf("Failed to load configuration: %v\n", err)
		os.Exit(1)
	}

	// Apply verbosity overrides
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}

	// Initialize logging
	err = logging.Init(cfg)
	if err != nil {
		fmt.Printf("Error initializing logger: %v\n", err)
		os.Exit(1)
	}
	defer logging.CloseLogger()

	// Handle signals
	signalChan := make(chan os.Signal, 1)
	signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		sig := <-signalChan
		logging.Info("Signal received, exiting gracefully", "signal", sig.String())
		logging.CloseLogger()
		os.Exit(1)
	}()

	// Run preflight
	if verbosity >= 2 {
		logging.Info("Initial configuration details before preflight run")
		cfgYaml, cfgErr := yaml.Marshal(cfg)
		if cfgErr == nil {
			logging.Info("Configuration:\n%s", string(cfgYaml))
		} else {
			logging.Warn("Failed to marshal config for debugging", "error", cfgErr)
		}
	}

	logging.Info("Running preflight script now...")
	err = preflight.RunPreflight(verbosity, logging.Info, logging.Error)
	if err != nil {
		logging.Error("Preflight script failed", "error", err)
		os.Exit(1)
	}
	logging.Info("Preflight script completed.")

	// Re-load config after preflight
	cfg, err = config.LoadConfig()
	if err != nil {
		logging.Error("Failed to reload configuration after preflight", "error", err)
		os.Exit(1)
	}

	// Re-apply verbosity
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}

	// Re-init logging after config reload
	err = logging.ReInit(cfg)
	if err != nil {
		fmt.Printf("Error re-initializing logger after preflight: %v\n", err)
		os.Exit(1)
	}
	defer logging.CloseLogger()

	if verbosity >= 2 {
		logging.Info("Configuration after preflight:")
		cfgYaml, cfgErr := yaml.Marshal(cfg)
		if cfgYaml != nil && cfgErr == nil {
			logging.Info("Configuration:\n%s", string(cfgYaml))
		} else {
			logging.Warn("Failed to marshal config after preflight", "error", cfgErr)
		}
	}

	if verbosity >= 3 {
		envVars := os.Environ()
		for _, e := range envVars {
			logging.Debug("Environment variable", "env", e)
		}
	}

	logging.Info("Initializing", "run_type", "custom")

	if *checkOnly && *installOnly {
		logging.Warn("Conflicting flags", "flags", "--checkonly and --installonly are mutually exclusive")
		pflag.Usage()
		os.Exit(1)
	}

	runType := "custom"
	if *auto {
		runType = "auto"
		*checkOnly = false
		*installOnly = false
	}

	logging.Info("Run type", "run_type", runType)

	// Check admin privileges
	admin, err := adminCheck()
	if err != nil || !admin {
		logging.Error("Administrative access required", "error", err, "admin", admin)
		os.Exit(1)
	}

	// Ensure cache directory
	cachePath := cfg.CachePath
	err = os.MkdirAll(filepath.Clean(cachePath), 0755)
	if err != nil {
		logging.Error("Failed to create cache directory", "cache_path", cachePath, "error", err)
		os.Exit(1)
	}

	if *showConfig {
		// Show config and exit
		cfgYaml, err := yaml.Marshal(cfg)
		if err != nil {
			logging.Error("Failed to marshal configuration", "error", err)
			os.Exit(1)
		}
		logging.Info("Current configuration:")
		logging.Info("%s", string(cfgYaml))
		os.Exit(0)
	}

	// Retrieve manifest items once here for all modes
	manifestItems, _ := manifest.AuthenticatedGet(cfg)

	if *installOnly {
		logging.Info("Running in install-only mode")

		downloadItems := prepareDownloadItems(manifestItems)
		err := download.InstallPendingUpdates(downloadItems, cfg)
		if err != nil {
			logging.Error("Failed to install pending updates", "error", err)
			os.Exit(1)
		}

		os.Exit(0)
	}

	if *checkOnly {
		logging.Info("Running in check-only mode")
		updatesAvailable := checkForUpdates(cfg, verbosity, manifestItems)
		if updatesAvailable {
			logging.Info("Updates are available.")
		} else {
			logging.Info("No updates available.")
		}
		os.Exit(0)
	}

	if *auto {
		// In auto mode, skip if user is active
		if isUserActive() {
			logging.Info("User is active. Skipping automatic updates", "idle_seconds", getIdleSeconds())
			os.Exit(0)
		}
	}

	// Normal or auto mode (not installOnly or checkOnly)
	updatesAvailable := checkForUpdates(cfg, verbosity, manifestItems)
	if updatesAvailable {
		downloadItems := prepareDownloadItems(manifestItems)
		err := download.InstallPendingUpdates(downloadItems, cfg)
		if err != nil {
			log.Fatalf("Failed to install pending updates: %v", err)
		}
	} else {
		logging.Info("No updates available")
	}

	if verbosity == 0 {
		fmt.Println("Updates completed.")
	} else {
		logging.Info("Software updates completed")
	}

	os.Exit(0)
}

// checkForUpdates checks if any manifest items need updates.
func checkForUpdates(cfg *config.Configuration, verbosity int, manifestItems []manifest.Item) bool {
	logging.Info("Checking for updates...")
	updatesAvailable := false

	for _, item := range manifestItems {
		if verbosity >= 1 {
			logging.Info("Checking item", "name", item.Name, "version", item.Version)
		}
		if verbosity >= 2 {
			logging.Info("Installer location", "installer_location", item.InstallerLocation)
			logging.Info("Catalogs", "catalogs", item.Catalogs)
		}

		if needsUpdate(item, cfg) {
			logging.Info("Update available for package", "package", item.Name)
			updatesAvailable = true
		} else {
			logging.Info("No update needed for package", "package", item.Name)
		}
	}
	return updatesAvailable
}

// prepareDownloadItems maps manifest items to their installer URLs.
func prepareDownloadItems(manifestItems []manifest.Item) map[string]string {
	downloadItems := make(map[string]string)
	for _, item := range manifestItems {
		if item.InstallerLocation != "" {
			downloadItems[item.Name] = item.InstallerLocation
		}
	}
	return downloadItems
}

// needsUpdate checks if the given item needs an update.
func needsUpdate(item manifest.Item, cfg *config.Configuration) bool {
	catalogItem := catalog.Item{
		Name:          item.Name,
		Version:       item.Version,
		SupportedArch: item.SupportedArch,
	}
	cachePath := cfg.CachePath
	actionNeeded, err := status.CheckStatus(catalogItem, "install", cachePath)
	return err != nil || actionNeeded
}

// adminCheck verifies if the current user has administrative privileges.
func adminCheck() (bool, error) {
	// Skip check in test environment
	if pflag.Lookup("test.v") != nil {
		return false, nil
	}

	var adminSid *windows.SID
	err := windows.AllocateAndInitializeSid(
		&windows.SECURITY_NT_AUTHORITY,
		2,
		windows.SECURITY_BUILTIN_DOMAIN_RID,
		windows.DOMAIN_ALIAS_RID_ADMINS,
		0, 0, 0, 0, 0, 0,
		&adminSid)
	if err != nil {
		return false, fmt.Errorf("sid error: %v", err)
	}
	defer windows.FreeSid(adminSid)

	token := windows.Token(0)
	admin, err := token.IsMember(adminSid)
	if err != nil {
		return false, fmt.Errorf("token membership error: %v", err)
	}

	return admin, nil
}

// LASTINPUTINFO is used for idle time detection
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

// isUserActive determines if the user is active.
func isUserActive() bool {
	idleSeconds := getIdleSeconds()
	return idleSeconds < 300
}
