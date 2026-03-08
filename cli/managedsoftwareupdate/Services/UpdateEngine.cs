using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Models;
using Cimian.Core.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;
using SessionPackageInfo = Cimian.Core.Models.SessionPackageInfo;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Main orchestration service for managed software updates
/// Coordinates all the other services to perform updates
/// Migrated from Go cmd/managedsoftwareupdate/main.go
/// </summary>
public class UpdateEngine : IDisposable
{
    // ANSI color codes (matching Go logging colors)
    private const string ColorReset = "\x1b[0m";
    private const string ColorRed = "\x1b[31m";
    private const string ColorGreen = "\x1b[32m";
    private const string ColorYellow = "\x1b[33m";
    private const string ColorBlue = "\x1b[34m";
    private const string ColorCyan = "\x1b[36m";

    private CimianConfig _config;
    private readonly ConfigurationService _configService;
    private ManifestService _manifestService;
    private CatalogService _catalogService;
    private DownloadService _downloadService;
    private InstallerService _installerService;
    private readonly StatusService _statusService;
    private readonly ScriptService _scriptService;
    private StatusReporter? _statusReporter;
    private SessionLogger? _sessionLogger;
    private LoopGuard? _loopGuard;

    private int _verbosity;
    private bool _isBootstrap;
    private bool _checkOnly;
    private bool _installOnly;
    private bool _auto;
    private bool _showStatus;
    
    // Store for managed items tracking (for status table)
    private List<ManifestItem> _allManifestItems = new();
    private Dictionary<string, CatalogItem> _catalogMap = new();

    public UpdateEngine(CimianConfig config)
    {
        _config = config;
        _configService = new ConfigurationService();
        _manifestService = new ManifestService(config);
        _catalogService = new CatalogService(config);
        _downloadService = new DownloadService(config);
        _installerService = new InstallerService(config);
        _statusService = new StatusService();
        _scriptService = new ScriptService();

        // Enable ANSI color support on Windows console
        EnableAnsiColors();
    }

    /// <summary>
    /// Computes a LoopGuard catalog fingerprint from a CatalogItem's install-behavior fields.
    /// If ANY of these fields change in the pkgsinfo, the fingerprint changes and LoopGuard
    /// auto-clears suppression — the admin may have fixed the root cause of the loop.
    ///
    /// Fields included: version, installcheck_script, installs array, check info,
    /// installer hash/url/type, install_script, postinstall_script, preinstall_script.
    /// </summary>
    private static string ComputeCatalogFingerprint(CatalogItem item)
    {
        var sb = new System.Text.StringBuilder(512);
        sb.Append(item.Version);
        sb.Append('|');
        sb.Append(item.InstallcheckScript ?? "");
        sb.Append('|');
        sb.Append(item.InstallScript ?? "");
        sb.Append('|');
        sb.Append(item.PostinstallScript ?? "");
        sb.Append('|');
        sb.Append(item.PreinstallScript ?? "");
        sb.Append('|');
        sb.Append(item.Installer?.Hash ?? "");
        sb.Append('|');
        sb.Append(item.Installer?.Location ?? "");
        sb.Append('|');
        sb.Append(item.Installer?.Type ?? "");
        sb.Append('|');
        // Serialize installs array — covers path, md5, version, product_code changes
        if (item.Installs?.Count > 0)
        {
            foreach (var inst in item.Installs)
            {
                sb.Append(inst.Type);
                sb.Append(':');
                sb.Append(inst.Path ?? "");
                sb.Append(':');
                sb.Append(inst.Md5Checksum ?? "");
                sb.Append(':');
                sb.Append(inst.Version ?? "");
                sb.Append(':');
                sb.Append(inst.ProductCode ?? "");
                sb.Append(';');
            }
        }
        sb.Append('|');
        // Serialize check info — covers registry name/version/path and file/script checks
        sb.Append(item.Check?.Registry?.Name ?? "");
        sb.Append(':');
        sb.Append(item.Check?.Registry?.Version ?? "");
        sb.Append(':');
        sb.Append(item.Check?.Registry?.Path ?? "");
        sb.Append(':');
        sb.Append(item.Check?.File?.Path ?? "");
        sb.Append(':');
        sb.Append(item.Check?.File?.Version ?? "");
        sb.Append(':');
        sb.Append(item.Check?.File?.Hash ?? "");
        sb.Append(':');
        sb.Append(item.Check?.Script ?? "");

        return LoopGuard.ComputeFingerprint(sb.ToString());
    }

    /// <summary>
    /// Enable ANSI escape codes for colored output on Windows console
    /// </summary>
    private static void EnableAnsiColors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            // Enable virtual terminal processing for stdout
            var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
            if (handle != IntPtr.Zero && GetConsoleMode(handle, out uint mode))
            {
                mode |= 0x0004; // ENABLE_VIRTUAL_TERMINAL_PROCESSING
                SetConsoleMode(handle, mode);
            }

