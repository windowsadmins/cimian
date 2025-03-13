// cmd/managedsoftwareupdate/main.go

package main

import (
	"fmt"
	"io/ioutil"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"unsafe"

	"github.com/spf13/pflag"
	"gopkg.in/yaml.v3"

	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/installer"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
	"github.com/windowsadmins/cimian/pkg/scripts"
	"github.com/windowsadmins/cimian/pkg/version"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

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
	versionFlag := pflag.Bool("version", false, "Print the version and exit.")

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
	if *auto {
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

	// Ensure cache directory exists.
	cachePath := cfg.CachePath
	if err := os.MkdirAll(filepath.Clean(cachePath), 0755); err != nil {
		logger.Error("Failed to create cache directory: %v", err)
		os.Exit(1)
	}

	// Retrieve manifests.
	manifestItems, mErr := manifest.AuthenticatedGet(cfg)
	if mErr != nil {
		logger.Error("Failed to retrieve manifests: %v", mErr)
		os.Exit(1)
	}

	// Load local catalogs into a map (keys are lowercase names).
	localCatalogMap, err := loadLocalCatalogItems(cfg)
	if err != nil {
		logger.Error("Failed to load local catalogs: %v", err)
		os.Exit(1)
	}

	// If install-only mode, perform installs and exit.
	if *installOnly {
		logger.Info("Running in install-only mode")
		itemsToInstall := prepareDownloadItemsWithCatalog(manifestItems, localCatalogMap, cfg)
		if err := downloadAndInstallPerItem(itemsToInstall, cfg); err != nil {
			logger.Error("Failed to install pending updates (install-only): %v", err)
			os.Exit(1)
		}
		os.Exit(0)
	}

	// Gather actions: updates, new installs, removals.
	var toInstall []catalog.Item
	var toUpdate []catalog.Item
	var toUninstall []catalog.Item

	updatesAvailable := checkForUpdatesWithCatalog(cfg, verbosity, manifestItems, localCatalogMap)
	if updatesAvailable {
		toUpdate = prepareDownloadItemsWithCatalog(manifestItems, localCatalogMap, cfg)
	}
	toInstall = identifyNewInstalls(manifestItems, localCatalogMap, cfg)
	toUninstall = identifyRemovals(localCatalogMap, cfg)

	// Print summary of planned actions.
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
	}

	// Combine install and update items and perform installations.
	var allToInstall []catalog.Item
	allToInstall = append(allToInstall, toInstall...)
	allToInstall = append(allToInstall, toUpdate...)
	if len(allToInstall) > 0 {
		if err := downloadAndInstallPerItem(allToInstall, cfg); err != nil {
			logger.Error("Failed installing items: %v", err)
		}
	}

	// Process uninstalls.
	if len(toUninstall) > 0 {
		if err := uninstallCatalogItems(toUninstall, cfg); err != nil {
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

	// Run postflight script.
	if verbosity > 0 {
		logger.Debug("Running postflight script with verbosity level: %d", verbosity)
	}
	runPostflightIfNeeded(verbosity)
	logger.Success("Postflight script completed.")

	cacheFolder := `C:\ProgramData\ManagedInstalls\Cache`
	logsFolder := `C:\ProgramData\ManagedInstalls\Logs`
	clearCacheFolderSelective(cacheFolder, logsFolder)

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
		os.Exit(1)
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

// loadLocalCatalogItems reads all .yaml files in cfg.CatalogsPath and returns a map of catalog items.
func loadLocalCatalogItems(cfg *config.Configuration) (map[string]catalog.Item, error) {
	// Create catalogs directory if it doesn't exist
	if err := os.MkdirAll(cfg.CatalogsPath, 0755); err != nil {
		return nil, fmt.Errorf("creating catalogs dir: %v", err)
	}

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
			logger.Warning("Failed to read catalog file: %v", err)
			continue
		}
		var catItems []catalog.Item
		if err := yaml.Unmarshal(data, &catItems); err != nil {
			logger.Warning("Failed to parse catalog YAML: %v", err)
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
	logger.Info("Checking for updates...")
	updates := false

	for _, item := range manifestItems {
		if verbosity > 0 {
			logger.Info("Checking item: %s, version: %s", item.Name, item.Version)
		}
		if installer.LocalNeedsUpdate(item, catMap, cfg) {
			logger.Info("Update available for package: %s", item.Name)
			updates = true
		} else {
			logger.Info("No update needed for package: %s", item.Name)
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
				logger.Warning("Skipping item not in local catalog: %s", m.Name)
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
	_ = cfg
	for _, cItem := range items {
		if cItem.Installer.Location == "" {
			logger.Warning("No installer location found for item: %s", cItem.Name)
			continue
		}

		// Build the full URL
		fullURL := cItem.Installer.Location
		if strings.HasPrefix(fullURL, "/") || strings.HasPrefix(fullURL, "\\") {
			// Normalize path separators to forward slashes
			fullURL = strings.ReplaceAll(fullURL, "\\", "/")
			// Ensure path starts with /
			if !strings.HasPrefix(fullURL, "/") {
				fullURL = "/" + fullURL
			}
			// Insert "/pkgs" between the repo URL and the relative path
			fullURL = strings.TrimRight(cfg.SoftwareRepoURL, "/") + "/pkgs" + fullURL
		}

		// Destination file path in cache
		destFile := filepath.Join(cfg.CachePath, filepath.Base(cItem.Installer.Location))

		// Download the installer
		logger.Info("Downloading item: %s, url: %s, destination: %s", cItem.Name, fullURL, destFile)
		if err := download.DownloadFile(fullURL, destFile, cfg); err != nil {
			logger.Error("Failed to download item: %s, error: %v", cItem.Name, err)
			continue
		}
		logger.Info("Downloaded item successfully: %s, file: %s", cItem.Name, destFile)

		// Perform the install using your custom function
		if err := installOneCatalogItem(cItem, destFile, cfg); err != nil {
			logger.Error("Installation command failed: %s, error: %v", cItem.Name, err)
			continue
		}
	}
	return nil
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
	sysArch := getSystemArchitecture()
	logger.Debug("Detected system architecture: %s", sysArch)
	logger.Debug("Supported architectures for item: %s, supported_arch: %v", cItem.Name, cItem.SupportedArch)

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
	logFiles, err := ioutil.ReadDir(logsPath)
	if err != nil {
		logger.Warning("Failed to read logs directory: %v", err)
	} else {
		for _, entry := range logFiles {
			if entry.IsDir() {
				continue
			}
			logFilePath := filepath.Join(logsPath, entry.Name())
			data, err := ioutil.ReadFile(logFilePath)
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
	cacheEntries, err := ioutil.ReadDir(cachePath)
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
	logging.Debug("Cleaned and recreated directory: %s", dirPath)
	return nil
}
