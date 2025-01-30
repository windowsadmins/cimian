// cmd/managedsoftware/main.go

package main

import (
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"unsafe"

	"github.com/spf13/pflag"

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/download"
	"github.com/windowsadmins/gorilla/pkg/installer"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/manifest"
	"github.com/windowsadmins/gorilla/pkg/preflight"
	"github.com/windowsadmins/gorilla/pkg/version"

	"golang.org/x/sys/windows"
	"gopkg.in/yaml.v3"
)

// LASTINPUTINFO is used to track user idle time
type LASTINPUTINFO struct {
	CbSize uint32
	DwTime uint32
}

func main() {
	// Define command-line flags
	showConfig := pflag.Bool("show-config", false, "Display the current configuration and exit.")
	checkOnly := pflag.Bool("checkonly", false, "Check for updates, but don't install them.")
	installOnly := pflag.Bool("installonly", false, "Install pending updates without checking for new ones.")
	auto := pflag.Bool("auto", false, "Perform automatic updates.")
	versionFlag := pflag.Bool("version", false, "Print the version and exit.")

	var verbosity int
	pflag.CountVarP(&verbosity, "verbose", "v", "Increase verbosity by adding more 'v' (e.g. -v, -vv, -vvv)")

	pflag.Parse()

	// Handle --version flag
	if *versionFlag {
		version.Print()
		os.Exit(0)
	}

	// 1) Load configuration
	cfg, err := config.LoadConfig()
	if err != nil {
		fmt.Printf("Failed to load configuration: %v\n", err)
		os.Exit(1)
	}

	// Apply verbosity settings
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}

	// 2) Initialize logging
	err = logging.Init(cfg)
	if err != nil {
		fmt.Printf("Error initializing logger: %v\n", err)
		os.Exit(1)
	}
	defer logging.CloseLogger()

	// 3) Handle system signals for graceful shutdown
	signalChan := make(chan os.Signal, 1)
	signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		sig := <-signalChan
		logging.Info("Signal received, exiting gracefully", "signal", sig.String())
		logging.CloseLogger()
		os.Exit(1)
	}()

	// 4) Run preflight script
	logging.Info("Running preflight script now...")
	pErr := preflight.RunPreflight(verbosity, logging.Info, logging.Error)
	if pErr != nil {
		logging.Error("Preflight script failed", "error", pErr)
		os.Exit(1)
	}
	logging.Info("Preflight script completed.")

	// Re-load configuration after preflight
	cfg, err = config.LoadConfig()
	if err != nil {
		logging.Error("Failed to reload configuration after preflight", "error", err)
		os.Exit(1)
	}

	// Re-apply verbosity settings
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}

	// Re-initialize logging with updated config
	err = logging.ReInit(cfg)
	if err != nil {
		fmt.Printf("Error re-initializing logger after preflight: %v\n", err)
		os.Exit(1)
	}
	defer logging.CloseLogger()

	// --show-config flag
	if *showConfig {
		cfgYaml, yerr := yaml.Marshal(cfg)
		if yerr == nil {
			logging.Info("Current configuration:\n%s", string(cfgYaml))
		}
		os.Exit(0)
	}

	// Ensure mutually exclusive flags are not set
	if *checkOnly && *installOnly {
		logging.Warn("Conflicting flags", "flags", "--checkonly and --installonly are mutually exclusive")
		pflag.Usage()
		os.Exit(1)
	}

	// Determine run type
	runType := "custom"
	if *auto {
		runType = "auto"
		// Override flags for auto
		*checkOnly = false
		*installOnly = false
	}
	logging.Info("Run type", "run_type", runType)

	// 5) Check administrative privileges
	admin, adminErr := adminCheck()
	if adminErr != nil || !admin {
		logging.Error("Administrative access required", "error", adminErr, "admin", admin)
		os.Exit(1)
	}

	// 6) Ensure cache directory exists
	cachePath := cfg.CachePath
	if err := os.MkdirAll(filepath.Clean(cachePath), 0755); err != nil {
		logging.Error("Failed to create cache directory", "cache_path", cachePath, "error", err)
		os.Exit(1)
	}

	// 7) Retrieve manifests
	manifestItems, mErr := manifest.AuthenticatedGet(cfg)
	if mErr != nil {
		logging.Error("Failed to retrieve manifests", "error", mErr)
		os.Exit(1)
	}

	// 7.1) Load local catalogs into a map (name.lower() => catalog.Item)
	localCatalogMap, err := loadLocalCatalogItems(cfg)
	if err != nil {
		logging.Error("Failed to load local catalogs", "error", err)
		os.Exit(1)
	}
	logging.Debug("Local catalogs loaded", "count", len(localCatalogMap))

	// Handle install-only mode
	if *installOnly {
		logging.Info("Running in install-only mode")
		itemsToInstall := prepareDownloadItemsWithCatalog(manifestItems, localCatalogMap, cfg)
		if err := downloadAndInstallPerItem(itemsToInstall, cfg); err != nil {
			logging.Error("Failed to install pending updates (install-only)", "error", err)
			os.Exit(1)
		}
		os.Exit(0)
	}

	// Handle check-only mode
	if *checkOnly {
		updatesAvailable := checkForUpdatesWithCatalog(cfg, verbosity, manifestItems, localCatalogMap)
		if updatesAvailable {
			logging.Info("Updates are available.")
		} else {
			logging.Info("No updates available.")
		}
		os.Exit(0)
	}

	// Handle auto mode: skip if user is active
	if *auto {
		if isUserActive() {
			logging.Info("User is active. Skipping automatic updates", "idle_seconds", getIdleSeconds())
			os.Exit(0)
		}
	}

	// 8) Normal or auto mode: check for updates, then download & install
	updatesAvailable := checkForUpdatesWithCatalog(cfg, verbosity, manifestItems, localCatalogMap)
	if updatesAvailable {
		// Gather items that need updates
		updateList := prepareDownloadItemsWithCatalog(manifestItems, localCatalogMap, cfg)

		// Download and install each item
		err := downloadAndInstallPerItem(updateList, cfg)
		if err != nil {
			logging.Error("Failed to download+install updates", "error", err)
			os.Exit(1)
		}
	} else {
		logging.Info("No updates available")
	}

	logging.Info("Software updates completed")

	// 9) If you want to do a final, full cleanup, call it here:
	clearCacheFolder(cfg.CachePath)

	os.Exit(0)
}

