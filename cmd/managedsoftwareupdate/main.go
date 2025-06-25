// cmd/managedsoftwareupdate/main.go

package main

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"
	"unsafe"

	"github.com/spf13/pflag"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
	"gopkg.in/yaml.v3"

	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/installer"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
	"github.com/windowsadmins/cimian/pkg/process"
	"github.com/windowsadmins/cimian/pkg/scripts"
	"github.com/windowsadmins/cimian/pkg/status"
	"github.com/windowsadmins/cimian/pkg/version"
)

// Bootstrap mode flag file - Windows equivalent of Munki's hidden dot file
const BootstrapFlagFile = `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`

var logger *logging.Logger

// LASTINPUTINFO is used to track user idle time.
type LASTINPUTINFO struct {
	CbSize uint32
	DwTime uint32
}

// enableANSIConsole enables ANSI colors in the console.
func enableANSIConsole() {
	for _, stream := range []*os.File{os.Stdout, os.Stderr} {
		handle := windows.Handle(stream.Fd())
		var mode uint32
		if err := windows.GetConsoleMode(handle, &mode); err == nil {
			mode |= windows.ENABLE_VIRTUAL_TERMINAL_PROCESSING
			_ = windows.SetConsoleMode(handle, mode)
		}
	}
}

func main() {
	enableANSIConsole()
	// Define command-line flags.
	showConfig := pflag.Bool("show-config", false, "Display the current configuration and exit.")
	checkOnly := pflag.Bool("checkonly", false, "Check for updates, but don't install them.")
	installOnly := pflag.Bool("installonly", false, "Install pending updates without checking for new ones.")
	auto := pflag.Bool("auto", false, "Perform automatic updates.")
	showStatus := pflag.Bool("show-status", false, "Show status window during operations (bootstrap mode).")
	versionFlag := pflag.Bool("version", false, "Print the version and exit.")

	// Bootstrap mode flags - similar to Munki's --set-bootstrap-mode and --clear-bootstrap-mode
	setBootstrapMode := pflag.Bool("set-bootstrap-mode", false, "Enable bootstrap mode for next boot.")
	clearBootstrapMode := pflag.Bool("clear-bootstrap-mode", false, "Disable bootstrap mode.")

	// Count the number of -v flags.
	var verbosity int
	pflag.CountVarP(&verbosity, "verbose", "v", "Increase verbosity (e.g. -v, -vv, -vvv, -vvvv)")
	pflag.Parse()

	// Load configuration (only once)
	cfg, err := config.LoadConfig()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to load configuration: %v\n", err)
		os.Exit(1)
	}

	// Dynamically override LogLevel based on the number of -v flags.
	// 0 => ERROR and SUCCESS, 1 => WARN, 2 => INFO, 3+ => DEBUG
	switch verbosity {
	case 0:
		cfg.LogLevel = "ERROR"
	case 1:
		cfg.LogLevel = "WARN"
	case 2:
		cfg.LogLevel = "INFO"
	default:
		cfg.LogLevel = "DEBUG"
	}

	// Initialize logger.
	logger = logging.New(verbosity > 0)
	if err := logging.Init(cfg); err != nil {
		logger.Fatal("Error initializing logger: %v", err)
	}
	defer logging.CloseLogger()

	// Handle --version flag.
	if *versionFlag {
		version.Print()
		os.Exit(0)
	}

	// Handle bootstrap mode flags first - these exit immediately
	if *setBootstrapMode {
		if err := enableBootstrapMode(); err != nil {
			logger.Error("Failed to enable bootstrap mode: %v", err)
			os.Exit(1)
		}
		logger.Success("Bootstrap mode enabled. System will enter bootstrap mode on next boot.")
		os.Exit(0)
	}

	if *clearBootstrapMode {
		if err := disableBootstrapMode(); err != nil {
			logger.Error("Failed to disable bootstrap mode: %v", err)
			os.Exit(1)
		}
		logger.Success("Bootstrap mode disabled.")
		os.Exit(0)
	}

	// Check if we're in bootstrap mode
	isBootstrap := isBootstrapModeEnabled()
	if isBootstrap {
		logger.Info("Bootstrap mode detected - entering non-interactive installation mode")
		*showStatus = true   // Always show status window in bootstrap mode
		*installOnly = false // Bootstrap mode does check + install
		*checkOnly = false
		*auto = false
	}

	catalogsDir := filepath.Join("C:\\ProgramData\\ManagedInstalls", "catalogs")
	manifestsDir := filepath.Join("C:\\ProgramData\\ManagedInstalls", "manifests")

	if err := cleanManifestsCatalogsPreRun(catalogsDir); err != nil {
		logger.Error("Failed to clean catalogs directory: %v", err)
		os.Exit(1)
	}
	if err := cleanManifestsCatalogsPreRun(manifestsDir); err != nil {
		logger.Error("Failed to clean manifests directory: %v", err)
		os.Exit(1)
	}

	// Handle system signals for graceful shutdown.
	signalChan := make(chan os.Signal, 1)
	signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		sig := <-signalChan
		logger.Warning("Signal received, exiting gracefully: %s", sig.String())
		logging.CloseLogger()
		os.Exit(1)
	}()

	// Run preflight script.
	runPreflightIfNeeded(verbosity)

	// Optionally update configuration based on verbosity.
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}
	// Reinitialize the logger so that any changes in cfg take effect.
	if err := logging.ReInit(cfg); err != nil {
		logger.Fatal("Error re-initializing logger after preflight: %v", err)
	}

	// Show configuration if requested.
	if *showConfig {
		if cfgYaml, err := yaml.Marshal(cfg); err == nil {
			logger.Printf("Current configuration:\n%s", string(cfgYaml))
		}
		os.Exit(0)
	}

	// Ensure mutually exclusive flags are not set.
	if *checkOnly && *installOnly {
		logger.Warning("Conflicting flags: --checkonly and --installonly are mutually exclusive")
		pflag.Usage()
		os.Exit(1)
	}

	// Determine run type.
	var runType string
	if isBootstrap {
		runType = "bootstrap"
		// Bootstrap mode overrides all other flags
		*auto = false
		*checkOnly = false
		*installOnly = false
	} else if *auto {
		runType = "auto"
		*checkOnly = false
		*installOnly = false
	} else if *checkOnly {
		runType = "checkonly"
		*installOnly = false
	} else if *installOnly {
		runType = "installonly"
		*checkOnly = false
	} else {
		runType = "manual"
	}
	logger.Printf("Run type: %s", runType)
	// Check administrative privileges.
	admin, adminErr := adminCheck()
	if adminErr != nil || !admin {
		logger.Fatal("Administrative access required. Error: %v, Admin: %v", adminErr, admin)
	}
	// Initialize status reporter if requested
	var statusReporter status.Reporter
	if *showStatus {
		statusReporter = status.NewPipeReporter()
		if err := statusReporter.Start(context.Background()); err != nil {
			logger.Error("Failed to start status reporter: %v", err)
			statusReporter = status.NewNoOpReporter() // Fallback to no-op
		}
		defer statusReporter.Stop()
		statusReporter.Message("Initializing Cimian...")
	} else {
		statusReporter = status.NewNoOpReporter()
	}

	// Ensure cache directory exists.
	cachePath := cfg.CachePath
	if err := os.MkdirAll(filepath.Clean(cachePath), 0755); err != nil {
		logger.Error("Failed to create cache directory: %v", err)
		os.Exit(1)
	}

	// Retrieve manifests.
	statusReporter.Message("Retrieving manifests...")
	manifestItems, mErr := manifest.AuthenticatedGet(cfg)
	if mErr != nil {
		statusReporter.Error(fmt.Errorf("failed to retrieve manifests: %v", mErr))
		logger.Error("Failed to retrieve manifests: %v", mErr)
		os.Exit(1)
	}

	statusReporter.Detail("Loading catalog data...")
	// Load local catalogs into a map (keys are lowercase names).
	localCatalogMap, err := loadLocalCatalogItems(cfg)
	if err != nil {
		statusReporter.Error(fmt.Errorf("failed to load local catalogs: %v", err))
		logger.Error("Failed to load local catalogs: %v", err)
		os.Exit(1)
	}

	// Convert to the expected format for advanced dependency processing
	statusReporter.Detail("Processing dependencies...")
	fullCatalogMap := catalog.AuthenticatedGet(*cfg)

	// If install-only mode, perform installs and exit.
	if *installOnly {
		logger.Info("Running in install-only mode")
		statusReporter.Message("Installing pending updates...")
		itemsToInstall := prepareDownloadItemsWithCatalog(manifestItems, localCatalogMap, cfg)
		if err := downloadAndInstallPerItem(itemsToInstall, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("failed to install pending updates: %v", err))
			logger.Error("Failed to install pending updates (install-only): %v", err)
			os.Exit(1)
		}
		statusReporter.Message("Installation completed successfully!")
		os.Exit(0)
	}

	// Gather actions: updates, new installs, removals.
	statusReporter.Detail("Analyzing required changes...")
	var toInstall []catalog.Item
	var toUpdate []catalog.Item
	var toUninstall []catalog.Item

	dedupedManifestItems := status.DeduplicateManifestItems(manifestItems)
	itemsToProcess := prepareDownloadItemsWithCatalog(dedupedManifestItems, localCatalogMap, cfg)

	toUpdate = itemsToProcess
	toInstall = identifyNewInstalls(dedupedManifestItems, localCatalogMap, cfg)
	toUninstall = identifyRemovals(localCatalogMap, cfg)

	// Print summary of planned actions.
	statusReporter.Detail(fmt.Sprintf("Found %d updates, %d new installs, %d removals", len(toUpdate), len(toInstall), len(toUninstall)))
	printPendingActions(toInstall, toUninstall, toUpdate)

	// If check-only mode, exit after summary.
	if *checkOnly {
		os.Exit(0)
	}

	// Prompt user for confirmation.
	fmt.Print("Proceed with installations/updates/uninstalls? (Y/n): ")
	var response string
	fmt.Scanln(&response)
	if strings.TrimSpace(strings.ToLower(response)) != "y" && response != "" {
		logger.Info("User aborted installation.")
		os.Exit(0)
	} // Combine install and update items and perform installations.
	var allToInstall []catalog.Item
	allToInstall = append(allToInstall, toInstall...)
	allToInstall = append(allToInstall, toUpdate...)
	if len(allToInstall) > 0 {
		statusReporter.Message("Installing and updating applications...")
		statusReporter.Percent(0) // Start progress tracking
		if err := downloadAndInstallWithAdvancedLogic(allToInstall, fullCatalogMap, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("failed installing items: %v", err))
			logger.Error("Failed installing items: %v", err)
		} else {
			statusReporter.Percent(50) // Mid-way progress
		}
	}

	// Process uninstalls.
	if len(toUninstall) > 0 {
		statusReporter.Message("Removing applications...")
		statusReporter.Percent(75) // Progress at 75%
		if err := uninstallWithAdvancedLogic(toUninstall, fullCatalogMap, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("failed uninstalling items: %v", err))
			logger.Error("Failed uninstalling items: %v", err)
		}
	}

	// For auto mode: if the user is active, skip updates.
	if *auto {
		if isUserActive() {
			logger.Info("User is active. Skipping automatic updates: %d", getIdleSeconds())
			os.Exit(0)
		}
	}

	logger.Info("Software updates completed")
	statusReporter.Message("Finalizing installation...")
	statusReporter.Percent(90)

	// Run postflight script.
	if verbosity > 0 {
		logger.Debug("Running postflight script with verbosity level: %d", verbosity)
	}
	statusReporter.Detail("Running post-installation scripts...")
	runPostflightIfNeeded(verbosity)
	logger.Success("Postflight script completed.")

	statusReporter.Detail("Cleaning up temporary files...")
	cacheFolder := `C:\ProgramData\ManagedInstalls\Cache`
	logsFolder := `C:\ProgramData\ManagedInstalls\Logs`
	clearCacheFolderSelective(cacheFolder, logsFolder)

	// Clear bootstrap mode if we completed successfully
	if isBootstrap {
		if err := clearBootstrapAfterSuccess(); err != nil {
			logger.Warning("Failed to clear bootstrap mode: %v", err)
		}
	}

	statusReporter.Message("All operations completed successfully!")
	statusReporter.Percent(100)
	statusReporter.Stop()

	os.Exit(0)
}

