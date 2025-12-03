using Cimian.CLI.managedsoftwareupdate.Models;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Main orchestration service for managed software updates
/// Coordinates all the other services to perform updates
/// Migrated from Go cmd/managedsoftwareupdate/main.go
/// </summary>
public class UpdateEngine
{
    private readonly CimianConfig _config;
    private readonly ConfigurationService _configService;
    private readonly ManifestService _manifestService;
    private readonly CatalogService _catalogService;
    private readonly DownloadService _downloadService;
    private readonly InstallerService _installerService;
    private readonly StatusService _statusService;
    private readonly ScriptService _scriptService;

    private int _verbosity;
    private bool _isBootstrap;
    private bool _checkOnly;
    private bool _installOnly;
    private bool _auto;

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
    }

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
                if (!preflightSuccess)
                {
                    HandlePreflightFailure(preflightOutput);
                }
            }

            // Get manifest items
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

            // Download and load catalogs
            LogInfo("Loading catalogs...");
            var catalogMap = await _catalogService.LoadCatalogsAsync();
            LogInfo($"Loaded {catalogMap.Count} catalog items");

            // Validate cache
            _downloadService.ValidateAndCleanCache();

            // Identify actions needed
            var (toInstall, toUpdate, toUninstall) = IdentifyActions(manifestItems, catalogMap);

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

        foreach (var item in manifestItems)
        {
            if (string.IsNullOrEmpty(item.Name)) continue;

            var key = item.Name.ToLowerInvariant();
            
            if (!catalogMap.TryGetValue(key, out var catalogItem))
            {
                LogDebug($"Item not in catalog: {item.Name}");
                continue;
            }

            // Check architecture compatibility
            if (!CatalogService.SupportsArchitecture(catalogItem, sysArch))
            {
                LogInfo($"Skipping {item.Name}: architecture mismatch (system: {sysArch})");
                continue;
            }

            switch (item.Action.ToLowerInvariant())
            {
                case "install":
                    var status = _statusService.CheckStatus(catalogItem, "install", _config.CachePath);
                    if (status.NeedsAction)
                    {
                        if (status.Reason?.Contains("Update") == true)
                        {
                            toUpdate.Add(catalogItem);
                        }
                        else
                        {
                            toInstall.Add(catalogItem);
                        }
                    }
                    break;

                case "update":
                    var updateStatus = _statusService.CheckStatus(catalogItem, "update", _config.CachePath);
                    if (updateStatus.NeedsAction)
                    {
                        toUpdate.Add(catalogItem);
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
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("All software is up to date");
            Console.WriteLine("================================================================================");
            Console.WriteLine();
            return;
        }

        Console.WriteLine();
        Console.WriteLine("================================================================================");
        Console.WriteLine("PENDING ACTIONS");
        Console.WriteLine("================================================================================");

        if (toInstall.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("NEW INSTALLS:");
            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine($"{"Package Name",-30} | {"Version",-15} | {"Type",-10}");
            Console.WriteLine("----------------------------------------------------------------------");
            foreach (var item in toInstall)
            {
                Console.WriteLine($"{Truncate(item.Name, 28),-30} | {Truncate(item.Version, 13),-15} | {item.Installer.Type,-10}");
            }
        }

        if (toUpdate.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("UPDATES:");
            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine($"{"Package Name",-30} | {"Version",-15} | {"Type",-10}");
            Console.WriteLine("----------------------------------------------------------------------");
            foreach (var item in toUpdate)
            {
                Console.WriteLine($"{Truncate(item.Name, 28),-30} | {Truncate(item.Version, 13),-15} | {item.Installer.Type,-10}");
            }
        }

        if (toUninstall.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("REMOVALS:");
            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine($"{"Package Name",-30} | {"Version",-15}");
            Console.WriteLine("----------------------------------------------------------------------");
            foreach (var item in toUninstall)
            {
                Console.WriteLine($"{Truncate(item.Name, 28),-30} | {Truncate(item.Version, 13),-15}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {total} actions ({toInstall.Count} installs, {toUpdate.Count} updates, {toUninstall.Count} removals)");
        Console.WriteLine();
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

    private void LogInfo(string message)
    {
        if (_verbosity >= 1)
        {
            Console.WriteLine($"[INFO] {message}");
        }
    }

    private void LogDebug(string message)
    {
        if (_verbosity >= 2)
        {
            Console.WriteLine($"[DEBUG] {message}");
        }
    }

    private void LogSuccess(string message)
    {
        Console.WriteLine($"[SUCCESS] {message}");
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= maxLength) return value;
        return value[..(maxLength - 3)] + "...";
    }
}
