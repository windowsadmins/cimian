using System.Runtime.InteropServices;
using System.Text.Json;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core;
using Cimian.Core.Models;
using Cimian.Core.Services;
using Cimian.Core.Version;
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

    // Cancelled when the GUI sends a stop command over the status connection.
    // Checked between items so a user cancel aborts gracefully, never mid-install.
    private readonly CancellationTokenSource _userStop = new();

    private int _verbosity;
    private bool _isBootstrap;
    private bool _checkOnly;
    private bool _installOnly;
    private bool _auto;
    private bool _showStatus;
    private bool _restartNeeded;
    private bool _logoutNeeded;

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

    internal static bool IsEligibleForOsVersion(CatalogItem item, out string reason, out string reasonCode)
    {
        reason = string.Empty;
        reasonCode = string.Empty;

        var min = item.MinimumOsVersion;
        var max = item.MaximumOsVersion;
        if (string.IsNullOrWhiteSpace(min) && string.IsNullOrWhiteSpace(max))
            return true;

        var current = Cimian.Core.Version.VersionService.GetCurrentOsVersion();

        if (!string.IsNullOrWhiteSpace(min) &&
            Cimian.Core.Version.VersionService.CompareOsVersion(current, min) < 0)
        {
            reason = $"requires OS {min} or newer, running {current}";
            reasonCode = StatusReasonCode.OsVersionTooOld;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(max) &&
            Cimian.Core.Version.VersionService.CompareOsVersion(current, max) > 0)
        {
            reason = $"requires OS {max} or older, running {current}";
            reasonCode = StatusReasonCode.OsVersionTooNew;
            return false;
        }

        return true;
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
        int statusPort = StatusReporter.DefaultPort,
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

        // Initialize loop guard for install loop prevention. Admins can disable it
        // fleet-wide via LoopGuardEnabled: false in config.yaml. The startup notice
        // is emitted further down, once ConsoleLogger.Verbosity is set and the
        // SessionLogger is attached, so it actually reaches the console and run.log.
        var loopGuardDisabled = !_config.LoopGuardEnabled;
        _loopGuard = new LoopGuard(_isBootstrap, disabled: loopGuardDisabled);

        // Track session duration for run.log summary
        var sessionStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Set global verbosity for ConsoleLogger
        ConsoleLogger.Verbosity = verbosity;

        // Initialize status reporter if --show-status is set
        if (_showStatus)
        {
            _statusReporter = new StatusReporter(verbosity, statusPort);
            // GUI Cancel button: stop gracefully before the next item.
            _statusReporter.StopRequested += () => _userStop.Cancel();
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

        // Now that verbosity is set and the SessionLogger is attached, surface the
        // LoopGuard kill-switch so it reaches both the console and run.log.
        if (loopGuardDisabled)
            ConsoleLogger.Info("LoopGuard disabled by config (LoopGuardEnabled: false) — install-loop suppression is off");

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
            var (toInstall, toUpdate, toUninstall, loopSuppressed) = IdentifyActions(manifestItems, catalogMap, itemFilterService);

            // Dictionary of items LoopGuard refused this run, keyed by lower-invariant
            // name. Surfaces in items.json as Warning + last_warning + status_reason_code,
            // and in a sibling reports/loop_suppressed.json for dashboards.
            var loopSuppressedByName = loopSuppressed.ToDictionary(
                x => x.Item.Name.ToLowerInvariant(),
                x => (x.Reason, x.InstalledVersion, x.WasUpdate));

            // AutoRemove: queue uninstall for packages installed by Cimian but no longer in any manifest
            if (_config.AutoRemove)
            {
                var autoRemoveItems = IdentifyAutoRemoveItems(manifestItems, catalogMap);
                if (autoRemoveItems.Count > 0)
                {
                    ConsoleLogger.Info($"AutoRemove: {autoRemoveItems.Count} package(s) no longer in manifests");
                    foreach (var item in autoRemoveItems)
                    {
                        ConsoleLogger.Info($"    -> Auto-removing: {item.Name} v{item.Version}");
                        _sessionLogger?.Log("INFO", $"AutoRemove: {item.Name} v{item.Version} no longer in any manifest");
                    }
                    toUninstall.AddRange(autoRemoveItems);
                }
            }

            // Stale-usage removal: queue uninstall for opted-in packages whose
            // tracked executables nobody on the device has used within
            // unused_software_removal_info. Peer of AutoRemove, not a
            // dependency walker — and placed before the downstream filters so
            // install_window / blocking_applications / unattended gating apply
            // to these uninstalls the same as any other.
            if (_config.UsageStaleUninstallEnabled)
            {
                // Resolved here rather than in the constructor: preflight can
                // reload _config, and the source's lazy snapshot should reflect
                // this run's on-disk data, not engine-construction time.
                var usageSource = new ReportMateUsageDataSource();
                // Anything already queued this run — including a pending update for a
                // managed_updates item — defers stale evaluation to the next run.
                var queuedThisRun = toUninstall.Concat(toInstall).Concat(toUpdate).ToList();
                var staleUsageItems = await IdentifyStaleUsageItemsAsync(manifestItems, catalogMap, queuedThisRun, usageSource);
                if (staleUsageItems.Count > 0)
                {
                    ConsoleLogger.Info($"StaleUsage: {staleUsageItems.Count} package(s) untouched past their threshold");
                    toUninstall.AddRange(staleUsageItems);
                }
            }

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

                // A self-serve click drives --item: the user explicitly asked for
                // these item(s). Any that produced no action are already in the
                // desired state, but with no signal the GUI navigates to Updates and
                // sits empty ("nothing happened"). Emit a terminal stage per
                // already-satisfied request so its row shows a confirming check
                // (Installed/Removed) instead of silence. Intent comes from the
                // manifest Action the self-serve merge set (install vs uninstall).
                // No-op without --show-status (no reporter).
                var actionedNames = new HashSet<string>(
                    toInstall.Concat(toUpdate).Concat(toUninstall).Select(c => c.Name),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var requested in itemFilterService.Items)
                {
                    if (actionedNames.Contains(requested)) continue;
                    var mi = manifestItems.FirstOrDefault(m =>
                        string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase));
                    var alreadyRemoved = string.Equals(mi?.Action, "uninstall", StringComparison.OrdinalIgnoreCase);
                    ReportItemStatus(requested, alreadyRemoved ? "removed" : "installed");
                }
            }

            // Emit an early "pending" stage for every item this session will act on,
            // so the GUI shows a per-row spinner immediately — through dependency
            // resolution and downloads — instead of each row looking idle
            // ("Will be installed") until the install loop finally reaches it. The
            // per-item pending/downloading/installing/installed stages then update
            // each row in place. No-op when --show-status is off (no reporter).
            foreach (var ci in toInstall.Concat(toUpdate))
                ReportItemStatus(ci.Name, "pending");
            foreach (var ci in toUninstall)
                ReportItemStatus(ci.Name, "pending");

            // Resolve dependencies and update_for items (Go parity: process.go dependency resolution).
            // Walks both `requires` and `update_for` to the transitive closure so that a stale
            // dep gets surfaced even when its parent is already at the catalog version
            // (e.g. ManageUsersPrefs current, ManageUsers stale).
            ResolveDependencies(manifestItems, catalogMap, toUpdate, itemFilterService);

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
                // Check-only mode never runs installs/uninstalls, so no outcomes exist.
                CollectSessionItems(manifestItems, toInstall, toUpdate, toUninstall, catalogMap,
                    new Dictionary<string, ItemOutcome>(),
                    loopSuppressedByName);

                // Write InstallInfo.yaml for MSC GUI
                WriteInstallInfo(manifestItems, toInstall, toUpdate, toUninstall, catalogMap);

                // End session for check-only
                EndSessionWithSummary("completed", toInstall.Count, toUpdate.Count, toUninstall.Count, 0, 0, manifestItems);
                return 0;
            }

            // Filter out items outside their install_window (applies to installs, updates, and uninstalls)
            // Exception: force_install_after_date overrides install_window — if deadline has passed, install anyway
            var deferredItems = new List<CatalogItem>();
            var now = DateTime.Now;
            foreach (var list in new[] { toInstall, toUpdate, toUninstall })
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var item = list[i];
                    if (item.InstallWindow != null && !item.InstallWindow.IsWithinWindow(now))
                    {
                        // Deadline override: force_install_after_date takes priority over install_window
                        if (item.ForceInstallAfterDate != null && now >= item.ForceInstallAfterDate.Value)
                        {
                            LogInfo($"Installing {item.Name} v{item.Version} despite install_window {item.InstallWindow}: force_install_after_date {item.ForceInstallAfterDate.Value:yyyy-MM-dd} has passed");
                            _sessionLogger?.LogStatusCheck(
                                item.Name, item.Version, "pending",
                                $"Deadline {item.ForceInstallAfterDate.Value:yyyy-MM-dd} overrides install window {item.InstallWindow}",
                                Cimian.Core.Models.StatusReasonCode.DeadlineOverridesWindow,
                                Cimian.Core.Models.DetectionMethod.None, null, true);
                            continue; // Keep in list, don't defer
                        }

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

            // Per-item: defer items whose blocking_applications are running.
            // Installing while the blocking app is open would fail or destroy
            // the user's open work. Always applied — independent of mode/user.
            // Snapshot the running-process set once so the per-item check is O(1).
            var runningProcessNames = StatusService.GetRunningProcessNames();
            var blockedItems = new List<CatalogItem>();
            foreach (var list in new[] { toInstall, toUpdate, toUninstall })
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var item = list[i];
                    if (item.BlockingApps.Count == 0) continue;

                    if (StatusService.CheckBlockingApps(item.BlockingApps, runningProcessNames, out var running))
                    {
                        var runningList = string.Join(", ", running);
                        LogInfo($"Deferred: {item.Name} v{item.Version} (blocking applications running: {runningList})");
                        _sessionLogger?.Log("INFO", $"Deferred {item.Name} v{item.Version}: blocking applications running ({runningList})");
                        _sessionLogger?.LogStatusCheck(
                            item.Name, item.Version, "deferred",
                            $"Blocking applications running: {runningList}",
                            Cimian.Core.Models.StatusReasonCode.BlockingApps,
                            Cimian.Core.Models.DetectionMethod.None, null, true);
                        blockedItems.Add(item);
                        list.RemoveAt(i);
                    }
                }
            }
            if (blockedItems.Count > 0)
            {
                LogInfo($"{blockedItems.Count} item(s) deferred while blocking applications are running");
            }

            // Auto mode + active user: restrict to items that can run silently
            // without disrupting the session. An item is eligible only if it is
            // marked unattended AND its restart_action would not reboot or log
            // the user out (Require* and Recommend* are both treated as
            // disruptive here). Everything else is deferred to a later run
            // (idle machine, interactive run, or scheduled maintenance window).
            var deferredForUser = new List<CatalogItem>();
            if (_auto && StatusService.IsUserActive())
            {
                LogInfo($"User is active (idle: {StatusService.GetIdleSeconds()}s) - restricting to unattended items that won't disrupt the session");
                _sessionLogger?.Log("INFO", "User is active - restricting auto run to unattended, non-disruptive items");

                foreach (var list in new[] { toInstall, toUpdate })
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var item = list[i];
                        string? deferReason = null;
                        if (!item.UnattendedInstall)
                        {
                            deferReason = "unattended_install is false";
                        }
                        else if (WouldInterruptUser(item))
                        {
                            deferReason = $"restart_action '{item.RestartAction}' would interrupt the active user";
                        }

                        if (deferReason != null)
                        {
                            LogInfo($"Deferred install of {item.Name} v{item.Version}: {deferReason}");
                            _sessionLogger?.Log("INFO", $"Deferred {item.Name} v{item.Version}: {deferReason} (auto mode, user active)");
                            _sessionLogger?.LogStatusCheck(
                                item.Name, item.Version, "deferred",
                                deferReason,
                                Cimian.Core.Models.StatusReasonCode.DeferredUserActive,
                                Cimian.Core.Models.DetectionMethod.None, null, true);
                            deferredForUser.Add(item);
                            list.RemoveAt(i);
                        }
                    }
                }

                for (int i = toUninstall.Count - 1; i >= 0; i--)
                {
                    var item = toUninstall[i];
                    string? deferReason = null;
                    if (!item.UnattendedUninstall)
                    {
                        deferReason = "unattended_uninstall is false";
                    }
                    else if (WouldInterruptUser(item))
                    {
                        deferReason = $"restart_action '{item.RestartAction}' would interrupt the active user";
                    }

                    if (deferReason != null)
                    {
                        LogInfo($"Deferred removal of {item.Name} v{item.Version}: {deferReason}");
                        _sessionLogger?.Log("INFO", $"Deferred removal of {item.Name} v{item.Version}: {deferReason} (auto mode, user active)");
                        _sessionLogger?.LogStatusCheck(
                            item.Name, item.Version, "deferred",
                            deferReason,
                            Cimian.Core.Models.StatusReasonCode.DeferredUserActive,
                            Cimian.Core.Models.DetectionMethod.None, null, true);
                        deferredForUser.Add(item);
                        toUninstall.RemoveAt(i);
                    }
                }

                if (deferredForUser.Count > 0)
                {
                    LogInfo($"{deferredForUser.Count} item(s) deferred while user is active");
                }
            }

            // Precache: download optional items marked with precache=true
            // This runs before installations so precached items are ready if the user requests them
            await PrecacheOptionalItemsAsync(manifestItems, catalogMap, cancellationToken);

            // Perform installations
            var installSuccess = true;
            var successCount = 0;
            var failCount = 0;
            var installOutcomes = new List<ItemOutcome>();
            var uninstallOutcomes = new List<ItemOutcome>();
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
                    installOutcomes = await PerformInstallationsAsync(allToInstall, cancellationToken);

                    // Per-item outcome counts (includes dependencies + update_for items)
                    successCount = installOutcomes.Count(o => o.Success);
                    failCount = installOutcomes.Count(o => !o.Success);
                    installSuccess = failCount == 0;
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
                uninstallOutcomes = await PerformUninstallsAsync(toUninstall, cancellationToken);
                uninstallSuccess = uninstallOutcomes.All(o => o.Success);

                // Consume satisfied self-serve removal requests so they are not
                // re-queued and shown as pending every run (clean_up_managed_uninstalls).
                await CleanUpSelfServeUninstallsAsync(uninstallOutcomes);
            }

            // Combine install + uninstall outcomes keyed by lower-invariant name so
            // CollectSessionItems can stamp each manifest item with its real result.
            var outcomesByName = new Dictionary<string, ItemOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in installOutcomes) outcomesByName[o.Name.ToLowerInvariant()] = o;
            foreach (var o in uninstallOutcomes) outcomesByName[o.Name.ToLowerInvariant()] = o;

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
                CollectSessionItems(manifestItems, toInstall, toUpdate, toUninstall, catalogMap, outcomesByName, loopSuppressedByName);

                // Write InstallInfo.yaml for MSC GUI (post-install: actions completed)
                WriteInstallInfo(manifestItems, toInstall, toUpdate, toUninstall, catalogMap, outcomesByName.Values);

                EndSessionWithSummary("completed", toInstall.Count, toUpdate.Count, toUninstall.Count,
                    toInstall.Count + toUpdate.Count + toUninstall.Count, 0, manifestItems);
                
                // Handle restart_action: restart takes precedence over logout (Munki parity)
                if (_restartNeeded)
                {
                    PerformRestartAction();
                }
                else if (_logoutNeeded)
                {
                    PerformLogoutAction();
                }

                return 0;
            }
            else
            {
                ConsoleLogger.Warn("Some operations failed");
                _sessionLogger?.Log("WARN", "Some operations failed");
                ReportError("Some operations failed");

                // Collect items data for items.json report
                CollectSessionItems(manifestItems, toInstall, toUpdate, toUninstall, catalogMap, outcomesByName, loopSuppressedByName);

                // Write InstallInfo.yaml for MSC GUI (post-install: reflects final state)
                WriteInstallInfo(manifestItems, toInstall, toUpdate, toUninstall, catalogMap, outcomesByName.Values);

                EndSessionWithSummary("partial_failure", toInstall.Count, toUpdate.Count, toUninstall.Count,
                    successCount, failCount, manifestItems);

                // Even on partial failure, honor restart/logout if any successful item required it
                if (_restartNeeded)
                {
                    PerformRestartAction();
                }
                else if (_logoutNeeded)
                {
                    PerformLogoutAction();
                }
                
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

    private (List<CatalogItem> ToInstall, List<CatalogItem> ToUpdate, List<CatalogItem> ToUninstall,
             List<(CatalogItem Item, string Reason, string? InstalledVersion, bool WasUpdate)> LoopSuppressed)
        IdentifyActions(List<ManifestItem> manifestItems, Dictionary<string, CatalogItem> catalogMap,
                        ItemFilterService? itemFilterService = null)
    {
        var toInstall = new List<CatalogItem>();
        var toUpdate = new List<CatalogItem>();
        var toUninstall = new List<CatalogItem>();
        // Items LoopGuard refused this run. Tracked separately so CollectSessionItems
        // can stamp them as Warning instead of letting them fall through to "Installed".
        var loopSuppressed = new List<(CatalogItem, string, string?, bool)>();

        var sysArch = StatusService.GetSystemArchitecture();

        // Log manifest and catalog stats for debugging
        ConsoleLogger.Detail($"    IdentifyActions: {manifestItems.Count} manifest items, {catalogMap.Count} catalog items");

        foreach (var item in manifestItems)
        {
            if (string.IsNullOrEmpty(item.Name)) continue;

            // Go parity: pkg/filter applies the --item filter to manifestItems
            // BEFORE status checking, so a targeted run only status-checks the
            // named items (their dependency closure is resolved and checked
            // separately by ResolveDependencies). Skipping the rest here turns a
            // self-serve install from a full ~85-item CheckStatus sweep — the
            // "starting from scratch / taking forever" slowness — into a handful
            // of checks. Non-targeted managed items default to "Installed" in the
            // session report (SessionItemStatusResolver), which is what they were.
            if (itemFilterService?.HasFilter == true
                && !itemFilterService.Items.Contains(item.Name))
            {
                continue;
            }

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
                case "default":
                    // Gate install-like actions on OS-version and agent-version eligibility.
                    // Uninstall is intentionally excluded so an item that becomes unsupported
                    // on the current OS or requires a newer agent can still be removed.
                    if (!IsEligibleForOsVersion(catalogItem, out var osReason, out var osReasonCode))
                    {
                        ConsoleLogger.Info($"Skipping {item.Name}: {osReason}");
                        _sessionLogger?.LogStatusCheck(
                            catalogItem.Name,
                            catalogItem.Version,
                            "skipped",
                            osReason,
                            osReasonCode,
                            DetectionMethod.None,
                            null,
                            false);
                        break;
                    }

                    if (!IsEligibleForAgentVersion(catalogItem, out var agentSkipReason, out var agentSkipCode))
                    {
                        ConsoleLogger.Info($"Skipping {item.Name}: {agentSkipReason}");
                        _sessionLogger?.LogStatusCheck(
                            catalogItem.Name,
                            catalogItem.Version,
                            "skipped",
                            agentSkipReason,
                            agentSkipCode,
                            DetectionMethod.None,
                            null,
                            false);
                        break;
                    }

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
                        // --item targets specific packages by name; bypass LoopGuard for those
                        // (run-scoped only — persistent suppression state is left intact so
                        // future runs without --item still honor it).
                        // OnDemand items also bypass: by design they (re)install every run, so
                        // the loop guard would otherwise suppress legitimate repeat installs.
                        // Recurring items bypass for the same reason: idempotent maintenance
                        // scripts (cache clears, time sync, account checks) are meant to run
                        // every session, so their repeated same-version runs are not a loop.
                        var bypassLoopGuard = catalogItem.OnDemand
                            || catalogItem.Recurring
                            || (itemFilterService != null
                                && itemFilterService.HasFilter
                                && itemFilterService.Items.Contains(catalogItem.Name));

                        if (bypassLoopGuard)
                        {
                            var bypassReason = catalogItem.OnDemand ? "OnDemand"
                                : catalogItem.Recurring ? "recurring" : "--item";
                            var msg = $"{bypassReason}: bypassing LoopGuard for '{catalogItem.Name}'";
                            ConsoleLogger.Info(msg);
                            _sessionLogger?.Log("INFO", msg);
                        }

                        // Check LoopGuard before adding to install list
                        if (_loopGuard != null && !bypassLoopGuard)
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
                                loopSuppressed.Add((catalogItem, loopReason, status.InstalledVersion, status.IsUpdate));
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

                case "optional":
                    // Optional items are normally user-selected via the GUI.
                    // But if force_install_after_date has passed, enforce installation.
                    if (catalogItem.ForceInstallAfterDate != null && DateTime.Now >= catalogItem.ForceInstallAfterDate.Value)
                    {
                        // Gate forced-optional installs on OS-version and agent-version eligibility.
                        if (!IsEligibleForOsVersion(catalogItem, out var optOsReason, out var optOsReasonCode))
                        {
                            ConsoleLogger.Info($"Skipping forced optional {item.Name}: {optOsReason}");
                            _sessionLogger?.LogStatusCheck(
                                catalogItem.Name,
                                catalogItem.Version,
                                "skipped",
                                optOsReason,
                                optOsReasonCode,
                                DetectionMethod.None,
                                null,
                                false);
                            break;
                        }

                        if (!IsEligibleForAgentVersion(catalogItem, out var optAgentReason, out var optAgentCode))
                        {
                            ConsoleLogger.Info($"Skipping forced optional {item.Name}: {optAgentReason}");
                            _sessionLogger?.LogStatusCheck(
                                catalogItem.Name,
                                catalogItem.Version,
                                "skipped",
                                optAgentReason,
                                optAgentCode,
                                DetectionMethod.None,
                                null,
                                false);
                            break;
                        }

                        var optStatus = _statusService.CheckStatus(catalogItem, "install", _config.CachePath);
                        ConsoleLogger.Detail($"    CheckStatus for {item.Name} (forced deadline): NeedsAction={optStatus.NeedsAction}, Status={optStatus.Status}");

                        _sessionLogger?.LogStatusCheck(
                            catalogItem.Name, catalogItem.Version, optStatus.Status,
                            optStatus.Reason, optStatus.ReasonCode, optStatus.DetectionMethod,
                            optStatus.InstalledVersion, optStatus.NeedsAction);

                        if (optStatus.NeedsAction)
                        {
                            ConsoleLogger.Info($"    -> force_install_after_date {catalogItem.ForceInstallAfterDate.Value:yyyy-MM-dd} has passed, forcing install of optional item {item.Name}");
                            _sessionLogger?.Log("INFO", $"Forcing install of optional item {item.Name}: deadline {catalogItem.ForceInstallAfterDate.Value:yyyy-MM-dd} has passed");
                            _sessionLogger?.LogStatusCheck(
                                catalogItem.Name, catalogItem.Version, "pending",
                                $"force_install_after_date {catalogItem.ForceInstallAfterDate.Value:yyyy-MM-dd} has passed",
                                Cimian.Core.Models.StatusReasonCode.ForceInstallDeadline,
                                Cimian.Core.Models.DetectionMethod.None,
                                optStatus.InstalledVersion, true);

                            if (optStatus.IsUpdate)
                                toUpdate.Add(catalogItem);
                            else
                                toInstall.Add(catalogItem);
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

        return (toInstall, toUpdate, toUninstall, loopSuppressed);
    }

    /// <summary>
    /// Identifies packages installed by Cimian (in ManagedInstalls registry) that are no longer
    /// referenced in any manifest. These are candidates for automatic removal.
    /// </summary>
    private List<CatalogItem> IdentifyAutoRemoveItems(
        List<ManifestItem> manifestItems, Dictionary<string, CatalogItem> catalogMap)
    {
        var autoRemove = new List<CatalogItem>();

        var manifestedNames = new HashSet<string>(
            manifestItems.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            using var managedKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\ManagedInstalls");
            if (managedKey == null) return autoRemove;

            foreach (var name in managedKey.GetSubKeyNames())
            {
                if (manifestedNames.Contains(name)) continue;

                using var itemKey = managedKey.OpenSubKey(name);
                var version = itemKey?.GetValue("Version")?.ToString() ?? "0";

                if (catalogMap.TryGetValue(name.ToLowerInvariant(), out var catalogItem))
                {
                    if (catalogItem.IsUninstallable())
                    {
                        autoRemove.Add(catalogItem);
                    }
                    else
                    {
                        ConsoleLogger.Detail($"    AutoRemove: skipping {name} (not uninstallable)");
                    }
                }
                else
                {
                    ConsoleLogger.Detail($"    AutoRemove: skipping {name} (not in catalog)");
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"AutoRemove: failed to enumerate ManagedInstalls registry: {ex.Message}");
        }

        return autoRemove;
    }

    /// <summary>
    /// Identifies Cimian-installed packages eligible for stale-usage removal:
    /// opted in via unused_software_removal_info, unattended-uninstallable,
    /// and untouched by every user on the device past the threshold per the
    /// usage data source. Candidates come from the ManagedInstalls registry,
    /// scoped per <see cref="StaleUsageEvaluator.ClassifyScope"/>:
    /// admin-manifested items are never touched; self-serve installs get their
    /// subscription cleared (so the removal sticks and the item stays offered
    /// in MSC), and unsubscribed optional items and orphans uninstall directly.
    /// </summary>
    private async Task<List<CatalogItem>> IdentifyStaleUsageItemsAsync(
        List<ManifestItem> manifestItems,
        Dictionary<string, CatalogItem> catalogMap,
        List<CatalogItem> alreadyQueued,
        IUsageDataSource usageSource)
    {
        var stale = new List<CatalogItem>();

        if (!usageSource.IsAvailable)
        {
            ConsoleLogger.Info($"StaleUsage: usage source '{usageSource.SourceName}' unavailable - skipping pass");
            return stale;
        }

        var freshness = usageSource.GetDataFreshnessDays();
        if (freshness > _config.UsageStaleUninstallMaxSourceStalenessDays)
        {
            ConsoleLogger.Warn(
                $"StaleUsage: usage data is {freshness} day(s) old (max {_config.UsageStaleUninstallMaxSourceStalenessDays}) - skipping pass");
            return stale;
        }

        // manifestItems is already deduplicated, so one entry per name carries
        // the winning action (and the IsSelfServe flag from the merge).
        var manifestByName = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var mi in manifestItems)
        {
            if (!string.IsNullOrEmpty(mi.Name)) manifestByName.TryAdd(mi.Name, mi);
        }
        var queuedNames = new HashSet<string>(
            alreadyQueued.Select(i => i.Name),
            StringComparer.OrdinalIgnoreCase);

        // One service for the whole pass: its YAML serializers and file lock are
        // instance-scoped, so reusing a single instance both avoids rebuilding
        // that state per item and keeps every subscription mutation behind one
        // semaphore. Only constructed when the pass actually has work to do.
        SelfServiceManifestService? selfServiceManifest = null;

        try
        {
            using var managedKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\ManagedInstalls");
            if (managedKey == null) return stale;

            foreach (var name in managedKey.GetSubKeyNames())
            {
                if (queuedNames.Contains(name)) continue;

                var scope = StaleUsageEvaluator.ClassifyScope(manifestByName.GetValueOrDefault(name));
                if (scope == StaleUsageScope.Protected) continue;

                if (!catalogMap.TryGetValue(name.ToLowerInvariant(), out var catalogItem)) continue;

                var decision = StaleUsageEvaluator.Evaluate(
                    catalogItem, usageSource, _config.UsageStaleUninstallMinimumHistoryDays);

                switch (decision.Outcome)
                {
                    case StaleUsageOutcome.Stale:
                        ConsoleLogger.Info(
                            $"    -> StaleUsage: {catalogItem.Name} ({scope}) untouched {decision.DaysSinceLastUsed:F0} day(s) (threshold {decision.ThresholdDays}) - queuing uninstall");
                        // "pending" (not a custom status) so NormalizeItemStatus maps it
                        // for downstream consumers; the reason code marks it a removal.
                        _sessionLogger?.LogStatusCheck(
                            catalogItem.Name, catalogItem.Version, "pending",
                            $"untouched {decision.DaysSinceLastUsed:F0} day(s), threshold {decision.ThresholdDays}, source {usageSource.SourceName}, scope {scope}",
                            StatusReasonCode.StaleUsageUninstall,
                            DetectionMethod.ReportMateUsage,
                            needsAction: true);

                        // Self-serve and optional items must also drop any self-serve
                        // subscription — same effect as the user clicking Remove in
                        // MSC — or the next run's merge would reinstall the item.
                        // ManagedUpdate items only get lingering install requests
                        // cleared (no removal request: the admin's update policy
                        // would veto-log it every run). Check-only must not mutate
                        // state, so only report there.
                        if (scope != StaleUsageScope.Orphan)
                        {
                            if (_checkOnly)
                            {
                                ConsoleLogger.Info($"    StaleUsage: would clear self-serve subscription for {catalogItem.Name} (check-only)");
                            }
                            else if (scope == StaleUsageScope.ManagedUpdate)
                            {
                                selfServiceManifest ??= new SelfServiceManifestService();
                                await selfServiceManifest.RemoveRequestAsync(catalogItem.Name);
                            }
                            else
                            {
                                selfServiceManifest ??= new SelfServiceManifestService();
                                await selfServiceManifest.AddRemovalRequestAsync(catalogItem.Name);
                                ConsoleLogger.Info($"    StaleUsage: cleared self-serve subscription for {catalogItem.Name} (still available in MSC)");
                            }
                        }
                        stale.Add(catalogItem);
                        break;

                    case StaleUsageOutcome.NoUsageData:
                        ConsoleLogger.Detail($"    StaleUsage: {catalogItem.Name} has no usage data - skipping (fail-safe)");
                        _sessionLogger?.LogStatusCheck(
                            catalogItem.Name, catalogItem.Version, "installed",
                            "stale-usage check skipped: no usage data for any tracked executable",
                            StatusReasonCode.StaleUsageSkippedNoData,
                            DetectionMethod.ReportMateUsage);
                        break;

                    case StaleUsageOutcome.InsufficientHistory:
                        ConsoleLogger.Detail($"    StaleUsage: {catalogItem.Name} - insufficient usage history on device - skipping");
                        _sessionLogger?.LogStatusCheck(
                            catalogItem.Name, catalogItem.Version, "installed",
                            $"stale-usage check skipped: device history {usageSource.GetHistoryDays()} day(s) below minimum",
                            StatusReasonCode.StaleUsageSkippedInsufficientHistory,
                            DetectionMethod.ReportMateUsage);
                        break;

                    case StaleUsageOutcome.RecentlyUsed:
                        ConsoleLogger.Detail(
                            $"    StaleUsage: {catalogItem.Name} used {decision.DaysSinceLastUsed:F0} day(s) ago (threshold {decision.ThresholdDays}) - keeping");
                        break;

                    case StaleUsageOutcome.NotUnattended:
                    case StaleUsageOutcome.NotUninstallable:
                    case StaleUsageOutcome.NoTrackedExecutables:
                        ConsoleLogger.Detail($"    StaleUsage: {catalogItem.Name} skipped ({decision.Outcome})");
                        break;

                    // NotOptedIn is the overwhelmingly common case - stay silent.
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"StaleUsage: failed to enumerate ManagedInstalls registry: {ex.Message}");
        }

        return stale;
    }

    /// <summary>
    /// Expands the manifest's install/update list along both <c>requires</c> and
    /// <c>update_for</c> to the transitive closure, status-checks every dep, and
    /// promotes anything that needs action into the toUpdate list.
    /// </summary>
    /// <remarks>
    /// Runs in both checkonly and real install paths (called before the checkonly
    /// exit). Replaces the prior one-shot <c>update_for</c>-only walk, which
    /// missed two cases:
    ///   1. A stale <c>requires:</c> dep when the parent is already at the catalog
    ///      version (e.g. ManageUsersPrefs current, ManageUsers stale) — the prior
    ///      code only ran requires-resolution from inside the install path, which
    ///      is unreachable when the parent's CheckStatus returns NeedsAction=False.
    ///   2. Chained <c>update_for</c> beyond depth 1, because the prior code only
    ///      iterated the original manifest list, not the deps it added.
    /// The closure walk lives in <see cref="CatalogService.BuildDependencyClosure"/>;
    /// this method does the I/O (status check, manifest mutation).
    /// </remarks>
    private void ResolveDependencies(
        List<ManifestItem> manifestItems,
        Dictionary<string, CatalogItem> catalogMap,
        List<CatalogItem> itemsToProcess,
        ItemFilterService? itemFilterService = null)
    {
        LogInfo("Resolving dependencies (requires and update_for)...");

        var existingNames = new HashSet<string>(
            manifestItems.Select(m => m.Name.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        // Map name → set of actions already declared by the manifest, so we can
        // detect deps that conflict with explicit uninstall/profile/app entries.
        // A single name may appear in multiple manifest items (e.g. install +
        // uninstall in different inputs); the explicit non-install action wins.
        var manifestActions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mi in manifestItems)
        {
            var action = mi.Action?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action)) continue;
            if (!manifestActions.TryGetValue(mi.Name, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                manifestActions[mi.Name] = set;
            }
            set.Add(action);
        }

        // Seeds: every manifest item whose intent is install/update. Action comes
        // from manifest intent (managed_installs/managed_updates), not current
        // install state, so up-to-date items are still walked for stale deps.
        // On a targeted (--item) run, only seed the dependency walk from the
        // named items. Otherwise ResolveDependencies would rebuild the full
        // closure from every install/update manifest item and CheckStatus each
        // one — re-introducing the very sweep the IdentifyActions filter skips.
        var seedNames = manifestItems
            .Where(m => m.Action?.ToLowerInvariant() == "install" || m.Action?.ToLowerInvariant() == "update")
            .Where(m => itemFilterService?.HasFilter != true || itemFilterService.Items.Contains(m.Name))
            .Select(m => m.Name)
            .ToList();

        LogDetail($"    Resolving deps for {seedNames.Count} manifest item(s)");

        var deps = CatalogService.BuildDependencyClosure(seedNames, catalogMap);

        foreach (var depName in deps)
        {
            var depKey = depName.ToLowerInvariant();
            if (!catalogMap.TryGetValue(depKey, out var depItem))
            {
                LogDetail($"    Skipping {depName} - not found in catalog");
                continue;
            }

            // Skip deps that the manifest has already claimed with a conflicting
            // action: explicit uninstall must not be reversed by a transitive
            // install; MDM-managed profile/app items are handled externally.
            if (manifestActions.TryGetValue(depItem.Name, out var actions)
                && (actions.Contains("uninstall") || actions.Contains("profile") || actions.Contains("app")))
            {
                LogInfo($"Skipping dependency {depItem.Name}: manifest action [{string.Join(",", actions)}] takes precedence");
                continue;
            }

            var status = _statusService.CheckStatus(depItem, "install", _config.CachePath);

            LogInfo($"Dependency {depItem.Name} v{depItem.Version}: needsAction={status.NeedsAction} ({status.Reason})");

            if (!existingNames.Contains(depKey))
            {
                manifestItems.Add(new ManifestItem
                {
                    Name = depItem.Name,
                    Action = "install",
                    SourceManifest = "dependency"
                });
                existingNames.Add(depKey);
            }

            if (status.NeedsAction
                && !itemsToProcess.Any(i => i.Name.Equals(depItem.Name, StringComparison.OrdinalIgnoreCase)))
            {
                itemsToProcess.Add(depItem);
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
            LogInfo($"Dependency resolution complete: {depCount} dependency item(s) tracked");
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

    private async Task<List<ItemOutcome>> PerformInstallationsAsync(
        List<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        LogInfo($"Installing/updating {items.Count} items with dependency processing...");

        var outcomes = new List<ItemOutcome>();
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

        // Seed every row in the GUI before work starts.
        foreach (var item in items)
        {
            ReportItemStatus(item.Name, "pending");
        }

        var downloadCount = 0;
        var downloadProgress = new Progress<(string ItemName, double Percent)>(p =>
        {
            // Report which item is being downloaded with version info
            var matchingItem = items.FirstOrDefault(i => i.Name == p.ItemName);
            var version = matchingItem?.Version;
            var label = !string.IsNullOrEmpty(version) ? $"{p.ItemName} {version}" : p.ItemName;

            if (p.Percent <= 0)
            {
                // Starting a new item download
                downloadCount++;
                ReportItemStatus(p.ItemName, "downloading");
                ReportDetail($"Downloading {label} ({downloadCount}/{items.Count})");
            }
        });
        var downloadedPaths = await _downloadService.DownloadItemsAsync(items, downloadProgress, cancellationToken);

        // Anything with a resolved local path is on disk (downloaded now or
        // already cached) — flip those rows to downloaded.
        foreach (var item in items)
        {
            if (downloadedPaths.ContainsKey(item.Name))
            {
                ReportItemStatus(item.Name, "downloaded");
            }
        }

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

            if (_userStop.IsCancellationRequested)
            {
                LogInfo("Stop requested from GUI - aborting before next item");
                ReportStatus("Cancelled");
                break;
            }

            itemIndex++;
            var progressPercent = (itemIndex * 100) / totalItems;
            var installLabel = !string.IsNullOrEmpty(item.Version)
                ? $"{item.Name} {item.Version}" : item.Name;
            ReportItemStatus(item.Name, "installing");
            ReportDetail($"Installing {installLabel} ({itemIndex}/{totalItems})");
            ReportPercent(progressPercent);

            // Skip if already processed (may have been installed as a dependency)
            if (installedItems.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                LogDetail($"Skipping {item.Name}: already installed as dependency");
                ReportItemStatus(item.Name, "installed");
                successCount++;
                continue;
            }

            var success = await ProcessInstallWithDependenciesAsync(
                item.Name,
                installedItems,
                scheduledItems,
                downloadedPaths,
                outcomes,
                cancellationToken);

            var failureDetail = success ? null : SummarizeFailure(
                outcomes.LastOrDefault(o =>
                    string.Equals(o.Name, item.Name, StringComparison.OrdinalIgnoreCase) && !o.Success)?.ErrorMessage);
            ReportItemStatus(item.Name, success ? "installed" : "failed", failureDetail);

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
        return outcomes;
    }

    /// <summary>
    /// Downloads optional items marked with precache=true to local cache without installing.
    /// Munki parity: precache key causes the installer to be downloaded proactively
    /// so it's ready when the user requests the item from Managed Software Center.
    /// </summary>
    private async Task PrecacheOptionalItemsAsync(
        List<ManifestItem> manifestItems,
        Dictionary<string, CatalogItem> catalogMap,
        CancellationToken cancellationToken)
    {
        var precacheItems = new List<CatalogItem>();

        foreach (var mi in manifestItems)
        {
            if (string.IsNullOrEmpty(mi.Name)) continue;
            if (!string.Equals(mi.Action, "optional", StringComparison.OrdinalIgnoreCase)) continue;

            var key = mi.Name.ToLowerInvariant();
            if (!catalogMap.TryGetValue(key, out var cat)) continue;
            if (!cat.Precache) continue;

            // Skip script-only items (no installer to download)
            if (string.IsNullOrEmpty(cat.Installer?.Location)) continue;

            // Skip if already cached
            var cachePath = _downloadService.GetCachePath(cat);
            if (File.Exists(cachePath)) continue;

            precacheItems.Add(cat);
        }

        if (precacheItems.Count == 0) return;

        LogInfo("----------------------------------------------------------------------");
        LogInfo("PRECACHING OPTIONAL ITEMS");
        LogInfo("----------------------------------------------------------------------");
        LogInfo($"Precaching {precacheItems.Count} optional item(s)...");
        _sessionLogger?.Log("INFO", $"Precaching {precacheItems.Count} optional items");

        foreach (var item in precacheItems)
        {
            if (cancellationToken.IsCancellationRequested) break;

            LogInfo($"    Precaching: {item.Name} v{item.Version}");
            var path = await _downloadService.DownloadItemAsync(item, cancellationToken: cancellationToken);

            if (!string.IsNullOrEmpty(path))
            {
                LogInfo($"    Precached: {item.Name} -> {path}");
                _sessionLogger?.Log("INFO", $"Precached {item.Name} v{item.Version}");
            }
            else
            {
                ConsoleLogger.Warn($"Failed to precache {item.Name}");
                _sessionLogger?.Log("WARN", $"Failed to precache {item.Name} v{item.Version}");
            }
        }
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
        List<ItemOutcome> outcomes,
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

        if (!IsEligibleForOsVersion(item, out var osReason, out var osReasonCode))
        {
            LogInfo($"Skipping {item.Name}: {osReason}");
            _sessionLogger?.LogStatusCheck(
                item.Name,
                item.Version,
                "skipped",
                osReason,
                osReasonCode,
                DetectionMethod.None,
                null,
                false);
            return true;
        }

        if (!IsEligibleForAgentVersion(item, out var agentSkipReason, out var agentSkipCode))
        {
            LogInfo($"Skipping {item.Name}: {agentSkipReason}");
            _sessionLogger?.LogStatusCheck(
                item.Name,
                item.Version,
                "skipped",
                agentSkipReason,
                agentSkipCode,
                DetectionMethod.None,
                null,
                false);
            return true;
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

                // Verify the dependency actually needs install — defer to its own
                // installs array / installcheck_script / receipts (Munki parity).
                // CheckDependencies() only consults in-memory lists; without this the
                // dep gets reinstalled every run when not yet recorded as installed.
                var depStatus = _statusService.CheckStatus(depItem, "install", _config.CachePath);
                if (!depStatus.NeedsAction)
                {
                    LogDetail($"Dependency {depName} already installed: {depStatus.Reason}");
                    if (!installedItems.Contains(depItem.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        installedItems.Add(depItem.Name);
                    }
                    if (!scheduledItems.Contains(dep, StringComparer.OrdinalIgnoreCase))
                    {
                        scheduledItems.Add(dep);
                    }
                    continue;
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
                if (!await ProcessInstallWithDependenciesAsync(dep, installedItems, newScheduled, downloadedPaths, outcomes, cancellationToken))
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

        // Guard: file-based installer types must have a valid downloaded file
        var installerType = (item.Installer?.Type ?? "").ToLowerInvariant();
        var requiresFile = installerType is not ("nopkg" or "script");
        if (requiresFile && string.IsNullOrEmpty(localFile))
        {
            var msg = $"Download missing for {item.Name} — cannot install {installerType} without a local file";
            ConsoleLogger.Error(msg);
            _sessionLogger?.Log("ERROR", msg);
            _sessionLogger?.LogInstall(item.Name, item.Version, "install", "failed", msg);
            outcomes.Add(new ItemOutcome(item.Name, item.Version, "install", false, msg, DateTime.UtcNow));
            return false;
        }

        var (success, output, warningMessage) = await _installerService.InstallAsync(item, localFile ?? "", cancellationToken);
        outcomes.Add(new ItemOutcome(item.Name, item.Version, "install", success, success ? null : output, DateTime.UtcNow, warningMessage));

        if (success)
        {
            LogSuccess($"Installed: {item.Name} v{item.Version}");
            
            // Track restart_action (Munki parity: requires_restart check)
            if (RequiresRestart(item))
            {
                _restartNeeded = true;
                LogInfo($"Restart required after installing {item.Name} (restart_action: {item.RestartAction})");
                _sessionLogger?.Log("INFO", $"Restart required: {item.Name} (restart_action: {item.RestartAction})");
            }
            else if (RequiresLogout(item))
            {
                _logoutNeeded = true;
                LogInfo($"Logout required after installing {item.Name} (restart_action: {item.RestartAction})");
                _sessionLogger?.Log("INFO", $"Logout required: {item.Name} (restart_action: {item.RestartAction})");
            }
            
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

            // Record successful install for loop guard tracking. When the postinstall
            // self-reported a Warning via the CIMIAN-WARNING marker, this isn't a real
            // install attempt for loop-detection purposes — the script ran successfully
            // but the system is in a known-bad state awaiting external remediation
            // (e.g. SecureBoot, BIOS password). Counting these would suppress packages
            // whose only job is to keep flagging the unremediated state.
            _loopGuard?.RecordAttempt(
                item.Name,
                item.Version,
                success: true,
                ComputeCatalogFingerprint(item),
                selfReportedWarning: warningMessage != null);

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
                    if (!await ProcessInstallWithDependenciesAsync(updateItemName, installedItems, scheduledItems, downloadedPaths, outcomes, cancellationToken))
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
        List<ItemOutcome> outcomes,
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
                if (!await ProcessUninstallWithDependenciesAsync(depItem.Name, installedItems, outcomes, cancellationToken))
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
        ReportItemStatus(item.Name, "removing");
        var (success, output) = await _installerService.UninstallAsync(item, cancellationToken);
        outcomes.Add(new ItemOutcome(item.Name, item.Version, "remove", success, success ? null : output, DateTime.UtcNow));
        ReportItemStatus(item.Name, success ? "removed" : "failed", success ? null : SummarizeFailure(output));

        if (success)
        {
            LogSuccess($"Removed: {item.Name}");
            
            // Track restart_action for uninstalls (Munki parity)
            if (RequiresRestart(item))
            {
                _restartNeeded = true;
                LogInfo($"Restart required after removing {item.Name} (restart_action: {item.RestartAction})");
                _sessionLogger?.Log("INFO", $"Restart required: {item.Name} (restart_action: {item.RestartAction})");
            }
            else if (RequiresLogout(item))
            {
                _logoutNeeded = true;
                LogInfo($"Logout required after removing {item.Name} (restart_action: {item.RestartAction})");
                _sessionLogger?.Log("INFO", $"Logout required: {item.Name} (restart_action: {item.RestartAction})");
            }

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

    private async Task<List<ItemOutcome>> PerformUninstallsAsync(
        List<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        LogInfo($"Removing {items.Count} items with dependency processing...");

        var outcomes = new List<ItemOutcome>();
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

            if (_userStop.IsCancellationRequested)
            {
                LogInfo("Stop requested from GUI - aborting before next removal");
                ReportStatus("Cancelled");
                break;
            }

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
                outcomes,
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
        return outcomes;
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
    /// Gets the running agent version string from assembly metadata.
    /// CI builds embed yyyy.MM.dd.HHmm via AssemblyInformationalVersion, but
    /// dev builds may fall back to AssemblyFileVersion (e.g. 1.0.0.0).
    /// </summary>
    private static string GetFormattedVersion() => VersionService.GetRunningAgentVersion();
    
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
    /// Returns only manifest items present in the loaded catalog map for this device,
    /// and emits an INFO log for each item that's in the manifest but absent from that
    /// map. The map is built by <see cref="CatalogService.LoadCatalogsAsync"/>, which
    /// includes only items from the device's subscribed catalogs that also pass the
    /// architecture filter — both reasons can cause exclusion here.
    /// </summary>
    private List<ManifestItem> FilterToDeviceCatalog(
        List<ManifestItem> items,
        Dictionary<string, CatalogItem> catalogMap,
        string sectionLabel)
    {
        var inCatalog = new List<ManifestItem>(items.Count);
        foreach (var item in items)
        {
            if (catalogMap.TryGetValue(item.Name, out _))
            {
                inCatalog.Add(item);
            }
            else
            {
                // Mirror CatalogService.LoadCatalogsAsync defaulting: empty config
                // resolves to ["Production"] at load time, so report that here too.
                var effectiveCatalogs = _config.Catalogs.Count > 0
                    ? _config.Catalogs
                    : new List<string> { "Production" };
                var catalogList = string.Join(", ", effectiveCatalogs);
                LogInfo($"{sectionLabel}: '{item.Name}' is in the manifest but not in this device's loaded catalog(s) [{catalogList}] — excluded from display");
            }
        }
        return inCatalog;
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

        // Only filter when a catalog map was actually loaded. An empty map (e.g.
        // catalog download failed) keeps the prior behavior of showing the section
        // rather than silently hiding everything.
        if (catalogMap.Count > 0)
        {
            managedInstalls = FilterToDeviceCatalog(managedInstalls, catalogMap, "MANAGED INSTALLS");
            if (managedInstalls.Count == 0) return;
        }

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

        if (catalogMap.Count > 0)
        {
            managedUpdates = FilterToDeviceCatalog(managedUpdates, catalogMap, "MANAGED UPDATES");
            if (managedUpdates.Count == 0) return;
        }

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

        if (catalogMap.Count > 0)
        {
            managedUninstalls = FilterToDeviceCatalog(managedUninstalls, catalogMap, "MANAGED UNINSTALLS");
            if (managedUninstalls.Count == 0) return;
        }

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
    /// Reports a per-item lifecycle stage to the GUI (pending, downloading,
    /// downloaded, installing, installed, removing, removed, failed) so the
    /// Updates list can render live status on each row.
    /// </summary>
    private void ReportItemStatus(string itemName, string stage, string? detail = null)
    {
        _statusReporter?.ItemStatus(itemName, stage, detail);
    }

    /// <summary>
    /// Condenses an installer's raw failure output into a short, user-readable
    /// reason for the GUI and problem_items — exit code first, with a plain-English
    /// gloss for the common MSI codes so users can report a meaningful issue
    /// instead of "Will be installed" that never changes.
    /// </summary>
    private static string? SummarizeFailure(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput)) return null;

        var firstLine = rawOutput
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? rawOutput.Trim();

        var match = System.Text.RegularExpressions.Regex.Match(firstLine, @"-?\d{3,5}");
        if (match.Success && int.TryParse(match.Value, out var code))
        {
            var gloss = code switch
            {
                1603 => "fatal error during installation (often a leftover/partial prior install)",
                1618 => "another installation is already in progress",
                1619 => "installer package could not be opened",
                1620 => "installer package could not be opened (invalid)",
                1622 => "error opening installation log file",
                1625 => "installation forbidden by system policy",
                1638 => "another version of this product is already installed",
                1639 => "invalid command line argument",
                3010 => "success, but a restart is required",
                _ => null
            };
            return gloss != null ? $"Exit code {code} - {gloss}" : $"Exit code {code}";
        }

        // No numeric code (script/exe message) — surface a trimmed first line.
        return firstLine.Length > 200 ? firstLine[..200] + "..." : firstLine;
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
        Dictionary<string, CatalogItem> catalogMap,
        IReadOnlyDictionary<string, ItemOutcome> outcomesByName,
        IReadOnlyDictionary<string, (string Reason, string? InstalledVersion, bool WasUpdate)> loopSuppressedByName)
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

            // LoopGuard suppression takes precedence: the item was deliberately not
            // acted on, but it is broken — surface as Warning so dashboards flag it.
            // Keep the conditional short-circuited so suppressed items don't fall
            // through to outcome/plan logic below.
            if (loopSuppressedByName.TryGetValue(key, out var suppression))
            {
                items.Add(new SessionPackageInfo
                {
                    Name = mi.Name,
                    Version = version,
                    Status = "Warning",
                    ItemType = itemType,
                    DisplayName = displayName,
                    InstalledVersion = suppression.InstalledVersion,
                    WarningMessage = suppression.Reason,
                    StatusReason = suppression.Reason,
                    StatusReasonCode = Cimian.Core.Models.StatusReasonCode.LoopSuppressed,
                    DetectionMethod = Cimian.Core.Models.DetectionMethod.None,
                    // Mark as touched this run so DataExporter stamps last_seen_in_session.
                    // Distinct from install/update/remove so consumers can filter on it.
                    ActionPerformed = "loop_suppressed",
                    OutcomeTimestamp = DateTime.UtcNow
                });
                continue;
            }

            // Determine status — prefer the actual install/uninstall outcome over the
            // pre-install plan. Only fall back to "Pending …" when nothing was attempted.
            var hadOutcome = outcomesByName.TryGetValue(key, out var outcome) && outcome is not null;
            var status = SessionItemStatusResolver.Resolve(
                hadOutcome ? outcome : null,
                isPendingInstall:   toInstallNames.Contains(key),
                isPendingUpdate:    toUpdateNames.Contains(key),
                isPendingUninstall: toUninstallNames.Contains(key),
                manifestAction:     action);

            // A postinstall Warning outcome (exit code 2 or CIMIAN-WARNING: marker)
            // overrides the resolver's Installed/Pending status. The install itself
            // succeeded (Success=true) but operationally needs follow-up — e.g.
            // "BIOS password did not match" for firmware pkginfos. Hard failures
            // continue to flow through ErrorMessage on the existing path.
            var hasWarning = hadOutcome && !string.IsNullOrEmpty(outcome!.WarningMessage);
            var effectiveStatus = hasWarning ? "Warning" : status;

            items.Add(new SessionPackageInfo
            {
                Name = mi.Name,
                Version = hadOutcome && !string.IsNullOrEmpty(outcome!.Version) ? outcome.Version : version,
                Status = effectiveStatus,
                ItemType = itemType,
                DisplayName = displayName,
                ErrorMessage = hadOutcome && !outcome!.Success ? outcome.ErrorMessage : null,
                WarningMessage = hasWarning ? outcome!.WarningMessage : null,
                ActionPerformed = hadOutcome ? outcome!.Action : null,
                OutcomeTimestamp = hadOutcome ? outcome!.Timestamp : null
            });
        }

        _sessionLogger.SetCurrentSessionItems(items);

        // Surface LoopGuard suppressions for reports/loop_suppressed.json. Pulled from
        // LoopGuard rather than this run's list so packages suppressed in earlier runs
        // (still active backoff) also appear.
        if (_loopGuard != null)
        {
            _sessionLogger.SetCurrentLoopSuppressed(_loopGuard.GetSuppressedReport());
        }
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
        Dictionary<string, CatalogItem> catalogMap,
        IReadOnlyCollection<ItemOutcome>? outcomes = null)
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
                        // Always bookkeep the name as processed.
                        info.ProcessedInstalls.Add(mi.Name);

                        // managed_updates from the manifest is surfaced as a name list.
                        if (action == "update")
                            info.ManagedUpdates.Add(mi.Name);

                        // Re-check status at write time. The toInstall/toUpdate lists were
                        // computed before the install phase ran, so an item installed this
                        // session is no longer pending and must not be written as such.
                        var installCheck = cat != null
                            ? _statusService.CheckStatus(cat, action, _config.CachePath)
                            : null;
                        var installScheduled = toUpdateNames.Contains(key) || toInstallNames.Contains(key);
                        var needsAction = installCheck?.NeedsAction ?? installScheduled;

                        // Targeted runs (--item) schedule only the filtered subset, but a
                        // queued self-serve item outside the filter is still pending — keep
                        // its record so the Updates list survives partial writes.
                        var installPending = needsAction && (installScheduled || mi.PromotedFromOptional);

                        if (installPending)
                        {
                            // Needs install or update this session — full record on managed_installs.
                            var isUpdate = toUpdateNames.Contains(key) || (installCheck?.IsUpdate ?? false);
                            var item = BuildInstallInfoItem(mi.Name, cat);
                            item.Status = isUpdate ? "update-available" : "will-be-installed";
                            item.WillBeInstalled = true;
                            item.NeedsUpdate = isUpdate;
                            item.InstalledVersion = installCheck?.InstalledVersion;
                            item.Installed = !string.IsNullOrEmpty(installCheck?.InstalledVersion);
                            info.ManagedInstalls.Add(item);
                        }
                        // Else: already installed and up-to-date. No pending record is written;
                        // the name on processed_installs is the only trace.

                        // A user-requested optional item stays on the optional list for its
                        // whole lifecycle; its status tracks the pending action.
                        if (mi.PromotedFromOptional)
                        {
                            info.OptionalInstalls.Add(BuildOptionalInstallRecord(
                                mi.Name, cat, installPending ? "will-be-installed" : null));
                        }
                        break;

                    case "uninstall":
                        info.ProcessedUninstalls.Add(mi.Name);

                        // An item is a pending removal only while it is still installed.
                        // CheckStatus(install).NeedsAction == false means the item is
                        // present (same signal BuildOptionalInstallRecord uses for the
                        // Installed flag). Once the uninstall has actually taken it off
                        // the system it is no longer pending, and CleanUpSelfServeUninstalls
                        // (run after the removal phase) drops the managed_uninstalls entry
                        // so it is not re-queued every run. Keying on real install state
                        // avoids the inverted reading of an uninstall status check, where
                        // "not verifiable" (NeedsAction) would read as "still pending".
                        var removalScheduled = toUninstallNames.Contains(key);
                        var stillInstalled = cat != null
                            && !_statusService.CheckStatus(cat, "install", _config.CachePath).NeedsAction;
                        var removalPending = stillInstalled && (removalScheduled || mi.PromotedFromOptional);

                        if (removalPending)
                        {
                            var item = BuildInstallInfoItem(mi.Name, cat);
                            item.Status = "will-be-removed";
                            item.WillBeRemoved = true;
                            item.Installed = true;
                            info.Removals.Add(item);
                        }

                        // A user-removed optional item stays offered on the optional list.
                        if (mi.PromotedFromOptional)
                        {
                            info.OptionalInstalls.Add(BuildOptionalInstallRecord(
                                mi.Name, cat, removalPending ? "will-be-removed" : null));
                        }
                        break;

                    case "optional":
                        // Optional installs always appear — with installed/needs_update booleans
                        info.OptionalInstalls.Add(BuildOptionalInstallRecord(mi.Name, cat, null));
                        break;

                    case "default":
                        // Default installs: treated like managed_installs but only when not already installed.
                        // If already installed, they silently disappear (not re-enforced).
                        if (toInstallNames.Contains(key) &&
                            (cat == null || _statusService.CheckStatus(cat, "install", _config.CachePath).NeedsAction))
                        {
                            var defItem = BuildInstallInfoItem(mi.Name, cat);
                            defItem.Status = "will-be-installed";
                            defItem.WillBeInstalled = true;
                            info.ManagedInstalls.Add(defItem);
                        }
                        // If already installed, don't add to any list — default installs are not enforced after first install
                        break;
                }
            }

            // Populate featured items from manifest service
            if (_manifestService.FeaturedItems.Count > 0)
            {
                info.FeaturedItems = _manifestService.FeaturedItems.ToList();
                LogInfo($"Featured items: {string.Join(", ", info.FeaturedItems)}");
            }

            // Surface this session's install/uninstall failures as problem_items so
            // the GUI shows them (with the exit code) until the next successful
            // attempt — instead of silently reverting to "Will be installed". This
            // is the durable, user-reportable record of every failure code.
            if (outcomes != null)
            {
                foreach (var o in outcomes.Where(o => !o.Success))
                {
                    catalogMap.TryGetValue(o.Name.ToLowerInvariant(), out var pcat);
                    info.ProblemItems.Add(new InstallInfoProblem
                    {
                        Name = o.Name,
                        DisplayName = pcat?.DisplayName,
                        Version = string.IsNullOrEmpty(o.Version) ? pcat?.Version : o.Version,
                        ErrorMessage = SummarizeFailure(o.ErrorMessage)
                            ?? $"{o.Action} failed",
                        Note = o.ErrorMessage
                    });
                }
            }

            // Serialize and write
            var yaml = YamlUtils.SerializeInstallInfo(info);
            var path = Path.Combine(Path.GetDirectoryName(_config.CachePath) ?? CimianPaths.ManagedInstallsRoot, "InstallInfo.yaml");
            File.WriteAllText(path, yaml);

            LogInfo($"Wrote {path}");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to write InstallInfo.yaml: {ex.Message}");
            _sessionLogger?.Log("WARN", $"Failed to write InstallInfo.yaml: {ex.Message}");
        }
    }

    /// <summary>
    /// Consumes self-serve removal requests that have been satisfied: once the
    /// uninstaller for an item the user asked to remove has succeeded, its name is
    /// dropped from SelfServeManifest.managed_uninstalls. Without this a completed
    /// removal is re-queued every run and keeps showing as a pending removal.
    /// Keyed on the uninstall outcome, not a post-removal status check: NSIS
    /// uninstallers relaunch themselves from %TEMP% and the launched process exits
    /// before file deletion finishes, so an immediate "is it still installed?" check
    /// races the deletion. A failed removal (app still present) is retained and
    /// retried next run. Mirrors the reference clean_up_managed_uninstalls.
    /// </summary>
    private async Task CleanUpSelfServeUninstallsAsync(List<ItemOutcome> uninstallOutcomes)
    {
        if (_config.SkipSelfService) return;

        var removed = uninstallOutcomes
            .Where(o => o.Success)
            .Select(o => o.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removed.Count == 0) return;

        try
        {
            var svc = new SelfServiceManifestService();
            var manifest = await svc.LoadAsync();
            var before = manifest.ManagedUninstalls.Count;
            manifest.ManagedUninstalls = manifest.ManagedUninstalls
                .Where(n => !removed.Contains(n))
                .ToList();
            var cleared = before - manifest.ManagedUninstalls.Count;
            if (cleared > 0)
            {
                await svc.SaveAsync(manifest);
                LogInfo($"Self-serve: consumed {cleared} completed removal(s) from SelfServeManifest");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Self-serve uninstall cleanup failed: {ex.Message}");
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
            RestartAction = cat?.RestartAction,
            ForceInstallAfterDate = cat?.ForceInstallAfterDate,
        };
        return item;
    }

    /// <summary>
    /// Builds the optional_installs record for an item, with live install status.
    /// <paramref name="pendingStatus"/> ("will-be-installed" / "will-be-removed")
    /// overrides the natural status while a user-requested action is pending, so
    /// the software list reflects the in-flight state instead of a stale snapshot.
    /// </summary>
    private InstallInfoItem BuildOptionalInstallRecord(string name, CatalogItem? cat, string? pendingStatus)
    {
        var optItem = BuildInstallInfoItem(name, cat);
        if (cat != null)
        {
            var status = _statusService.CheckStatus(cat, "install", _config.CachePath);
            optItem.Installed = !status.NeedsAction;
            optItem.InstalledVersion = status.InstalledVersion;
            optItem.NeedsUpdate = status.IsUpdate;
            optItem.Status = pendingStatus ?? (status.NeedsAction
                ? (status.IsUpdate ? "update-available" : "not-installed")
                : "installed");

            // Check if installer is precached (already downloaded to local cache)
            if (!string.IsNullOrEmpty(cat.Installer?.Location))
            {
                var cachePath = _downloadService.GetCachePath(cat);
                optItem.Precached = File.Exists(cachePath);
            }
        }
        else
        {
            optItem.Status = pendingStatus ?? "not-installed";
        }

        optItem.WillBeInstalled = pendingStatus == "will-be-installed";
        optItem.WillBeRemoved = pendingStatus == "will-be-removed";
        return optItem;
    }

    internal static bool IsEligibleForAgentVersion(CatalogItem item, out string reason, out string reasonCode)
    {
        reason = string.Empty;
        reasonCode = string.Empty;

        if (string.IsNullOrWhiteSpace(item.MinimumCimianVersion))
        {
            return true;
        }

        var currentVersion = VersionService.GetRunningAgentVersion();
        if (VersionService.CompareVersions(currentVersion, item.MinimumCimianVersion) >= 0)
        {
            return true;
        }

        reason = $"requires Cimian {item.MinimumCimianVersion} or newer, running {currentVersion}";
        reasonCode = StatusReasonCode.AgentVersionTooOld;
        return false;
    }

    /// <summary>
    /// Checks whether a catalog item's RestartAction indicates a reboot is needed.
    /// Matches Munki's restartAction handling: "RequireRestart" and "RecommendRestart" both trigger reboot.
    /// </summary>
    private static bool RequiresRestart(CatalogItem item)
    {
        return item.RestartAction is "RequireRestart" or "RecommendRestart";
    }

    private static bool RequiresLogout(CatalogItem item)
    {
        return item.RestartAction is "RequireLogout";
    }

    /// <summary>
    /// True for any restart_action that would disrupt an active user session
    /// (Require/Recommend × Restart/Logout). Use this for auto-mode deferral
    /// decisions; do NOT use it to drive PerformLogoutAction/PerformRestartAction,
    /// which honour only the stricter Require* variants.
    /// </summary>
    private static bool WouldInterruptUser(CatalogItem item)
    {
        return item.RestartAction is "RequireRestart" or "RecommendRestart"
            or "RequireLogout" or "RecommendLogout";
    }

    /// <summary>
    /// Triggers a system restart after all install/uninstall operations complete.
    /// In auto/bootstrap mode: schedules a reboot with a 5-minute grace period.
    /// In interactive mode: logs a recommendation only.
    /// </summary>
    private void PerformRestartAction()
    {
        ConsoleLogger.Warn("One or more items require a system restart");
        _sessionLogger?.Log("INFO", "Restart required by installed/removed items");

        if (_auto || _isBootstrap)
        {
            ConsoleLogger.Warn("Scheduling system restart in 5 minutes...");
            _sessionLogger?.Log("INFO", "Scheduling system restart (auto/bootstrap mode)");

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/r /t 300 /c \"Cimian: System restarting to complete software updates\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
                ConsoleLogger.Info("System restart scheduled (300 second delay)");
                _sessionLogger?.Log("INFO", "System restart scheduled via shutdown.exe /r /t 300");
            }
            catch (Exception ex)
            {
                ConsoleLogger.Error($"Failed to schedule system restart: {ex.Message}");
                _sessionLogger?.Log("ERROR", $"Failed to schedule system restart: {ex.Message}");
            }
        }
        else
        {
            ConsoleLogger.Warn("Restart recommended - please restart your computer to complete updates");
            _sessionLogger?.Log("INFO", "Restart recommended (interactive mode - not forcing)");
        }
    }

    /// <summary>
    /// Forces a user logout after all install/uninstall operations complete.
    /// Matches Munki's RequireLogout behavior.
    /// </summary>
    private void PerformLogoutAction()
    {
        ConsoleLogger.Warn("One or more items require a user logout");
        _sessionLogger?.Log("INFO", "Logout required by installed/removed items");

        if (_auto || _isBootstrap)
        {
            ConsoleLogger.Warn("Forcing user logout to complete software updates...");
            _sessionLogger?.Log("INFO", "Forcing user logout (auto/bootstrap mode)");

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/l",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
                _sessionLogger?.Log("INFO", "User logout initiated via shutdown.exe /l");
            }
            catch (Exception ex)
            {
                ConsoleLogger.Error($"Failed to initiate user logout: {ex.Message}");
                _sessionLogger?.Log("ERROR", $"Failed to initiate user logout: {ex.Message}");
            }
        }
        else
        {
            ConsoleLogger.Warn("Logout recommended - please log out and back in to complete updates");
            _sessionLogger?.Log("INFO", "Logout recommended (interactive mode - not forcing)");
        }
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
