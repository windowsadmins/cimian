// cmd/managedsoftwareupdate/main.go
//
// Cimian Managed Software Update - Enterprise Software Management
//
// Key Features:
// - Automatic timeout protection for installers (prevents hanging on GUI dialogs)
// - Bootstrap mode for initial system setup
// - Self-update management for Cimian components
// - Comprehensive logging and reporting
// - Munki-compatible manifest processing
// - Multiple installer types (MSI, EXE, PowerShell, MSIX, Chocolatey)
//
// Installer Timeout Protection:
// Automatically terminates installers that exceed the configured timeout (default: 15 minutes)
// to prevent batch installations from hanging on interactive GUI prompts.

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
	"github.com/windowsadmins/cimian/pkg/selfupdate"
	"github.com/windowsadmins/cimian/pkg/status"
	"github.com/windowsadmins/cimian/pkg/version"
)

// Bootstrap mode flag file - Windows equivalent of Munki's hidden dot file
const BootstrapFlagFile = `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`

var logger *logging.Logger

// Single instance mutex name for preventing concurrent executions
const mutexName = "Global\\CimianManagedSoftwareUpdate_v2"

// LASTINPUTINFO is used to track user idle time.
type LASTINPUTINFO struct {
	CbSize uint32
	DwTime uint32
}

// checkSingleInstance ensures only one instance of managedsoftwareupdate.exe runs at a time
func checkSingleInstance() (windows.Handle, error) {
	// Create or open a named mutex
	mutexNamePtr, err := windows.UTF16PtrFromString(mutexName)
	if err != nil {
		return 0, fmt.Errorf("failed to create mutex name: %v", err)
	}

	mutex, err := windows.CreateMutex(nil, true, mutexNamePtr)
	if err != nil {
		return 0, fmt.Errorf("failed to create mutex: %v", err)
	}

	// Check if another instance is already running
	if err := windows.GetLastError(); err == windows.ERROR_ALREADY_EXISTS {
		windows.CloseHandle(mutex)
		return 0, fmt.Errorf("another instance of managedsoftwareupdate.exe is already running")
	}

	return mutex, nil
}

