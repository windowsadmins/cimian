using System.Net.Http.Headers;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.CLI.managedsoftwareupdate.Models;

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

        if (!string.IsNullOrEmpty(config.AuthToken))
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

        foreach (var catalogName in catalogs)
        {
            var catalogItems = await DownloadCatalogAsync(catalogName);
            foreach (var item in catalogItems)
            {
                var key = item.Name.ToLowerInvariant();
                // Keep highest version if duplicate
                if (!items.ContainsKey(key) || 
                    CompareVersions(item.Version, items[key].Version) > 0)
                {
                    items[key] = item;
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

        try
        {
            var response = await _httpClient.GetAsync(catalogUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                
                // Save locally
                var dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(localPath, content);

                items = ParseCatalog(content);
            }
            else
            {
                Console.Error.WriteLine($"[WARNING] Failed to download catalog {catalogName}: {response.StatusCode}");
                // Try to load from local cache
                items = LoadLocalCatalog(localPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARNING] Error downloading catalog {catalogName}: {ex.Message}");
            // Try to load from local cache
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
            return new List<CatalogItem>();
        }

        try
        {
            var yaml = File.ReadAllText(path);
            return ParseCatalog(yaml);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARNING] Failed to load local catalog {path}: {ex.Message}");
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
                // Keep highest version if duplicate
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
}
