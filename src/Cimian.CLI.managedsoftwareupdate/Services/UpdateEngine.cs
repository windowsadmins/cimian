using System.Reflection;
using System.Runtime.InteropServices;
using Cimian.CLI.managedsoftwareupdate.Models;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Main orchestration service for managed software updates
/// Coordinates all the other services to perform updates
/// Migrated from Go cmd/managedsoftwareupdate/main.go
/// </summary>
public class UpdateEngine
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

    private int _verbosity;
    private bool _isBootstrap;
    private bool _checkOnly;
    private bool _installOnly;
    private bool _auto;
    
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
        CancellationToken cancellationToken = default)
    {
        _checkOnly = checkOnly;
        _installOnly = installOnly;
        _auto = auto;
        _isBootstrap = bootstrap || StatusService.IsBootstrapMode();
        _verbosity = verbosity;

        try
        {
            // Print verbose header if enabled
            if (_verbosity >= 1)
            {
                PrintVerboseHeader();
            }

            // Check admin privileges
            if (!StatusService.IsAdministrator())
            {
                Console.Error.WriteLine("[ERROR] Administrative access required.");
                return 1;
            }

            // Ensure directories exist
            _configService.EnsureDirectoriesExist(_config);

            // Clean pre-run directories
            CleanManifestsAndCatalogsPreRun();

            // Run preflight unless skipped
            if (!skipPreflight && !_config.NoPreflight)
            {
                var (preflightSuccess, preflightOutput) = await _scriptService.RunPreflightAsync(cancellationToken);
                
                // Print preflight output in verbose mode
                if (_verbosity >= 1 && !string.IsNullOrWhiteSpace(preflightOutput))
                {
                    Console.WriteLine(preflightOutput);
                }
                
                if (!preflightSuccess)
                {
                    HandlePreflightFailure(preflightOutput);
                }

                // Reload configuration after preflight - preflight script may have updated config
                // This is critical: preflight sets SoftwareRepoURL, ClientIdentifier, etc.
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
            }

            // Get manifest items
            if (_verbosity >= 1)
            {
                PrintSystemConfiguration();
            }
            
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

            LogInfo($"Retrieved {manifestItems.Count} manifest items");
            _allManifestItems = manifestItems;

            // Download and load catalogs
            LogInfo("Loading catalogs...");
            var catalogMap = await _catalogService.LoadCatalogsAsync();
            _catalogMap = catalogMap;
            LogInfo($"Loaded {catalogMap.Count} catalog items");

            // Validate cache
            _downloadService.ValidateAndCleanCache();

            // Identify actions needed
            var (toInstall, toUpdate, toUninstall) = IdentifyActions(manifestItems, catalogMap);

            // Print hierarchy and tables in checkonly mode (matches Go behavior - always shows this)
            if (_checkOnly)
            {
                PrintManifestHierarchy(manifestItems);
                PrintManagedInstallsTable(manifestItems, toInstall, toUpdate, catalogMap);
            }

            // Print summary
            PrintActionSummary(toInstall, toUpdate, toUninstall);

            // Exit if check-only mode
            if (_checkOnly)
            {
                LogInfo("Check-only mode - no actions performed");
                return 0;
            }

            // Exit if auto mode and user is active
            if (_auto && StatusService.IsUserActive())
            {
                LogInfo($"User is active (idle: {StatusService.GetIdleSeconds()}s). Skipping automatic updates.");
                return 0;
            }

            // Perform installations
            var installSuccess = true;
            if (toInstall.Count > 0 || toUpdate.Count > 0)
            {
                var allToInstall = toInstall.Concat(toUpdate).ToList();
                installSuccess = await PerformInstallationsAsync(allToInstall, cancellationToken);
            }

            // Perform uninstalls
            var uninstallSuccess = true;
            if (toUninstall.Count > 0)
            {
                uninstallSuccess = await PerformUninstallsAsync(toUninstall, cancellationToken);
            }

            // Run postflight unless skipped
            if (!skipPostflight && !_config.NoPostflight)
            {
                var (postflightSuccess, postflightOutput) = await _scriptService.RunPostflightAsync(cancellationToken);
                if (!postflightSuccess)
                {
                    Console.Error.WriteLine($"[WARNING] Postflight script failed: {postflightOutput}");
                }
            }

            // Clear bootstrap mode if successful
            if (_isBootstrap && installSuccess && uninstallSuccess)
            {
                StatusService.DisableBootstrapMode();
            }

            // Print final status
            if (installSuccess && uninstallSuccess)
            {
                LogSuccess("All operations completed successfully");
                return 0;
            }
            else
            {
                Console.Error.WriteLine("[WARNING] Some operations failed");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Update failed: {ex.Message}");
            if (_verbosity >= 2)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
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
        LogDebug($"IdentifyActions: {manifestItems.Count} manifest items, {catalogMap.Count} catalog items");

        foreach (var item in manifestItems)
        {
            if (string.IsNullOrEmpty(item.Name)) continue;

            var key = item.Name.ToLowerInvariant();
            
            if (!catalogMap.TryGetValue(key, out var catalogItem))
            {
                // Go behavior: items not in catalog with action=install are new installs
                // But we need the catalog item for installation - log this discrepancy
                LogDebug($"Item not in catalog: {item.Name} (action: {item.Action})");
                continue;
            }

            // Check architecture compatibility
            if (!CatalogService.SupportsArchitecture(catalogItem, sysArch))
            {
                LogInfo($"Skipping {item.Name}: architecture mismatch (system: {sysArch}, item version: {catalogItem.Version}, item arch: [{string.Join(",", catalogItem.SupportedArch)}])");
                continue;
            }

            switch (item.Action.ToLowerInvariant())
            {
                case "install":
                case "update":
                    // Go treats both install and update actions the same - calls CheckStatus
                    var status = _statusService.CheckStatus(catalogItem, item.Action.ToLowerInvariant(), _config.CachePath);
                    LogDebug($"CheckStatus for {item.Name}: NeedsAction={status.NeedsAction}, IsUpdate={status.IsUpdate}, Status={status.Status}, Reason={status.Reason}");
                    
                    if (status.NeedsAction)
                    {
                        // Go doesn't distinguish - all items needing action go to toUpdate
                        // unless they are truly new (not in catalog, which we already handled)
                        toUpdate.Add(catalogItem);
                        LogDebug($"  -> Adding to toUpdate");
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
                    LogDebug($"Skipping external item: {item.Name} (action: {item.Action})");
                    break;
            }
        }

        return (toInstall, toUpdate, toUninstall);
    }

    private void PrintActionSummary(
        List<CatalogItem> toInstall,
        List<CatalogItem> toUpdate,
        List<CatalogItem> toUninstall)
    {
        var total = toInstall.Count + toUpdate.Count + toUninstall.Count;

        if (total == 0)
        {
            Log();
            Log("================================================================================");
            Log("All software is up to date");
            Log("================================================================================");
            Log();
            return;
        }

        Log();
        Log("================================================================================");
        Log("PENDING ACTIONS");
        Log("================================================================================");

        if (toInstall.Count > 0)
        {
            Log();
            Log("NEW INSTALLS:");
            Log("----------------------------------------------------------------------");
            Log($"{"Package Name",-30} | {"Version",-15} | {"Type",-10}");
            Log("----------------------------------------------------------------------");
            foreach (var item in toInstall)
            {
                Log($"{Truncate(item.Name, 28),-30} | {Truncate(item.Version, 13),-15} | {item.Installer.Type,-10}");
            }
        }

        if (toUpdate.Count > 0)
        {
            Log();
            Log("UPDATES:");
            Log("----------------------------------------------------------------------");
            Log($"{"Package Name",-30} | {"Version",-15} | {"Type",-10}");
            Log("----------------------------------------------------------------------");
            foreach (var item in toUpdate)
            {
                Log($"{Truncate(item.Name, 28),-30} | {Truncate(item.Version, 13),-15} | {item.Installer.Type,-10}");
            }
        }

        if (toUninstall.Count > 0)
        {
            Log();
            Log("REMOVALS:");
            Log("----------------------------------------------------------------------");
            Log($"{"Package Name",-30} | {"Version",-15}");
            Log("----------------------------------------------------------------------");
            foreach (var item in toUninstall)
            {
                Log($"{Truncate(item.Name, 28),-30} | {Truncate(item.Version, 13),-15}");
            }
        }

        Log();
        Log($"Total: {total} actions ({toInstall.Count} installs, {toUpdate.Count} updates, {toUninstall.Count} removals)");
        Log();
    }

    private async Task<bool> PerformInstallationsAsync(
        List<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        LogInfo($"Installing/updating {items.Count} items...");

        var successCount = 0;
        var failCount = 0;

        // Download all items first
        var downloadedPaths = await _downloadService.DownloadItemsAsync(items, null, cancellationToken);

        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Check for blocking apps
            if (_installerService.CheckBlockingApps(item, out var runningApps))
            {
                Console.Error.WriteLine($"[WARNING] Skipping {item.Name}: blocking apps running: {string.Join(", ", runningApps)}");
                failCount++;
                continue;
            }

            // Get downloaded file path (may be null for script-only items)
            downloadedPaths.TryGetValue(item.Name, out var localFile);

            var (success, output) = await _installerService.InstallAsync(item, localFile ?? "", cancellationToken);

            if (success)
            {
                LogSuccess($"Installed: {item.Name} v{item.Version}");
                successCount++;
            }
            else
            {
                Console.Error.WriteLine($"[ERROR] Failed to install {item.Name}: {output}");
                failCount++;
            }
        }

        LogInfo($"Installation summary: {successCount} succeeded, {failCount} failed");
        return failCount == 0;
    }

    private async Task<bool> PerformUninstallsAsync(
        List<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        LogInfo($"Removing {items.Count} items...");

        var successCount = 0;
        var failCount = 0;

        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Check for blocking apps
            if (_installerService.CheckBlockingApps(item, out var runningApps))
            {
                Console.Error.WriteLine($"[WARNING] Skipping {item.Name}: blocking apps running: {string.Join(", ", runningApps)}");
                failCount++;
                continue;
            }

            var (success, output) = await _installerService.UninstallAsync(item, cancellationToken);

            if (success)
            {
                LogSuccess($"Removed: {item.Name}");
                successCount++;
            }
            else
            {
                Console.Error.WriteLine($"[ERROR] Failed to remove {item.Name}: {output}");
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

            if (Directory.Exists(_config.ManifestsPath))
            {
                Directory.Delete(_config.ManifestsPath, true);
            }
            Directory.CreateDirectory(_config.ManifestsPath);
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
                Console.Error.WriteLine($"[ERROR] Preflight script failed: {output}");
                Console.Error.WriteLine("[ERROR] Aborting due to PreflightFailureAction=abort");
                throw new Exception("Preflight script failed");

            case "warn":
                Console.Error.WriteLine($"[WARNING] Preflight script failed: {output}");
                Console.Error.WriteLine("[WARNING] Continuing despite preflight failure (PreflightFailureAction=warn)");
                break;

            default: // "continue"
                Console.Error.WriteLine($"[WARNING] Preflight script failed: {output}");
                Console.Error.WriteLine("[WARNING] Continuing with software updates (PreflightFailureAction=continue)");
                break;
        }
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// Log a line with timestamp prefix (no log level) - for verbose output like Go
    /// </summary>
    private void Log(string message = "")
    {
        Console.WriteLine($"[{Timestamp()}] {message}");
    }

    private void LogInfo(string message)
    {
        if (_verbosity >= 1)
        {
            // INFO has no color in Go (default terminal color)
            Console.WriteLine($"[{Timestamp()}] INFO  {message}");
        }
    }

    private void LogDebug(string message)
    {
        if (_verbosity >= 2)
        {
            // DEBUG is blue in Go
            Console.WriteLine($"{ColorBlue}[{Timestamp()}] DEBUG {message}{ColorReset}");
        }
    }

    private void LogSuccess(string message)
    {
        // SUCCESS is green in Go
        Console.WriteLine($"{ColorGreen}[{Timestamp()}] SUCCESS {message}{ColorReset}");
    }

    private void LogWarn(string message)
    {
        // WARN is yellow in Go
        Console.WriteLine($"{ColorYellow}[{Timestamp()}] WARN  {message}{ColorReset}");
    }

    private void LogError(string message)
    {
        // ERROR is red in Go
        Console.Error.WriteLine($"{ColorRed}[{Timestamp()}] ERROR {message}{ColorReset}");
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= maxLength) return value;
        return value[..(maxLength - 3)] + "...";
    }

    #region Verbose Output Methods (Go Parity)
    
    /// <summary>
    /// Prints the verbose header banner - matches Go output with timestamps
    /// </summary>
    private void PrintVerboseHeader()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        
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
            
            packageStatuses.Add((name, version, status));
        }
        
        // Sort: Installed first, then Pending Install, then Pending Update
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
        
        Log("----------------------------------------------------------------------");
        Log();
        
        // Print inventory summary
        var installedCount = packageStatuses.Count(p => p.Status == "Installed");
        var pendingInstallCount = packageStatuses.Count(p => p.Status == "Pending Install");
        var pendingUpdateCount = packageStatuses.Count(p => p.Status == "Pending Update");
        
        Log("INVENTORY SUMMARY");
        Log($"   Total managed items: {managedInstalls.Count}");
        Log($"   Installed: {installedCount} | Pending Install: {pendingInstallCount} | Pending Update: {pendingUpdateCount}");
        Log();
    }
    
    #endregion
}