// releaseSingleInstance releases the single instance mutex
func releaseSingleInstance(mutex windows.Handle) {
	if mutex != 0 {
		windows.ReleaseMutex(mutex)
		windows.CloseHandle(mutex)
	}
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

// runCommand executes a command and returns any error
func runCommand(command string, args []string) error {
	cmd := exec.Command(command, args...)
	output, err := cmd.CombinedOutput()

	if err != nil {
		return fmt.Errorf("command failed: %w (output: %s)", err, string(output))
	}

	return nil
}

// restartCimianWatcherService restarts the CimianWatcher Windows service
func restartCimianWatcherService() error {
	// Stop the service
	if err := runCommand("sc", []string{"stop", "CimianWatcher"}); err != nil {
		// Service might not be running, log but continue
		fmt.Printf("⚠️  Warning: Failed to stop CimianWatcher service: %v\n", err)
	} else {
		fmt.Println("✅ CimianWatcher service stopped")

		// Wait a moment for service to fully stop
		fmt.Print("   Waiting for service to stop...")
		time.Sleep(2 * time.Second)
		fmt.Println(" done")
	}

	// Start the service
	if err := runCommand("sc", []string{"start", "CimianWatcher"}); err != nil {
		return fmt.Errorf("failed to start CimianWatcher service: %w", err)
	}

	return nil
}

func main() {
	enableANSIConsole()

	// Check for single instance - prevent multiple concurrent executions
	mutex, err := checkSingleInstance()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error: %v\n", err)
		os.Exit(1)
	}
	// Ensure mutex is released when the program exits
	defer releaseSingleInstance(mutex)

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

	// Self-update management flags
	clearSelfUpdate := pflag.Bool("clear-selfupdate", false, "Clear pending self-update flag.")
	checkSelfUpdate := pflag.Bool("check-selfupdate", false, "Check if self-update is pending.")
	performSelfUpdate := pflag.Bool("perform-selfupdate", false, "Perform pending self-update (internal use).")
	selfUpdateStatus := pflag.Bool("selfupdate-status", false, "Show self-update status and exit.")
	restartService := pflag.Bool("restart-service", false, "Restart CimianWatcher service and exit.")

	// Cache management flags
	validateCache := pflag.Bool("validate-cache", false, "Validate cache integrity and remove corrupt files.")
	cacheStatus := pflag.Bool("cache-status", false, "Show cache status and statistics.")

	// Munki-compatible flags for preflight bypass and manifest override
	noPreflight := pflag.Bool("no-preflight", false, "Skip preflight script execution.")
	noPostflight := pflag.Bool("no-postflight", false, "Skip postflight script execution.")
	localOnlyManifest := pflag.String("local-only-manifest", "", "Use specified local manifest file instead of server manifest.")

	// Manifest targeting flag - process only a specific manifest from server
	manifestTarget := pflag.String("manifest", "", "Process only the specified manifest from server (e.g., 'Shared/Curriculum/RenderingFarm'). Automatically skips preflight.")

	// Initialize item filter and register its flags before parsing
	itemFilter := filter.NewItemFilter(nil) // logger will be set later
	itemFilter.RegisterFlags()

	// Count the number of -v flags.
	var verbosity int
	pflag.CountVarP(&verbosity, "verbose", "v", "Increase verbosity (e.g. -v, -vv, -vvv, -vvvv)")
	pflag.Parse()

	// Handle --version flag first, before any other initialization.
	if *versionFlag {
		version.PrintVersion()
		os.Exit(0)
	}

	// Load configuration (only once)
	cfg, err := config.LoadConfig()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to load configuration: %v\n", err)
		os.Exit(1)
	}

	// Handle --show-config flag early, before preflight and other processing
	if *showConfig {
		fmt.Printf("Configuration file location: %s\n", config.ConfigPath)
		if cfgYaml, err := yaml.Marshal(cfg); err == nil {
			fmt.Printf("Current configuration:\n%s", string(cfgYaml))
		} else {
			fmt.Printf("Error marshaling configuration: %v\n", err)
		}
		os.Exit(0)
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

	// Initialize logger with config that respects LogLevel
	if err := logging.Init(cfg); err != nil {
		fmt.Fprintf(os.Stderr, "Error initializing logger: %v\n", err)
		os.Exit(1)
	}
	defer logging.CloseLogger()

	// Enhanced startup info - show basic header even without verbose mode
	logging.Info("================================================================================")
	if verbosity > 0 {
		logging.Info("🚀 CIMIAN MANAGED SOFTWARE UPDATE - VERBOSE MODE")
	} else {
		logging.Info("🚀 CIMIAN MANAGED SOFTWARE UPDATE")
	}
	logging.Info("================================================================================")

	// Update the item filter with the initialized logger
	logger = logging.New(verbosity >= 2) // Create properly initialized logger for compatibility
	itemFilter.SetLogger(logger)

	// Handle bootstrap mode flags first - these exit immediately
	if *setBootstrapMode {
		if err := enableBootstrapMode(); err != nil {
			logging.Error("Failed to enable bootstrap mode: %v", err)
			os.Exit(1)
		}
		logging.Success("Bootstrap mode enabled. System will enter bootstrap mode on next boot.")
		os.Exit(0)
	}

	if *clearBootstrapMode {
		if err := disableBootstrapMode(); err != nil {
			logging.Error("Failed to disable bootstrap mode: %v", err)
			os.Exit(1)
		}
		logging.Success("Bootstrap mode disabled.")
		os.Exit(0)
	}

	// Handle self-update management flags
	if *clearSelfUpdate {
		if err := selfupdate.ClearSelfUpdateFlag(); err != nil {
			logging.Error("Failed to clear self-update flag: %v", err)
			os.Exit(1)
		}
		logging.Success("Self-update flag cleared.")
		os.Exit(0)
	}

	if *checkSelfUpdate {
		pending, metadata, err := selfupdate.GetSelfUpdateStatus()
		if err != nil {
			logging.Error("Failed to check self-update status: %v", err)
			os.Exit(1)
		}
		if pending {
			logging.Info("Self-update is pending:")
			for key, value := range metadata {
				logging.Info("  %s: %s", key, value)
			}
		} else {
			logging.Info("No self-update pending.")
		}
		os.Exit(0)
	}

	if *selfUpdateStatus {
		pending, metadata, err := selfupdate.GetSelfUpdateStatus()
		if err != nil {
			fmt.Printf("❌ Failed to check self-update status: %v\n", err)
			os.Exit(1)
		}

		fmt.Println("🔄 Cimian Self-Update Status")
		fmt.Println("════════════════════════════")

		if pending {
			fmt.Println("📋 Status: Self-update pending")
			fmt.Println("📦 Update details:")
			for key, value := range metadata {
				fmt.Printf("   %s: %s\n", key, value)
			}
			fmt.Println()
			fmt.Println("💡 To trigger the update:")
			fmt.Println("   managedsoftwareupdate --restart-service")
		} else {
			fmt.Println("📋 Status: No self-update pending")
			fmt.Println("✅ Cimian is up to date")
		}
		os.Exit(0)
	}

	if *restartService {
		fmt.Println("🔄 Restarting CimianWatcher service...")

		if err := restartCimianWatcherService(); err != nil {
			fmt.Printf("❌ Failed to restart service: %v\n", err)
			os.Exit(1)
		}

		fmt.Println("✅ CimianWatcher service restarted successfully")
		fmt.Println("ℹ️  Self-update will be processed if pending")
		os.Exit(0)
	}

	// Handle cache management flags
	if *validateCache {
		fmt.Println("🔍 Validating cache integrity...")

		// Load minimal config for cache path
		if err := download.ValidateAndCleanCache(cfg.CachePath); err != nil {
			fmt.Printf("❌ Cache validation failed: %v\n", err)
			os.Exit(1)
		}

		fmt.Println("✅ Cache validation completed successfully")
		os.Exit(0)
	}

	if *cacheStatus {
		fmt.Println("📊 Cimian Cache Status")
		fmt.Println("═══════════════════════")

		fmt.Printf("📁 Cache Path: %s\n", cfg.CachePath)

		// Count files and calculate sizes
		var totalFiles, corruptFiles, totalSize int64
		filepath.Walk(cfg.CachePath, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() {
				return nil
			}
			totalFiles++
			totalSize += info.Size()
			if info.Size() == 0 {
				corruptFiles++
			}
			return nil
		})

		fmt.Printf("📦 Total Files: %d\n", totalFiles)
		fmt.Printf("💾 Total Size: %.2f MB\n", float64(totalSize)/(1024*1024))
		if corruptFiles > 0 {
			fmt.Printf("⚠️  Corrupt Files: %d (0-byte files detected)\n", corruptFiles)
			fmt.Println("💡 Run with --validate-cache to clean up corrupt files")
		} else {
			fmt.Println("✅ No corruption detected")
		}
		os.Exit(0)
	}

	if *performSelfUpdate {
		// Load configuration first
		cfg, err := config.LoadConfig()
		if err != nil {
			logging.Error("Failed to load configuration for self-update: %v", err)
			os.Exit(1)
		}

		// Initialize logging for self-update
		if err := logging.Init(cfg); err != nil {
			fmt.Fprintf(os.Stderr, "Error initializing logger for self-update: %v\n", err)
			os.Exit(1)
		}
		defer logging.CloseLogger()

		selfUpdateManager := selfupdate.NewSelfUpdateManager()
		if err := selfUpdateManager.PerformSelfUpdate(cfg); err != nil {
			logging.Error("Self-update failed: %v", err)
			os.Exit(1)
		}
		logging.Success("Self-update completed successfully.")
		os.Exit(0)
	}

	// Check if we're in bootstrap mode
	isBootstrap := isBootstrapModeEnabled()
	if isBootstrap {
		if verbosity > 0 {
			logging.Info("----------------------------------------------------------------------")
			logging.Info("🔄 BOOTSTRAP MODE ACTIVE")
			logging.Info("   Non-interactive installation mode")
			logging.Info("----------------------------------------------------------------------")
		} else {
			logging.Info("Bootstrap mode detected - entering non-interactive installation mode")
		}
		*showStatus = true   // Always show status window in bootstrap mode
		*installOnly = false // Bootstrap mode does check + install
		*checkOnly = false
		*auto = false
	}

	catalogsDir := filepath.Join("C:\\ProgramData\\ManagedInstalls", "catalogs")
	manifestsDir := filepath.Join("C:\\ProgramData\\ManagedInstalls", "manifests")

	if verbosity >= 2 {
		logging.Info("→ Cleaning catalogs directory", "path", catalogsDir)
	}
	if err := cleanManifestsCatalogsPreRun(catalogsDir); err != nil {
		logging.Warn("Failed to clean catalogs directory, continuing anyway", "error", err)
	}
	if verbosity >= 2 {
		logging.Info("→ Cleaning manifests directory", "path", manifestsDir)
	}
	if err := cleanManifestsCatalogsPreRun(manifestsDir); err != nil {
		logging.Warn("Failed to clean manifests directory, continuing anyway", "error", err)
	}

	// Handle system signals for graceful shutdown.
	signalChan := make(chan os.Signal, 1)
	signal.Notify(signalChan, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		sig := <-signalChan
		logging.Warn("Signal received, exiting gracefully: %s", sig.String())
		logging.CloseLogger()
		os.Exit(1)
	}()

	// Run preflight script (unless bypassed by flag or config).
	skipPreflight := *noPreflight || cfg.NoPreflight || (*manifestTarget != "") || *showConfig
	if !skipPreflight {
		logging.Info("----------------------------------------------------------------------")
		logging.Info("🔄 PREFLIGHT EXECUTION")
		logging.Info("----------------------------------------------------------------------")
		runPreflightIfNeeded(verbosity, cfg)
	} else {
		if verbosity > 0 {
			logging.Info("----------------------------------------------------------------------")
			logging.Info("⏭️  PREFLIGHT SCRIPT BYPASSED")
			if *noPreflight {
				logging.Info("   Reason: --no-preflight flag")
			} else if *manifestTarget != "" {
				logging.Info("   Reason: --manifest flag")
			} else {
				logging.Info("   Reason: NoPreflight config setting")
			}
			logging.Info("----------------------------------------------------------------------")
		} else {
			if *noPreflight {
				logging.Info("Preflight script execution bypassed by --no-preflight flag")
			} else if *manifestTarget != "" {
				logging.Info("Preflight script execution bypassed by --manifest flag")
			} else {
				logging.Info("Preflight script execution bypassed by NoPreflight configuration setting")
			}
		}
	}

	// Display verbose information after preflight
	if verbosity > 0 {
		logging.Info("================================================================================")
		logging.Info("📊 SYSTEM CONFIGURATION")
		logging.Info("================================================================================")
		logging.Info(fmt.Sprintf("📊 Verbosity Level: %d", verbosity))
		logging.Info(fmt.Sprintf("📝 Log Level: %s", cfg.LogLevel))
		if verbosity >= 2 {
			wd, _ := os.Getwd()
			logging.Info(fmt.Sprintf("📁 Working Directory: %s", wd))
			logging.Info(fmt.Sprintf("⚙️  Config Path: %s", config.ConfigPath))
			logging.Info(fmt.Sprintf("💾 Cache Path: %s", cfg.CachePath))
			logging.Info(fmt.Sprintf("🌐 Software Repo URL: %s", cfg.SoftwareRepoURL))
		}
		logging.Info("================================================================================")
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
		logging.Fatal("Error re-initializing logger after preflight: %v", err)
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

	// Check administrative privileges.
	admin, adminErr := adminCheck()
	if adminErr != nil || !admin {
		logging.Fatal("Administrative access required. Error: %v, Admin: %v", adminErr, admin)
	}

	// Start structured logging session with comprehensive metadata
	var cachePath string = cfg.CachePath
	var localManifestPath string = *localOnlyManifest
	if localManifestPath == "" && cfg.LocalOnlyManifest != "" {
		localManifestPath = cfg.LocalOnlyManifest
	}

	sessionMetadata := map[string]interface{}{
		"verbosity":       verbosity,
		"bootstrap":       isBootstrap,
		"admin_check":     admin,
		"cache_path":      cachePath,
		"local_manifest":  localManifestPath,
		"manifest_target": *manifestTarget,
		"show_status":     *showStatus,
		"skip_preflight":  skipPreflight,
		"skip_postflight": *noPostflight || cfg.NoPostflight,
		"flags": map[string]bool{
			"checkonly":       *checkOnly,
			"installonly":     *installOnly,
			"auto":            *auto,
			"no_preflight":    *noPreflight,
			"no_postflight":   *noPostflight,
			"show_config":     *showConfig,
			"set_bootstrap":   *setBootstrapMode,
			"clear_bootstrap": *clearBootstrapMode,
		},
		"system_info": map[string]interface{}{
			"hostname":     getHostname(),
			"architecture": status.GetSystemArchitecture(),
			"user":         getCurrentUser(),
		},
	}
	if err := logging.StartSession(runType, sessionMetadata); err != nil {
		logging.Warn("Failed to start structured logging session: %v", err)
	}

	// Log session start event
	logging.LogEventEntry("session", "start", "started",
		fmt.Sprintf("Starting %s run with verbosity level %d", runType, verbosity),
		logging.WithContext("run_type", runType),
		logging.WithContext("verbosity", verbosity),
		logging.WithContext("bootstrap_mode", isBootstrap))
	// Initialize status reporter if requested
	var statusReporter status.Reporter
	if *showStatus {
		statusReporter = status.NewPipeReporter()
		if err := statusReporter.Start(context.Background()); err != nil {
			logging.Error("Failed to start status reporter: %v", err)
			statusReporter = status.NewNoOpReporter() // Fallback to no-op
		}
		defer statusReporter.Stop()
		statusReporter.Message("Initializing Cimian...")
	} else {
		statusReporter = status.NewNoOpReporter()
	}

	// Ensure cache directory exists.
	if err := os.MkdirAll(filepath.Clean(cachePath), 0755); err != nil {
		logging.Error("Failed to create cache directory: %v", err)
		os.Exit(1)
	}

	// Perform cache validation and cleanup to prevent corrupt downloads from causing issues
	statusReporter.Message("Validating cache integrity...")
	if err := download.ValidateAndCleanCache(cfg.CachePath); err != nil {
		logging.Warn("Cache validation encountered errors", "error", err)
		// Don't exit - continue with potentially degraded cache
	}

	// Retrieve manifests.
	statusReporter.Message("Retrieving manifests...")

	// Clear item sources tracking for this run
	process.ClearItemSources()

	var manifestItems []manifest.Item
	var mErr error

	// Check for local-only manifest override (Munki-compatible)
	// Command-line flag takes precedence over configuration setting
	if localManifestPath == "" && cfg.LocalOnlyManifest != "" {
		localManifestPath = cfg.LocalOnlyManifest
	}

	// Check for specific manifest target (--manifest flag)
	if *manifestTarget != "" {
		logging.Info("----------------------------------------------------------------------")
		logging.Info("🎯 SPECIFIC MANIFEST MODE")
		if verbosity > 0 {
			logging.Info("   Processing single manifest from server")
			logging.Info("----------------------------------------------------------------------")
			logging.Info("→ Target manifest", "target", *manifestTarget)
		}

		// Log manifest loading start
		logging.LogEventEntry("manifest", "load", "started",
			fmt.Sprintf("Loading specific manifest: %s", *manifestTarget),
			logging.WithContext("manifest_type", "specific"),
			logging.WithContext("manifest_target", *manifestTarget))

		manifestStart := time.Now()
		manifestItems, mErr = loadSpecificManifest(*manifestTarget, cfg)
		manifestDuration := time.Since(manifestStart)

		if mErr != nil {
			statusReporter.Error(fmt.Errorf("failed to load specific manifest: %v", mErr))
			logging.Error("Failed to load specific manifest '%s': %v", *manifestTarget, mErr)

			// Log manifest loading failure
			logging.LogEventEntry("manifest", "load", "failed",
				fmt.Sprintf("Failed to load specific manifest: %s", *manifestTarget),
				logging.WithContext("manifest_type", "specific"),
				logging.WithContext("manifest_target", *manifestTarget),
				logging.WithDuration(manifestDuration),
				logging.WithError(mErr))

			os.Exit(1)
		}

		// Log successful manifest loading
		logging.LogEventEntry("manifest", "load", "completed",
			fmt.Sprintf("Successfully loaded specific manifest with %d items", len(manifestItems)),
			logging.WithContext("manifest_type", "specific"),
			logging.WithContext("manifest_target", *manifestTarget),
			logging.WithContext("item_count", len(manifestItems)),
			logging.WithDuration(manifestDuration))

		if verbosity > 0 {
			logging.Success("✓ Loaded manifest with %d items in %v", len(manifestItems), manifestDuration)
		}

	} else if localManifestPath != "" {
		logging.Info("----------------------------------------------------------------------")
		logging.Info("📁 LOCAL-ONLY MANIFEST MODE")
		if verbosity > 0 {
			logging.Info("   Processing local manifest file only")
			logging.Info("----------------------------------------------------------------------")
			logging.Info("→ Local manifest path: %s", localManifestPath)
		}

		// Log manifest loading start
		logging.LogEventEntry("manifest", "load", "started",
			fmt.Sprintf("Loading local-only manifest: %s", localManifestPath),
			logging.WithContext("manifest_type", "local_only"),
			logging.WithContext("manifest_path", localManifestPath))

		manifestStart := time.Now()
		manifestItems, mErr = loadLocalOnlyManifest(localManifestPath)
		manifestDuration := time.Since(manifestStart)

		if mErr != nil {
			statusReporter.Error(fmt.Errorf("failed to load local-only manifest: %v", mErr))
			logging.Error("Failed to load local-only manifest: %v", mErr)

			// Log manifest loading failure
			logging.LogEventEntry("manifest", "load", "failed",
				fmt.Sprintf("Failed to load local-only manifest: %s", localManifestPath),
				logging.WithContext("manifest_type", "local_only"),
				logging.WithContext("manifest_path", localManifestPath),
				logging.WithDuration(manifestDuration),
				logging.WithError(mErr))

			os.Exit(1)
		}

		// Log successful manifest loading
		logging.LogEventEntry("manifest", "load", "completed",
			fmt.Sprintf("Successfully loaded local-only manifest with %d items", len(manifestItems)),
			logging.WithContext("manifest_type", "local_only"),
			logging.WithContext("manifest_path", localManifestPath),
			logging.WithContext("item_count", len(manifestItems)),
			logging.WithDuration(manifestDuration))

		if verbosity > 0 {
			logging.Success("✓ Loaded local manifest with %d items in %v", len(manifestItems), manifestDuration)
		}

	} else {
		logging.Info("----------------------------------------------------------------------")
		// logging.Info("🌐 STANDARD MANIFEST MODE")
		if verbosity > 0 {
			logging.Info("   Retrieving manifests from server")
			logging.Info("----------------------------------------------------------------------")
			if cfg.ClientIdentifier != "" {
				logging.Info("→ Client identifier", "identifier", cfg.ClientIdentifier)
			}
		}

		// Display enhanced loading header
		if verbosity >= 2 {
			targetItems := []string{}
			if itemFilter.HasFilter() {
				targetItems = itemFilter.GetItems()
			}
			displayLoadingHeader(targetItems, verbosity)
		}

		// Log standard manifest loading start
		logging.LogEventEntry("manifest", "load", "started",
			"Loading standard manifests from server",
			logging.WithContext("manifest_type", "server"),
			logging.WithContext("client_identifier", cfg.ClientIdentifier))

		manifestStart := time.Now()
		manifestItems, mErr = manifest.AuthenticatedGet(cfg)
		manifestDuration := time.Since(manifestStart)

		if mErr != nil {
			statusReporter.Error(fmt.Errorf("failed to retrieve manifests: %v", mErr))
			logging.Error("Failed to retrieve manifests: %v", mErr)

			// Log manifest loading failure
			logging.LogEventEntry("manifest", "load", "failed",
				"Failed to retrieve manifests from server",
				logging.WithContext("manifest_type", "server"),
				logging.WithContext("client_identifier", cfg.ClientIdentifier),
				logging.WithDuration(manifestDuration),
				logging.WithError(mErr))

			os.Exit(1)
		}

		// Log successful manifest loading
		logging.LogEventEntry("manifest", "load", "completed",
			fmt.Sprintf("Successfully loaded server manifests with %d items", len(manifestItems)),
			logging.WithContext("manifest_type", "server"),
			logging.WithContext("client_identifier", cfg.ClientIdentifier),
			logging.WithContext("item_count", len(manifestItems)),
			logging.WithDuration(manifestDuration))

		if verbosity > 0 {
			logging.Success("✓ Retrieved manifest items", "count", len(manifestItems), "duration", manifestDuration)
		}
	}

	// Apply item filter if specified
	manifestItems = itemFilter.Apply(manifestItems)

	// Clear and set up source tracking for all manifest items
	process.ClearItemSources()
	for _, manifestItem := range manifestItems {
		// Each manifestItem is now an individual item with an Action field, not a manifest with arrays
		if manifestItem.Name != "" {
			// Determine the source type based on the Action field
			sourceType := ""
			switch manifestItem.Action {
			case "install":
				sourceType = "managed_installs"
			case "update":
				sourceType = "managed_updates"
			case "uninstall":
				sourceType = "managed_uninstalls"
			case "profile":
				sourceType = "managed_profiles"
			case "app":
				sourceType = "managed_apps"
			default:
				sourceType = "optional_installs" // fallback for items without explicit action
			}

			// Debug logging to see what's being set
			if verbosity > 1 {
				logging.Debug("Setting item source", "item", manifestItem.Name, "sourceManifest", manifestItem.SourceManifest, "sourceType", sourceType)
			}
			process.SetItemSource(manifestItem.Name, manifestItem.SourceManifest, sourceType)
		}
	}

	// Override checkonly mode if item filter is active, but only if --checkonly wasn't explicitly set
	if itemFilter.ShouldOverrideCheckOnly() && !pflag.CommandLine.Changed("checkonly") {
		if verbosity > 0 {
			logging.Info("→ Item filter active, overriding default checkonly mode")
		} else {
			logging.Info("--item flag specified, overriding default checkonly mode")
		}
	} else if itemFilter.HasFilter() && *checkOnly {
		if verbosity > 0 {
			logging.Info("→ Item filter with explicit --checkonly: will check only specified items")
		} else {
			logging.Info("--item flag with explicit --checkonly: will check only specified items")
		}
	}

	// Special fallback for --manifest flag: if no catalogs were loaded from the manifest, use Production
	if *manifestTarget != "" && len(cfg.Catalogs) == 0 {
		cfg.Catalogs = []string{"Production"}
		logging.Info("No catalogs found in manifest when using --manifest flag, falling back to Production catalog")
	}

	logging.Info("----------------------------------------------------------------------")
	logging.Info("📚 LOADING CATALOG DATA")
	logging.Info("----------------------------------------------------------------------")
	statusReporter.Detail("Loading catalog data...")

	// Load local catalogs into a map (keys are lowercase names).
	if verbosity >= 2 {
		logging.Info("→ Loading local catalog items...")
	}
	localCatalogMap, err := loadLocalCatalogItems(cfg)
	if err != nil {
		statusReporter.Error(fmt.Errorf("failed to load local catalogs: %v", err))
		logging.Error("Failed to load local catalogs: %v", err)
		os.Exit(1)
	}
	if verbosity >= 2 {
		logging.Info("✓ Loaded local catalog items", "count", len(localCatalogMap))
	}

	// Convert to the expected format for advanced dependency processing
	statusReporter.Detail("Processing dependencies...")
	if verbosity >= 2 {
		logging.Info("→ Loading full catalog for dependency processing...")
	}
	fullCatalogMap := catalog.AuthenticatedGet(*cfg)
	if verbosity >= 2 {
		totalCatalogItems := 0
		for _, versionMap := range fullCatalogMap {
			totalCatalogItems += len(versionMap)
		}
		logging.Info("✓ Loaded catalog items across all versions", "count", totalCatalogItems)
	}

	// If install-only mode, perform installs and exit.
	if *installOnly {
		if verbosity > 0 {
			logging.Info("----------------------------------------------------------------------")
			logging.Info("⚡ INSTALL-ONLY MODE")
			logging.Info("   Installing pending updates")
			logging.Info("----------------------------------------------------------------------")
		} else {
			logging.Info("Running in install-only mode")
		}
		statusReporter.Message("Installing pending updates...")

		itemsToInstall := prepareDownloadItemsWithCatalog(manifestItems, localCatalogMap, cfg)
		if verbosity > 0 {
			logging.Info("→ Found items to install", "count", len(itemsToInstall))
		}

		if err := downloadAndInstallPerItem(itemsToInstall, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("failed to install pending updates: %v", err))
			logging.Error("Failed to install pending updates (install-only): %v", err)
			os.Exit(1)
		}

		if verbosity > 0 {
			logging.Success("✓ Install-only mode completed successfully!")
		} else {
			statusReporter.Message("Installation completed successfully!")
		}
		os.Exit(0)
	}

	// Gather actions: updates, new installs, removals.
	logging.Info("----------------------------------------------------------------------")
	logging.Info("🔍 ANALYZING SOFTWARE REQUIREMENTS")
	logging.Info("----------------------------------------------------------------------")
	statusReporter.Detail("Analyzing required changes...")

	var toInstall []catalog.Item
	var toUpdate []catalog.Item
	var toUninstall []catalog.Item

	if verbosity >= 2 {
		logging.Info("→ Deduplicating manifest items...")
	}
	dedupedManifestItems := status.DeduplicateManifestItems(manifestItems)

	if verbosity >= 2 {
		logging.Info("→ Preparing download items...")
	}
	itemsToProcess := prepareDownloadItemsWithCatalog(dedupedManifestItems, localCatalogMap, cfg)

	toUpdate = itemsToProcess

	toInstall = identifyNewInstalls(dedupedManifestItems, localCatalogMap, cfg)

	toUninstall = identifyRemovals(localCatalogMap, cfg)

	// Print summary of planned actions.
	statusReporter.Detail(fmt.Sprintf("Found %d updates, %d new installs, %d removals", len(toUpdate), len(toInstall), len(toUninstall)))

	printEnhancedManagedItemsSnapshot(toInstall, toUninstall, toUpdate, dedupedManifestItems, localCatalogMap)

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
			logging.Warn("Failed to end structured logging session: %v", err)
		}

		// Generate reports for external monitoring tools
		logging.Info("Generating reports for external monitoring tools...")
		baseDir := filepath.Join(os.Getenv("ProgramData"), "ManagedInstalls", "logs")
		exporter := reporting.NewDataExporter(baseDir)
		if err := exporter.ExportToReportsDirectory(48); err != nil {
			logging.Warn("Failed to export reports: %v", err)
		}

		os.Exit(0)
	}

	// Proceed with installations without user confirmation
	if verbosity > 0 {
		logging.Info("----------------------------------------------------------------------")
		logging.Info("⚡ EXECUTION PHASE")
		logging.Info("----------------------------------------------------------------------")
		if *auto {
			logging.Info("→ Auto mode enabled - proceeding automatically")
		} else {
			logging.Info("→ Proceeding with planned actions")
		}
	} else {
		if *auto {
			logging.Info("Auto mode enabled - proceeding with installation without confirmation")
		} else {
			logging.Info("Proceeding with installation without confirmation")
		}
	}

	// Combine install and update items and perform installations.
	var allToInstall []catalog.Item
	allToInstall = append(allToInstall, toInstall...)
	allToInstall = append(allToInstall, toUpdate...)

	// Check for self-updates and handle them specially
	var selfUpdateItems []catalog.Item
	var regularItems []catalog.Item

	for _, item := range allToInstall {
		if selfupdate.IsCimianPackage(item) {
			selfUpdateItems = append(selfUpdateItems, item)
			logging.Info("Detected Cimian self-update package", "item", item.Name, "version", item.Version)
		} else {
			regularItems = append(regularItems, item)
		}
	}

	// Handle self-updates by scheduling them for next restart
	if len(selfUpdateItems) > 0 {
		logging.Info("----------------------------------------------------------------------")
		logging.Info("🔄 CIMIAN SELF-UPDATE DETECTED")
		logging.Info("   %d self-update package(s) will be scheduled for next restart", len(selfUpdateItems))
		logging.Info("----------------------------------------------------------------------")

		selfUpdateManager := selfupdate.NewSelfUpdateManager()

		for _, item := range selfUpdateItems {
			// Download the self-update package first
			downloadItems := make(map[string]string)
			fullURL := item.Installer.Location
			if strings.HasPrefix(fullURL, "/") || strings.HasPrefix(fullURL, "\\") {
				fullURL = strings.ReplaceAll(fullURL, "\\", "/")
				if !strings.HasPrefix(fullURL, "/") {
					fullURL = "/" + fullURL
				}
				fullURL = strings.TrimRight(cfg.SoftwareRepoURL, "/") + "/pkgs" + fullURL
			}
			downloadItems[item.Name] = fullURL

			// Download the package
			statusReporter.Message(fmt.Sprintf("Downloading self-update package: %s", item.Name))
			downloadedPaths, err := download.InstallPendingUpdates(downloadItems, cfg)
			if err != nil {
				logging.Error("Failed to download self-update package", "item", item.Name, "error", err)
				continue
			}

			localFile, exists := downloadedPaths[item.Name]
			if !exists {
				logging.Error("Downloaded path not found for self-update package", "item", item.Name)
				continue
			}

			// Schedule the self-update
			if err := selfUpdateManager.ScheduleSelfUpdate(item, localFile, cfg); err != nil {
				logging.Error("Failed to schedule self-update", "item", item.Name, "error", err)
			} else {
				logging.Success("Self-update scheduled successfully", "item", item.Name, "version", item.Version)
			}
		}

		if len(selfUpdateItems) > 0 {
			logging.Info("----------------------------------------------------------------------")
			logging.Info("✅ SELF-UPDATE SCHEDULING COMPLETE")
			logging.Info("   Cimian will update itself on the next service restart")
			logging.Info("   You can restart the CimianWatcher service to apply updates immediately")
			logging.Info("----------------------------------------------------------------------")
		}

		// Update allToInstall to only include regular items
		allToInstall = regularItems
	}

	var installSuccess bool = true
	if len(allToInstall) > 0 {
		if verbosity > 0 {
			logger.Info("----------------------------------------------------------------------")
			logger.Info("📦 INSTALLING/UPDATING (%2d items)", len(allToInstall))
			logger.Info("----------------------------------------------------------------------")
		}
		statusReporter.Message("Installing and updating applications...")
		statusReporter.Percent(0) // Start progress tracking
		if err := downloadAndInstallWithAdvancedLogic(allToInstall, fullCatalogMap, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("some installations failed: %v", err))
			if verbosity > 0 {
				logger.Error("✗ Some installations failed: %v", err)
			} else {
				logger.Warning("Some items failed to install, continuing with remaining operations: %v", err)
			}
			installSuccess = false
		} else {
			if verbosity > 0 {
				logger.Success("✓ All installations completed successfully")
			}
			statusReporter.Percent(50) // Mid-way progress
		}
	}

	// Process uninstalls.
	var uninstallSuccess bool = true
	if len(toUninstall) > 0 {
		if verbosity > 0 {
			logger.Info("----------------------------------------------------------------------")
			logger.Info("🗑️  REMOVING (%2d items)", len(toUninstall))
			logger.Info("----------------------------------------------------------------------")
		}
		statusReporter.Message("Removing applications...")
		statusReporter.Percent(75) // Progress at 75%
		if err := uninstallWithAdvancedLogic(toUninstall, fullCatalogMap, cfg, statusReporter); err != nil {
			statusReporter.Error(fmt.Errorf("some uninstalls failed: %v", err))
			if verbosity > 0 {
				logger.Error("✗ Some removals failed: %v", err)
			} else {
				logger.Warning("Some items failed to uninstall, continuing with remaining operations: %v", err)
			}
			uninstallSuccess = false
		} else {
			if verbosity > 0 {
				logger.Success("✓ All removals completed successfully")
			}
		}
	}

	// For auto mode: if the user is active, skip updates.
	if *auto {
		if isUserActive() {
			logger.Info("User is active. Skipping automatic updates: %d", getIdleSeconds())
			os.Exit(0)
		}
	}

	// Log summary of operations with structured events
	sessionStatus := "completed"
	if installSuccess && uninstallSuccess {
		if verbosity > 0 {
			logger.Info("================================================================================")
			logger.Info("✅ OPERATION SUCCESSFUL")
			logger.Info("   All software updates completed")
			logger.Info("================================================================================")
		} else {
			logger.Info("Software updates completed successfully")
		}
		logging.LogEventEntry("session", "summary", "completed",
			fmt.Sprintf("All operations completed successfully: %d installs, %d updates, %d removals",
				len(toInstall), len(toUpdate), len(toUninstall)),
			logging.WithContext("install_count", len(toInstall)),
			logging.WithContext("update_count", len(toUpdate)),
			logging.WithContext("removal_count", len(toUninstall)),
			logging.WithContext("install_success", installSuccess),
			logging.WithContext("uninstall_success", uninstallSuccess))
	} else if !installSuccess && !uninstallSuccess {
		if verbosity > 0 {
			logger.Warning("================================================================================")
			logger.Warning("⚠️  PARTIAL FAILURES")
			logger.Warning("   Some installations and removals failed")
			logger.Warning("================================================================================")
		} else {
			logger.Warning("Software updates completed with some failures in both installations and uninstalls")
		}
		sessionStatus = "partial_failure"
		logging.LogEventEntry("session", "summary", "partial_failure",
			"Software updates completed with failures in both installations and uninstalls",
			logging.WithContext("install_count", len(toInstall)),
			logging.WithContext("update_count", len(toUpdate)),
			logging.WithContext("removal_count", len(toUninstall)),
			logging.WithContext("install_success", installSuccess),
			logging.WithContext("uninstall_success", uninstallSuccess))
	} else if !installSuccess {
		if verbosity > 0 {
			logger.Warning("================================================================================")
			logger.Warning("❌ INSTALLATION FAILURES")
			logger.Warning("   Some installations failed")
			logger.Warning("================================================================================")
		} else {
			logger.Warning("Software updates completed with some installation failures")
		}
		sessionStatus = "partial_failure"
		logging.LogEventEntry("session", "summary", "partial_failure",
			"Software updates completed with some installation failures",
			logging.WithContext("install_count", len(toInstall)),
			logging.WithContext("update_count", len(toUpdate)),
			logging.WithContext("removal_count", len(toUninstall)),
			logging.WithContext("install_success", installSuccess),
			logging.WithContext("uninstall_success", uninstallSuccess))
	} else {
		if verbosity > 0 {
			logger.Warning("================================================================================")
			logger.Warning("🗑️  REMOVAL FAILURES")
			logger.Warning("   Some removals failed")
			logger.Warning("================================================================================")
		} else {
			logger.Warning("Software updates completed with some uninstall failures")
		}
		sessionStatus = "partial_failure"
		logging.LogEventEntry("session", "summary", "partial_failure",
			"Software updates completed with some uninstall failures",
			logging.WithContext("install_count", len(toInstall)),
			logging.WithContext("update_count", len(toUpdate)),
			logging.WithContext("removal_count", len(toUninstall)),
			logging.WithContext("install_success", installSuccess),
			logging.WithContext("uninstall_success", uninstallSuccess))
	}

	statusReporter.Message("Finalizing installation...")
	statusReporter.Percent(90)

	// Run postflight script (unless bypassed by flag or config).
	skipPostflight := *noPostflight || cfg.NoPostflight
	if !skipPostflight {
		if verbosity > 0 {
			logger.Info("----------------------------------------------------------------------")
			logger.Info("🔄 POSTFLIGHT EXECUTION")
			logger.Info("----------------------------------------------------------------------")
		}
		statusReporter.Detail("Running post-installation scripts...")
		runPostflightIfNeeded(verbosity, cfg)
		if verbosity > 0 {
			logger.Success("✓ Postflight script completed")
		} else {
			logger.Success("Postflight script completed.")
		}
	} else {
		if verbosity > 0 {
			logging.Info("----------------------------------------------------------------------")
			logging.Info("⏭️  POSTFLIGHT SCRIPT BYPASSED")
			if *noPostflight {
				logging.Info("   Reason: --no-postflight flag")
			} else {
				logging.Info("   Reason: NoPostflight config setting")
			}
			logging.Info("----------------------------------------------------------------------")
		} else {
			if *noPostflight {
				logging.Info("Postflight script execution bypassed by --no-postflight flag")
			} else {
				logging.Info("Postflight script execution bypassed by NoPostflight configuration setting")
			}
		}
	}

	// Generate reports for external monitoring tools
	statusReporter.Detail("Generating system reports...")
	exporter := reporting.NewDataExporter(`C:\ProgramData\ManagedInstalls\logs`)
	if err := exporter.ExportToReportsDirectory(7); err != nil { // Export last 7 days
		logger.Warning("Failed to generate reports: %v", err)
	} else {
		logger.Info("Reports exported successfully to C:\\ProgramData\\ManagedInstalls\\reports")
	}

	statusReporter.Detail("Cleaning up temporary files...")
	cacheFolder := `C:\ProgramData\ManagedInstalls\Cache`
	currentLogDir := logging.GetCurrentLogDir()
	clearCacheFolderSelective(cacheFolder, currentLogDir)

	// Clear bootstrap mode if we completed successfully
	if isBootstrap {
		if err := clearBootstrapAfterSuccess(); err != nil {
			logger.Warning("Failed to clear bootstrap mode: %v", err)
		}
	}

	statusReporter.Message("All operations completed successfully!")
	statusReporter.Percent(100)
	statusReporter.Stop()

	// End structured logging session with comprehensive summary
	successCount := 0
	failureCount := 0
	if installSuccess {
		successCount += len(toInstall) + len(toUpdate)
	} else {
		failureCount += len(toInstall) + len(toUpdate)
	}
	if uninstallSuccess {
		successCount += len(toUninstall)
	} else {
		failureCount += len(toUninstall)
	}

	summary := logging.SessionSummary{
		TotalActions:    len(toInstall) + len(toUpdate) + len(toUninstall),
		Installs:        len(toInstall),
		Updates:         len(toUpdate),
		Removals:        len(toUninstall),
		Successes:       successCount,
		Failures:        failureCount,
		PackagesHandled: extractPackageNames(toInstall, toUpdate, toUninstall),
	}

	// Log session end event
	logging.LogEventEntry("session", "end", sessionStatus,
		fmt.Sprintf("Session completed with status: %s", sessionStatus),
		logging.WithContext("total_actions", summary.TotalActions),
		logging.WithContext("success_count", summary.Successes),
		logging.WithContext("failure_count", summary.Failures),
		logging.WithContext("packages", summary.PackagesHandled))

	if err := logging.EndSession(sessionStatus, summary); err != nil {
		logger.Warning("Failed to end structured logging session: %v", err)
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
// Behavior depends on configuration: continue (default), abort, or warn.
func runPreflightIfNeeded(verbosity int, cfg *config.Configuration) {
	logInfo := func(format string, args ...interface{}) {
		if verbosity >= 2 {
			logger.Info(format, args...)
		} else {
			logger.Debug(format, args...)
		}
	}
	logError := func(format string, args ...interface{}) {
		logger.Error(format, args...)
	}

	if err := scripts.RunPreflight(verbosity, logInfo, logError); err != nil {
		// Get the failure action from configuration (default to "continue")
		failureAction := cfg.PreflightFailureAction
		if failureAction == "" {
			failureAction = "continue"
		}

		switch strings.ToLower(failureAction) {
		case "abort":
			logger.Error("Preflight script failed: %v", err)
			logger.Error("managedsoftwareupdate run aborted by preflight script failure (PreflightFailureAction=abort)")
			// Exit like Munki does when preflight fails
			os.Exit(1)
		case "warn":
			logger.Warning("Preflight script failed: %v", err)
			logger.Warning("Preflight script failure detected - continuing with software updates (PreflightFailureAction=warn)")
		default: // "continue" or any other value
			logger.Error("Preflight script failed: %v", err)
			logger.Warning("Preflight script failure detected - continuing with software updates (PreflightFailureAction=continue)")
			logger.Warning("Consider investigating preflight script issues to ensure proper system preparation")
		}

		// Log structured event for preflight failure
		logging.LogEventEntry("preflight", "execute", "failed",
			fmt.Sprintf("Preflight script failed (action=%s): %v", failureAction, err),
			logging.WithError(err),
			logging.WithContext("failure_action", failureAction),
			logging.WithContext("continue_on_failure", failureAction != "abort"))
	}
}

// loadLocalOnlyManifest loads a local manifest file for processing.
// This implements Munki-compatible LocalOnlyManifest functionality.
func loadLocalOnlyManifest(manifestPath string) ([]manifest.Item, error) {
	logger.Info("Loading local-only manifest from: %s", manifestPath)

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
		ManagedProfiles:   manifestFile.ManagedProfiles,
		ManagedApps:       manifestFile.ManagedApps,
		Catalogs:          manifestFile.Catalogs,
		Includes:          manifestFile.IncludedManifests,
	}

	items = append(items, item)

	logger.Info("Successfully loaded local-only manifest with %d managed_installs, %d managed_uninstalls, %d managed_updates, %d managed_profiles, %d managed_apps",
		len(item.ManagedInstalls), len(item.ManagedUninstalls), len(item.ManagedUpdates), len(item.ManagedProfiles), len(item.ManagedApps))

	return items, nil
}

