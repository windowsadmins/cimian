// cmd/managedsoftwareupdate/main.go

package main

import (
	"context"
	"fmt"
	"os"
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

	"github.com/windowsadmins/cimian/pkg/blocking"
	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/filter"
	"github.com/windowsadmins/cimian/pkg/installer"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
	"github.com/windowsadmins/cimian/pkg/process"
	"github.com/windowsadmins/cimian/pkg/reporting"
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

	// Munki-compatible flags for preflight bypass and manifest override
	noPreflight := pflag.Bool("no-preflight", false, "Skip preflight script execution.")
	localOnlyManifest := pflag.String("local-only-manifest", "", "Use specified local manifest file instead of server manifest.")

	// Manifest targeting flag - process only a specific manifest from server
	manifestTarget := pflag.String("manifest", "", "Process only the specified manifest from server (e.g., 'Shared/Curriculum/RenderingFarm'). Automatically skips preflight.")

	// Report generation flag - regenerate reports from existing logs without any installation
	reportOnly := pflag.Bool("report", false, "Generate reports from existing logs without performing any installation or updates.")

	// Initialize item filter and register its flags before parsing
	itemFilter := filter.NewItemFilter(nil) // logger will be set later
	itemFilter.RegisterFlags()

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
		logging.Fatal("Error initializing logger", "error", err)
	}
	defer logging.CloseLogger()

	// Update the item filter with the initialized logger
	itemFilter.SetLogger(logger)

	// Handle --version flag.
	if *versionFlag {
		version.Print()
		os.Exit(0)
	}

	// Handle --report flag - generate reports from existing logs and exit
	if *reportOnly {
		logging.Info("Generating reports from existing logs...")

		// Initialize minimal logging for report generation
		logger = logging.New(verbosity > 0)
		defer logging.CloseLogger()

		// Generate reports from existing log data
		exporter := reporting.NewDataExporter(`C:\ProgramData\ManagedInstalls\logs`)
		if err := exporter.ExportToReportsDirectory(30); err != nil { // Last 30 days
			logging.Error("Error generating reports", "error", err)
			os.Exit(1)
		}

		logging.Success("Reports generated successfully", "location", `C:\ProgramData\ManagedInstalls\reports\`)
		os.Exit(0)
	}

	// Handle bootstrap mode flags first - these exit immediately
	if *setBootstrapMode {
		if err := enableBootstrapMode(); err != nil {
			logging.Error("Failed to enable bootstrap mode", "error", err)
			os.Exit(1)
		}
		logger.Success("Bootstrap mode enabled. System will enter bootstrap mode on next boot.")
		os.Exit(0)
	}

	if *clearBootstrapMode {
		if err := disableBootstrapMode(); err != nil {
			logging.Error("Failed to disable bootstrap mode", "error", err)
			os.Exit(1)
		}
		logger.Success("Bootstrap mode disabled.")
		os.Exit(0)
	}

	// Check if we're in bootstrap mode
	isBootstrap := isBootstrapModeEnabled()
	if isBootstrap {
		logging.Info("Bootstrap mode detected - entering non-interactive installation mode")
		*showStatus = true   // Always show status window in bootstrap mode
		*installOnly = false // Bootstrap mode does check + install
		*checkOnly = false
		*auto = false
	}

	catalogsDir := filepath.Join("C:\\ProgramData\\ManagedInstalls", "catalogs")
	manifestsDir := filepath.Join("C:\\ProgramData\\ManagedInstalls", "manifests")

	if err := cleanManifestsCatalogsPreRun(catalogsDir); err != nil {
		logging.Error("Failed to clean catalogs directory", "error", err)
		os.Exit(1)
	}
	if err := cleanManifestsCatalogsPreRun(manifestsDir); err != nil {
		logging.Error("Failed to clean manifests directory", "error", err)
		os.Exit(1)
	}

	// Handle system signals for graceful shutdown.
	signalChan := make(chan os.Signal, 1)
	signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		sig := <-signalChan
		logging.Warn("Signal received, exiting gracefully", "signal", sig.String())
		logging.CloseLogger()
		os.Exit(1)
	}()

	// Run preflight script (unless bypassed by flag or config).
	skipPreflight := *noPreflight || cfg.NoPreflight || (*manifestTarget != "")
	if !skipPreflight {
		runPreflightIfNeeded(verbosity)
	} else {
		if *noPreflight {
			logging.Info("Preflight script execution bypassed by --no-preflight flag")
		} else if *manifestTarget != "" {
			logging.Info("Preflight script execution bypassed by --manifest flag")
		} else {
			logging.Info("Preflight script execution bypassed by NoPreflight configuration setting")
		}
	}

	// Optionally update configuration based on verbosity.
	if verbosity > 0 {
		cfg.Verbose = true
		if verbosity >= 3 {
			cfg.Debug = true
		}
	}
	// Reinitialize the logger so that any changes in cfg take effect.
	if err := logging.ReInit(cfg); err != nil {
		logging.Fatal("Error re-initializing logger after preflight", "error", err)
	}

	// Show configuration if requested.
	if *showConfig {
		if cfgYaml, err := yaml.Marshal(cfg); err == nil {
			logging.Info("Current configuration", "config", string(cfgYaml))
		}
		os.Exit(0)
	}

	// Ensure mutually exclusive flags are not set.
	if *checkOnly && *installOnly {
		logging.Warn("Conflicting flags: --checkonly and --installonly are mutually exclusive")
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
	logging.Info("Run type", "type", runType)

	// Update the logger's run type for consistent logging
	logging.SetRunType(runType)

	// Start structured logging session
	sessionMetadata := map[string]interface{}{
		"verbosity": verbosity,
		"bootstrap": isBootstrap,
		"flags": map[string]bool{
			"checkonly":   *checkOnly,
			"installonly": *installOnly,
			"auto":        *auto,
		},
	}
	if err := logging.StartSession(runType, sessionMetadata); err != nil {
		logging.Warn("Failed to start structured logging session", "error", err)
	}

	// Check administrative privileges.
	admin, adminErr := adminCheck()
	if adminErr != nil || !admin {
		logging.Fatal("Administrative access required", "error", adminErr, "admin", admin)
	}
	// Initialize status reporter if requested
	var statusReporter status.Reporter
	if *showStatus {
		statusReporter = status.NewPipeReporter()
		if err := statusReporter.Start(context.Background()); err != nil {
			logging.Error("Failed to start status reporter", "error", err)
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
		logging.Error("Failed to create cache directory", "error", err)
		os.Exit(1)
	}

	// Retrieve manifests.
	statusReporter.Message("Retrieving manifests...")

	// Clear item sources tracking for this run
	process.ClearItemSources()

	var manifestItems []manifest.Item
	var mErr error

	// Check for local-only manifest override (Munki-compatible)
	// Command-line flag takes precedence over configuration setting
	localManifestPath := *localOnlyManifest
	if localManifestPath == "" && cfg.LocalOnlyManifest != "" {
		localManifestPath = cfg.LocalOnlyManifest
	}

	// Check for specific manifest target (--manifest flag)
	if *manifestTarget != "" {
		logging.Info("Processing specific manifest", "manifest", *manifestTarget)
		manifestItems, mErr = loadSpecificManifest(*manifestTarget, cfg)
		if mErr != nil {
			statusReporter.Error(fmt.Errorf("failed to load specific manifest: %v", mErr))
			logging.Error("Failed to load specific manifest", "manifest", *manifestTarget, "error", mErr)
			os.Exit(1)
		}
	} else if localManifestPath != "" {
		logging.Info("Using local-only manifest", "path", localManifestPath)
		manifestItems, mErr = loadLocalOnlyManifest(localManifestPath)
		if mErr != nil {
			statusReporter.Error(fmt.Errorf("failed to load local-only manifest: %v", mErr))
			logging.Error("Failed to load local-only manifest", "error", mErr)
			os.Exit(1)
		}
	} else {
		// Display enhanced loading header only at highest debug level (verbosity 3+)
		if verbosity >= 3 {
			targetItems := []string{}
			if itemFilter.HasFilter() {
				targetItems = itemFilter.GetItems()
			}
			displayLoadingHeader(targetItems, verbosity)
		}

		// Use optimized manifest loading if item filter is active for better performance
		if itemFilter.HasFilter() {
			manifestItems, mErr = manifest.AuthenticatedGetOptimized(cfg, itemFilter.GetItems())
			// Optimized loader already filters, so no need to apply filter again
		} else {
			manifestItems, mErr = manifest.AuthenticatedGet(cfg)
			// Apply item filter if specified (only needed for non-optimized path)
			if itemFilter.HasFilter() {
				manifestItems = itemFilter.Apply(manifestItems)
			}
		}

		if mErr != nil {
			statusReporter.Error(fmt.Errorf("failed to retrieve manifests: %v", mErr))
			logging.Error("Failed to retrieve manifests", "error", mErr)
			os.Exit(1)
		}

		// Display manifest tree structure
		displayManifestTree(manifestItems)
	}

	// Clear and set up source tracking for all manifest items
	process.ClearItemSources()
	for _, manifestItem := range manifestItems {
		// Track source information for each type of managed item
		for _, item := range manifestItem.ManagedInstalls {
			if item != "" {
				process.SetItemSource(item, manifestItem.Name, "managed_installs")
			}
		}
		for _, item := range manifestItem.ManagedUpdates {
			if item != "" {
				process.SetItemSource(item, manifestItem.Name, "managed_updates")
			}
		}
		for _, item := range manifestItem.ManagedUninstalls {
			if item != "" {
				process.SetItemSource(item, manifestItem.Name, "managed_uninstalls")
			}
		}
		for _, item := range manifestItem.OptionalInstalls {
			if item != "" {
				process.SetItemSource(item, manifestItem.Name, "optional_installs")
			}
		}
	}

	// Override checkonly mode if item filter is active, but only if --checkonly wasn't explicitly set
	if itemFilter.ShouldOverrideCheckOnly() && !pflag.CommandLine.Changed("checkonly") {
		*checkOnly = false
		logging.Info("--item flag specified, overriding default checkonly mode")
	} else if itemFilter.HasFilter() && *checkOnly {
		logging.Info("--item flag with explicit --checkonly: will check only specified items")
	}

	statusReporter.Detail("Loading catalog data...")
	// Load local catalogs into a map (keys are lowercase names).
	localCatalogMap, err := loadLocalCatalogItems(cfg)
	if err != nil {
		statusReporter.Error(fmt.Errorf("failed to load local catalogs: %v", err))
		logging.Error("Failed to load local catalogs", "error", err)
		os.Exit(1)
	}

	// Display catalog box with enhanced formatting
	displayCatalogBox(localCatalogMap, cfg.Catalogs)

	// Convert to the expected format for advanced dependency processing
	statusReporter.Detail("Processing dependencies...")
	fullCatalogMap := catalog.AuthenticatedGetEnhanced(*cfg)

	// If install-only mode, perform installs and exit.
	if *installOnly {
		logging.Info("Running in install-only mode")
		statusReporter.Message("Installing pending updates...")
		itemsToInstall := prepareDownloadItemsWithCatalog(manifestItems, localCatalogMap, cfg)
		if err := downloadAndInstallPerItem(itemsToInstall, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("failed to install pending updates: %v", err))
			logging.Error("Failed to install pending updates (install-only)", "error", err)
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
		// Enhanced checkonly mode: provide detailed package analysis
		if itemFilter.HasFilter() && verbosity >= 2 {
			printEnhancedPackageAnalysis(toInstall, toUpdate, toUninstall, localCatalogMap)
		}
		// End structured logging session before exit
		summary := logging.SessionSummary{
			TotalActions:    len(toInstall) + len(toUpdate) + len(toUninstall),
			Installs:        len(toInstall),
			Updates:         len(toUpdate),
			Removals:        len(toUninstall),
			Successes:       0, // No actions performed in check-only mode
			Failures:        0,
			PackagesHandled: extractPackageNames(toInstall, toUpdate, toUninstall),
		}
		if err := logging.EndSession("completed", summary); err != nil {
			logging.Warn("Failed to end structured logging session", "error", err)
		}

		// Generate reports for external monitoring tools
		logging.Info("Generating reports for external monitoring tools...")
		baseDir := filepath.Join(os.Getenv("ProgramData"), "ManagedInstalls", "logs")
		exporter := reporting.NewDataExporter(baseDir)
		if err := exporter.ExportToReportsDirectory(48); err != nil {
			logging.Warn("Failed to export reports", "error", err)
		}

		os.Exit(0)
	}

	// Proceed with installations without user confirmation
	if *auto {
		logging.Info("Auto mode enabled - proceeding with installation without confirmation")
	} else {
		logging.Info("Proceeding with installation without confirmation")
	}

	// Combine install and update items and perform installations.
	var allToInstall []catalog.Item
	allToInstall = append(allToInstall, toInstall...)
	allToInstall = append(allToInstall, toUpdate...)
	var installSuccess bool = true
	if len(allToInstall) > 0 {
		statusReporter.Message("Installing and updating applications...")
		statusReporter.Percent(0) // Start progress tracking
		if err := downloadAndInstallWithAdvancedLogic(allToInstall, fullCatalogMap, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("some installations failed: %v", err))
			logging.Warn("Some items failed to install, continuing with remaining operations", "error", err)
			installSuccess = false
		} else {
			statusReporter.Percent(50) // Mid-way progress
		}
	}

	// Process uninstalls.
	var uninstallSuccess bool = true
	if len(toUninstall) > 0 {
		statusReporter.Message("Removing applications...")
		statusReporter.Percent(75) // Progress at 75%
		if err := uninstallWithAdvancedLogic(toUninstall, fullCatalogMap, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("some uninstalls failed: %v", err))
			logging.Warn("Some items failed to uninstall, continuing with remaining operations", "error", err)
			uninstallSuccess = false
		}
	}

	// For auto mode: if the user is active, skip updates.
	if *auto {
		if isUserActive() {
			logging.Info("User is active. Skipping automatic updates", "idle_seconds", getIdleSeconds())
			os.Exit(0)
		}
	}

	// Log summary of operations
	if installSuccess && uninstallSuccess {
		logging.Success("Software updates completed successfully")
	} else if !installSuccess && !uninstallSuccess {
		logging.Warn("Software updates completed with some failures in both installations and uninstalls")
	} else if !installSuccess {
		logging.Warn("Software updates completed with some installation failures")
	} else {
		logging.Warn("Software updates completed with some uninstall failures")
	}

	statusReporter.Message("Finalizing installation...")
	statusReporter.Percent(90)

	// Run postflight script.
	if verbosity > 0 {
		logging.Debug("Running postflight script with verbosity level", "verbosity", verbosity)
	}
	statusReporter.Detail("Running post-installation scripts...")
	runPostflightIfNeeded(verbosity)
	logger.Success("Postflight script completed.")

	// Generate reports for external monitoring tools
	statusReporter.Detail("Generating system reports...")
	exporter := reporting.NewDataExporter(`C:\ProgramData\ManagedInstalls\logs`)
	if err := exporter.ExportToReportsDirectory(7); err != nil { // Export last 7 days
		logging.Warn("Failed to generate reports", "error", err)
	} else {
		logging.Success("Reports exported successfully", "location", `C:\ProgramData\ManagedInstalls\reports`)
	}

	statusReporter.Detail("Cleaning up temporary files...")
	cacheFolder := `C:\ProgramData\ManagedInstalls\Cache`
	logsFolder := `C:\ProgramData\ManagedInstalls\logs`
	clearCacheFolderSelective(cacheFolder, logsFolder)

	// Clear bootstrap mode if we completed successfully
	if isBootstrap {
		if err := clearBootstrapAfterSuccess(); err != nil {
			logging.Warn("Failed to clear bootstrap mode", "error", err)
		}
	}

	statusReporter.Message("All operations completed successfully!")
	statusReporter.Percent(100)
	statusReporter.Stop()

	// End structured logging session
	summary := logging.SessionSummary{
		TotalActions:    len(toInstall) + len(toUpdate) + len(toUninstall), // Use actual counts
		Installs:        len(toInstall),
		Updates:         len(toUpdate),
		Removals:        len(toUninstall),
		Successes:       0, // TODO: Track actual success/failure counts during execution
		Failures:        0,
		PackagesHandled: extractPackageNames(toInstall, toUpdate, toUninstall),
	}
	if err := logging.EndSession("completed", summary); err != nil {
		logging.Warn("Failed to end structured logging session", "error", err)
	}

	// Display final summary if we have the required data
	if len(manifestItems) > 0 || len(localCatalogMap) > 0 {
		targetItems := []string{}
		if itemFilter.HasFilter() {
			targetItems = itemFilter.GetItems()
		}
		totalTime := time.Since(time.Now().Add(-10 * time.Second)) // Approximate, we don't have exact start time
		displayFinalSummary(totalTime, len(manifestItems), localCatalogMap, targetItems)
	}

	os.Exit(0)
}

// extractPackageNames extracts package names from catalog items for session tracking
func extractPackageNames(installs, updates, uninstalls []catalog.Item) []string {
	var packages []string

	// Add install packages
	for _, item := range installs {
		packages = append(packages, item.Name)
	}

	// Add update packages
	for _, item := range updates {
		packages = append(packages, item.Name)
	}

	// Add uninstall packages
	for _, item := range uninstalls {
		packages = append(packages, item.Name)
	}

	return packages
}

// runPreflightIfNeeded runs the preflight script.
// If the preflight script fails, execution is aborted (like Munki behavior).
func runPreflightIfNeeded(verbosity int) {
	logInfo := func(format string, args ...interface{}) {
		logging.Debug(fmt.Sprintf(format, args...))
	}
	logError := func(format string, args ...interface{}) {
		logging.Error(fmt.Sprintf(format, args...))
	}

	if err := scripts.RunPreflight(verbosity, logInfo, logError); err != nil {
		logging.Error("Preflight script failed", "error", err)
		logging.Error("managedsoftwareupdate run aborted by preflight script failure")
		// Exit like Munki does when preflight fails
		os.Exit(1)
	}
}

// loadLocalOnlyManifest loads a local manifest file for processing.
// This implements Munki-compatible LocalOnlyManifest functionality.
func loadLocalOnlyManifest(manifestPath string) ([]manifest.Item, error) {
	logging.Info("Loading local-only manifest from", "path", manifestPath)

	// Check if the file exists
	if _, err := os.Stat(manifestPath); os.IsNotExist(err) {
		return nil, fmt.Errorf("local manifest file does not exist: %s", manifestPath)
	}

	// Read the manifest file
	data, err := os.ReadFile(manifestPath)
	if err != nil {
		return nil, fmt.Errorf("failed to read local manifest file: %v", err)
	}

	// Parse the manifest
	var manifestFile manifest.ManifestFile
	if err := yaml.Unmarshal(data, &manifestFile); err != nil {
		return nil, fmt.Errorf("failed to parse local manifest YAML: %v", err)
	}

	// Convert to Item format (similar to what manifest.AuthenticatedGet returns)
	var items []manifest.Item

	// Create a single item representing this local manifest
	item := manifest.Item{
		Name:              manifestFile.Name,
		ManagedInstalls:   manifestFile.ManagedInstalls,
		ManagedUninstalls: manifestFile.ManagedUninstalls,
		ManagedUpdates:    manifestFile.ManagedUpdates,
		OptionalInstalls:  manifestFile.OptionalInstalls,
		Catalogs:          manifestFile.Catalogs,
		Includes:          manifestFile.IncludedManifests,
	}

	items = append(items, item)

	logging.Info("Successfully loaded local-only manifest",
		"managed_installs", len(item.ManagedInstalls),
		"managed_uninstalls", len(item.ManagedUninstalls),
		"managed_updates", len(item.ManagedUpdates))

	return items, nil
}

// loadSpecificManifest loads a specific manifest from the server.
// This allows targeting a specific manifest path instead of using ClientIdentifier.
func loadSpecificManifest(manifestName string, cfg *config.Configuration) ([]manifest.Item, error) {
	logging.Info("Loading specific manifest from server", "manifest", manifestName)

	// Temporarily override the ClientIdentifier to target the specific manifest
	originalClientIdentifier := cfg.ClientIdentifier
	originalSkipSelfService := cfg.SkipSelfService
	cfg.ClientIdentifier = manifestName
	cfg.SkipSelfService = true // Skip self-service manifest when using --manifest flag
	defer func() {
		cfg.ClientIdentifier = originalClientIdentifier
		cfg.SkipSelfService = originalSkipSelfService
	}()

	// Use the standard manifest.AuthenticatedGet with the overridden ClientIdentifier
	manifestItems, err := manifest.AuthenticatedGet(cfg)
	if err != nil {
		return nil, fmt.Errorf("failed to load specific manifest '%s': %v", manifestName, err)
	}

	logging.Info("Successfully loaded specific manifest",
		"manifest", manifestName,
		"items", len(manifestItems),
		"note", "self-service skipped")

	return manifestItems, nil
}

// runPostflightIfNeeded runs the postflight script.
func runPostflightIfNeeded(verbosity int) {
	logInfo := func(format string, args ...interface{}) {
		logging.Debug(fmt.Sprintf(format, args...))
	}
	logError := func(format string, args ...interface{}) {
		logging.Error(fmt.Sprintf(format, args...))
	}

	if err := scripts.RunPostflight(verbosity, logInfo, logError); err != nil {
		logging.Error("Postflight script failed", "error", err)
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
		// For example, if the Uninstaller array is non-empty, if an uninstall check is defined,
		// or if the item is explicitly marked as uninstallable.
		if (len(catItem.Uninstaller) > 0 || (catItem.Check.Registry.Name != "" && catItem.Check.Registry.Version != "")) && catItem.IsUninstallable() {
			logging.Info("Catalog item marked for removal", "item", catItem.Name)
			toRemove = append(toRemove, catItem)
		} else if !catItem.IsUninstallable() {
			logging.Info("Catalog item marked as not uninstallable, skipping removal", "item", catItem.Name)
		}
	}

	return toRemove
}

// identifyNewInstalls checks each manifest item and returns those that need installation.
// Items not found in the local catalog will be logged as warnings and skipped.
func identifyNewInstalls(manifestItems []manifest.Item, localCatalogMap map[string]catalog.Item, cfg *config.Configuration) []catalog.Item {
	_ = cfg // dummy reference to suppress "unused parameter" warning

	var toInstall []catalog.Item
	for _, mItem := range manifestItems {
		if mItem.Name == "" {
			continue
		}
		key := strings.ToLower(mItem.Name)
		catItem, found := localCatalogMap[key]
		if !found {
			logging.Warn("Package not found in catalog - cannot install", "package", mItem.Name, "source_manifest", mItem.SourceManifest)
			continue
		}

		// Check if this item actually needs installation using the catalog item
		if installer.LocalNeedsUpdate(mItem, localCatalogMap, cfg) {
			toInstall = append(toInstall, catItem)
		}
	}
	return toInstall
}

// uninstallCatalogItems loops over the items and uninstalls each.
// Continues with remaining items even if some fail, only returns error if ALL fail
func uninstallCatalogItems(items []catalog.Item, cfg *config.Configuration) error {
	_ = cfg // dummy reference to suppress "unused parameter" warning

	if len(items) == 0 {
		logging.Debug("No items to uninstall.")
		return nil
	}

	logging.Info("Starting batch uninstall of items", "count", len(items))
	var failedItems []string
	var successCount int

	for _, item := range items {
		// Get source information for logging
		var sourceDesc string
		if source, exists := process.GetItemSource(item.Name); exists {
			sourceDesc = source.GetSourceDescription()
		} else {
			sourceDesc = "unknown"
		}

		// Check for blocking applications before unattended uninstalls
		if blocking.BlockingApplicationsRunning(item) {
			runningApps := blocking.GetRunningBlockingApps(item)
			logging.Warn("Blocking applications are running", "item", item.Name, "running_apps", runningApps, "source", sourceDesc)
			logging.Info("Skipping unattended uninstall due to blocking applications", "item", item.Name, "source", sourceDesc)
			failedItems = append(failedItems, fmt.Sprintf("%s (blocked by: %v)", item.Name, runningApps))
			continue
		}

		_, err := installer.Install(item, "uninstall", "", cfg.CachePath, cfg.CheckOnly, cfg)
		if err != nil {
			logging.Error("Failed to uninstall item, continuing with others", "item", item.Name, "error", err, "source", sourceDesc)
			failedItems = append(failedItems, item.Name)
		} else {
			logging.Info("Uninstall successful", "item", item.Name, "source", sourceDesc)
			successCount++
		}
	}

	// Log summary of results
	if len(failedItems) > 0 {
		logging.Warn("Uninstall summary", "succeeded", successCount, "failed", len(failedItems), "total", len(items))
		// Only return error if ALL items failed
		if successCount == 0 {
			return fmt.Errorf("all %d items failed to uninstall: %v", len(items), failedItems)
		}
	} else {
		logging.Info("All items uninstalled successfully", "count", successCount)
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
			logging.Warn("Failed to read catalog file", "catalog", catPath, "error", readErr)
			continue
		}

		var wrapper catalogWrapper
		if err := yaml.Unmarshal(data, &wrapper); err != nil {
			logging.Warn("Failed to parse catalog YAML", "catalog", catPath, "error", err)
			continue
		}

		// catItems is the slice from the "items" array in that catalog.
		catItems := wrapper.Items
		filteredCount := 0

		// Add them to our multi-map keyed by item name, in lowercase for deduping.
		for _, cItem := range catItems {
			key := strings.ToLower(cItem.Name)

			// Apply early architecture filtering
			if status.SupportsArchitecture(cItem, status.GetSystemArchitecture()) {
				itemsMulti[key] = append(itemsMulti[key], cItem)
			} else {
				filteredCount++
			}
		}

		catalogName := strings.TrimSuffix(entry.Name(), ".yaml")
		logging.Debug("Processed catalog", "name", catalogName, "items", len(catItems)-filteredCount, "filtered", filteredCount)
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
		key := strings.ToLower(m.Name)
		catItem, found := catMap[key]
		if !found {
			logging.Warn("Package not available in downloaded catalogs - skipping", "package", m.Name, "source_manifest", m.SourceManifest)
			continue
		}

		if installer.LocalNeedsUpdate(m, catMap, cfg) {
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
		// Get source information for logging
		var sourceDesc string
		if source, exists := process.GetItemSource(cItem.Name); exists {
			sourceDesc = source.GetSourceDescription()
		} else {
			sourceDesc = "unknown"
		}

		if cItem.Installer.Location == "" {
			logging.Warn("No installer location found for item", "item", cItem.Name, "source", sourceDesc)
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
		logging.Warn("Some downloads may have failed, attempting installation with available files", "error", err)
		// Continue with whatever was downloaded successfully
		if downloadedPaths == nil {
			downloadedPaths = make(map[string]string)
		}
	}

	var successCount, failCount int
	// Perform installation for each item using the correct paths
	for _, cItem := range items {
		// Get source information for logging
		var sourceDesc string
		if source, exists := process.GetItemSource(cItem.Name); exists {
			sourceDesc = source.GetSourceDescription()
		} else {
			sourceDesc = "unknown"
		}

		localFile, exists := downloadedPaths[cItem.Name]
		if !exists {
			logging.Error("Downloaded path not found for item", "item", cItem.Name, "source", sourceDesc)
			failCount++
			continue
		}

		logging.Info("Installing downloaded item", "item", cItem.Name, "file", localFile, "source", sourceDesc)

		if err := installOneCatalogItem(cItem, localFile, cfg); err != nil {
			logging.Error("Installation command failed", "item", cItem.Name, "error", err, "source", sourceDesc)
			failCount++
			continue
		}
		successCount++
	}

	// Log summary
	if failCount > 0 {
		logging.Warn("Installation summary", "succeeded", successCount, "failed", failCount, "total", len(items))
	} else {
		logging.Info("All items installed successfully", "count", successCount)
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
	fmt.Println("")
	fmt.Println("📋 Summary of planned actions:")
	fmt.Println("")

	// Check if no actions are planned.
	if len(toInstall) == 0 && len(toUninstall) == 0 && len(toUpdate) == 0 {
		fmt.Println("✅ No actions are planned.")
		return
	}

	// Print INSTALL actions in a clean format.
	if len(toInstall) > 0 {
		fmt.Println("📦 Will install these items:")
		fmt.Println(strings.Repeat("-", 80))
		for _, item := range toInstall {
			installerPath := item.Installer.Location
			if installerPath == "" {
				installerPath = "No installer specified"
			}
			fmt.Printf("%-30s %-20s %s\n", item.Name, item.Version, installerPath)
		}
		fmt.Println("")
	}

	// Print UPDATE actions in a clean format.
	if len(toUpdate) > 0 {
		fmt.Println("🔄 Will update these items:")
		fmt.Println(strings.Repeat("-", 80))
		for _, item := range toUpdate {
			installerPath := item.Installer.Location
			if installerPath == "" {
				installerPath = "No installer specified"
			}
			fmt.Printf("%-30s %-20s %s\n", item.Name, item.Version, installerPath)
		}
		fmt.Println("")
	}

	// Print UNINSTALL actions in a clean format.
	if len(toUninstall) > 0 {
		fmt.Println("🗑️  Will remove these items:")
		fmt.Println(strings.Repeat("-", 80))
		for _, item := range toUninstall {
			uninstallerInfo := "Auto-detected"
			if len(item.Uninstaller) == 1 {
				uninstallerInfo = fmt.Sprintf("%s: %s", item.Uninstaller[0].Type, item.Uninstaller[0].Path)
			} else if len(item.Uninstaller) > 1 {
				uninstallerInfo = "Multiple uninstallers"
			}
			fmt.Printf("%-30s %-20s %s\n", item.Name, "Current version", uninstallerInfo)
		}
		fmt.Println("")
	}
}

// installOneCatalogItem installs a single catalog item using the installer package.
// It normalizes the architecture and handles installation output for error detection.
func installOneCatalogItem(cItem catalog.Item, localFile string, cfg *config.Configuration) error {
	// Get source information for logging
	var sourceDesc string
	if source, exists := process.GetItemSource(cItem.Name); exists {
		sourceDesc = source.GetSourceDescription()
	} else {
		sourceDesc = "unknown"
	}

	normalizeArchitecture(&cItem)
	sysArch := status.GetSystemArchitecture()
	logging.Debug("Detected system architecture", "architecture", sysArch)
	logging.Debug("Supported architectures for item", "item", cItem.Name, "supported_arch", cItem.SupportedArch, "source", sourceDesc)

	// Check for blocking applications before unattended installs (following Munki's behavior)
	// Only applies to items marked as unattended_install or in auto/bootstrap mode
	if blocking.BlockingApplicationsRunning(cItem) {
		runningApps := blocking.GetRunningBlockingApps(cItem)
		logging.Warn("Blocking applications are running", "item", cItem.Name, "running_apps", runningApps, "source", sourceDesc)
		logging.Info("Skipping unattended install due to blocking applications", "item", cItem.Name, "source", sourceDesc)

		// Return a special error to indicate this was skipped due to blocking applications
		return fmt.Errorf("skipped install of %s due to blocking applications: %v", cItem.Name, runningApps)
	}

	// Actually install
	installedOutput, installErr := installer.Install(cItem, "install", localFile, cfg.CachePath, cfg.CheckOnly, cfg)
	if installErr != nil {
		// DO NOT say "Installed item successfully" because it failed
		return installErr
	}

	// If we get here => success
	logging.Info("Install output", "item", cItem.Name, "output", installedOutput, "source", sourceDesc)
	logging.Info("Installed item successfully", "item", cItem.Name, "file", localFile, "source", sourceDesc)

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
		logging.Error("Error getting last input info", "error", err)
		return 0
	}
	tickCount, _, err2 := syscall.NewLazyDLL("kernel32.dll").NewProc("GetTickCount").Call()
	if tickCount == 0 {
		logging.Error("Error getting tick count", "error", err2)
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

// clearCacheFolderSelective reads log files from logsPath (e.g. "C:\ProgramData\ManagedInstalls\logs")
// and only deletes files in cachePath (e.g. "C:\ProgramData\ManagedInstalls\Cache")
// that are associated with a successful installation.
func clearCacheFolderSelective(cachePath, logsPath string) {
	// successSet will hold the base names (e.g. "GitCredentialManager-x64-2.6.0.0.exe")
	// of installer files that ran successfully.
	successSet := make(map[string]bool)

	// Read log files from the logsPath
	logFiles, err := os.ReadDir(logsPath)
	if err != nil {
		logging.Warn("Failed to read logs directory", "directory", logsPath, "error", err)
	} else {
		for _, entry := range logFiles {
			if entry.IsDir() {
				continue
			}
			logFilePath := filepath.Join(logsPath, entry.Name())
			data, err := os.ReadFile(logFilePath)
			if err != nil {
				logging.Warn("Failed to read log file", "file", logFilePath, "error", err)
				continue
			}
			// Split the log file into lines
			lines := strings.Split(string(data), "\n")
			for _, line := range lines {
				// Look for successful installation completion with file path
				if strings.Contains(line, "Installed item successfully:") && strings.Contains(line, "file:") {
					// Expect a pattern like: "Installed item successfully: Chrome, file: C:\ProgramData\ManagedInstalls\cache\apps\browsers\Chrome-x64-135.0.7023.0.exe"
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
		logging.Warn("Failed to read cache directory", "directory", cachePath, "error", err)
		return
	}

	for _, entry := range cacheEntries {
		fullPath := filepath.Join(cachePath, entry.Name())
		// Only remove the file if its base name is in the success set and marked as true (successfully installed).
		if successSet[entry.Name()] {
			err := os.RemoveAll(fullPath)
			if err != nil {
				logging.Warn("Failed to remove cached item", "path", fullPath, "error", err)
			} else {
				logging.Debug("Removed cached item", "path", fullPath)
			}
		} else {
			logging.Debug("Retaining cached item", "path", fullPath)
		}
	}
	logging.Info("Selective cache clearing complete", "folder", cachePath)
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

	logging.Info("Bootstrap mode enabled - CimianWatcher service will detect and respond")
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

	logging.Info("Bootstrap mode disabled")
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

// printEnhancedPackageAnalysis provides detailed package information in checkonly mode
func printEnhancedPackageAnalysis(toInstall, toUpdate, toUninstall []catalog.Item, catalogMap map[string]catalog.Item) {
	fmt.Println("\n" + strings.Repeat("=", 80))
	fmt.Println("ENHANCED PACKAGE ANALYSIS")
	fmt.Println(strings.Repeat("=", 80))

	// Summary statistics
	totalPackages := len(toInstall) + len(toUpdate) + len(toUninstall)
	fmt.Printf("📊 Summary: %d total packages (%d new installs, %d updates, %d removals)\n\n",
		totalPackages, len(toInstall), len(toUpdate), len(toUninstall))

	// Detailed analysis for each category
	if len(toInstall) > 0 {
		fmt.Println("🆕 NEW INSTALLATIONS:")
		fmt.Println(strings.Repeat("-", 40))
		for _, item := range toInstall {
			printPackageDetails(item, catalogMap, "INSTALL")
		}
		fmt.Println("")
	}

	if len(toUpdate) > 0 {
		fmt.Println("🔄 UPDATES:")
		fmt.Println(strings.Repeat("-", 40))
		for _, item := range toUpdate {
			printPackageDetails(item, catalogMap, "UPDATE")
		}
		fmt.Println("")
	}

	if len(toUninstall) > 0 {
		fmt.Println("❌ REMOVALS:")
		fmt.Println(strings.Repeat("-", 40))
		for _, item := range toUninstall {
			printPackageDetails(item, catalogMap, "REMOVE")
		}
		fmt.Println("")
	}

	fmt.Println(strings.Repeat("=", 80))
}

// printPackageDetails prints detailed information about a single package
func printPackageDetails(item catalog.Item, catalogMap map[string]catalog.Item, action string) {
	fmt.Printf("📦 %s (%s)\n", item.Name, action)

	// Version information
	if item.Version != "" {
		fmt.Printf("   📋 Version: %s\n", item.Version)
	}

	// Check if we have catalog entry for this item
	if catalogEntry, exists := catalogMap[strings.ToLower(item.Name)]; exists {

		// Dependencies
		if len(catalogEntry.Requires) > 0 {
			fmt.Printf("   🔗 Dependencies: %s\n", strings.Join(catalogEntry.Requires, ", "))
		}

		// Supported architectures
		if len(catalogEntry.SupportedArch) > 0 {
			fmt.Printf("   🏗️  Architecture: %s\n", strings.Join(catalogEntry.SupportedArch, ", "))
		}

		// Display name
		if catalogEntry.DisplayName != "" && catalogEntry.DisplayName != catalogEntry.Name {
			fmt.Printf("   📝 Display Name: %s\n", catalogEntry.DisplayName)
		}

		// Blocking applications
		if len(catalogEntry.BlockingApps) > 0 {
			fmt.Printf("   ⛔ Blocking Apps: %s\n", strings.Join(catalogEntry.BlockingApps, ", "))
		}
	}

	fmt.Println("")
}

// formatBytes converts bytes to human-readable format
func formatBytes(bytes int64) string {
	const unit = 1024
	if bytes < unit {
		return fmt.Sprintf("%d B", bytes)
	}
	div, exp := int64(unit), 0
	for n := bytes / unit; n >= unit; n /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.1f %cB", float64(bytes)/float64(div), "KMGTPE"[exp])
}

// displayLoadingHeader shows the initial loading information with tree format
func displayLoadingHeader(targetItems []string, verbosity int) {
	if len(targetItems) > 0 {
		logging.Info("Targeted Item Loading", "items", strings.Join(targetItems, ", "))
	} else {
		logging.Info("Full Manifest Loading")
	}
}

// displayManifestTree shows the manifest hierarchy in tree format
func displayManifestTree(manifestItems []manifest.Item) {
	// Create a map to track manifest counts by name
	manifestCounts := make(map[string]int)

	// Count items by their source manifest
	for _, item := range manifestItems {
		sourceManifest := item.SourceManifest
		if sourceManifest == "" {
			sourceManifest = "Unknown"
		}
		manifestCounts[sourceManifest]++
	}

	fmt.Printf("📁 Manifest Hierarchy (%d manifests found)\n", len(manifestCounts))
	fmt.Printf("\n")

	// Build the tree structure based on manifest path hierarchy
	// We need to show the actual manifest structure: RodChristiansen -> B1115 -> IT -> Staff -> Assigned
	manifestTree := buildManifestHierarchy(manifestCounts)
	displayManifestHierarchy(manifestTree, "", true)

	fmt.Printf("\n")
}

// ManifestNode represents a node in the manifest hierarchy tree
type ManifestNode struct {
	Name      string
	ItemCount int
	Children  map[string]*ManifestNode
	IsLeaf    bool
}

// buildManifestHierarchy creates a tree structure from manifest names and their paths
func buildManifestHierarchy(manifestCounts map[string]int) *ManifestNode {
	root := &ManifestNode{
		Name:     "root",
		Children: make(map[string]*ManifestNode),
	}

	// Define known manifest hierarchy from the logs we've seen
	// This represents the actual structure: RodChristiansen -> B1115 -> IT -> Staff -> Assigned
	knownHierarchy := map[string][]string{
		"RodChristiansen":   {"Assigned", "Staff", "IT", "B1115"},
		"B1115":             {"Assigned", "Staff", "IT"},
		"IT":                {"Assigned", "Staff"},
		"Staff":             {"Assigned"},
		"Assigned":          {},
		"Apps":              {"Shared", "Curriculum"},
		"Curriculum":        {"Shared"},
		"Shared":            {},
		"CoreApps":          {},
		"ManagementTools":   {},
		"ManagementPrefs":   {},
		"CoreManifest":      {},
		"SelfServeManifest": {},
	}

	// First pass: create all nodes with their hierarchy
	allNodes := make(map[string]*ManifestNode)

	// Add all manifests that have items
	for manifestName := range manifestCounts {
		if manifestName == "Unknown" {
			continue
		}

		allNodes[manifestName] = &ManifestNode{
			Name:      manifestName,
			ItemCount: manifestCounts[manifestName],
			Children:  make(map[string]*ManifestNode),
			IsLeaf:    true,
		}
	}

	// Add all known manifests (including those with 0 items) to ensure full hierarchy is shown
	for manifestName := range knownHierarchy {
		if allNodes[manifestName] == nil {
			allNodes[manifestName] = &ManifestNode{
				Name:      manifestName,
				ItemCount: 0, // These are parent manifests with 0 direct items
				Children:  make(map[string]*ManifestNode),
				IsLeaf:    false,
			}
		}
	}

	// Create parent nodes that might not be in the known hierarchy
	for manifestName := range manifestCounts {
		if manifestName == "Unknown" {
			continue
		}

		if parents, exists := knownHierarchy[manifestName]; exists {
			for _, parentName := range parents {
				if allNodes[parentName] == nil {
					allNodes[parentName] = &ManifestNode{
						Name:      parentName,
						ItemCount: 0, // Parent nodes may have 0 items
						Children:  make(map[string]*ManifestNode),
						IsLeaf:    false,
					}
				}
			}
		}
	}

	// Second pass: build the hierarchy
	for manifestName := range allNodes {
		if manifestName == "Unknown" {
			continue
		}

		node := allNodes[manifestName]
		if parents, exists := knownHierarchy[manifestName]; exists && len(parents) > 0 {
			// Find the immediate parent (last in the list)
			parentName := parents[len(parents)-1]
			if parentNode, parentExists := allNodes[parentName]; parentExists {
				parentNode.Children[manifestName] = node
				parentNode.IsLeaf = false
			} else {
				// If parent doesn't exist, add to root
				root.Children[manifestName] = node
			}
		} else {
			// No known hierarchy, add to root
			root.Children[manifestName] = node
		}
	}

	// Third pass: add any orphaned nodes to root
	for nodeName, node := range allNodes {
		// Check if this node is not already a child of someone
		isChild := false
		for _, otherNode := range allNodes {
			if otherNode.Children[nodeName] != nil {
				isChild = true
				break
			}
		}
		if !isChild && root.Children[nodeName] == nil {
			root.Children[nodeName] = node
		}
	}

	return root
}

// displayManifestHierarchy recursively displays the manifest tree
func displayManifestHierarchy(node *ManifestNode, prefix string, isLast bool) {
	if node.Name == "root" {
		// Display root children
		names := make([]string, 0, len(node.Children))
		for name := range node.Children {
			names = append(names, name)
		}

		for i, name := range names {
			child := node.Children[name]
			isChildLast := i == len(names)-1
			displayManifestHierarchy(child, "", isChildLast)
		}
		return
	}

	// Display this node
	connector := "├─"
	if isLast {
		connector = "└─"
	}

	fmt.Printf("%s%s 📄 %s (%d items)\n", prefix, connector, node.Name, node.ItemCount)

	// Display children if any
	if len(node.Children) > 0 {
		childPrefix := prefix
		if isLast {
			childPrefix += "   "
		} else {
			childPrefix += "│  "
		}

		names := make([]string, 0, len(node.Children))
		for name := range node.Children {
			names = append(names, name)
		}

		for i, name := range names {
			child := node.Children[name]
			isChildLast := i == len(names)-1
			displayManifestHierarchy(child, childPrefix, isChildLast)
		}
	}
}

// displayCatalogBox shows catalogs in a box format around the manifest tree
func displayCatalogBox(catalogMap map[string]catalog.Item, catalogNames []string) {
	// Decorative catalog box removed - not needed for normal operations
}

// displayFinalSummary shows the overall loading summary
func displayFinalSummary(totalTime time.Duration, manifestItems int, catalogMap map[string]catalog.Item, targetItems []string) {
	// Only show decorative summary at highest debug level
	// For normal usage, these decorative elements are not needed
}