// loadLocalCatalogItems reads all .yaml files in cfg.CatalogsPath and returns a map of catalog items.
func loadLocalCatalogItems(cfg *config.Configuration) (map[string]catalog.Item, error) {
	itemsMap := make(map[string]catalog.Item)
	dirEntries, err := os.ReadDir(cfg.CatalogsPath)
	if err != nil {
		return nil, fmt.Errorf("reading catalogs dir: %v", err)
	}

	for _, e := range dirEntries {
		if e.IsDir() {
			continue
		}
		if filepath.Ext(e.Name()) != ".yaml" {
			continue
		}
		path := filepath.Join(cfg.CatalogsPath, e.Name())
		data, err := os.ReadFile(path)
		if err != nil {
			logging.Warn("Failed to read catalog file", "path", path, "error", err)
			continue
		}
		var catItems []catalog.Item
		if err := yaml.Unmarshal(data, &catItems); err != nil {
			logging.Warn("Failed to parse catalog YAML", "path", path, "error", err)
			continue
		}
		// Store each item in the map with lowercase keys for case-insensitive matching
		for _, cItem := range catItems {
			key := strings.ToLower(cItem.Name)
			itemsMap[key] = cItem
		}
	}
	return itemsMap, nil
}

// checkForUpdatesWithCatalog checks all manifest items against the local catalog to determine if updates are available.
func checkForUpdatesWithCatalog(cfg *config.Configuration, verbosity int, manifestItems []manifest.Item, catMap map[string]catalog.Item) bool {
	logging.Info("Checking for updates...")
	updates := false

	for _, item := range manifestItems {
		if verbosity > 0 {
			logging.Info("Checking item", "name", item.Name, "version", item.Version)
		}
		if installer.LocalNeedsUpdate(item, catMap, cfg) {
			logging.Info("Update available for package", "package", item.Name)
			updates = true
		} else {
			logging.Info("No update needed for package", "package", item.Name)
		}
	}
	return updates
}

// prepareDownloadItemsWithCatalog prepares a list of catalog items that need to be downloaded and installed.
func prepareDownloadItemsWithCatalog(manifestItems []manifest.Item, catMap map[string]catalog.Item, cfg *config.Configuration) []catalog.Item {
	var results []catalog.Item
	for _, m := range manifestItems {
		if installer.LocalNeedsUpdate(m, catMap, cfg) {
			key := strings.ToLower(m.Name)
			catItem, found := catMap[key]
			if !found {
				logging.Warn("Skipping item not in local catalog", "item", m.Name)
				continue
			}
			results = append(results, catItem)
		}
	}
	return results
}

