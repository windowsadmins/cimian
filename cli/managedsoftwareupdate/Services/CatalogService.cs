using System.Net.Http.Headers;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for loading and managing catalogs
/// Migrated from Go pkg/catalog
/// </summary>
public class CatalogService
{
    private readonly HttpClient _httpClient;
    private readonly IDeserializer _deserializer;
    private readonly CimianConfig _config;

    public CatalogService(CimianConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? CreateHttpClient(config);
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    private static HttpClient CreateHttpClient(CimianConfig config)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        // First try to get auth from registry (DPAPI encrypted)
        var authHeader = AuthService.GetAuthHeader();
        if (!string.IsNullOrEmpty(authHeader))
        {
            client.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Basic", authHeader);
        }
        // Fall back to config file auth if registry not available
        else if (!string.IsNullOrEmpty(config.AuthToken))
        {
            client.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", config.AuthToken);
        }
        else if (!string.IsNullOrEmpty(config.AuthUser) && !string.IsNullOrEmpty(config.AuthPassword))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.AuthUser}:{config.AuthPassword}"));
            client.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Basic", credentials);
        }

        client.DefaultRequestHeaders.Add("User-Agent", "Cimian-ManagedSoftwareUpdate/1.0");

        return client;
    }

    /// <summary>
    /// Downloads and loads all configured catalogs
    /// </summary>
    public async Task<Dictionary<string, CatalogItem>> LoadCatalogsAsync()
    {
        var items = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
        var catalogs = _config.Catalogs.Count > 0 ? _config.Catalogs : new List<string> { "Production" };
        var sysArch = GetSystemArchitecture();
        ConsoleLogger.Info($"    Loading catalogs catalogCount: {catalogs.Count} systemArch: {sysArch}");

        foreach (var catalogName in catalogs)
        {
            ConsoleLogger.Info($"    Downloading catalog: {catalogName}");
            var catalogItems = await DownloadCatalogAsync(catalogName);
            ConsoleLogger.Info($"    Downloaded catalog: {catalogName} itemCount: {catalogItems.Count}");
            foreach (var item in catalogItems)
            {
                // Filter by architecture first (Go parity)
                if (!SupportsArchitecture(item, sysArch))
                {
                    ConsoleLogger.Debug($"Skipping item (arch mismatch) item: {item.Name} arch: {string.Join(",", item.SupportedArch ?? new List<string>())} sysArch: {sysArch}");
                    continue;
                }
                
                var key = item.Name.ToLowerInvariant();
                // Keep highest version if duplicate
                if (!items.ContainsKey(key) || 
                    CompareVersions(item.Version, items[key].Version) > 0)
                {
                    if (items.ContainsKey(key))
                    {
                        ConsoleLogger.Debug($"Replaced catalog item name: {item.Name} oldVersion: {items[key].Version} newVersion: {item.Version}");
                    }
                    else
                    {
                        ConsoleLogger.Debug($"Added catalog item name: {item.Name} version: {item.Version} arch: {string.Join(" ", item.SupportedArch ?? new List<string>())}");
                    }
                    items[key] = item;
                }
                else
                {
                    ConsoleLogger.Debug($"Kept existing catalog item name: {item.Name} existingVersion: {items[key].Version} skippedVersion: {item.Version}");
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Downloads a specific catalog from the server
    /// </summary>
    public async Task<List<CatalogItem>> DownloadCatalogAsync(string catalogName)
    {
        var items = new List<CatalogItem>();
        var catalogUrl = $"{_config.SoftwareRepoURL.TrimEnd('/')}/catalogs/{catalogName}.yaml";
        var localPath = Path.Combine(_config.CatalogsPath, $"{catalogName}.yaml");
        ConsoleLogger.Debug($"Starting download url: {catalogUrl} destination: {localPath}");

        try
        {
            var response = await _httpClient.GetAsync(catalogUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                ConsoleLogger.Debug($"Download completed to temp file tempFile: {localPath}.downloading size: {content.Length}");
                
                // Save locally
                var dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(localPath, content);
                ConsoleLogger.Debug($"File saved successfully file: {localPath} size: {content.Length}");
                ConsoleLogger.Debug($"Download completed successfully file: {localPath}");
                ConsoleLogger.Debug($"Downloaded catalog: {catalogName}");

                items = ParseCatalog(content);
            }
            else
            {
                ConsoleLogger.Warn($"Failed to download catalog {catalogName}: {response.StatusCode}");
                // Try to load from local cache
                ConsoleLogger.Info($"    Falling back to local cache: {localPath}");
                items = LoadLocalCatalog(localPath);
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Error downloading catalog {catalogName}: {ex.Message}");
            // Try to load from local cache
            ConsoleLogger.Info($"    Falling back to local cache: {localPath}");
            items = LoadLocalCatalog(localPath);
        }

        return items;
    }

    /// <summary>
    /// Loads catalog from local file
    /// </summary>
    public List<CatalogItem> LoadLocalCatalog(string path)
    {
        if (!File.Exists(path))
        {
            ConsoleLogger.Debug($"    Local catalog not found: {path}");
            return new List<CatalogItem>();
        }

        try
        {
            ConsoleLogger.Debug($"    Loading local catalog: {path}");
            var yaml = File.ReadAllText(path);
            var items = ParseCatalog(yaml);
            ConsoleLogger.Debug($"    Loaded local catalog itemCount: {items.Count}");
            return items;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load local catalog {path}: {ex.Message}");
            return new List<CatalogItem>();
        }
    }

    /// <summary>
    /// Loads all local catalog items from the catalogs directory
    /// </summary>
    public Dictionary<string, CatalogItem> LoadLocalCatalogItems()
    {
        var items = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
        var catalogsPath = _config.CatalogsPath;

        if (!Directory.Exists(catalogsPath))
        {
            return items;
        }

        var sysArch = GetSystemArchitecture();

        foreach (var file in Directory.GetFiles(catalogsPath, "*.yaml"))
        {
            var catalogItems = LoadLocalCatalog(file);
            foreach (var item in catalogItems)
            {
                // Filter by architecture
                if (!SupportsArchitecture(item, sysArch))
                {
                    continue;
                }

                var key = item.Name.ToLowerInvariant();
                // Go parity: Keep highest version (Go uses DeduplicateCatalogItems which picks highest version)
                if (!items.ContainsKey(key) || 
                    CompareVersions(item.Version, items[key].Version) > 0)
                {
                    items[key] = item;
                }
            }
        }

        return items;
    }

    private List<CatalogItem> ParseCatalog(string yaml)
    {
        try
        {
            var wrapper = _deserializer.Deserialize<CatalogWrapper>(yaml);
            return wrapper?.Items ?? new List<CatalogItem>();
        }
        catch
        {
            // Try parsing as a list directly
            try
            {
                return _deserializer.Deserialize<List<CatalogItem>>(yaml) ?? new List<CatalogItem>();
            }
            catch
            {
                return new List<CatalogItem>();
            }
        }
    }

    /// <summary>
    /// Gets the system architecture
    /// </summary>
    public static string GetSystemArchitecture()
    {
        var arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
        return arch?.ToLowerInvariant() switch
        {
            "amd64" => "x64",
            "x86" => "x86",
            "arm64" => "arm64",
            "arm" => "arm",
            _ => "x64"
        };
    }

    /// <summary>
    /// Checks if an item supports the given architecture
    /// </summary>
    public static bool SupportsArchitecture(CatalogItem item, string architecture)
    {
        if (item.SupportedArch.Count == 0)
        {
            return true; // No restriction means all architectures
        }

        var normalizedArch = architecture.ToLowerInvariant() switch
        {
            "amd64" => "x64",
            "x86_64" => "x64",
            _ => architecture.ToLowerInvariant()
        };

        return item.SupportedArch.Any(a => 
            a.Equals(normalizedArch, StringComparison.OrdinalIgnoreCase) ||
            a.Equals("amd64", StringComparison.OrdinalIgnoreCase) && normalizedArch == "x64" ||
            a.Equals("x86_64", StringComparison.OrdinalIgnoreCase) && normalizedArch == "x64");
    }

    /// <summary>
    /// Compares two version strings
    /// Returns: -1 if v1 < v2, 0 if v1 == v2, 1 if v1 > v2
    /// </summary>
    public static int CompareVersions(string v1, string v2)
    {
        if (v1 == v2) return 0;
        if (string.IsNullOrEmpty(v1)) return -1;
        if (string.IsNullOrEmpty(v2)) return 1;

        var parts1 = v1.Split('.', '-', '_');
        var parts2 = v2.Split('.', '-', '_');

        var maxLen = Math.Max(parts1.Length, parts2.Length);

        for (int i = 0; i < maxLen; i++)
        {
            var p1 = i < parts1.Length ? ParseVersionPart(parts1[i]) : 0;
            var p2 = i < parts2.Length ? ParseVersionPart(parts2[i]) : 0;

            if (p1 < p2) return -1;
            if (p1 > p2) return 1;
        }

        return 0;
    }

    private static int ParseVersionPart(string part)
    {
        // Try to parse as integer
        if (int.TryParse(part, out var value))
        {
            return value;
        }

        // Handle prerelease tags
        var lowerPart = part.ToLowerInvariant();
        if (lowerPart.StartsWith("alpha")) return -3;
        if (lowerPart.StartsWith("beta")) return -2;
        if (lowerPart.StartsWith("rc")) return -1;
        if (lowerPart.StartsWith("release")) return 0;

        return 0;
    }

    /// <summary>
    /// Finds a catalog item by name
    /// </summary>
    public CatalogItem? FindItem(Dictionary<string, CatalogItem> catalog, string name)
    {
        var key = name.ToLowerInvariant();
        return catalog.TryGetValue(key, out var item) ? item : null;
    }

    /// <summary>
    /// Gets the full catalog map organized by version priority
    /// </summary>
    public Dictionary<int, Dictionary<string, CatalogItem>> GetFullCatalogMap()
    {
        var result = new Dictionary<int, Dictionary<string, CatalogItem>>();
        var localItems = LoadLocalCatalogItems();

        // Priority 0 = highest (Production)
        result[0] = localItems;

        return result;
    }

    #region Dependency Processing (Go parity: pkg/catalog/catalog.go)

    /// <summary>
    /// Searches for items that declare they are updates for the given item name.
    /// This handles updates that aren't simply later versions of the same item.
    /// For example, AdobeCameraRaw is an update for Adobe Photoshop.
    /// Migrated from Go: catalog.LookForUpdates() - catalog.go lines 181-207
    /// </summary>
    /// <param name="itemName">The item name to find updates for</param>
    /// <param name="catalog">The loaded catalog dictionary</param>
    /// <returns>List of catalog item names that are updates for the given item</returns>
    public static List<string> LookForUpdates(string itemName, Dictionary<string, CatalogItem> catalog)
    {
        var updateList = new List<string>();

        foreach (var kvp in catalog)
        {
            var catalogItem = kvp.Value;
            if (catalogItem.UpdateFor != null && catalogItem.UpdateFor.Count > 0)
            {
                foreach (var updateForItem in catalogItem.UpdateFor)
                {
                    if (string.Equals(updateForItem, itemName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!updateList.Contains(catalogItem.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            updateList.Add(catalogItem.Name);
                        }
                    }
                }
            }
        }

        return updateList;
    }

    /// <summary>
    /// Searches for updates for a specific version of an item.
    /// Since these can appear in manifests as item-version or item--version, we search for both.
    /// Migrated from Go: catalog.LookForUpdatesForVersion() - catalog.go lines 209-222
    /// </summary>
    public static List<string> LookForUpdatesForVersion(string itemName, string itemVersion, Dictionary<string, CatalogItem> catalog)
    {
        var nameAndVersion = $"{itemName}-{itemVersion}";
        var altNameAndVersion = $"{itemName}--{itemVersion}";

        var updateList = LookForUpdates(nameAndVersion, catalog);
        updateList.AddRange(LookForUpdates(altNameAndVersion, catalog));

        // Remove duplicates
        return updateList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Checks if all required dependencies for an item are installed or scheduled for install.
    /// Migrated from Go: catalog.CheckDependencies() - catalog.go lines 224-290
    /// </summary>
    /// <param name="item">The catalog item to check dependencies for</param>
    /// <param name="installedItems">List of currently installed item names</param>
    /// <param name="scheduledItems">List of items scheduled for installation</param>
    /// <returns>List of missing dependencies that need to be installed</returns>
    public static List<string> CheckDependencies(CatalogItem item, List<string> installedItems, List<string> scheduledItems)
    {
        if (item.Requires == null || item.Requires.Count == 0)
        {
            return new List<string>();
        }

        var missingDeps = new List<string>();
        var allAvailableItems = installedItems.Concat(scheduledItems).ToList();

        foreach (var reqItem in item.Requires)
        {
            var (reqName, reqVersion) = SplitNameAndVersion(reqItem);

            var satisfied = false;
            foreach (var availableItem in allAvailableItems)
            {
                var (availableName, availableVersion) = SplitNameAndVersion(availableItem);

                // Check if names match (case-insensitive)
                if (string.Equals(availableName, reqName, StringComparison.OrdinalIgnoreCase))
                {
                    // If no specific version required, any version satisfies
                    if (string.IsNullOrEmpty(reqVersion))
                    {
                        satisfied = true;
                        break;
                    }

                    // If specific version required, check if it matches
                    if (!string.IsNullOrEmpty(reqVersion) && !string.IsNullOrEmpty(availableVersion))
                    {
                        // Simple exact version match for now
                        if (string.Equals(availableVersion, reqVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            satisfied = true;
                            break;
                        }
                    }
                    else if (!string.IsNullOrEmpty(reqVersion) && string.IsNullOrEmpty(availableVersion))
                    {
                        // Required version specified but available item has no version
                        // Assume satisfied if name matches (Go parity)
                        satisfied = true;
                        break;
                    }
                }
            }

            if (!satisfied)
            {
                missingDeps.Add(reqItem);
            }
        }

        return missingDeps;
    }

    /// <summary>
    /// Finds all items in the catalog that require the given item.
    /// This is used during removal to determine what dependent items also need to be removed.
    /// Migrated from Go: catalog.FindItemsRequiring() - catalog.go lines 292-320
    /// </summary>
    /// <param name="itemName">The item name to find dependents for</param>
    /// <param name="catalog">The loaded catalog dictionary</param>
    /// <returns>List of catalog items that require the given item</returns>
    public static List<CatalogItem> FindItemsRequiring(string itemName, Dictionary<string, CatalogItem> catalog)
    {
        var dependentItems = new List<CatalogItem>();

        foreach (var kvp in catalog)
        {
            var catalogItem = kvp.Value;
            if (catalogItem.Requires != null && catalogItem.Requires.Count > 0)
            {
                foreach (var reqItem in catalogItem.Requires)
                {
                    var (reqName, _) = SplitNameAndVersion(reqItem);

                    if (string.Equals(reqName, itemName, StringComparison.OrdinalIgnoreCase))
                    {
                        dependentItems.Add(catalogItem);
                        break; // No need to check more requires for this item
                    }
                }
            }
        }

        return dependentItems;
    }

    /// <summary>
    /// Splits an item name that may contain a version suffix.
    /// Handles formats like "itemname-1.0.0" or "itemname--1.0.0"
    /// Migrated from Go: catalog.SplitNameAndVersion() - catalog.go lines 393-424
    /// </summary>
    /// <param name="nameWithVersion">The combined name and version string</param>
    /// <returns>Tuple of (name, version) - version may be empty if not found</returns>
    public static (string name, string version) SplitNameAndVersion(string nameWithVersion)
    {
        if (string.IsNullOrEmpty(nameWithVersion))
        {
            return (string.Empty, string.Empty);
        }

        // Handle the double dash format first (itemname--version)
        if (nameWithVersion.Contains("--"))
        {
            var parts = nameWithVersion.Split(new[] { "--" }, 2, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                return (parts[0], parts[1]);
            }
        }

        // Handle single dash format (itemname-version)
        // Need to be careful: some item names contain dashes
        // Look for the last dash followed by a digit (likely version)
        var lastDashIndex = nameWithVersion.LastIndexOf('-');
        if (lastDashIndex > 0 && lastDashIndex < nameWithVersion.Length - 1)
        {
            var afterDash = nameWithVersion[(lastDashIndex + 1)..];
            // Check if what follows looks like a version (starts with digit)
            if (char.IsDigit(afterDash[0]))
            {
                return (nameWithVersion[..lastDashIndex], afterDash);
            }
        }

        // No version found, return just the name
        return (nameWithVersion, string.Empty);
    }

    /// <summary>
    /// Checks if an item is installed by comparing against a list of installed items
    /// Migrated from Go: catalog.IsItemInstalled() - catalog.go lines 349-358
    /// </summary>
    public static bool IsItemInstalled(string itemName, List<string> installedItems)
    {
        var (checkName, _) = SplitNameAndVersion(itemName);

        foreach (var installed in installedItems)
        {
            var (installedName, _) = SplitNameAndVersion(installed);
            if (string.Equals(installedName, checkName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the version of an installed item, if available
    /// Migrated from Go: catalog.GetVersionFromInstalledItems() - catalog.go lines 375-389
    /// </summary>
    public static string GetVersionFromInstalledItems(string itemName, List<string> installedItems)
    {
        var (checkName, _) = SplitNameAndVersion(itemName);

        foreach (var installed in installedItems)
        {
            var (installedName, installedVersion) = SplitNameAndVersion(installed);
            if (string.Equals(installedName, checkName, StringComparison.OrdinalIgnoreCase))
            {
                return installedVersion;
            }
        }

        return string.Empty;
    }

    #endregion
}