            // Enable virtual terminal processing for stderr
            handle = GetStdHandle(-12); // STD_ERROR_HANDLE
            if (handle != IntPtr.Zero && GetConsoleMode(handle, out mode))
            {
                mode |= 0x0004;
                SetConsoleMode(handle, mode);
            }
        }
        catch
        {
            // Ignore errors - colors just won't work
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>
    /// Runs the update process with the specified options
    /// </summary>
    public async Task<int> RunAsync(
        bool checkOnly = false,
        bool installOnly = false,
        bool auto = false,
        bool bootstrap = false,
        int verbosity = 0,
        string? manifestTarget = null,
        string? localManifest = null,
        bool skipPreflight = false,
        bool skipPostflight = false,
        bool showStatus = false,
        IEnumerable<string>? itemFilter = null,
        CancellationToken cancellationToken = default)
    {
        // Create item filter service (Go parity: pkg/filter)
        var itemFilterService = new ItemFilterService(itemFilter);
        
        _checkOnly = checkOnly;
        _installOnly = installOnly;
        _auto = auto;
        _isBootstrap = bootstrap || StatusService.IsBootstrapMode();
        _verbosity = verbosity;
        _showStatus = showStatus;

        // Initialize loop guard for install loop prevention
        _loopGuard = new LoopGuard(_isBootstrap);

        // Track session duration for run.log summary
        var sessionStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Set global verbosity for ConsoleLogger
        ConsoleLogger.Verbosity = verbosity;

        // Initialize status reporter if --show-status is set
        if (_showStatus)
        {
            _statusReporter = new StatusReporter(verbosity);
            _statusReporter.TryConnect();
        }

        // Initialize session logger for structured logging (Go parity: pkg/logging)
        // This creates timestamped directories in C:\ProgramData\ManagedInstalls\logs
        // and writes to reports directory for external monitoring tools
        var runType = _isBootstrap ? "bootstrap" : 
                      _auto ? "auto" : 
                      _checkOnly ? "checkonly" : 
                      _installOnly ? "installonly" : "manual";
        
        _sessionLogger = new SessionLogger();
        var sessionId = _sessionLogger.StartSession(runType, new Dictionary<string, object>
        {
            ["verbosity"] = verbosity,
            ["bootstrap"] = _isBootstrap,
            ["check_only"] = checkOnly,
            ["install_only"] = installOnly,
            ["auto"] = auto,
            ["show_status"] = showStatus,
            ["skip_preflight"] = skipPreflight,
            ["skip_postflight"] = skipPostflight,
            ["manifest_target"] = manifestTarget ?? "",
            ["local_manifest"] = localManifest ?? "",
            ["client_identifier"] = _config.ClientIdentifier
        });
        
        // Bridge ConsoleLogger → SessionLogger so all output goes to log files
        ConsoleLogger.SetSessionLogger(_sessionLogger);
        
        // Pass session logger to services for structured logging
        _installerService.SetSessionLogger(_sessionLogger);
        
        _sessionLogger.Log("INFO", $"Session started: {sessionId}");
        _sessionLogger.Log("INFO", $"Run type: {runType}");

        try
        {
            // Report initial status
            ReportStatus("Checking for updates...");
            ReportDetail("Initializing...");

            // Go parity: Always print header to run.log; console display is gated by verbosity
            PrintVerboseHeader();

            // Check admin privileges
            if (!StatusService.IsAdministrator())
            {
                ReportError("Administrative access required");
                ConsoleLogger.Error("Administrative access required.");
                return 1;
            }

            // Ensure directories exist
            _configService.EnsureDirectoriesExist(_config);

            // Clean pre-run directories
            CleanManifestsAndCatalogsPreRun();

            // Run preflight unless skipped
            if (!skipPreflight && !_config.NoPreflight)
            {
                LogInfo("----------------------------------------------------------------------");
                LogInfo("PREFLIGHT EXECUTION");
                LogInfo("----------------------------------------------------------------------");
                ReportDetail("Running preflight script...");
                var (preflightSuccess, preflightOutput) = await _scriptService.RunPreflightAsync(cancellationToken);
                
                // Note: ExecuteScriptFileAsync already streams output to console in real-time.
                // Do NOT print preflightOutput again here or the output appears twice.
                
                if (!preflightSuccess)
                {
                    HandlePreflightFailure(preflightOutput);
                }

                // Reload configuration after preflight - preflight script may have updated config
                // preflight sets SoftwareRepoURL, ClientIdentifier, etc.
                _config = _configService.LoadConfig();
                
                // Apply verbosity settings again after reload
                if (verbosity >= 1)
                {
                    _config.Verbose = true;
                    _config.LogLevel = "INFO";
                }
                if (verbosity >= 3)
                {
                    _config.Debug = true;
                    _config.LogLevel = "DEBUG";
                }
                
                // Recreate services with updated config
                _manifestService = new ManifestService(_config);
                _catalogService = new CatalogService(_config);
                _downloadService = new DownloadService(_config);
                _installerService = new InstallerService(_config);
                _installerService.SetSessionLogger(_sessionLogger);
            }

            // Go parity: Always log system configuration to run.log
            PrintSystemConfiguration();
            
            LogInfo("----------------------------------------------------------------------");
            LogInfo("MANIFEST RETRIEVAL");
            LogInfo("----------------------------------------------------------------------");
            ReportDetail("Retrieving manifests...");
            LogInfo("Retrieving manifests...");
            List<ManifestItem> manifestItems;

            if (!string.IsNullOrEmpty(localManifest))
            {
                manifestItems = _manifestService.LoadLocalOnlyManifest(localManifest);
            }
            else if (!string.IsNullOrEmpty(manifestTarget))
            {
                manifestItems = await _manifestService.LoadSpecificManifestAsync(manifestTarget);
            }
            else
            {
                manifestItems = await _manifestService.GetManifestItemsAsync();
            }

            // Go parity: pkg/status.DeduplicateManifestItems - deduplicate before processing
            var rawCount = manifestItems.Count;
            manifestItems = _manifestService.DeduplicateItems(manifestItems);
            if (manifestItems.Count < rawCount)
            {
                LogInfo($"Deduplicated manifest items: {rawCount} → {manifestItems.Count}");
            }

            LogInfo($"Retrieved {manifestItems.Count} manifest items");
            _allManifestItems = manifestItems;

            // Download and load catalogs
            LogInfo("----------------------------------------------------------------------");
            LogInfo("CATALOG LOADING");
            LogInfo("----------------------------------------------------------------------");
            ReportDetail("Loading catalogs...");
            LogInfo("Loading catalogs...");
            var catalogMap = await _catalogService.LoadCatalogsAsync();
            _catalogMap = catalogMap;
            LogInfo($"Loaded {catalogMap.Count} catalog items");

            // Validate cache
            ReportDetail("Validating cache...");
            _downloadService.ValidateAndCleanCache();

            // Identify actions needed
            LogInfo("----------------------------------------------------------------------");
            LogInfo("STATUS CHECKING");
            LogInfo("----------------------------------------------------------------------");
            var (toInstall, toUpdate, toUninstall) = IdentifyActions(manifestItems, catalogMap);

            // Apply --item filter if specified (Go parity: pkg/filter)
            if (itemFilterService.HasFilter)
            {
                ConsoleLogger.Info($"Applying --item filter: [{string.Join(", ", itemFilterService.Items)}]");
                toInstall = itemFilterService.FilterCatalogItems(toInstall);
                toUpdate = itemFilterService.FilterCatalogItems(toUpdate);
                toUninstall = itemFilterService.FilterCatalogItems(toUninstall);
                
                // Log if everything was filtered out
                if (toInstall.Count == 0 && toUpdate.Count == 0 && toUninstall.Count == 0)
                {
                    ConsoleLogger.Warn("No pending actions match the --item filter");
                }
            }

            // Resolve dependencies and update_for items (Go parity: process.go dependency resolution)
            // This adds items like ReportMatePrefs (update_for ReportMate) to the manifest items
            ResolveDependenciesForCheckOnly(manifestItems, catalogMap, toUpdate);

            // Print hierarchy and tables in checkonly mode (matches Go behavior - always shows this)
            if (_checkOnly)
            {
                // Go parity: When --item filter is active, display only filtered items
                // (Go filters manifestItems early via itemFilter.Apply)
                var displayItems = itemFilterService.HasFilter 
                    ? itemFilterService.FilterManifestItems(manifestItems) 
                    : manifestItems;
                    
                PrintManifestHierarchy(displayItems);
                PrintManagedInstallsTable(displayItems, toInstall, toUpdate, catalogMap);
                PrintManagedUpdatesTable(displayItems, toUpdate, catalogMap);
                PrintManagedUninstallsTable(displayItems, toUninstall, catalogMap);
            }

            // Print summary
            PrintActionSummary(manifestItems, toInstall, toUpdate, toUninstall);

            // Exit if check-only mode
            if (_checkOnly)
            {
                sessionStopwatch.Stop();
                LogInfo("----------------------------------------------------------------------");
                LogInfo("SESSION COMPLETE");
                LogInfo($"Total duration: {sessionStopwatch.Elapsed.TotalSeconds:F1}s");
                LogInfo("----------------------------------------------------------------------");
                LogInfo("Check-only mode - no actions performed");
                ReportStatus("Check complete");
                ReportPercent(100);
                
                // Collect items data for items.json report (Go parity: SetCurrentSessionPackagesInfo)
                CollectSessionItems(manifestItems, toInstall, toUpdate, toUninstall, catalogMap);
                
                // Write InstallInfo.yaml for MSC GUI
                WriteInstallInfo(manifestItems, toInstall, toUpdate, toUninstall, catalogMap);
                
                // End session for check-only
                EndSessionWithSummary("completed", toInstall.Count, toUpdate.Count, toUninstall.Count, 0, 0, manifestItems);
                return 0;
            }

            // Exit if auto mode and user is active
            if (_auto && StatusService.IsUserActive())
            {
                LogInfo($"User is active (idle: {StatusService.GetIdleSeconds()}s). Skipping automatic updates.");
                _sessionLogger?.Log("INFO", $"User is active - skipping automatic updates");
                ReportStatus("Skipped - user active");
                
                EndSessionWithSummary("skipped", 0, 0, 0, 0, 0, manifestItems);
                return 0;
            }

            // Filter out items outside their install_window (applies to installs, updates, and uninstalls)
            var deferredItems = new List<CatalogItem>();
            var now = DateTime.Now;
            foreach (var list in new[] { toInstall, toUpdate, toUninstall })
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var item = list[i];
                    if (item.InstallWindow != null && !item.InstallWindow.IsWithinWindow(now))
                    {
                        LogInfo($"Deferred: {item.Name} v{item.Version} (outside install window {item.InstallWindow})");
                        _sessionLogger?.Log("INFO", $"Deferred {item.Name} v{item.Version}: outside install window {item.InstallWindow}");
                        _sessionLogger?.LogStatusCheck(
                            item.Name, item.Version, "deferred",
                            $"Outside install window {item.InstallWindow}",
                            Cimian.Core.Models.StatusReasonCode.DeferredInstallWindow,
                            Cimian.Core.Models.DetectionMethod.None, null, false);
                        deferredItems.Add(item);
                        list.RemoveAt(i);
                    }
                }
            }
            if (deferredItems.Count > 0)
            {
                LogInfo($"{deferredItems.Count} item(s) deferred due to install_window restrictions");
            }

            // Perform installations
            var installSuccess = true;
            var successCount = 0;
            var failCount = 0;
            if (toInstall.Count > 0 || toUpdate.Count > 0)
            {
                var allToInstall = toInstall.Concat(toUpdate).ToList();
                
                // Go parity: Separate Cimian self-update packages from regular packages
                // Self-updates must be scheduled for next service restart, not installed directly
                // (because we can't update the running binary)
                var selfUpdateItems = new List<CatalogItem>();
                var regularItems = new List<CatalogItem>();
                
                foreach (var item in allToInstall)
                {
                    if (StatusService.IsCimianPackage(item))
                    {
                        selfUpdateItems.Add(item);
                        LogInfo($"Detected Cimian self-update package: {item.Name} v{item.Version}");
                        _sessionLogger?.Log("INFO", $"Detected Cimian self-update package: {item.Name} v{item.Version}");
                    }
                    else
                    {
                        regularItems.Add(item);
                    }
                }
                
                // Handle self-updates by scheduling them for next restart
                if (selfUpdateItems.Count > 0)
                {
                    LogInfo("═══════════════════════════════════════════════════════════════════════");
                    LogInfo("CIMIAN SELF-UPDATE DETECTED");
                    LogInfo($"   {selfUpdateItems.Count} self-update package(s) will be scheduled for next restart");
                    LogInfo("═══════════════════════════════════════════════════════════════════════");
                    _sessionLogger?.Log("INFO", $"Scheduling {selfUpdateItems.Count} self-update package(s) for next restart");
                    
                    foreach (var item in selfUpdateItems)
                    {
                        // Download the self-update package first
                        var downloads = await _downloadService.DownloadItemsAsync(new List<CatalogItem> { item }, null, cancellationToken);
                        downloads.TryGetValue(item.Name, out var localFile);
                        
                        if (string.IsNullOrEmpty(localFile))
                        {
                            ConsoleLogger.Error($"Failed to download self-update package: {item.Name}");
                            _sessionLogger?.Log("ERROR", $"Failed to download self-update package: {item.Name}");
                            continue;
                        }
                        
                        // Schedule the self-update for next service restart
                        var scheduled = SelfUpdateService.ScheduleSelfUpdate(
                            item.Name, 
                            item.Version, 
                            item.Installer.Type ?? "pkg", 
                            localFile);
                        
                        if (scheduled)
                        {
                            LogSuccess($"Self-update scheduled: {item.Name} v{item.Version}");
                            _sessionLogger?.Log("INFO", $"Self-update scheduled successfully: {item.Name} v{item.Version}");
                        }
                        else
                        {
                            ConsoleLogger.Error($"Failed to schedule self-update: {item.Name}");
                            _sessionLogger?.Log("ERROR", $"Failed to schedule self-update: {item.Name}");
                        }
                    }
                    
                    // Update allToInstall to only include regular items
                    allToInstall = regularItems;
                }
                
                // Install regular items (non-Cimian packages)
                if (allToInstall.Count > 0)
                {
                    ReportStatus("Installing updates...");
                    _sessionLogger?.Log("INFO", $"Installing {allToInstall.Count} items...");
                    installSuccess = await PerformInstallationsAsync(allToInstall, cancellationToken);
                    
                    // Count successes/failures based on result
                    successCount = installSuccess ? allToInstall.Count : 0;
                    failCount = installSuccess ? 0 : allToInstall.Count;
                }
                else if (selfUpdateItems.Count > 0)
                {
                    // Only self-updates were pending - they're scheduled, count as success
                    LogInfo("No regular packages to install. Self-updates will apply on next restart.");
                    _sessionLogger?.Log("INFO", "No regular packages to install. Self-updates will apply on next restart.");
                    successCount = selfUpdateItems.Count;
                }
            }

            // Perform uninstalls
            var uninstallSuccess = true;
            if (toUninstall.Count > 0)
            {
                _sessionLogger?.Log("INFO", $"Removing {toUninstall.Count} items...");
                uninstallSuccess = await PerformUninstallsAsync(toUninstall, cancellationToken);
            }

            // Run postflight unless skipped
            if (!skipPostflight && !_config.NoPostflight)
            {
                LogInfo("----------------------------------------------------------------------");
                LogInfo("POSTFLIGHT EXECUTION");
                LogInfo("----------------------------------------------------------------------");
                _sessionLogger?.Log("INFO", "Running postflight script...");
                var (postflightSuccess, postflightOutput) = await _scriptService.RunPostflightAsync(cancellationToken);
                if (!postflightSuccess)
                {
                    ConsoleLogger.Warn($"Postflight script failed: {postflightOutput}");
                    _sessionLogger?.Log("WARN", $"Postflight script failed: {postflightOutput}");
                }
            }

            // Clear bootstrap mode if successful
            if (_isBootstrap && installSuccess && uninstallSuccess)
            {
                StatusService.DisableBootstrapMode();
            }

            // Print final status
            sessionStopwatch.Stop();
            LogInfo("----------------------------------------------------------------------");
            LogInfo("SESSION COMPLETE");
            LogInfo($"Total duration: {sessionStopwatch.Elapsed.TotalSeconds:F1}s");
            LogInfo("----------------------------------------------------------------------");
            if (installSuccess && uninstallSuccess)
            {
                LogSuccess("All operations completed successfully");
                _sessionLogger?.Log("INFO", "All operations completed successfully");
                ReportStatus("Complete");
                ReportPercent(100);
                
                // Collect items data for items.json report
                CollectSessionItems(manifestItems, toInstall, toUpdate, toUninstall, catalogMap);
                
                // Write InstallInfo.yaml for MSC GUI (post-install: actions completed)
                WriteInstallInfo(manifestItems, toInstall, toUpdate, toUninstall, catalogMap);
                
                EndSessionWithSummary("completed", toInstall.Count, toUpdate.Count, toUninstall.Count, 
                    toInstall.Count + toUpdate.Count + toUninstall.Count, 0, manifestItems);
                return 0;
            }
            else
            {
                ConsoleLogger.Warn("Some operations failed");
                _sessionLogger?.Log("WARN", "Some operations failed");
                ReportError("Some operations failed");
                
                // Collect items data for items.json report
                CollectSessionItems(manifestItems, toInstall, toUpdate, toUninstall, catalogMap);
                
                // Write InstallInfo.yaml for MSC GUI (post-install: reflects final state)
                WriteInstallInfo(manifestItems, toInstall, toUpdate, toUninstall, catalogMap);
                
                EndSessionWithSummary("partial_failure", toInstall.Count, toUpdate.Count, toUninstall.Count,
                    successCount, failCount, manifestItems);
                return 1;
            }
        }
        catch (Exception ex)
        {
            ReportError($"Update failed: {ex.Message}");
            ConsoleLogger.Error($"Update failed: {ex.Message}");
            _sessionLogger?.Log("ERROR", $"Update failed: {ex.Message}");
            if (_verbosity >= 2)
            {
                ConsoleLogger.Debug(ex.StackTrace ?? "");
            }
            
            // End session with failure
            _sessionLogger?.EndSession("failed", new SessionLogSummary
            {
                TotalActions = 0,
                Failures = 1,
                PackagesHandled = new List<string>()
            });
            return 1;
        }
        finally
        {
            // Detach ConsoleLogger from SessionLogger before disposing
            ConsoleLogger.SetSessionLogger(null);
            // Always send quit and dispose resources
            _statusReporter?.Dispose();
            _sessionLogger?.Dispose();
        }
    }

    private (List<CatalogItem> ToInstall, List<CatalogItem> ToUpdate, List<CatalogItem> ToUninstall) 
        IdentifyActions(List<ManifestItem> manifestItems, Dictionary<string, CatalogItem> catalogMap)
    {
        var toInstall = new List<CatalogItem>();
        var toUpdate = new List<CatalogItem>();
        var toUninstall = new List<CatalogItem>();

        var sysArch = StatusService.GetSystemArchitecture();

        // Log manifest and catalog stats for debugging
        ConsoleLogger.Detail($"    IdentifyActions: {manifestItems.Count} manifest items, {catalogMap.Count} catalog items");

        foreach (var item in manifestItems)
        {
            if (string.IsNullOrEmpty(item.Name)) continue;

            var key = item.Name.ToLowerInvariant();
            
            if (!catalogMap.TryGetValue(key, out var catalogItem))
            {
                // Go behavior: items not in catalog with action=install are new installs
                // But we need the catalog item for installation - log this discrepancy
                ConsoleLogger.Detail($"    Item not in catalog: {item.Name} (action: {item.Action})");
                continue;
            }

            // Check architecture compatibility
            if (!CatalogService.SupportsArchitecture(catalogItem, sysArch))
            {
                ConsoleLogger.Info($"Skipping {item.Name}: architecture mismatch (system: {sysArch}, item version: {catalogItem.Version}, item arch: [{string.Join(",", catalogItem.SupportedArch)}])");
                continue;
            }

            switch (item.Action.ToLowerInvariant())
            {
                case "install":
                case "update":
                    // Go treats both install and update actions the same - calls CheckStatus
                    var status = _statusService.CheckStatus(catalogItem, item.Action.ToLowerInvariant(), _config.CachePath);
                    ConsoleLogger.Detail($"    CheckStatus for {item.Name}: NeedsAction={status.NeedsAction}, IsUpdate={status.IsUpdate}, Status={status.Status}, Reason={status.Reason}, ReasonCode={status.ReasonCode}");
                    
                    // Log status check event with full reason tracking
                    _sessionLogger?.LogStatusCheck(
                        catalogItem.Name,
                        catalogItem.Version,
                        status.Status,
                        status.Reason,
                        status.ReasonCode,
                        status.DetectionMethod,
                        status.InstalledVersion,
                        status.NeedsAction);
                    
                    if (status.NeedsAction)
                    {
                        // Check LoopGuard before adding to install list
                        if (_loopGuard != null)
                        {
                            var fingerprint = ComputeCatalogFingerprint(catalogItem);
                            var (suppress, loopReason) = _loopGuard.ShouldSuppress(catalogItem.Name, catalogItem.Version, fingerprint);
                            if (suppress)
                            {
                                ConsoleLogger.Warn(loopReason);
                                _sessionLogger?.Log("WARN", loopReason);
                                _sessionLogger?.LogStatusCheck(
                                    catalogItem.Name,
                                    catalogItem.Version,
                                    "suppressed",
                                    loopReason,
                                    Cimian.Core.Models.StatusReasonCode.LoopSuppressed,
                                    Cimian.Core.Models.DetectionMethod.None,
                                    status.InstalledVersion,
                                    false);
                                break; // Skip this item
                            }
                        }

                        if (status.IsUpdate)
                        {
                            toUpdate.Add(catalogItem);
                            ConsoleLogger.Info($"    -> Adding to toUpdate");
                        }
                        else
                        {
                            toInstall.Add(catalogItem);
                            ConsoleLogger.Info($"    -> Adding to toInstall");
                        }
                    }
                    break;

                case "uninstall":
                    if (catalogItem.IsUninstallable())
                    {
                        toUninstall.Add(catalogItem);
                    }
                    break;

                case "profile":
                case "app":
                    // External MDM management - skip
                    ConsoleLogger.Detail($"    Skipping external item: {item.Name} (action: {item.Action})");
                    break;
            }
        }

        return (toInstall, toUpdate, toUninstall);
    }

    /// <summary>
    /// Resolves dependencies for checkonly mode to show what will be installed.
    /// This finds update_for items that would be installed when the target package is installed.
    /// Go parity: This happens during ProcessInstallWithDependencies in process.go
    /// Example: ReportMatePrefs has update_for: [ReportMate], so when ReportMate is in the install list,
    /// ReportMatePrefs should also appear in the list with source "dependency".
    /// </summary>
    private void ResolveDependenciesForCheckOnly(
        List<ManifestItem> manifestItems, 
        Dictionary<string, CatalogItem> catalogMap,
        List<CatalogItem> itemsToProcess)
    {
        LogInfo("Resolving dependencies (requires and update_for)...");
        
        // Get list of item names already in the manifest
        var existingNames = manifestItems.Select(m => m.Name.ToLowerInvariant()).ToHashSet();
        
        // Get names of items that will be installed/updated
        var installListNames = manifestItems
            .Where(m => m.Action?.ToLowerInvariant() == "install" || m.Action?.ToLowerInvariant() == "update")
            .Select(m => m.Name)
            .ToList();
        
        LogDetail($"    Items to check for update_for: {string.Join(", ", installListNames.Take(10))}...");
        
        // Look for update_for items for each item in the install list
        foreach (var itemName in installListNames)
        {
            var updateList = CatalogService.LookForUpdates(itemName, catalogMap);
            
            if (updateList.Count > 0)
            {
                LogDetail($"    Found update_for items for {itemName}: {string.Join(", ", updateList)}");
            }
            
            foreach (var updateItemName in updateList)
            {
                var updateKey = updateItemName.ToLowerInvariant();
                
                // Skip if already in manifest
                if (existingNames.Contains(updateKey))
                {
                    LogDetail($"    Skipping {updateItemName} - already in manifest");
                    continue;
                }
                
                // Get the update item from catalog
                if (!catalogMap.TryGetValue(updateKey, out var updateItem))
                {
                    LogDetail($"    Skipping {updateItemName} - not found in catalog");
                    continue;
                }
                
                // Check if the update item needs action
                var status = _statusService.CheckStatus(updateItem, "install", _config.CachePath);
                
                LogInfo($"Adding update_for item to install list update: {updateItemName} updateFor: {itemName}");
                
                // Add to manifest items with source "dependency"
                manifestItems.Add(new ManifestItem
                {
                    Name = updateItemName,
                    Action = "install",
                    SourceManifest = "dependency"
                });
                existingNames.Add(updateKey);
                
                // If it needs action, add to the toUpdate list
                if (status.NeedsAction && !itemsToProcess.Any(i => i.Name.Equals(updateItemName, StringComparison.OrdinalIgnoreCase)))
                {
                    itemsToProcess.Add(updateItem);
                }
            }
        }
        
        var depCount = manifestItems.Count(m => m.SourceManifest == "dependency");
        // Go parity: Count only managed items (exclude profile/app which are MDM-managed)
        var managedCount = manifestItems.Count(m => 
        {
            var action = m.Action?.ToLowerInvariant();
            return action != "profile" && action != "app";
        });
        if (depCount > 0)
        {
            LogInfo($"Dependency resolution complete: {depCount} update_for items added");
            LogInfo($"Added dependencies: {string.Join(", ", manifestItems.Where(m => m.SourceManifest == "dependency").Select(m => m.Name))}");
        }
        LogInfo($"Managed items: {managedCount} (excludes {manifestItems.Count - managedCount} MDM profiles/apps)");
    }

    private void PrintActionSummary(
        List<ManifestItem> manifestItems,
        List<CatalogItem> toInstall,
        List<CatalogItem> toUpdate,
        List<CatalogItem> toUninstall)
    {
        var total = toInstall.Count + toUpdate.Count + toUninstall.Count;

        if (total == 0)
        {
            Log();
            Log("All software is up to date");
            Log();
            return;
        }

        Log();
        Log("SUMMARY");
        // Go parity: Exclude profile/app actions - these are MDM-managed externally (managed_profiles, managed_apps)
        var managedItems = manifestItems.Where(m => 
        {
            var action = m.Action?.ToLowerInvariant();
            return action != "profile" && action != "app";
        }).ToList();
        
        var installCount = managedItems.Count(m => m.Action?.ToLowerInvariant() == "install");
        var updateCount = managedItems.Count(m => m.Action?.ToLowerInvariant() == "update");
        var uninstallCount = managedItems.Count(m => m.Action?.ToLowerInvariant() == "uninstall");
        Log($"   Total managed items: {managedItems.Count} ({installCount} installs, {updateCount} updates, {uninstallCount} removals)");
        Log($"   Pending actions: {total} ({toInstall.Count} installs, {toUpdate.Count} updates, {toUninstall.Count} removals)");
        Log();
    }

    private async Task<bool> PerformInstallationsAsync(
        List<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        LogInfo($"Installing/updating {items.Count} items with dependency processing...");

        var successCount = 0;
        var failCount = 0;
        var totalItems = items.Count;

        // Download all items first (including potential dependencies)
        // Note: Dependencies not in this list will be downloaded on-demand during processing
        LogInfo("----------------------------------------------------------------------");
        LogInfo("DOWNLOADING PACKAGES");
        LogInfo("----------------------------------------------------------------------");
        ReportStatus("Downloading...");
        ReportDetail($"Downloading {items.Count} items...");
        LogInfo($"Downloading {items.Count} items...");
        var downloadedPaths = await _downloadService.DownloadItemsAsync(items, null, cancellationToken);

        // Track installed and scheduled items for dependency checking
        // Start with items that are already confirmed installed (from status checks)
        var installedItems = new List<string>();
        var scheduledItems = items.Select(i => i.Name).ToList();
        var itemIndex = 0;

        // Process each item with full dependency handling
        // This is Go parity: ProcessInstallWithDependencies from process.go
        LogInfo("----------------------------------------------------------------------");
        LogInfo("INSTALLING PACKAGES");
        LogInfo("----------------------------------------------------------------------");
        ReportStatus("Installing...");
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested) break;

            itemIndex++;
            var progressPercent = (itemIndex * 100) / totalItems;
            ReportDetail($"Installing {item.Name} ({itemIndex}/{totalItems})");
            ReportPercent(progressPercent);

            // Skip if already processed (may have been installed as a dependency)
            if (installedItems.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                LogDetail($"Skipping {item.Name}: already installed as dependency");
                successCount++;
                continue;
            }

            var success = await ProcessInstallWithDependenciesAsync(
                item.Name,
                installedItems,
                scheduledItems,
                downloadedPaths,
                cancellationToken);

            if (success)
            {
                successCount++;
            }
            else
            {
                failCount++;
            }
        }

        LogInfo($"Installation summary: {successCount} succeeded, {failCount} failed");
        return failCount == 0;
    }

    #region Dependency-Aware Installation (Go parity: pkg/process/process.go)

    /// <summary>
    /// Process installation of an item with full dependency handling.
    /// This handles: requires dependencies, legacy dependencies, and update_for items.
    /// Migrated from Go: ProcessInstallWithDependencies() - process.go lines 490-610
    /// </summary>
    /// <param name="itemName">The name of the item to install</param>
    /// <param name="installedItems">List of currently installed items (for dependency checking)</param>
    /// <param name="scheduledItems">List of items scheduled for installation (mutable, grows as dependencies are processed)</param>
    /// <param name="downloadedPaths">Dictionary of already downloaded file paths</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if installation succeeded (including all dependencies), false otherwise</returns>
    private async Task<bool> ProcessInstallWithDependenciesAsync(
        string itemName,
        List<string> installedItems,
        List<string> scheduledItems,
        Dictionary<string, string> downloadedPaths,
        CancellationToken cancellationToken)
    {
        LogDetail($"ProcessInstallWithDependencies: {itemName}");

        // Get the item from catalog
        var key = itemName.ToLowerInvariant();
        if (!_catalogMap.TryGetValue(key, out var item))
        {
            ConsoleLogger.Error($"Item not found in catalog: {itemName}");
            return false;
        }

        var systemArch = StatusService.GetSystemArchitecture();

        // Check architecture support
        if (!CatalogService.SupportsArchitecture(item, systemArch))
        {
            LogInfo($"Skipping {item.Name}: architecture mismatch (system: {systemArch})");
            return true; // Not an error, just skipped
        }

        // Check and install requires dependencies first
        if (item.Requires.Count > 0)
        {
            LogDetail($"Processing requires dependencies for {itemName}: {string.Join(", ", item.Requires)}");
            var missingDeps = CatalogService.CheckDependencies(item, installedItems, scheduledItems);

            if (missingDeps.Count > 0)
            {
                LogInfo($"Found {missingDeps.Count} missing dependencies for {itemName}: {string.Join(", ", missingDeps)}");
            }

            foreach (var dep in missingDeps)
            {
                LogInfo($"Installing required dependency: {dep} (for {itemName})");

                // Parse dependency name (may have version)
                var (depName, _) = CatalogService.SplitNameAndVersion(dep);

                // Check if dependency exists in catalog
                var depKey = depName.ToLowerInvariant();
                if (!_catalogMap.TryGetValue(depKey, out var depItem))
                {
                    ConsoleLogger.Error($"Required dependency not found in catalog: {depName} (for {itemName})");
                    return false;
                }

                // Download the dependency if not already downloaded
                if (!downloadedPaths.ContainsKey(depItem.Name))
                {
                    var depDownloads = await _downloadService.DownloadItemsAsync(new List<CatalogItem> { depItem }, null, cancellationToken);
                    foreach (var kvp in depDownloads)
                    {
                        downloadedPaths[kvp.Key] = kvp.Value;
                    }
                }

                // Recursively process the dependency
                var newScheduled = new List<string>(scheduledItems) { dep };
                if (!await ProcessInstallWithDependenciesAsync(dep, installedItems, newScheduled, downloadedPaths, cancellationToken))
                {
                    ConsoleLogger.Error($"Failed to install required dependency: {dep}");
                    return false;
                }

                // Add to scheduled items for future dependency checks
                if (!scheduledItems.Contains(dep, StringComparer.OrdinalIgnoreCase))
                {
                    scheduledItems.Add(dep);
                }

                LogDetail($"Successfully processed dependency: {dep} (for {itemName})");
            }
        }

        // Install the main item
        LogInfo($"Installing: {item.Name} v{item.Version}");

        // Check for blocking apps
        if (_installerService.CheckBlockingApps(item, out var runningApps))
        {
            var blockingAppsStr = string.Join(", ", runningApps);
            ConsoleLogger.Warn($"Skipping {item.Name}: blocking apps running: {blockingAppsStr}");
            
            // Log with status reason tracking
            _sessionLogger?.LogInstallWithReason(
                item.Name,
                item.Version,
                "install",
                "blocked",
                $"Waiting for {blockingAppsStr} to close",
                $"Waiting for {blockingAppsStr} to close",
                Cimian.Core.Models.StatusReasonCode.BlockingApps,
                Cimian.Core.Models.DetectionMethod.None);
            
            return false;
        }

        // Get downloaded file path (may be null for script-only items)
        downloadedPaths.TryGetValue(item.Name, out var localFile);

        var (success, output) = await _installerService.InstallAsync(item, localFile ?? "", cancellationToken);

        if (success)
        {
            LogSuccess($"Installed: {item.Name} v{item.Version}");
            
            // Log structured event for external monitoring with reason tracking
            _sessionLogger?.LogInstallWithReason(
                item.Name,
                item.Version,
                "install",
                "completed",
                $"Successfully installed {item.Name} v{item.Version}",
                $"Installation completed successfully",
                Cimian.Core.Models.StatusReasonCode.InstallCompleted,
                Cimian.Core.Models.DetectionMethod.None,
                item.Version);

            // Record successful install for loop guard tracking
            _loopGuard?.RecordAttempt(item.Name, item.Version, success: true, ComputeCatalogFingerprint(item));

            // Add to installed items
            if (!installedItems.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                installedItems.Add(item.Name);
            }
        }
        else
        {
            ConsoleLogger.Error($"Failed to install {item.Name}: {output}");
            
            // Log structured event for failure with reason tracking
            _sessionLogger?.LogInstallWithReason(
                item.Name,
                item.Version,
                "install",
                "failed",
                $"Failed to install {item.Name}",
                output,
                Cimian.Core.Models.StatusReasonCode.CheckFailed,
                Cimian.Core.Models.DetectionMethod.None,
                null,
                output);

            // Record failed install for loop guard tracking
            _loopGuard?.RecordAttempt(item.Name, item.Version, success: false, ComputeCatalogFingerprint(item));
            return false;
        }

        // Look for updates that should be applied after this install
        var updateList = CatalogService.LookForUpdates(item.Name, _catalogMap);
        if (updateList.Count > 0)
        {
            LogDetail($"Found {updateList.Count} update_for items for {item.Name}: {string.Join(", ", updateList)}");
        }

        foreach (var updateItemName in updateList)
        {
            LogInfo($"Installing update for {item.Name}: {updateItemName}");

            // Check if update item needs action
            var updateKey = updateItemName.ToLowerInvariant();
            if (_catalogMap.TryGetValue(updateKey, out var updateItem))
            {
                var status = _statusService.CheckStatus(updateItem, "install", _config.CachePath);
                if (status.NeedsAction)
                {
                    // Download the update item if not already downloaded
                    if (!downloadedPaths.ContainsKey(updateItem.Name))
                    {
                        var updateDownloads = await _downloadService.DownloadItemsAsync(new List<CatalogItem> { updateItem }, null, cancellationToken);
                        foreach (var kvp in updateDownloads)
                        {
                            downloadedPaths[kvp.Key] = kvp.Value;
                        }
                    }

                    // Process the update item (which may have its own dependencies)
                    if (!await ProcessInstallWithDependenciesAsync(updateItemName, installedItems, scheduledItems, downloadedPaths, cancellationToken))
                    {
                        LogInfo($"[WARNING] Failed to install update item: {updateItemName} (continuing)");
                        // Don't fail the main install just because an update_for item failed
                    }
                }
                else
                {
                    LogDetail($"Update item {updateItemName} doesn't need action: {status.Reason}");
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Process uninstallation of an item with dependency checking.
    /// This handles: finding dependent items and removing them first.
    /// Migrated from Go: ProcessUninstallWithDependencies() - process.go lines 642-690
    /// </summary>
    private async Task<bool> ProcessUninstallWithDependenciesAsync(
        string itemName,
        List<string> installedItems,
        CancellationToken cancellationToken)
    {
        LogDetail($"ProcessUninstallWithDependencies: {itemName}");

        // Find items that require this item
        var dependentItems = CatalogService.FindItemsRequiring(itemName, _catalogMap);

        // Remove dependent items first
        foreach (var depItem in dependentItems)
        {
            // Check if dependent item is actually installed
            if (CatalogService.IsItemInstalled(depItem.Name, installedItems))
            {
                LogInfo($"Removing dependent item first: {depItem.Name} (requires {itemName})");
                if (!await ProcessUninstallWithDependenciesAsync(depItem.Name, installedItems, cancellationToken))
                {
                    ConsoleLogger.Error($"Failed to remove dependent item: {depItem.Name}");
                    return false;
                }
            }
        }

        // Get the main item and uninstall it
        var key = itemName.ToLowerInvariant();
        if (!_catalogMap.TryGetValue(key, out var item))
        {
            ConsoleLogger.Error($"Item not found in catalog: {itemName}");
            return false;
        }

        // Check for blocking apps
        if (_installerService.CheckBlockingApps(item, out var runningApps))
        {
            ConsoleLogger.Warn($"Skipping {item.Name}: blocking apps running: {string.Join(", ", runningApps)}");
            return false;
        }

        LogInfo($"Removing: {item.Name}");
        var (success, output) = await _installerService.UninstallAsync(item, cancellationToken);

        if (success)
        {
            LogSuccess($"Removed: {item.Name}");
            installedItems.RemoveAll(i => string.Equals(i, item.Name, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        else
        {
            ConsoleLogger.Error($"Failed to remove {item.Name}: {output}");
            return false;
        }
    }

    #endregion

    private async Task<bool> PerformUninstallsAsync(
        List<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        LogInfo($"Removing {items.Count} items with dependency processing...");

        var successCount = 0;
        var failCount = 0;

        // Track installed items - start with what we're about to remove
        // (In a real scenario, we'd have a better way to track currently installed items)
        var installedItems = items.Select(i => i.Name).ToList();

        // Process each uninstall with dependency checking
        // This is Go parity: ProcessUninstallWithDependencies from process.go
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Skip if already removed (may have been removed as a dependent)
            if (!installedItems.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                LogDetail($"Skipping {item.Name}: already removed as dependent");
                successCount++;
                continue;
            }

            var success = await ProcessUninstallWithDependenciesAsync(
                item.Name,
                installedItems,
                cancellationToken);

            if (success)
            {
                successCount++;
            }
            else
            {
                failCount++;
            }
        }

        LogInfo($"Uninstall summary: {successCount} succeeded, {failCount} failed");
        return failCount == 0;
    }

    private void CleanManifestsAndCatalogsPreRun()
    {
        try
        {
            if (Directory.Exists(_config.CatalogsPath))
            {
                Directory.Delete(_config.CatalogsPath, true);
            }
            Directory.CreateDirectory(_config.CatalogsPath);
            LogDebug($"Cleaned and recreated directory dirPath: {_config.CatalogsPath}");

            if (Directory.Exists(_config.ManifestsPath))
            {
                Directory.Delete(_config.ManifestsPath, true);
            }
            Directory.CreateDirectory(_config.ManifestsPath);
            LogDebug($"Cleaned and recreated directory dirPath: {_config.ManifestsPath}");
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to clean pre-run directories: {ex.Message}");
        }
    }

    private void HandlePreflightFailure(string output)
    {
        var action = _config.PreflightFailureAction.ToLowerInvariant();

        switch (action)
        {
            case "abort":
                ConsoleLogger.Error($"Preflight script failed: {output}");
                ConsoleLogger.Error("Aborting due to PreflightFailureAction=abort");
                throw new Exception("Preflight script failed");

            case "warn":
                ConsoleLogger.Warn($"Preflight script failed: {output}");
                ConsoleLogger.Warn("Continuing despite preflight failure (PreflightFailureAction=warn)");
                break;

            default: // "continue"
                ConsoleLogger.Warn($"Preflight script failed: {output}");
                ConsoleLogger.Warn("Continuing with software updates (PreflightFailureAction=continue)");
                break;
        }
    }

    /// <summary>
    /// Log a plain message (always shown)
    /// </summary>
    private void Log(string message = "")
    {
        ConsoleLogger.Log(message);
    }

    private void LogInfo(string message)
    {
        ConsoleLogger.Info(message);
    }

    private void LogDetail(string message)
    {
        ConsoleLogger.Detail(message);
    }

    private void LogDebug(string message)
    {
        ConsoleLogger.Debug(message);
    }

    private void LogSuccess(string message)
    {
        ConsoleLogger.Success(message);
    }

    private void LogWarn(string message)
    {
        ConsoleLogger.Warn(message);
    }

    private void LogError(string message)
    {
        ConsoleLogger.Error(message);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= maxLength) return value;
        return value[..(maxLength - 3)] + "...";
    }

    #region Verbose Output Methods (Go Parity)
    
    /// <summary>
    /// Gets the version string in YYYY.MM.DD.HHMM format
    /// </summary>
    private static string GetFormattedVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        // Try AssemblyInformationalVersion first (has proper YYYY.MM.DD.HHMM format)
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+');
            if (plusIndex >= 0)
            {
                return informationalVersion[..plusIndex];
            }
            return informationalVersion;
        }
        
        // Fall back to AssemblyFileVersion
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrEmpty(fileVersion))
        {
            return fileVersion;
        }
        
        // Last resort - assembly version (may lose leading zeros)
        return assembly.GetName().Version?.ToString() ?? "Unknown";
    }
    
    /// <summary>
    /// Prints the verbose header banner - matches Go output with timestamps
    /// </summary>
    private void PrintVerboseHeader()
    {
        var version = GetFormattedVersion();
        
        Log($"Cimian version is {version}");
        Log();
        LogInfo("================================================================================");
        LogInfo("CIMIAN MANAGED SOFTWARE UPDATE - VERBOSE MODE");
        LogInfo($"Version: {version}");
        LogInfo("================================================================================");
        Log();
    }
    
    /// <summary>
    /// Prints the system configuration block - matches Go output with timestamps
    /// </summary>
    private void PrintSystemConfiguration()
    {
        LogInfo("================================================================================");
        LogInfo("SYSTEM CONFIGURATION");
        LogInfo("================================================================================");
        LogInfo($"Verbosity Level: {_verbosity}");
        LogInfo($"Log Level: {(_verbosity >= 2 ? "DEBUG" : "INFO")}");
        LogInfo($"Working Directory: {Environment.CurrentDirectory}");
        LogInfo($"Config Path: {CimianConfig.ConfigPath}");
        LogInfo($"Cache Path: {_config.CachePath}");
        LogInfo($"Software Repo URL: {_config.SoftwareRepoURL}");
        LogInfo($"Client Identifier: {_config.ClientIdentifier}");
        LogInfo("================================================================================");
        Log();
    }
    
    /// <summary>
    /// Prints the manifest hierarchy tree - matches Go output
    /// </summary>
    private void PrintManifestHierarchy(List<ManifestItem> manifestItems)
    {
        // Group items by source manifest
        var manifestCounts = new Dictionary<string, int>();
        var manifestPackages = new Dictionary<string, List<ManifestItem>>();
        
        foreach (var item in manifestItems)
        {
            var source = string.IsNullOrEmpty(item.SourceManifest) ? "Unknown" : item.SourceManifest;
            
            if (!manifestCounts.ContainsKey(source))
            {
                manifestCounts[source] = 0;
                manifestPackages[source] = new List<ManifestItem>();
            }
            
            if (item.Action?.ToLowerInvariant() == "install")
            {
                manifestPackages[source].Add(item);
            }
            manifestCounts[source]++;
        }
        
        Log("----------------------------------------------------------------------");
        Log("MANIFEST HIERARCHY");
        Log("----------------------------------------------------------------------");
        
        // Build hierarchy tree
        var tree = BuildManifestHierarchy(manifestCounts, manifestPackages);
        PrintManifestTree(tree, "", true, manifestPackages);
        
        Log();
    }
    
    private class ManifestNode
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public Dictionary<string, ManifestNode> Children { get; set; } = new();
    }
    
    private ManifestNode BuildManifestHierarchy(Dictionary<string, int> counts, Dictionary<string, List<ManifestItem>> packages)
    {
        var root = new ManifestNode { Name = "root" };
        
        // Core manifests first (not part of client identifier hierarchy)
        var coreManifests = new[] { "ManagementTools", "ManagementPrefs", "CoreApps", "CoreDrivers" };
        
        foreach (var manifest in coreManifests)
        {
            if (counts.ContainsKey(manifest) || packages.ContainsKey(manifest))
            {
                var packageCount = packages.TryGetValue(manifest, out var pkgs) ? pkgs.Count : 0;
                root.Children[manifest] = new ManifestNode 
                { 
                    Name = manifest, 
                    Count = packageCount
                };
            }
        }
        
        // Build client identifier hierarchy (e.g., Assigned/Staff/IT/B1115/Desktop)
        var clientId = _config.ClientIdentifier;
        if (!string.IsNullOrEmpty(clientId))
        {
            var parts = clientId.Split('/');
            var current = root;
            
            // Add to last core manifest's children (CoreManifest)
            if (root.Children.TryGetValue("ManagementTools", out var managementNode))
            {
                var coreManifestNode = new ManifestNode { Name = "CoreManifest", Count = 1 };
                managementNode.Children["CoreManifest"] = coreManifestNode;
                current = coreManifestNode;
            }
            
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                
                var packageCount = packages.TryGetValue(part, out var pkgs) ? pkgs.Count : 0;
                
                if (!current.Children.ContainsKey(part))
                {
                    current.Children[part] = new ManifestNode 
                    { 
                        Name = part, 
                        Count = packageCount
                    };
                }
                current = current.Children[part];
            }
        }
        
        return root;
    }
    
    private void PrintManifestTree(ManifestNode node, string prefix, bool isLast, Dictionary<string, List<ManifestItem>> packages)
    {
        if (node.Name == "root")
        {
            var names = node.Children.Keys.ToList();
            for (int i = 0; i < names.Count; i++)
            {
                PrintManifestTree(node.Children[names[i]], "", i == names.Count - 1, packages);
            }
            return;
        }
        
        var connector = isLast ? "└─" : "├─";
        var childPrefix = prefix + (isLast ? "   " : "│  ");
        
        // Calculate items count
        var itemCount = node.Count + node.Children.Count;
        if (packages.TryGetValue(node.Name, out var pkgs))
        {
            itemCount = pkgs.Count + node.Children.Count;
        }
        
        Log($"{prefix}{connector} {node.Name} [{itemCount} items]");
        
        // Print packages for this manifest
        if (packages.TryGetValue(node.Name, out var manifestPkgs) && manifestPkgs.Count > 0)
        {
            for (int i = 0; i < manifestPkgs.Count; i++)
            {
                var isLastPkg = i == manifestPkgs.Count - 1 && node.Children.Count == 0;
                var pkgConnector = isLastPkg ? "└─" : "├─";
                Log($"{childPrefix}{pkgConnector} {Truncate(manifestPkgs[i].Name, 30)}");
            }
        }
        
        // Print children
        var childNames = node.Children.Keys.ToList();
        for (int i = 0; i < childNames.Count; i++)
        {
            PrintManifestTree(node.Children[childNames[i]], childPrefix, i == childNames.Count - 1, packages);
        }
    }
    
    /// <summary>
    /// Prints the managed installs status table - matches Go output
    /// </summary>
    private void PrintManagedInstallsTable(
        List<ManifestItem> manifestItems, 
        List<CatalogItem> toInstall, 
        List<CatalogItem> toUpdate,
        Dictionary<string, CatalogItem> catalogMap)
    {
        // Filter to install actions only
        var managedInstalls = manifestItems
            .Where(m => m.Action?.ToLowerInvariant() == "install")
            .ToList();
        
        if (managedInstalls.Count == 0) return;
        
        // Build status for each item
        var packageStatuses = new List<(string Name, string Version, string Status)>();
        var toInstallNames = toInstall.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
        var toUpdateNames = toUpdate.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
        
        foreach (var item in managedInstalls)
        {
            var name = item.Name;
            var version = "Unknown";
            var status = "Installed";
            
            // Get catalog version
            if (catalogMap.TryGetValue(name.ToLowerInvariant(), out var catalogItem))
            {
                version = catalogItem.Version;
            }
            
            // Determine status
            if (toInstallNames.Contains(name.ToLowerInvariant()))
            {
                status = "Pending Install";
            }
            else if (toUpdateNames.Contains(name.ToLowerInvariant()))
            {
                status = "Pending Update";
            }

            // Annotate items deferred by install_window
            if (catalogItem?.InstallWindow != null 
                && !catalogItem.InstallWindow.IsWithinWindow(DateTime.Now) && status.StartsWith("Pending"))
            {
                status = $"Deferred ({catalogItem.InstallWindow})";
            }
            
            packageStatuses.Add((name, version, status));
        }
        
        // Sort: Installed first, then Pending Install, then Pending Update, then Deferred
        var statusPriority = new Dictionary<string, int>
        {
            { "Installed", 1 },
            { "Pending Install", 2 },
            { "Pending Update", 3 }
        };
        
        packageStatuses = packageStatuses
            .OrderBy(p => statusPriority.TryGetValue(p.Status, out var priority) ? priority : 99)
            .ThenBy(p => p.Name)
            .ToList();
        
        Log("----------------------------------------------------------------------");
        Log($"MANAGED INSTALLS ({managedInstalls.Count} items)");
        Log("----------------------------------------------------------------------");
        Log($"{"Package Name",-27} | {"Version",-17} | {"Status",-15}");
        Log("----------------------------------------------------------------------");
        
        foreach (var (name, version, status) in packageStatuses)
        {
            Log($"{Truncate(name, 25),-27} | {Truncate(version, 15),-17} | {status,-15}");
        }
    }

    /// <summary>
    /// Prints the managed updates status table - for items from managed_updates section
    /// This matches Go behavior which shows a separate section for managed_updates items
    /// </summary>
    private void PrintManagedUpdatesTable(
        List<ManifestItem> manifestItems,
        List<CatalogItem> toUpdate,
        Dictionary<string, CatalogItem> catalogMap)
    {
        // Filter to update actions only (from managed_updates)
        var managedUpdates = manifestItems
            .Where(m => m.Action?.ToLowerInvariant() == "update")
            .ToList();
        
        if (managedUpdates.Count == 0) return;
        
        // Build status for each item
        var packageStatuses = new List<(string Name, string Version, string Status)>();
        var toUpdateNames = toUpdate.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
        
        foreach (var item in managedUpdates)
        {
            var name = item.Name;
            var version = "Unknown";
            var status = "Installed";
            
            // Get catalog version
            if (catalogMap.TryGetValue(name.ToLowerInvariant(), out var catalogItem))
            {
                version = catalogItem.Version;
            }
            
            // Determine status
            if (toUpdateNames.Contains(name.ToLowerInvariant()))
            {
                status = "Pending Update";
            }

            // Annotate items deferred by install_window
            if (catalogItem?.InstallWindow != null
                && !catalogItem.InstallWindow.IsWithinWindow(DateTime.Now) && status.StartsWith("Pending"))
            {
                status = $"Deferred ({catalogItem.InstallWindow})";
            }
            
            packageStatuses.Add((name, version, status));
        }
        
        // Sort: Installed first, then Pending Update, then Deferred
        packageStatuses = packageStatuses
            .OrderBy(p => p.Status == "Installed" ? 0 : p.Status.StartsWith("Pending") ? 1 : 2)
            .ThenBy(p => p.Name)
            .ToList();
        
        Log("----------------------------------------------------------------------");
        Log($"MANAGED UPDATES ({managedUpdates.Count} items)");
        Log("----------------------------------------------------------------------");
        
        foreach (var (name, version, status) in packageStatuses)
        {
            Log($"{Truncate(name, 25),-27} | {Truncate(version, 15),-17} | {status,-15}");
        }
    }

    /// <summary>
    /// Prints the managed uninstalls status table - for items from managed_uninstalls section
    /// This matches Go behavior which shows a separate section for managed_uninstalls items
    /// </summary>
    private void PrintManagedUninstallsTable(
        List<ManifestItem> manifestItems,
        List<CatalogItem> toUninstall,
        Dictionary<string, CatalogItem> catalogMap)
    {
        // Filter to uninstall actions only (from managed_uninstalls)
        var managedUninstalls = manifestItems
            .Where(m => m.Action?.ToLowerInvariant() == "uninstall")
            .ToList();
        
        if (managedUninstalls.Count == 0) return;
        
        // Build status for each item
        var packageStatuses = new List<(string Name, string Version, string Status)>();
        var toUninstallNames = toUninstall.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
        
        foreach (var item in managedUninstalls)
        {
            var name = item.Name;
            var version = "Unknown";
            var status = "Removed";
            
            // Get catalog version
            if (catalogMap.TryGetValue(name.ToLowerInvariant(), out var catalogItem))
            {
                version = catalogItem.Version;
            }
            
            // Determine status - if in toUninstall list, it's still installed and pending removal
            if (toUninstallNames.Contains(name.ToLowerInvariant()))
            {
                status = "Pending Removal";
            }
            
            packageStatuses.Add((name, version, status));
        }
        
        // Sort: Removed first, then Pending Removal
        packageStatuses = packageStatuses
            .OrderBy(p => p.Status == "Removed" ? 0 : 1)
            .ThenBy(p => p.Name)
            .ToList();
        
        Log("----------------------------------------------------------------------");
        Log($"MANAGED UNINSTALLS ({managedUninstalls.Count} items)");
        Log("----------------------------------------------------------------------");
        
        foreach (var (name, version, status) in packageStatuses)
        {
            Log($"{Truncate(name, 25),-27} | {Truncate(version, 15),-17} | {status,-15}");
        }
    }
    
    #endregion

    #region Status Reporter Methods (GUI integration)

    /// <summary>
    /// Reports a status message to the GUI (main headline)
    /// </summary>
    private void ReportStatus(string message)
    {
        _statusReporter?.Message(message);
    }

    /// <summary>
    /// Reports a detail message to the GUI (secondary text)
    /// </summary>
    private void ReportDetail(string message)
    {
        _statusReporter?.Detail(message);
    }

    /// <summary>
    /// Reports progress percentage to the GUI
    /// </summary>
    private void ReportPercent(int percent)
    {
        _statusReporter?.Percent(percent);
    }

    /// <summary>
    /// Reports an error to the GUI
    /// </summary>
    private void ReportError(string message)
    {
        _statusReporter?.Error(message);
    }

    #endregion

    #region Session Logging Helpers

    /// <summary>
    /// Collects current session items data and passes to SessionLogger for items.json generation.
    /// Go parity: main.go lines 3308-3430 where SessionPackageInfo is collected for each manifest item.
    /// Excludes MDM profiles/apps (managed externally) - those are filtered by SessionLogger.
    /// </summary>
    private void CollectSessionItems(
        List<ManifestItem> manifestItems,
        List<CatalogItem> toInstall,
        List<CatalogItem> toUpdate,
        List<CatalogItem> toUninstall,
        Dictionary<string, CatalogItem> catalogMap)
    {
        if (_sessionLogger == null) return;

        var toInstallNames = toInstall.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
        var toUpdateNames = toUpdate.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
        var toUninstallNames = toUninstall.Select(i => i.Name.ToLowerInvariant()).ToHashSet();

        var items = new List<SessionPackageInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mi in manifestItems)
        {
            if (string.IsNullOrEmpty(mi.Name) || !seen.Add(mi.Name))
                continue;

            var action = mi.Action?.ToLowerInvariant() ?? "install";
            var key = mi.Name.ToLowerInvariant();

            // Determine item type (Go parity: determineItemType mapping)
            var itemType = action switch
            {
                "install" => "managed_installs",
                "update" => "managed_updates",
                "uninstall" => "managed_uninstalls",
                "profile" => "managedprofile",
                "app" => "managedapp",
                _ => "managed_installs"
            };

            var version = "";
            var displayName = mi.Name;
            if (catalogMap.TryGetValue(key, out var catItem))
            {
                version = catItem.Version;
                if (!string.IsNullOrEmpty(catItem.DisplayName))
                    displayName = catItem.DisplayName;
            }

            // Determine status
            string status;
            if (toInstallNames.Contains(key))
                status = "Pending Install";
            else if (toUpdateNames.Contains(key))
                status = "Pending Update";
            else if (toUninstallNames.Contains(key))
                status = "Pending Removal";
            else if (action == "uninstall")
                status = "Removed";
            else
                status = "Installed";

            items.Add(new SessionPackageInfo
            {
                Name = mi.Name,
                Version = version,
                Status = status,
                ItemType = itemType,
                DisplayName = displayName
            });
        }

        _sessionLogger.SetCurrentSessionItems(items);
    }

    /// <summary>
    /// Ends the session with a summary of operations performed
    /// </summary>
    private void EndSessionWithSummary(
        string status, 
        int installCount, 
        int updateCount, 
        int uninstallCount,
        int successCount,
        int failCount,
        List<ManifestItem> manifestItems)
    {
        if (_sessionLogger == null) return;

        var packagesHandled = manifestItems
            .Where(m => !string.IsNullOrEmpty(m.Name))
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        var summary = new SessionLogSummary
        {
            TotalActions = installCount + updateCount + uninstallCount,
            Installs = installCount,
            Updates = updateCount,
            Removals = uninstallCount,
            Successes = successCount,
            Failures = failCount,
            PackagesHandled = packagesHandled
        };

        _sessionLogger.EndSession(status, summary);
    }

    #endregion

    #region InstallInfo.yaml

    /// <summary>
    /// Writes InstallInfo.yaml to ManagedInstallDir
    /// the single source of truth for the MSC GUI.
    /// enriches each item with full catalog metadata and
    /// computed status for the GUI to deserialize and render.
    /// </summary>
    private void WriteInstallInfo(
        List<ManifestItem> manifestItems,
        List<CatalogItem> toInstall,
        List<CatalogItem> toUpdate,
        List<CatalogItem> toUninstall,
        Dictionary<string, CatalogItem> catalogMap)
    {
        try
        {
            var toInstallNames = toInstall.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
            var toUpdateNames = toUpdate.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
            var toUninstallNames = toUninstall.Select(i => i.Name.ToLowerInvariant()).ToHashSet();

            var info = new InstallInfoFile
            {
                LastCheck = DateTime.Now
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mi in manifestItems)
            {
                if (string.IsNullOrEmpty(mi.Name) || !seen.Add(mi.Name))
                    continue;

                var key = mi.Name.ToLowerInvariant();
                catalogMap.TryGetValue(key, out var cat);

                var action = mi.Action?.ToLowerInvariant() ?? "install";

                switch (action)
                {
                    case "install":
                    case "update":
                        if (toUpdateNames.Contains(key) || toInstallNames.Contains(key))
                        {
                            // Needs action — add to managed_installs
                            var isUpdate = toUpdateNames.Contains(key);
                            var item = BuildInstallInfoItem(mi.Name, cat);
                            item.Status = isUpdate ? "update-available" : "will-be-installed";
                            item.WillBeInstalled = true;
                            item.NeedsUpdate = isUpdate;

                            // Try to get installed version from status check
                            if (cat != null)
                            {
                                var status = _statusService.CheckStatus(cat, action, _config.CachePath);
                                item.InstalledVersion = status.InstalledVersion;
                            }

                            if (isUpdate)
                                info.ManagedUpdates.Add(item);
                            else
                                info.ManagedInstalls.Add(item);
                        }
                        // Items that are already installed and up-to-date are omitted from
                        // managed_installs/managed_updates (only items needing action are included)
                        break;

                    case "uninstall":
                        if (toUninstallNames.Contains(key))
                        {
                            var item = BuildInstallInfoItem(mi.Name, cat);
                            item.Status = "will-be-removed";
                            item.WillBeRemoved = true;
                            item.Installed = true;
                            info.Removals.Add(item);
                        }
                        break;

                    case "optional":
                        // Optional installs always appear — with installed/needs_update booleans
                        var optItem = BuildInstallInfoItem(mi.Name, cat);
                        if (cat != null)
                        {
                            var status = _statusService.CheckStatus(cat, "install", _config.CachePath);
                            optItem.Installed = !status.NeedsAction;
                            optItem.InstalledVersion = status.InstalledVersion;
                            optItem.NeedsUpdate = status.IsUpdate;
                            optItem.Status = status.NeedsAction
                                ? (status.IsUpdate ? "update-available" : "not-installed")
                                : "installed";
                        }
                        else
                        {
                            optItem.Status = "not-installed";
                        }
                        info.OptionalInstalls.Add(optItem);
                        break;
                }
            }

            // Serialize and write
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();

            var yaml = serializer.Serialize(info);
            var path = Path.Combine(Path.GetDirectoryName(_config.CachePath) ?? @"C:\ProgramData\ManagedInstalls", "InstallInfo.yaml");
            File.WriteAllText(path, yaml);

            LogInfo($"Wrote {path}");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to write InstallInfo.yaml: {ex.Message}");
            _sessionLogger?.Log("WARN", $"Failed to write InstallInfo.yaml: {ex.Message}");
        }
    }

    private static InstallInfoItem BuildInstallInfoItem(string name, CatalogItem? cat)
    {
        var item = new InstallInfoItem
        {
            Name = name,
            Version = cat?.Version ?? "",
            DisplayName = cat?.DisplayName,
            Description = cat?.Description,
            Category = cat?.Category,
            Developer = cat?.Developer,
            InstallerItemSize = cat?.Installer?.Size ?? 0,
            Uninstallable = cat?.IsUninstallable() ?? false,
        };
        return item;
    }

    #endregion

    #region IDisposable

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _statusReporter?.Dispose();
        _sessionLogger?.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}