// downloadAndInstallPerItem handles downloading & installing each catalog item individually.
// It calls installOneCatalogItem(...) so we can reuse your custom logic.
func downloadAndInstallPerItem(items []catalog.Item, cfg *config.Configuration) error {
	for _, cItem := range items { // <--- valid 'continue' usage inside this for loop
		if cItem.Installer.Location == "" {
			logging.Warn("No installer location found for item", "item", cItem.Name)
			continue
		}

		// Build the full URL
		fullURL := cItem.Installer.Location
		if strings.HasPrefix(fullURL, "/") {
			fullURL = strings.TrimRight(cfg.SoftwareRepoURL, "/") + fullURL
		}

		// Destination file path in cache
		destFile := filepath.Join(cfg.CachePath, filepath.Base(cItem.Installer.Location))

		// Download the installer
		logging.Info("Downloading item", "name", cItem.Name, "url", fullURL, "destination", destFile)
		if err := download.DownloadFile(fullURL, destFile, cfg); err != nil {
			logging.Error("Failed to download item", "name", cItem.Name, "error", err)
			continue
		}
		logging.Info("Downloaded item successfully", "name", cItem.Name, "file", destFile)

		// Perform the install using your custom function
		if err := installOneCatalogItem(cItem, destFile, cfg); err != nil {
			logging.Error("Installation command failed", "item", cItem.Name, "error", err)
			continue
		}
	}
	return nil
}

// installOneCatalogItem installs a single catalog item using the installer package.
// It normalizes the architecture and handles installation output for error detection.
func installOneCatalogItem(cItem catalog.Item, localFile string, cfg *config.Configuration) error {
	normalizeArchitecture(&cItem)
	sysArch := getSystemArchitecture()
	logging.Debug("Detected system architecture", "sysArch", sysArch)
	logging.Debug("Supported architectures for item", "item", cItem.Name, "supported_arch", cItem.SupportedArch)

	// Actually install
	installedOutput, installErr := installer.Install(cItem, "install", localFile, cfg.CachePath, cfg.CheckOnly, cfg)
	if installErr != nil {
		// DO NOT say "Installed item successfully" because it failed
		return installErr
	}

	// If we get here => success
	logging.Info("Install output", "item", cItem.Name, "output", installedOutput)
	logging.Info("Installed item successfully", "item", cItem.Name)

	// If you want to remove it from cache here, do so:
	// os.Remove(localFile)

	// Check for architecture mismatch in the output
	if strings.Contains(strings.ToLower(installedOutput), "unsupported architecture") {
		return fmt.Errorf("architecture mismatch for item %s", cItem.Name)
	}
	return nil
}

// normalizeArchitecture ensures that the system architecture matches the catalog's expectations.
// It maps "amd64" and "x86_64" to "x64" to resolve compatibility issues.
func normalizeArchitecture(item *catalog.Item) {
	sysArch := getSystemArchitecture()
	for i, arch := range item.SupportedArch {
		if strings.EqualFold(arch, "amd64") || strings.EqualFold(arch, "x86_64") {
			item.SupportedArch[i] = "x64"
		}
	}
	// Ensure the system architecture is included in SupportedArch
	var found bool
	for _, arch := range item.SupportedArch {
		if strings.EqualFold(arch, sysArch) {
			found = true
			break
		}
	}
	if !found {
		item.SupportedArch = append(item.SupportedArch, sysArch)
	}
}

// getSystemArchitecture determines the system's architecture and maps it to the catalog's expected values.
func getSystemArchitecture() string {
	arch := runtime.GOARCH
	switch arch {
	case "amd64", "x86_64":
		return "x64"
	case "arm64":
		return "arm64"
	case "386":
		return "x86"
	default:
		return arch
	}
}

// adminCheck verifies if the current process has administrative privileges.
func adminCheck() (bool, error) {
	var adminSid *windows.SID
	err := windows.AllocateAndInitializeSid(
		&windows.SECURITY_NT_AUTHORITY,
		2,
		windows.SECURITY_BUILTIN_DOMAIN_RID,
		windows.DOMAIN_ALIAS_RID_ADMINS,
		0, 0, 0, 0, 0, 0,
		&adminSid)
	if err != nil {
		return false, err
	}
	defer windows.FreeSid(adminSid)

	token := windows.Token(0)
	isMember, err := token.IsMember(adminSid)
	return isMember, err
}

// getIdleSeconds returns the number of seconds the user has been idle.
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

// isUserActive determines if the user is active based on idle time.
func isUserActive() bool {
	idleSeconds := getIdleSeconds()
	return idleSeconds < 300
}

// clearCacheFolder removes all items in the cache directory.
func clearCacheFolder(cachePath string) {
	dirEntries, err := os.ReadDir(cachePath)
	if err != nil {
		logging.Warn("Failed to read cache directory", "path", cachePath, "error", err)
		return
	}
	for _, e := range dirEntries {
		p := filepath.Join(cachePath, e.Name())
		if rmErr := os.RemoveAll(p); rmErr != nil {
			logging.Warn("Failed to remove cached item", "path", p, "error", rmErr)
		} else {
			logging.Debug("Removed cached item", "path", p)
		}
	}
	logging.Info("Cache folder emptied after run", "cachePath", cachePath)
}