// runPreflightIfNeeded runs the preflight script.
func runPreflightIfNeeded(verbosity int) {
	logInfo := func(format string, args ...interface{}) {
		logger.Debug(format, args...)
	}
	logError := func(format string, args ...interface{}) {
		logger.Error(format, args...)
	}

	if err := scripts.RunPreflight(verbosity, logInfo, logError); err != nil {
		logger.Error("Preflight script failed: %v", err)
		// Don't exit - preflight script failures should not be fatal
		// os.Exit(1)
	}
}

// runPostflightIfNeeded runs the postflight script.
func runPostflightIfNeeded(verbosity int) {
	logInfo := func(format string, args ...interface{}) {
		logger.Debug(format, args...)
	}
	logError := func(format string, args ...interface{}) {
		logger.Error(format, args...)
	}

	if err := scripts.RunPostflight(verbosity, logInfo, logError); err != nil {
		logger.Error("Postflight script failed: %v", err)
	}
}

// adminCheck verifies whether the current process has administrative privileges.
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

// identifyRemovals enumerates all installed items (registry subkeys)
// and returns only those items that are both present in the local catalog
// and are explicitly marked for removal (for example, if their catalog item
// has an Uninstaller specified or an uninstall check defined).
func identifyRemovals(localCatalogMap map[string]catalog.Item, _ *config.Configuration) []catalog.Item {
	var toRemove []catalog.Item

	// Open the registry key for ManagedInstalls.
	regKeyPath := `SOFTWARE\ManagedInstalls`
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, regKeyPath, registry.READ)
	if err != nil {
		if err == registry.ErrNotExist {
			logging.Debug("No items in HKLM\\Software\\ManagedInstalls => no removals")
			return toRemove
		}
		logging.Warn("Failed to open registry key for removals", "key", regKeyPath, "error", err)
		return toRemove
	}
	defer k.Close()

	// Iterate over the local catalog; we ignore any installed items that are no longer in the catalog.
	for _, catItem := range localCatalogMap {
		// Try to open the registry key for this catalog item.
		regKeyItemPath := regKeyPath + `\` + catItem.Name
		_, err := registry.OpenKey(registry.LOCAL_MACHINE, regKeyItemPath, registry.READ)
		if err != nil {
			// If there's no registry key for this catalog item, skip it.
			continue
		}
		// Only mark for removal if the catalog explicitly indicates it should be uninstalled.
		// For example, if the Uninstaller field is non-empty or if an uninstall check is defined.
		if catItem.Uninstaller.Location != "" || (catItem.Check.Registry.Name != "" && catItem.Check.Registry.Version != "") {
			logging.Info("Catalog item marked for removal", "item", catItem.Name)
			toRemove = append(toRemove, catItem)
		}
	}

	return toRemove
}

// identifyNewInstalls checks each manifest item and returns those NOT present in the local catalog.
func identifyNewInstalls(manifestItems []manifest.Item, localCatalogMap map[string]catalog.Item, cfg *config.Configuration) []catalog.Item {
	_ = cfg // dummy reference to suppress "unused parameter" warning

	var toInstall []catalog.Item
	for _, mItem := range manifestItems {
		if mItem.Name == "" {
			continue
		}
		key := strings.ToLower(mItem.Name)
		if _, found := localCatalogMap[key]; !found {
			logging.Info("Identified new item for installation", "item", mItem.Name)
			newCatItem := catalog.Item{
				Name:    mItem.Name,
				Version: mItem.Version,
				Installer: catalog.InstallerItem{
					Location: mItem.InstallerLocation,
					Type:     "exe", // or "msi"/"nupkg" if determinable
				},
				SupportedArch: mItem.SupportedArch,
			}
			toInstall = append(toInstall, newCatItem)
		}
	}
	return toInstall
}

// uninstallCatalogItems loops over the items and uninstalls each.
func uninstallCatalogItems(items []catalog.Item, cfg *config.Configuration) error {
	_ = cfg // dummy reference to suppress "unused parameter" warning

	if len(items) == 0 {
		logging.Debug("No items to uninstall.")
		return nil
	}
	logging.Info("Starting batch uninstall of items", "count", len(items))
	for _, item := range items {
		_, err := installer.Install(item, "uninstall", "", cfg.CachePath, cfg.CheckOnly, cfg)
		if err != nil {
			return fmt.Errorf("failed uninstalling '%s': %w", item.Name, err)
		}
		logging.Info("Uninstall successful", "item", item.Name)
	}
	return nil
}

// Wrapper struct for the top-level "items" key:
type catalogWrapper struct {
	Items []catalog.Item `yaml:"items"`
}

// loadLocalCatalogItems returns a map[string]catalog.Item, but only the
// highest-version item if a name appears in multiple times.
func loadLocalCatalogItems(cfg *config.Configuration) (map[string]catalog.Item, error) {
	// Make sure the catalogs directory exists (or create it).
	if err := os.MkdirAll(cfg.CatalogsPath, 0755); err != nil {
		return nil, fmt.Errorf("error creating catalogs directory: %v", err)
	}

	// We will first collect items in a "multi-map" so that if a name appears
	// in multiple catalogs, we keep each one for a version check.
	itemsMulti := make(map[string][]catalog.Item)

	dirEntries, err := os.ReadDir(cfg.CatalogsPath)
	if err != nil {
		return nil, fmt.Errorf("failed reading catalogs dir %q: %v", cfg.CatalogsPath, err)
	}

	type catalogWrapper struct {
		Items []catalog.Item `yaml:"items"`
	}

	for _, entry := range dirEntries {
		if entry.IsDir() {
			continue
		}
		if filepath.Ext(entry.Name()) != ".yaml" {
			// skip any non-YAML
			continue
		}

		// Build full path to the .yaml catalog file.
		catPath := filepath.Join(cfg.CatalogsPath, entry.Name())

		data, readErr := os.ReadFile(catPath)
		if readErr != nil {
			logging.Warn("Failed to read catalog file %s: %v", catPath, readErr)
			continue
		}

		var wrapper catalogWrapper
		if err := yaml.Unmarshal(data, &wrapper); err != nil {
			logging.Warn("Failed to parse catalog YAML %s: %v", catPath, err)
			continue
		}

		// catItems is the slice from the "items" array in that catalog.
		catItems := wrapper.Items
		// Add them to our multi-map keyed by item name, in lowercase for deduping.
		for _, cItem := range catItems {
			key := strings.ToLower(cItem.Name)
			itemsMulti[key] = append(itemsMulti[key], cItem)
		}
	}

	// Now deduplicate by picking the highest-version item for each name.
	finalMap := make(map[string]catalog.Item)
	for key, sliceOfItems := range itemsMulti {
		// Use your existing logic (e.g. status.DeduplicateCatalogItems) to pick a “best” item.
		// For demonstration, we’ll just pick the first if you don't have version logic:
		bestItem := status.DeduplicateCatalogItems(sliceOfItems)
		finalMap[key] = bestItem
	}

	return finalMap, nil
}

// prepareDownloadItemsWithCatalog returns the catalog items that need to be installed/updated,
// using the deduplicated manifest items.
func prepareDownloadItemsWithCatalog(manifestItems []manifest.Item, catMap map[string]catalog.Item, cfg *config.Configuration) []catalog.Item {
	var results []catalog.Item
	dedupedItems := status.DeduplicateManifestItems(manifestItems)
	for _, m := range dedupedItems {
		if installer.LocalNeedsUpdate(m, catMap, cfg) {
			key := strings.ToLower(m.Name)
			catItem, found := catMap[key]
			if !found {
				logger.Warning("Skipping item not in local catalog: %s", m.Name)
				continue
			}
			results = append(results, catItem)
		}
	}
	return results
}

// downloadAndInstallPerItem handles downloading & installing each catalog item individually,
// ensuring exact file paths match installer expectations.
func downloadAndInstallPerItem(items []catalog.Item, cfg *config.Configuration, statusReporter status.Reporter) error {
	downloadItems := make(map[string]string)

	// Prepare the correct full URLs for each item
	for _, cItem := range items {
		if cItem.Installer.Location == "" {
			logger.Warning("No installer location found for item: %s", cItem.Name)
			continue
		}

		fullURL := cItem.Installer.Location
		if strings.HasPrefix(fullURL, "/") || strings.HasPrefix(fullURL, "\\") {
			fullURL = strings.ReplaceAll(fullURL, "\\", "/")
			if !strings.HasPrefix(fullURL, "/") {
				fullURL = "/" + fullURL
			}
			fullURL = strings.TrimRight(cfg.SoftwareRepoURL, "/") + "/pkgs" + fullURL
		}

		downloadItems[cItem.Name] = fullURL
	}

	// Download each item and retrieve precise downloaded file paths
	downloadedPaths, err := download.InstallPendingUpdates(downloadItems, cfg)
	if err != nil {
		logger.Error("Error downloading pending updates: %v", err)
		return err
	}

	// Perform installation for each item using the correct paths
	for _, cItem := range items {
		localFile, exists := downloadedPaths[cItem.Name]
		if !exists {
			logger.Error("Downloaded path not found for item: %s", cItem.Name)
			continue
		}

		logger.Info("Installing downloaded item: %s, file: %s", cItem.Name, localFile)

		if err := installOneCatalogItem(cItem, localFile, cfg); err != nil {
			logger.Error("Installation command failed: %s, error: %v", cItem.Name, err)
			continue
		}
	}

	return nil
}

// downloadAndInstallWithAdvancedLogic handles downloading & installing with advanced dependency logic
func downloadAndInstallWithAdvancedLogic(items []catalog.Item, catalogMap map[int]map[string]catalog.Item, cfg *config.Configuration, statusReporter status.Reporter) error {
	// Get list of currently installed items for dependency checking
	statusReporter.Detail("Checking currently installed items...")
	installedItems := getInstalledItemNames()

	// Convert catalog.Item slice to string slice for processing
	var itemNames []string
	for _, item := range items {
		itemNames = append(itemNames, item.Name)
	}

	statusReporter.Detail(fmt.Sprintf("Processing %d items for installation...", len(itemNames)))
	// Use the new advanced dependency processing
	process.InstallsWithAdvancedLogic(itemNames, catalogMap, installedItems, cfg.CachePath, false, cfg)

	return nil
}

// uninstallWithAdvancedLogic handles uninstalling with advanced dependency logic
func uninstallWithAdvancedLogic(items []catalog.Item, catalogMap map[int]map[string]catalog.Item, cfg *config.Configuration, statusReporter status.Reporter) error {
	// Get list of currently installed items for dependency checking
	installedItems := getInstalledItemNames()

	// Convert catalog.Item slice to string slice for processing
	var itemNames []string
	for _, item := range items {
		itemNames = append(itemNames, item.Name)
	}
	// Use the new advanced dependency processing
	process.UninstallsWithAdvancedLogic(itemNames, catalogMap, installedItems, cfg.CachePath, false, cfg)

	return nil
}

// getInstalledItemNames returns a list of currently installed item names
// This would typically check the registry or local state to determine what's installed
func getInstalledItemNames() []string {
	// TODO: Implement proper installed items detection
	// For now, return empty slice - this should be enhanced to read from
	// registry or local state to determine what packages are actually installed
	return []string{}
}

// printPendingActions prints a summary of planned actions: installs, updates, and uninstalls.
func printPendingActions(toInstall, toUninstall, toUpdate []catalog.Item) {
	logger.Info("")
	logger.Info("Summary of planned actions:")
	logger.Info("")
	// Check if no actions are planned.
	if len(toInstall) == 0 && len(toUninstall) == 0 && len(toUpdate) == 0 {
		logger.Info("No actions are planned.")
		return
	}

	// Helper to print a table header and divider.
	printTableHeader := func(header string) {
		logger.Info(strings.Repeat("-", len(header)))
	}

	// Print INSTALL actions as a table.
	if len(toInstall) > 0 {
		logger.Info("Will install these items:")
		// Changed format: Version column now 20 characters wide.
		printTableHeader(fmt.Sprintf("%-20s %-20s %s", "Name", "Version", "Installer"))
		for _, item := range toInstall {
			logger.Info("%-20s %-20s %s", item.Name, item.Version, item.Installer.Location)
		}
		logger.Info("")
	}

	// Print UPDATE actions as a table.
	if len(toUpdate) > 0 {
		logger.Info("Will update these items:")
		printTableHeader(fmt.Sprintf("%-20s %-20s %s", "Name", "Version", "Installer"))
		for _, item := range toUpdate {
			logger.Info("%-20s %-20s %s", item.Name, item.Version, item.Installer.Location)
		}
		logger.Info("")
	}

	// Print UNINSTALL actions as a table.
	if len(toUninstall) > 0 {
		logger.Info("Will remove these items:")
		printTableHeader(fmt.Sprintf("%-20s %-20s %s", "Name", "InstalledVersion", "Uninstaller"))
		for _, item := range toUninstall {
			logger.Info("%-20s %-20s %s", item.Name, "?", item.Uninstaller.Location)
		}
		logger.Info("")
	}
}

// installOneCatalogItem installs a single catalog item using the installer package.
// It normalizes the architecture and handles installation output for error detection.
func installOneCatalogItem(cItem catalog.Item, localFile string, cfg *config.Configuration) error {
	normalizeArchitecture(&cItem)
	sysArch := status.GetSystemArchitecture()
	logging.Debug("Detected system architecture: %s", sysArch)
	logging.Debug("Supported architectures for item: %s, supported_arch: %v", cItem.Name, cItem.SupportedArch)

	// Actually install
	installedOutput, installErr := installer.Install(cItem, "install", localFile, cfg.CachePath, cfg.CheckOnly, cfg)
	if installErr != nil {
		// DO NOT say "Installed item successfully" because it failed
		return installErr
	}

	// If we get here => success
	logger.Info("Install output: %s, output: %s", cItem.Name, installedOutput)
	logger.Info("Installed item successfully: %s", cItem.Name)

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
	sysArch := status.GetSystemArchitecture()
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
	tickCount, _, err2 := syscall.NewLazyDLL("kernel32.dll").NewProc("GetTickCount").Call()
	if tickCount == 0 {
		fmt.Printf("Error getting tick count: %v\n", err2)
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

// clearCacheFolderSelective reads log files from logsPath (e.g. "C:\ProgramData\ManagedInstalls\Logs")
// and only deletes files in cachePath (e.g. "C:\ProgramData\ManagedInstalls\Cache")
// that are associated with a successful installation.
func clearCacheFolderSelective(cachePath, logsPath string) {
	// successSet will hold the base names (e.g. "GitCredentialManager-x64-2.6.0.0.exe")
	// of installer files that ran successfully.
	successSet := make(map[string]bool)

	// Read log files from the logsPath
	logFiles, err := os.ReadDir(logsPath)
	if err != nil {
		logger.Warning("Failed to read logs directory: %v", err)
	} else {
		for _, entry := range logFiles {
			if entry.IsDir() {
				continue
			}
			logFilePath := filepath.Join(logsPath, entry.Name())
			data, err := os.ReadFile(logFilePath)
			if err != nil {
				logger.Warning("Failed to read log file %s: %v", logFilePath, err)
				continue
			}
			// Split the log file into lines
			lines := strings.Split(string(data), "\n")
			for _, line := range lines {
				// Look for lines where an installer was successfully downloaded
				if strings.Contains(line, "Downloaded item successfully:") {
					// Expect a pattern like: "file: C:\ProgramData\ManagedInstalls\cache\SomeApp-x64-1.2.3.exe"
					idx := strings.Index(line, "file:")
					if idx != -1 {
						filePart := strings.TrimSpace(line[idx+len("file:"):])
						baseName := filepath.Base(filePart)
						successSet[baseName] = true
					}
				}
				// Also check for an explicit "Installed item successfully:" log line.
				if strings.Contains(line, "Installed item successfully:") {
					idx := strings.Index(line, "file:")
					if idx != -1 {
						filePart := strings.TrimSpace(line[idx+len("file:"):])
						baseName := filepath.Base(filePart)
						successSet[baseName] = true
					}
				}
				// If an installation failed, remove any matching installer from the success set.
				if strings.Contains(line, "Installation failed") {
					// Expect a pattern like: "Installation failed item=SomeApp error=..."
					idx := strings.Index(line, "item=")
					if idx != -1 {
						afterItem := line[idx+len("item="):]
						fields := strings.Fields(afterItem)
						if len(fields) > 0 {
							failedItem := fields[0]
							// Remove any installer whose name starts with the failed item plus a hyphen.
							for k := range successSet {
								if strings.HasPrefix(k, failedItem+"-") {
									delete(successSet, k)
								}
							}
						}
					}
				}
			}
		}
	}

	// Now iterate through the cache folder.
	cacheEntries, err := os.ReadDir(cachePath)
	if err != nil {
		logger.Warning("Failed to read cache directory: %v", err)
		return
	}

	for _, entry := range cacheEntries {
		fullPath := filepath.Join(cachePath, entry.Name())
		// Only remove the file if its base name is in the success set.
		if successSet[entry.Name()] {
			err := os.RemoveAll(fullPath)
			if err != nil {
				logger.Warning("Failed to remove cached item %s: %v", fullPath, err)
			} else {
				logger.Debug("Removed cached item: %s", fullPath)
			}
		} else {
			logger.Debug("Retaining cached item: %s", fullPath)
		}
	}
	logger.Info("Selective cache clearing complete for folder: %s", cachePath)
}

func cleanManifestsCatalogsPreRun(dirPath string) error {
	if err := os.RemoveAll(dirPath); err != nil {
		return fmt.Errorf("failed to remove %s: %w", dirPath, err)
	}
	if err := os.MkdirAll(dirPath, 0755); err != nil {
		return fmt.Errorf("failed to create %s: %w", dirPath, err)
	}
	logging.Debug("Cleaned and recreated directory", "dirPath", dirPath)
	return nil
}

// Bootstrap mode functions - Windows equivalent of Munki's bootstrap system

// isBootstrapModeEnabled checks if bootstrap mode is enabled by checking for the flag file
func isBootstrapModeEnabled() bool {
	if _, err := os.Stat(BootstrapFlagFile); err == nil {
		return true
	}
	return false
}

// enableBootstrapMode creates the bootstrap flag file - similar to Munki's --set-bootstrap-mode
func enableBootstrapMode() error {
	// Create the flag file
	file, err := os.Create(BootstrapFlagFile)
	if err != nil {
		return fmt.Errorf("failed to create bootstrap flag file: %w", err)
	}
	defer file.Close()

	// Write creation timestamp to the file
	timestamp := fmt.Sprintf("Bootstrap mode enabled at: %s\n", time.Now().Format(time.RFC3339))
	if _, err := file.WriteString(timestamp); err != nil {
		return fmt.Errorf("failed to write to bootstrap flag file: %w", err)
	}

	// Update the scheduled task to run at startup if not already configured
	if err := ensureBootstrapScheduledTask(); err != nil {
		// Log warning but don't fail - the task might already exist
		logger.Warning("Failed to update scheduled task for bootstrap mode: %v", err)
	}

	return nil
}

// disableBootstrapMode removes the bootstrap flag file - similar to Munki's --clear-bootstrap-mode
func disableBootstrapMode() error {
	if _, err := os.Stat(BootstrapFlagFile); os.IsNotExist(err) {
		// File doesn't exist, nothing to do
		return nil
	}

	if err := os.Remove(BootstrapFlagFile); err != nil {
		return fmt.Errorf("failed to remove bootstrap flag file: %w", err)
	}

	// Also remove the bootstrap scheduled task when disabling bootstrap mode
	if err := removeBootstrapScheduledTask(); err != nil {
		// Log warning but don't fail - the task might not exist
		logger.Warning("Failed to remove bootstrap scheduled task: %v", err)
	}

	return nil
}

// ensureBootstrapScheduledTask ensures the scheduled task is configured for bootstrap mode
func ensureBootstrapScheduledTask() error {
	return createBootstrapScheduledTask()
}

// createBootstrapScheduledTask creates a separate scheduled task specifically for bootstrap mode
// This task runs at system startup and checks for the bootstrap flag file
func createBootstrapScheduledTask() error {
	// Get the current executable path
	exePath, err := os.Executable()
	if err != nil {
		return fmt.Errorf("failed to get executable path: %w", err)
	}

	// Create the bootstrap task that runs at startup
	// This task will run every time the system starts and check for bootstrap mode
	taskName := "CimianBootstrapCheck"
	taskCmd := fmt.Sprintf(`"%s" --auto --show-status`, exePath)

	// First, delete any existing bootstrap task to ensure clean creation
	deleteCmd := fmt.Sprintf(`SCHTASKS.EXE /DELETE /F /TN "%s"`, taskName)
	if err := runWindowsCommand(deleteCmd); err != nil {
		// Ignore errors when deleting - task might not exist
		logger.Debug("Could not delete existing bootstrap task (this is normal if it doesn't exist): %v", err)
	}

	// Create the new bootstrap task
	// This task runs at system startup with HIGHEST privileges as SYSTEM
	createCmd := fmt.Sprintf(
		`SCHTASKS.EXE /CREATE /F /SC ONSTART /TN "%s" /TR "%s" /RU SYSTEM /RL HIGHEST /DELAY 0000:30`,
		taskName, taskCmd)

	if err := runWindowsCommand(createCmd); err != nil {
		return fmt.Errorf("failed to create bootstrap scheduled task: %w", err)
	}

	logger.Info("Created bootstrap scheduled task: %s", taskName)
	return nil
}

// removeBootstrapScheduledTask removes the bootstrap scheduled task
func removeBootstrapScheduledTask() error {
	taskName := "CimianBootstrapCheck"
	deleteCmd := fmt.Sprintf(`SCHTASKS.EXE /DELETE /F /TN "%s"`, taskName)

	if err := runWindowsCommand(deleteCmd); err != nil {
		return fmt.Errorf("failed to remove bootstrap scheduled task: %w", err)
	}

	logger.Info("Removed bootstrap scheduled task: %s", taskName)
	return nil
}

// runWindowsCommand executes a Windows command and returns any error
func runWindowsCommand(command string) error {
	// Split the command to get the program and arguments
	parts := strings.Fields(command)
	if len(parts) == 0 {
		return fmt.Errorf("empty command")
	}

	cmd := exec.Command(parts[0], parts[1:]...)
	output, err := cmd.CombinedOutput()

	logger.Debug("Executing command: %s", command)
	logger.Debug("Command output: %s", string(output))

	if err != nil {
		return fmt.Errorf("command failed: %s, output: %s, error: %w", command, string(output), err)
	}

	return nil
}

// clearBootstrapAfterSuccess removes the bootstrap flag after successful completion
func clearBootstrapAfterSuccess() error {
	if !isBootstrapModeEnabled() {
		return nil
	}

	logging.Info("Bootstrap process completed successfully - clearing bootstrap mode")
	return disableBootstrapMode()
}