// loadSpecificManifest loads a specific manifest from the server.
// This allows targeting a specific manifest path instead of using ClientIdentifier.
func loadSpecificManifest(manifestName string, cfg *config.Configuration) ([]manifest.Item, error) {
	logger.Info("Loading specific manifest from server: %s", manifestName)

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

	logger.Info("Successfully loaded specific manifest '%s' with %d items (self-service skipped)", manifestName, len(manifestItems))

	return manifestItems, nil
}

// runPostflightIfNeeded runs the postflight script.
// Behavior depends on configuration: continue (default), abort, or warn.
func runPostflightIfNeeded(verbosity int, cfg *config.Configuration) {
	logInfo := func(format string, args ...interface{}) {
		if verbosity >= 2 {
			logger.Info(format, args...)
		} else {
			logger.Debug(format, args...)
		}
	}
	logError := func(format string, args ...interface{}) {
		logger.Error(format, args...)
	}

	if err := scripts.RunPostflight(verbosity, logInfo, logError); err != nil {
		// Get the failure action from configuration (default to "continue")
		failureAction := cfg.PostflightFailureAction
		if failureAction == "" {
			failureAction = "continue"
		}

		switch strings.ToLower(failureAction) {
		case "abort":
			logger.Error("Postflight script failed: %v", err)
			logger.Error("managedsoftwareupdate run aborted by postflight script failure (PostflightFailureAction=abort)")
			// Exit when postflight fails with abort setting
			os.Exit(1)
		case "warn":
			logger.Warning("Postflight script failed: %v", err)
			logger.Warning("Postflight script failure detected - software updates completed but cleanup may be incomplete (PostflightFailureAction=warn)")
		default: // "continue" or any other value
			logger.Error("Postflight script failed: %v", err)
			logger.Warning("Postflight script failure detected - software updates completed but cleanup may be incomplete (PostflightFailureAction=continue)")
			logger.Warning("Consider investigating postflight script issues to ensure proper system cleanup")
		}

		// Log structured event for postflight failure
		logging.LogEventEntry("postflight", "execute", "failed",
			fmt.Sprintf("Postflight script failed (action=%s): %v", failureAction, err),
			logging.WithError(err),
			logging.WithContext("failure_action", failureAction),
			logging.WithContext("continue_on_failure", failureAction != "abort"))
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

// identifyNewInstalls checks each manifest item and returns those NOT present in the local catalog.
func identifyNewInstalls(manifestItems []manifest.Item, localCatalogMap map[string]catalog.Item, cfg *config.Configuration) []catalog.Item {
	_ = cfg // dummy reference to suppress "unused parameter" warning

	var toInstall []catalog.Item
	for _, mItem := range manifestItems {
		if mItem.Name == "" {
			continue
		}

		// Only process items with Action "install" - skip profiles, apps, updates, etc.
		if mItem.Action != "install" {
			// Don't log profile/app skips as they're expected behavior
			if mItem.Action != "profile" && mItem.Action != "app" {
				logging.Debug("Skipping non-install item", "item", mItem.Name, "action", mItem.Action)
			}
			continue
		}

		key := strings.ToLower(mItem.Name)
		if _, found := localCatalogMap[key]; !found {
			logging.Info("Identified new item for installation", "item", mItem.Name)

			// Source manifest is already set in the mItem structure
			sourceManifest := mItem.SourceManifest
			process.SetItemSource(mItem.Name, sourceManifest, "managed_installs")

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
// Continues with remaining items even if some fail, only returns error if ALL fail
func uninstallCatalogItems(items []catalog.Item, cfg *config.Configuration) error {
	_ = cfg // dummy reference to suppress "unused parameter" warning

	if len(items) == 0 {
		logging.Debug("No items to uninstall.")
		return nil
	}

	// Log batch uninstall start
	logging.LogEventEntry("batch_uninstall", "start", "started",
		fmt.Sprintf("Starting batch uninstall of %d items", len(items)),
		logging.WithContext("item_count", len(items)),
		logging.WithContext("batch_id", logging.GetSessionID()))

	logging.Info("Starting batch uninstall of items", "count", len(items))
	var failedItems []string
	var uninstallErrors []error
	var successCount int
	itemNames := make([]string, 0, len(items))

	for _, item := range items {
		itemNames = append(itemNames, item.Name)

		// Log individual uninstall start
		logging.LogEventEntry("uninstall", "start", "started",
			fmt.Sprintf("Starting uninstall of %s", item.Name),
			logging.WithPackage(item.Name, item.Version),
			logging.WithContext("checkonly_mode", cfg.CheckOnly))

		// Check for blocking applications before unattended uninstalls
		if blocking.BlockingApplicationsRunning(item) {
			runningApps := blocking.GetRunningBlockingApps(item)
			logger.Warning("Blocking applications are running for %s: %v", item.Name, runningApps)
			logger.Info("Skipping unattended uninstall of %s due to blocking applications", item.Name)

			// Log structured event for blocking applications
			logging.LogEventEntry("uninstall", "blocked", "skipped",
				fmt.Sprintf("Uninstall of %s skipped due to blocking applications", item.Name),
				logging.WithPackage(item.Name, item.Version),
				logging.WithContext("blocking_apps", runningApps),
				logging.WithError(fmt.Errorf("blocking applications running: %v", runningApps)))

			failedItems = append(failedItems, fmt.Sprintf("%s (blocked by: %v)", item.Name, runningApps))
			continue
		}

		uninstallStart := time.Now()
		uninstallOutput, err := installer.Install(item, "uninstall", "", cfg.CachePath, cfg.CheckOnly, cfg)
		uninstallDuration := time.Since(uninstallStart)

		if err != nil {
			logger.Error("Failed to uninstall item, continuing with others: %s, error: %v", item.Name, err)

			// Log detailed failure information
			logging.LogEventEntry("uninstall", "complete", "failed",
				fmt.Sprintf("Failed to uninstall %s: %v", item.Name, err),
				logging.WithPackage(item.Name, item.Version),
				logging.WithDuration(uninstallDuration),
				logging.WithContext("uninstaller_output", uninstallOutput),
				logging.WithError(err))

			failedItems = append(failedItems, item.Name)
			uninstallErrors = append(uninstallErrors, fmt.Errorf("%s: %w", item.Name, err))
		} else {
			logging.Info("Uninstall successful", "item", item.Name)

			// Log successful uninstall with detailed metrics
			logging.LogEventEntry("uninstall", "complete", "completed",
				fmt.Sprintf("Successfully uninstalled %s", item.Name),
				logging.WithPackage(item.Name, item.Version),
				logging.WithDuration(uninstallDuration),
				logging.WithContext("uninstaller_output", uninstallOutput))

			successCount++
		}
	}

	// Log batch uninstall summary
	if len(failedItems) > 0 {
		logger.Warning("Uninstall summary: %d succeeded, %d failed out of %d total items", successCount, len(failedItems), len(items))

		logging.LogEventEntry("batch_uninstall", "complete", "partial_failure",
			fmt.Sprintf("Batch uninstall completed: %d succeeded, %d failed out of %d total items",
				successCount, len(failedItems), len(items)),
			logging.WithContext("success_count", successCount),
			logging.WithContext("fail_count", len(failedItems)),
			logging.WithContext("total_count", len(items)),
			logging.WithContext("failed_items", failedItems),
			logging.WithError(fmt.Errorf("some uninstalls failed: %v", uninstallErrors)))

		// Only return error if ALL items failed
		if successCount == 0 {
			return fmt.Errorf("all %d items failed to uninstall: %v", len(items), failedItems)
		}
	} else {
		logger.Info("All %d items uninstalled successfully", successCount)

		logging.LogEventEntry("batch_uninstall", "complete", "completed",
			fmt.Sprintf("All %d items uninstalled successfully", successCount),
			logging.WithContext("success_count", successCount),
			logging.WithContext("total_count", len(items)),
			logging.WithContext("items", itemNames))
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
		// Only process items with Action "update" or "install" - skip profiles, apps, etc.
		if m.Action != "update" && m.Action != "install" {
			// Don't log profile/app skips as they're expected behavior
			if m.Action != "profile" && m.Action != "app" {
				logging.Debug("Skipping non-installable item", "item", m.Name, "action", m.Action)
			}
			continue
		}

		if installer.LocalNeedsUpdate(m, catMap, cfg) {
			key := strings.ToLower(m.Name)
			catItem, found := catMap[key]
			if !found {
				logger.Warning("Skipping item not in local catalog: %s", m.Name)
				continue
			}

			// Source manifest is already set in the m structure
			sourceManifest := m.SourceManifest
			process.SetItemSource(m.Name, sourceManifest, "managed_updates")

			results = append(results, catItem)
		}
	}
	return results
}

// downloadAndInstallPerItem handles downloading & installing each catalog item individually,
// ensuring exact file paths match installer expectations.
func downloadAndInstallPerItem(items []catalog.Item, cfg *config.Configuration, statusReporter status.Reporter) error {
	// Log the start of batch installation
	logging.LogEventEntry("batch_install", "start", "started",
		fmt.Sprintf("Starting batch installation of %d items", len(items)),
		logging.WithContext("item_count", len(items)),
		logging.WithContext("batch_id", logging.GetSessionID()))

	downloadItems := make(map[string]string)
	itemNames := make([]string, 0, len(items))

	// Prepare the correct full URLs for each item
	for _, cItem := range items {
		itemNames = append(itemNames, cItem.Name)

		// Check if this is a script-only item (no installer file needed)
		logging.Debug("Script-only detection values",
			"item", cItem.Name,
			"installer_type", cItem.Installer.Type,
			"installer_location", cItem.Installer.Location,
			"installcheck_script_len", len(string(cItem.InstallCheckScript)),
			"preinstall_script_len", len(string(cItem.PreScript)),
			"postinstall_script_len", len(string(cItem.PostScript)))

		if cItem.Installer.Type == "" && cItem.Installer.Location == "" &&
			(string(cItem.InstallCheckScript) != "" || string(cItem.PreScript) != "" || string(cItem.PostScript) != "") {
			logging.Debug("Script-only item detected, skipping download preparation", "item", cItem.Name)
			continue
		}

		if cItem.Installer.Location == "" {
			logger.Warning("No installer location found for item: %s", cItem.Name)
			logging.LogEventEntry("install", "prepare", "failed",
				fmt.Sprintf("No installer location found for %s", cItem.Name),
				logging.WithPackage(cItem.Name, cItem.Version),
				logging.WithError(fmt.Errorf("missing installer location")))
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

		// Log download preparation
		logging.LogEventEntry("download", "prepare", "prepared",
			fmt.Sprintf("Prepared download for %s from %s", cItem.Name, fullURL),
			logging.WithPackage(cItem.Name, cItem.Version),
			logging.WithContext("download_url", fullURL))
	}

	// Log batch download start
	logging.LogEventEntry("batch_download", "start", "started",
		fmt.Sprintf("Starting batch download of %d items", len(downloadItems)),
		logging.WithContext("item_count", len(downloadItems)),
		logging.WithContext("items", itemNames))

	statusReporter.Detail(fmt.Sprintf("Downloading %d packages...", len(downloadItems)))

	// Download each item and retrieve precise downloaded file paths
	downloadedPaths, err := download.InstallPendingUpdates(downloadItems, cfg)
	if err != nil {
		logger.Warning("Some downloads may have failed, attempting installation with available files: %v", err)
		logging.LogEventEntry("batch_download", "complete", "partial_failure",
			fmt.Sprintf("Batch download completed with some failures: %v", err),
			logging.WithContext("item_count", len(downloadItems)),
			logging.WithError(err))
		// Continue with whatever was downloaded successfully
		if downloadedPaths == nil {
			downloadedPaths = make(map[string]string)
		}
	} else {
		logging.LogEventEntry("batch_download", "complete", "completed",
			fmt.Sprintf("Successfully downloaded %d items", len(downloadedPaths)),
			logging.WithContext("downloaded_count", len(downloadedPaths)))
	}

	var successCount, failCount int
	var installErrors []error

	// Perform installation for each item using the correct paths
	for _, cItem := range items {
		// Check if this is a script-only item (no download needed)
		isScriptOnly := cItem.Installer.Type == "" && cItem.Installer.Location == "" &&
			(cItem.Check.Script != "" || string(cItem.PreScript) != "" || string(cItem.PostScript) != "")

		var localFile string
		if isScriptOnly {
			// Script-only item - no local file needed
			logger.Info("Installing script-only item: %s", cItem.Name)
			localFile = "" // Empty string for script-only items
		} else {
			// Regular item - must have a downloaded file
			var exists bool
			localFile, exists = downloadedPaths[cItem.Name]
			if !exists {
				logger.Error("Downloaded path not found for item: %s", cItem.Name)
				logging.LogEventEntry("install", "start", "failed",
					fmt.Sprintf("Downloaded path not found for %s", cItem.Name),
					logging.WithPackage(cItem.Name, cItem.Version),
					logging.WithError(fmt.Errorf("downloaded path not found")))
				failCount++
				continue
			}
			logger.Info("Installing downloaded item: %s, file: %s", cItem.Name, localFile)
		}

		statusReporter.Detail(fmt.Sprintf("Installing %s...", cItem.Name))

		// Log individual install start
		logging.LogEventEntry("install", "start", "started",
			fmt.Sprintf("Starting installation of %s", cItem.Name),
			logging.WithPackage(cItem.Name, cItem.Version),
			logging.WithContext("installer_path", localFile))

		installStart := time.Now()
		if err := installOneCatalogItem(cItem, localFile, cfg); err != nil {
			duration := time.Since(installStart)
			logger.Error("Installation command failed: %s, error: %v", cItem.Name, err)
			logging.LogEventEntry("install", "complete", "failed",
				fmt.Sprintf("Failed to install %s: %v", cItem.Name, err),
				logging.WithPackage(cItem.Name, cItem.Version),
				logging.WithDuration(duration),
				logging.WithError(err))
			installErrors = append(installErrors, fmt.Errorf("%s: %w", cItem.Name, err))
			failCount++
			continue
		}

		duration := time.Since(installStart)
		logging.LogEventEntry("install", "complete", "completed",
			fmt.Sprintf("Successfully installed %s", cItem.Name),
			logging.WithPackage(cItem.Name, cItem.Version),
			logging.WithDuration(duration))
		successCount++
	}

	// Log batch installation summary
	if failCount > 0 {
		logger.Warning("Installation summary: %d succeeded, %d failed out of %d total items", successCount, failCount, len(items))
		logging.LogEventEntry("batch_install", "complete", "partial_failure",
			fmt.Sprintf("Batch installation completed: %d succeeded, %d failed out of %d total items",
				successCount, failCount, len(items)),
			logging.WithContext("success_count", successCount),
			logging.WithContext("fail_count", failCount),
			logging.WithContext("total_count", len(items)),
			logging.WithError(fmt.Errorf("some installations failed: %v", installErrors)))
	} else {
		if successCount > 0 {
			logger.Info("All %d items installed successfully", successCount)
			logging.LogEventEntry("batch_install", "complete", "completed",
				fmt.Sprintf("All %d items installed successfully", successCount),
				logging.WithContext("success_count", successCount),
				logging.WithContext("total_count", len(items)))
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
	// Check if no actions are planned.
	if len(toInstall) == 0 && len(toUninstall) == 0 && len(toUpdate) == 0 {
		logger.Info("")
		logger.Info("================================================================================")
		logger.Info("✅ All software is up to date")
		logger.Info("================================================================================")
		logger.Info("")
		return
	}

	// Print INSTALL actions section
	if len(toInstall) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("⬇️   NEW INSTALLS")
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-27s | %-17s | %-15s", "Package Name", "Version", "Type")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range toInstall {
			installType := item.Installer.Type
			if installType == "" {
				installType = "script"
			}
			logger.Info("%-27s | %-17s | %-15s",
				truncateString(item.Name, 25),
				truncateString(item.Version, 15),
				truncateString(installType, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Print UPDATE actions section
	if len(toUpdate) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("🔄 UPDATES")
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-27s | %-17s | %-15s", "Package Name", "New Version", "Type")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range toUpdate {
			updateType := item.Installer.Type
			if updateType == "" {
				updateType = "scripts"
			}
			logger.Info("%-27s | %-17s | %-15s",
				truncateString(item.Name, 25),
				truncateString(item.Version, 15),
				truncateString(updateType, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Print UNINSTALL actions section
	if len(toUninstall) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("🗑️  REMOVALS")
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-27s | %-17s | %-15s", "Package Name", "Current Ver.", "Method")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range toUninstall {
			uninstallMethod := "Auto-detect"
			if len(item.Uninstaller) == 1 {
				uninstallMethod = item.Uninstaller[0].Type
			} else if len(item.Uninstaller) > 1 {
				uninstallMethod = "Multiple"
			}

			currentVersion := "Unknown"
			if item.Version != "" {
				currentVersion = item.Version
			}

			logger.Info("%-27s | %-17s | %-15s",
				truncateString(item.Name, 25),
				truncateString(currentVersion, 15),
				truncateString(uninstallMethod, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Summary footer
	totalActions := len(toInstall) + len(toUpdate) + len(toUninstall)
	logger.Info("📊 Total actions: %d", totalActions)
	logger.Info("⬇️  Installs: %d | 🔄 Updates: %d | 🗑️  Removals: %d",
		len(toInstall), len(toUpdate), len(toUninstall))
	logger.Info("")
}

// truncateString truncates a string to the specified width, adding "..." if needed
func truncateString(s string, width int) string {
	if len(s) <= width {
		return s
	}
	if width <= 3 {
		return s[:width]
	}
	return s[:width-3] + "..."
}

// printEnhancedManagedItemsSnapshot prints a comprehensive snapshot of all managed items
// including pending actions and the complete managed software inventory
func printEnhancedManagedItemsSnapshot(toInstall, toUninstall, toUpdate []catalog.Item, manifestItems []manifest.Item, localCatalogMap map[string]catalog.Item) {
	// Categorize all manifest items by action type
	var managedInstalls []manifest.Item
	var managedUpdates []manifest.Item
	var managedUninstalls []manifest.Item
	var optionalInstalls []manifest.Item
	var managedProfiles []manifest.Item
	var managedApps []manifest.Item

	for _, item := range manifestItems {
		switch item.Action {
		case "install":
			managedInstalls = append(managedInstalls, item)
		case "update":
			managedUpdates = append(managedUpdates, item)
		case "uninstall":
			managedUninstalls = append(managedUninstalls, item)
		case "optional":
			optionalInstalls = append(optionalInstalls, item)
		case "profile":
			managedProfiles = append(managedProfiles, item)
		case "app":
			managedApps = append(managedApps, item)
		}
	}

	// Only show the enhanced inventory if there are actually managed items
	totalManagedItems := len(managedInstalls) + len(optionalInstalls) + len(managedUpdates) + len(managedUninstalls)
	totalExternalItems := len(managedProfiles) + len(managedApps)
	
	if totalManagedItems == 0 && totalExternalItems == 0 {
		logger.Info("📋 No managed software inventory found")
		logger.Info("")
		return
	}

	// Display manifest hierarchy with package details
	displayManifestTreeWithPackages(manifestItems, toInstall, toUpdate, localCatalogMap)

	// Show Managed Installs section
	if len(managedInstalls) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("📦 MANAGED INSTALLS (%d items)", len(managedInstalls))
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-27s | %-17s | %-15s", "Package Name", "Version", "Status")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range managedInstalls {
			status := getPackageStatusDisplay(item, toInstall, toUpdate, localCatalogMap)
			version := item.Version
			if version == "" {
				version = "Unknown"
			}
			logger.Info("%-27s | %-17s | %-15s",
				truncateString(item.Name, 25),
				truncateString(version, 15),
				truncateString(status, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Show Optional Installs section
	if len(optionalInstalls) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("🔧 OPTIONAL INSTALLS (%d items)", len(optionalInstalls))
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-27s | %-17s | %-15s", "Package Name", "Version", "Status")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range optionalInstalls {
			status := getPackageStatusDisplay(item, toInstall, toUpdate, localCatalogMap)
			version := item.Version
			if version == "" {
				version = "Unknown"
			}
			logger.Info("%-27s | %-17s | %-15s",
				truncateString(item.Name, 25),
				truncateString(version, 15),
				truncateString(status, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Show Managed Updates section
	if len(managedUpdates) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("🔄 MANAGED UPDATES (%d items)", len(managedUpdates))
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-27s | %-17s | %-15s", "Package Name", "Version", "Status")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range managedUpdates {
			status := getPackageStatusDisplay(item, toInstall, toUpdate, localCatalogMap)
			version := item.Version
			if version == "" {
				version = "Unknown"
			}
			logger.Info("%-27s | %-17s | %-15s",
				truncateString(item.Name, 25),
				truncateString(version, 15),
				truncateString(status, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Show Managed Uninstalls section
	if len(managedUninstalls) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("🗑️  MANAGED UNINSTALLS (%d items)", len(managedUninstalls))
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-27s | %-17s | %-15s", "Package Name", "Version", "Status")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range managedUninstalls {
			status := getPackageStatusDisplay(item, toInstall, toUpdate, localCatalogMap)
			version := item.Version
			if version == "" {
				version = "Unknown"
			}
			logger.Info("%-27s | %-17s | %-15s",
				truncateString(item.Name, 25),
				truncateString(version, 15),
				truncateString(status, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Show Managed Profiles section (external MDM management)
	if len(managedProfiles) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("📋 MANAGED PROFILES (%d items) - External MDM Management", len(managedProfiles))
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-45s | %-15s", "Profile Name", "Source")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range managedProfiles {
			source := item.SourceManifest
			if source == "" {
				source = "Unknown"
			}
			logger.Info("%-45s | %-15s",
				truncateString(item.Name, 43),
				truncateString(source, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Show Managed Apps section (external MDM management)
	if len(managedApps) > 0 {
		logger.Info("----------------------------------------------------------------------")
		logger.Info("📱 MANAGED APPS (%d items) - External MDM Management", len(managedApps))
		logger.Info("----------------------------------------------------------------------")
		logger.Info("%-45s | %-15s", "App Name", "Source")
		logger.Info("----------------------------------------------------------------------")

		for _, item := range managedApps {
			source := item.SourceManifest
			if source == "" {
				source = "Unknown"
			}
			logger.Info("%-45s | %-15s",
				truncateString(item.Name, 43),
				truncateString(source, 15))
		}
		logger.Info("----------------------------------------------------------------------")
		logger.Info("")
	}

	// Summary footer with complete inventory statistics
	totalItems := totalManagedItems + totalExternalItems

	logger.Info("📊 INVENTORY SUMMARY")
	logger.Info("   Total managed items: %d", totalItems)
	logger.Info("   📦 Installs: %d | 🔧 Optionals: %d | 🔄 Updates: %d | 🗑️  Uninstalls: %d", 
		len(managedInstalls), len(optionalInstalls), len(managedUpdates), len(managedUninstalls))
	if totalExternalItems > 0 {
		logger.Info("   📋 Managed profiles: %d | 📱 Managed apps: %d", len(managedProfiles), len(managedApps))
	}
	logger.Info("")
	logger.Info("⚡ PENDING ACTIONS SUMMARY")
	totalActions := len(toInstall) + len(toUpdate) + len(toUninstall)
	logger.Info("   Total pending actions: %d", totalActions)
	logger.Info("   ⬇️  New installs: %d | 🔄 Updates: %d | 🗑️  Removals: %d", len(toInstall), len(toUpdate), len(toUninstall))
	logger.Info("")
}

// getPackageStatusDisplay determines the display status for a package in the inventory
func getPackageStatusDisplay(manifestItem manifest.Item, toInstall, toUpdate []catalog.Item, localCatalogMap map[string]catalog.Item) string {
	itemName := strings.ToLower(manifestItem.Name)
	
	// Check if it's in pending installs
	for _, installItem := range toInstall {
		if strings.ToLower(installItem.Name) == itemName {
			return "Pending Install"
		}
	}
	
	// Check if it's in pending updates
	for _, updateItem := range toUpdate {
		if strings.ToLower(updateItem.Name) == itemName {
			return "Pending Update"
		}
	}
	
	// Check if it exists in local catalog (installed)
	if _, exists := localCatalogMap[itemName]; exists {
		return "Installed"
	}
	
	// Default status
	return "Not Installed"
}

// installOneCatalogItem installs a single catalog item using the installer package.
// It normalizes the architecture and handles installation output for error detection.
func installOneCatalogItem(cItem catalog.Item, localFile string, cfg *config.Configuration) error {
	normalizeArchitecture(&cItem)
	sysArch := status.GetSystemArchitecture()
	logging.Debug("Detected system architecture: %s", sysArch)
	logging.Debug("Supported architectures for item: %s, supported_arch: %v", cItem.Name, cItem.SupportedArch)

	// Log detailed pre-installation context
	logging.LogEventEntry("install", "pre_check", "checking",
		fmt.Sprintf("Pre-installation check for %s", cItem.Name),
		logging.WithPackage(cItem.Name, cItem.Version),
		logging.WithContext("installer_type", cItem.Installer.Type),
		logging.WithContext("installer_path", localFile),
		logging.WithContext("system_arch", sysArch),
		logging.WithContext("supported_arch", cItem.SupportedArch))

	// Check for blocking applications before unattended installs (following Munki's behavior)
	// Only applies to items marked as unattended_install or in auto/bootstrap mode
	if blocking.BlockingApplicationsRunning(cItem) {
		runningApps := blocking.GetRunningBlockingApps(cItem)
		logger.Warning("Blocking applications are running for %s: %v", cItem.Name, runningApps)
		logger.Info("Skipping unattended install of %s due to blocking applications", cItem.Name)

		// Log structured event for blocking applications
		logging.LogEventEntry("install", "blocked", "skipped",
			fmt.Sprintf("Installation of %s skipped due to blocking applications", cItem.Name),
			logging.WithPackage(cItem.Name, cItem.Version),
			logging.WithContext("blocking_apps", runningApps),
			logging.WithError(fmt.Errorf("blocking applications running: %v", runningApps)))

		// Return a special error to indicate this was skipped due to blocking applications
		return fmt.Errorf("skipped install of %s due to blocking applications: %v", cItem.Name, runningApps)
	}

	// Log installation start with comprehensive context
	logging.LogEventEntry("install", "execute", "started",
		fmt.Sprintf("Executing installation of %s", cItem.Name),
		logging.WithPackage(cItem.Name, cItem.Version),
		logging.WithContext("installer_type", cItem.Installer.Type),
		logging.WithContext("checkonly_mode", cfg.CheckOnly),
		logging.WithContext("cache_path", cfg.CachePath))

	// Actually install
	installStart := time.Now()
	installedOutput, installErr := installer.Install(cItem, "install", localFile, cfg.CachePath, cfg.CheckOnly, cfg)
	installDuration := time.Since(installStart)

	if installErr != nil {
		// Log detailed failure information
		logging.LogEventEntry("install", "execute", "failed",
			fmt.Sprintf("Installation of %s failed: %v", cItem.Name, installErr),
			logging.WithPackage(cItem.Name, cItem.Version),
			logging.WithDuration(installDuration),
			logging.WithContext("installer_output", installedOutput),
			logging.WithError(installErr))

		// DO NOT say "Installed item successfully" because it failed
		return installErr
	}

	// If we get here => success
	logger.Info("Install output: %s, output: %s", cItem.Name, installedOutput)
	logger.Info("Installed item successfully: %s, file: %s", cItem.Name, localFile)

	// Log successful installation with detailed metrics
	logging.LogEventEntry("install", "execute", "completed",
		fmt.Sprintf("Successfully installed %s", cItem.Name),
		logging.WithPackage(cItem.Name, cItem.Version),
		logging.WithDuration(installDuration),
		logging.WithContext("installer_output", installedOutput),
		logging.WithContext("installer_path", localFile))

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
		logger.Warning("Failed to read cache directory: %v", err)
		return
	}

	for _, entry := range cacheEntries {
		fullPath := filepath.Join(cachePath, entry.Name())
		fileName := entry.Name()

		// Remove old log files that were incorrectly saved in cache directory
		if strings.HasSuffix(fileName, ".log") &&
			(strings.HasPrefix(fileName, "install_choco_") ||
				strings.HasPrefix(fileName, "upgrade_choco_") ||
				strings.HasPrefix(fileName, "choco_install_") ||
				strings.HasPrefix(fileName, "choco_upgrade_")) {
			err := os.RemoveAll(fullPath)
			if err != nil {
				logger.Warning("Failed to remove old log file %s: %v", fullPath, err)
			} else {
				logger.Debug("Removed old log file from cache: %s", fullPath)
			}
			continue
		}

		// Only remove the file if its base name is in the success set and marked as true (successfully installed).
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

	logger.Info("Bootstrap mode enabled - CimianWatcher service will detect and respond")
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

	logger.Info("Bootstrap mode disabled")
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

// displayLoadingHeader shows the initial loading information with enhanced format
func displayLoadingHeader(targetItems []string, verbosity int) {
	if len(targetItems) > 0 {
		logger.Info("Targeted Item Loading: %s", strings.Join(targetItems, ", "))
	} else {
		logger.Info("Full Manifest Loading")
	}
}

// ManifestNode represents a node in the manifest hierarchy tree
type ManifestNode struct {
	Name      string
	ItemCount int
	Children  map[string]*ManifestNode
	IsLeaf    bool
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

	logger.Info("----------------------------------------------------------------------")
	logger.Info("📁 Manifest Hierarchy (%d manifests with managed items)", len(manifestCounts))
	logger.Info("----------------------------------------------------------------------")

	// Build the tree structure based on manifest path hierarchy
	manifestTree := buildManifestHierarchy(manifestCounts)
	displayManifestHierarchy(manifestTree, "", true)

	logger.Info("")
}

// buildManifestHierarchy creates a tree structure from manifest names and their paths
func buildManifestHierarchy(manifestCounts map[string]int) *ManifestNode {
	root := &ManifestNode{
		Name:     "root",
		Children: make(map[string]*ManifestNode),
	}

	// Define known manifest hierarchy from the logs we've seen
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

	logger.Info("%s%s 📄 %s (%d items)", prefix, connector, node.Name, node.ItemCount)

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

// displayManifestTreeWithPackages shows the manifest hierarchy with package details embedded
func displayManifestTreeWithPackages(manifestItems []manifest.Item, toInstall, toUpdate []catalog.Item, localCatalogMap map[string]catalog.Item) {
	// Group items by their source manifest
	manifestPackages := make(map[string][]manifest.Item)
	manifestCounts := make(map[string]int)

	// Categorize items by manifest and action type
	for _, item := range manifestItems {
		sourceManifest := item.SourceManifest
		if sourceManifest == "" {
			sourceManifest = "Unknown"
		}
		
		// Only include items with action "install" for the main display
		if item.Action == "install" {
			manifestPackages[sourceManifest] = append(manifestPackages[sourceManifest], item)
		}
		
		// Count all items for the hierarchy numbers
		manifestCounts[sourceManifest]++
	}

	logger.Info("----------------------------------------------------------------------")
	logger.Info("📁 MANIFEST HIERARCHY WITH PACKAGES (%d manifests found)", len(manifestCounts))
	logger.Info("----------------------------------------------------------------------")

	// Build and display the tree structure with package details
	manifestTree := buildManifestHierarchy(manifestCounts)
	displayManifestHierarchyWithPackages(manifestTree, "", true, manifestPackages, toInstall, toUpdate, localCatalogMap)

	logger.Info("")
}

// displayManifestHierarchyWithPackages recursively displays the manifest tree with package details
func displayManifestHierarchyWithPackages(node *ManifestNode, prefix string, isLast bool, manifestPackages map[string][]manifest.Item, toInstall, toUpdate []catalog.Item, localCatalogMap map[string]catalog.Item) {
	if node.Name == "root" {
		// Display root children
		names := make([]string, 0, len(node.Children))
		for name := range node.Children {
			names = append(names, name)
		}

		for i, name := range names {
			child := node.Children[name]
			isChildLast := i == len(names)-1
			displayManifestHierarchyWithPackages(child, "", isChildLast, manifestPackages, toInstall, toUpdate, localCatalogMap)
		}
		return
	}

	// Display this node
	connector := "├─"
	if isLast {
		connector = "└─"
	}

	logger.Info("%s%s 📄 %s (%d items)", prefix, connector, node.Name, node.ItemCount)

	// Display packages from this manifest
	if packages, exists := manifestPackages[node.Name]; exists && len(packages) > 0 {
		for i, pkg := range packages {
			status := getPackageStatusDisplay(pkg, toInstall, toUpdate, localCatalogMap)
			version := pkg.Version
			if version == "" {
				version = "Unknown"
			}

			// Determine status icon
			statusIcon := "✅"
			if status == "Pending Install" {
				statusIcon = "⬇️"
			} else if status == "Pending Update" {
				statusIcon = "🔄"
			}

			isLastPackage := i == len(packages)-1
			var pkgConnector string
			var pkgPrefix string
			
			if isLast {
				pkgPrefix = prefix + "   "
			} else {
				pkgPrefix = prefix + "│  "
			}
			
			if isLastPackage && len(node.Children) == 0 {
				pkgConnector = "└─"
			} else {
				pkgConnector = "├─"
			}

			logger.Info("%s%s %s %s (%s)", pkgPrefix, pkgConnector, statusIcon, 
				truncateString(pkg.Name, 30), truncateString(version, 15))
		}
	}

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
			displayManifestHierarchyWithPackages(child, childPrefix, isChildLast, manifestPackages, toInstall, toUpdate, localCatalogMap)
		}
	}
}

// printEnhancedPackageAnalysis provides detailed package information in checkonly mode
func printEnhancedPackageAnalysis(toInstall, toUpdate, toUninstall []catalog.Item, catalogMap map[string]catalog.Item) {
	logger.Info("")
	logger.Info(strings.Repeat("=", 80))
	logger.Info("ENHANCED PACKAGE ANALYSIS")
	logger.Info(strings.Repeat("=", 80))

	// Summary statistics
	totalPackages := len(toInstall) + len(toUpdate) + len(toUninstall)
	logger.Info("📊 Summary: %d total packages (%d new installs, %d updates, %d removals)",
		totalPackages, len(toInstall), len(toUpdate), len(toUninstall))
	logger.Info("")

	// Detailed analysis for each category
	if len(toInstall) > 0 {
		logger.Info("🆕 NEW INSTALLATIONS:")
		logger.Info(strings.Repeat("-", 40))
		for _, item := range toInstall {
			printPackageDetails(item, catalogMap, "INSTALL")
		}
		logger.Info("")
	}

	if len(toUpdate) > 0 {
		logger.Info("🔄 UPDATES:")
		logger.Info(strings.Repeat("-", 40))
		for _, item := range toUpdate {
			printPackageDetails(item, catalogMap, "UPDATE")
		}
		logger.Info("")
	}

	if len(toUninstall) > 0 {
		logger.Info("❌ REMOVALS:")
		logger.Info(strings.Repeat("-", 40))
		for _, item := range toUninstall {
			printPackageDetails(item, catalogMap, "REMOVE")
		}
		logger.Info("")
	}

	logger.Info(strings.Repeat("=", 80))
}

// printPackageDetails prints detailed information about a single package
func printPackageDetails(item catalog.Item, catalogMap map[string]catalog.Item, action string) {
	logger.Info("📦 %s (%s)", item.Name, action)

	// Version information
	if item.Version != "" {
		logger.Info("   📋 Version: %s", item.Version)
	}

	// Check if we have catalog entry for this item
	if catalogEntry, exists := catalogMap[strings.ToLower(item.Name)]; exists {

		// Dependencies
		if len(catalogEntry.Requires) > 0 {
			logger.Info("   🔗 Dependencies: %s", strings.Join(catalogEntry.Requires, ", "))
		}

		// Supported architectures
		if len(catalogEntry.SupportedArch) > 0 {
			logger.Info("   🏗️  Architecture: %s", strings.Join(catalogEntry.SupportedArch, ", "))
		}

		// Display name
		if catalogEntry.DisplayName != "" && catalogEntry.DisplayName != catalogEntry.Name {
			logger.Info("   📝 Display Name: %s", catalogEntry.DisplayName)
		}

		// Blocking applications
		if len(catalogEntry.BlockingApps) > 0 {
			logger.Info("   ⛔ Blocking Apps: %s", strings.Join(catalogEntry.BlockingApps, ", "))
		}
	}

	logger.Info("")
}

// getHostname returns the system hostname
func getHostname() string {
	hostname, err := os.Hostname()
	if err != nil {
		return "unknown"
	}
	return hostname
}

// getCurrentUser returns the current user name
func getCurrentUser() string {
	username := os.Getenv("USERNAME")
	if username == "" {
		username = os.Getenv("USER")
	}
	if username == "" {
		username = "unknown"
	}
	return username
}
