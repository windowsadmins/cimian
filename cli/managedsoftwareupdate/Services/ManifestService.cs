using System.Net.Http.Headers;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for retrieving and processing manifests
/// Migrated from Go pkg/manifest
/// </summary>
public class ManifestService
{
    private readonly HttpClient _httpClient;
    private readonly IDeserializer _deserializer;
    private readonly CimianConfig _config;
    private readonly Dictionary<string, string> _itemSources = new();

    public ManifestService(CimianConfig config, HttpClient? httpClient = null)
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
    /// Retrieves all manifest items from server
    /// Uses two-pass approach: first collect catalogs, then process conditional items
    /// </summary>
    public async Task<List<ManifestItem>> GetManifestItemsAsync()
    {
        var items = new List<ManifestItem>();
        var processedManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingConditionals = new List<(List<ConditionalItem> Items, string SourceManifest)>();

        // Start with the client identifier manifest
        var clientIdentifier = _config.ClientIdentifier;
        if (string.IsNullOrEmpty(clientIdentifier))
        {
            clientIdentifier = Environment.MachineName;
        }

        // PASS 1: Process all manifests, collecting catalogs and deferring conditional items
        await ProcessManifestAsync(clientIdentifier, items, processedManifests, pendingConditionals);

        // Log collected catalogs before processing conditionals
        ConsoleLogger.Debug($"Collected catalogs for conditional evaluation: [{string.Join(", ", _config.Catalogs)}]");

        // PASS 2: Now process all conditional items with full catalog context
        foreach (var (conditionalItems, sourceManifest) in pendingConditionals)
        {
            ConsoleLogger.Debug($"Processing {conditionalItems.Count} conditional items from {sourceManifest}");
            var conditionalResults = ProcessConditionalItems(conditionalItems, sourceManifest);
            items.AddRange(conditionalResults);
        }

        return items;
    }

    /// <summary>
    /// Loads a specific manifest from the server
    /// </summary>
    public async Task<List<ManifestItem>> LoadSpecificManifestAsync(string manifestName)
    {
        var items = new List<ManifestItem>();
        var processedManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingConditionals = new List<(List<ConditionalItem> Items, string SourceManifest)>();

        await ProcessManifestAsync(manifestName, items, processedManifests, pendingConditionals);
        
        // Process deferred conditional items
        foreach (var (conditionalItems, sourceManifest) in pendingConditionals)
        {
            var conditionalResults = ProcessConditionalItems(conditionalItems, sourceManifest);
            items.AddRange(conditionalResults);
        }

        return items;
    }

    /// <summary>
    /// Loads a local-only manifest from a file path
    /// </summary>
    public List<ManifestItem> LoadLocalOnlyManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Local manifest file not found: {manifestPath}");
        }

        var yaml = File.ReadAllText(manifestPath);
        var manifest = _deserializer.Deserialize<ManifestFile>(yaml);

        return ConvertToManifestItems(manifest, Path.GetFileNameWithoutExtension(manifestPath));
    }

    private async Task ProcessManifestAsync(
        string manifestName,
        List<ManifestItem> items,
        HashSet<string> processedManifests,
        List<(List<ConditionalItem> Items, string SourceManifest)> pendingConditionals)
    {
        // Avoid infinite loops from circular includes
        if (processedManifests.Contains(manifestName))
        {
            return;
        }
        processedManifests.Add(manifestName);

        // Try to download the manifest
        var manifestUrl = $"{_config.SoftwareRepoURL.TrimEnd('/')}/manifests/{manifestName}.yaml";
        var localPath = Path.Combine(_config.ManifestsPath, $"{manifestName}.yaml");

        try
        {
            var response = await _httpClient.GetAsync(manifestUrl);
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

                var manifest = _deserializer.Deserialize<ManifestFile>(content);
                if (manifest != null)
                {
                    // Add catalogs to config FIRST (before processing anything else)
                    if (manifest.Catalogs != null && manifest.Catalogs.Count > 0)
                    {
                        ConsoleLogger.Debug($"Processing catalogs from manifest {manifestName}: [{string.Join(", ", manifest.Catalogs)}]");
                        foreach (var catalog in manifest.Catalogs)
                        {
                            if (!_config.Catalogs.Contains(catalog))
                            {
                                _config.Catalogs.Add(catalog);
                            }
                        }
                    }

                    // Process included manifests (Munki-like behavior)
                    if (manifest.IncludedManifests != null)
                    {
                        foreach (var include in manifest.IncludedManifests)
                        {
                            // Clean up the include path - normalize slashes and remove .yaml extension
                            var includeName = include.Replace(".yaml", "").Replace("\\", "/");
                            
                            // Include paths are relative or absolute manifest references
                            // They should be passed as-is to ProcessManifestAsync
                            await ProcessManifestAsync(includeName, items, processedManifests, pendingConditionals);
                        }
                    }

                    // Convert to manifest items (excluding conditional items - they're deferred)
                    var manifestItems = ConvertToManifestItems(manifest, manifestName);
                    items.AddRange(manifestItems);
                    
                    // DEFER conditional items processing until all manifests are loaded
                    // This ensures catalogs are fully populated before conditional evaluation
                    if (manifest.ConditionalItems != null && manifest.ConditionalItems.Count > 0)
                    {
                        pendingConditionals.Add((manifest.ConditionalItems, manifestName));
                    }
                }
            }
            else
            {
                Console.Error.WriteLine($"[WARNING] Failed to download manifest {manifestName}: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARNING] Error processing manifest {manifestName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes conditional items by evaluating conditions against system facts
    /// </summary>
    private List<ManifestItem> ProcessConditionalItems(List<ConditionalItem> conditionalItems, string sourceManifest)
    {
        var items = new List<ManifestItem>();
        
        foreach (var conditional in conditionalItems)
        {
            if (string.IsNullOrWhiteSpace(conditional.Condition))
            {
                continue;
            }
            
            // Evaluate the condition
            if (EvaluateCondition(conditional.Condition))
            {
                ConsoleLogger.Debug($"Conditional item matched: {conditional.Condition}");
                
                // Add managed_installs from this conditional
                if (conditional.ManagedInstalls != null)
                {
                    foreach (var name in conditional.ManagedInstalls)
                    {
                        items.Add(new ManifestItem
                        {
                            Name = name,
                            Action = "install",
                            SourceManifest = sourceManifest
                        });
                        SetItemSource(name, sourceManifest, "conditional_managed_installs");
                    }
                }
                
                // Add managed_uninstalls from this conditional
                if (conditional.ManagedUninstalls != null)
                {
                    foreach (var name in conditional.ManagedUninstalls)
                    {
                        items.Add(new ManifestItem
                        {
                            Name = name,
                            Action = "uninstall",
                            SourceManifest = sourceManifest
                        });
                        SetItemSource(name, sourceManifest, "conditional_managed_uninstalls");
                    }
                }
                
                // Add managed_updates from this conditional
                if (conditional.ManagedUpdates != null)
                {
                    foreach (var name in conditional.ManagedUpdates)
                    {
                        items.Add(new ManifestItem
                        {
                            Name = name,
                            Action = "update",
                            SourceManifest = sourceManifest
                        });
                        SetItemSource(name, sourceManifest, "conditional_managed_updates");
                    }
                }
                
                // Add optional_installs from this conditional
                if (conditional.OptionalInstalls != null)
                {
                    foreach (var name in conditional.OptionalInstalls)
                    {
                        items.Add(new ManifestItem
                        {
                            Name = name,
                            Action = "optional",
                            SourceManifest = sourceManifest
                        });
                        SetItemSource(name, sourceManifest, "conditional_optional_installs");
                    }
                }
            }
            else
            {
                ConsoleLogger.Debug($"Conditional item did not match: {conditional.Condition}");
            }
        }
        
        return items;
    }

    /// <summary>
    /// Evaluates a simple condition string like "catalogs == Staging" or "arch == x64"
    /// </summary>
    private bool EvaluateCondition(string condition)
    {
        // Parse simple conditions like "key == value" or "key CONTAINS value"
        var parts = condition.Split(new[] { "==", "!=", "CONTAINS", "LIKE" }, StringSplitOptions.None);
        if (parts.Length != 2)
        {
            Console.WriteLine($"[WARNING] Could not parse condition: {condition}");
            return false;
        }
        
        var key = parts[0].Trim().ToLowerInvariant();
        var expectedValue = parts[1].Trim();
        
        // Strip surrounding quotes from expected value (handles both "Testing" and Testing)
        if ((expectedValue.StartsWith('"') && expectedValue.EndsWith('"')) ||
            (expectedValue.StartsWith('\'') && expectedValue.EndsWith('\'')))
        {
            expectedValue = expectedValue[1..^1];
        }
        
        // Determine the operator
        var isEquals = condition.Contains("==");
        var isNotEquals = condition.Contains("!=");
        var isContains = condition.ToUpperInvariant().Contains("CONTAINS");
        
        // Get the actual value from facts
        object? actualValue = key switch
        {
            "catalogs" => _config.Catalogs,
            "arch" or "architecture" => Environment.Is64BitOperatingSystem ? "x64" : "x86",
            "hostname" => Environment.MachineName,
            "os" or "operatingsystem" => "Windows",
            _ => null
        };
        
        if (actualValue == null)
        {
            ConsoleLogger.Warn($"Unknown fact key: {key}");
            return false;
        }
        
        // Handle array comparison (like catalogs)
        if (actualValue is List<string> list)
        {
            ConsoleLogger.Debug($"Comparing array [{string.Join(", ", list)}] with '{expectedValue}'");
            if (isEquals)
            {
                return list.Any(v => string.Equals(v, expectedValue, StringComparison.OrdinalIgnoreCase));
            }
            if (isNotEquals)
            {
                return !list.Any(v => string.Equals(v, expectedValue, StringComparison.OrdinalIgnoreCase));
            }
            if (isContains)
            {
                return list.Any(v => v.Contains(expectedValue, StringComparison.OrdinalIgnoreCase));
            }
        }
        
        // Handle string comparison
        var actualStr = actualValue.ToString() ?? "";
        if (isEquals)
        {
            return string.Equals(actualStr, expectedValue, StringComparison.OrdinalIgnoreCase);
        }
        if (isNotEquals)
        {
            return !string.Equals(actualStr, expectedValue, StringComparison.OrdinalIgnoreCase);
        }
        if (isContains)
        {
            return actualStr.Contains(expectedValue, StringComparison.OrdinalIgnoreCase);
        }
        
        return false;
    }

    private List<ManifestItem> ConvertToManifestItems(ManifestFile manifest, string sourceManifest)
    {
        var items = new List<ManifestItem>();

        // Add managed_installs
        if (manifest.ManagedInstalls != null)
        {
            foreach (var name in manifest.ManagedInstalls)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "install",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "managed_installs");
            }
        }

        // Add managed_updates
        if (manifest.ManagedUpdates != null)
        {
            foreach (var name in manifest.ManagedUpdates)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "update",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "managed_updates");
            }
        }

        // Add managed_uninstalls
        if (manifest.ManagedUninstalls != null)
        {
            foreach (var name in manifest.ManagedUninstalls)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "uninstall",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "managed_uninstalls");
            }
        }

        // Add optional_installs
        if (manifest.OptionalInstalls != null)
        {
            foreach (var name in manifest.OptionalInstalls)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "optional",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "optional_installs");
            }
        }

        // Add managed_profiles
        if (manifest.ManagedProfiles != null)
        {
            foreach (var name in manifest.ManagedProfiles)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "profile",
                    SourceManifest = sourceManifest
                });
            }
        }

        // Add managed_apps
        if (manifest.ManagedApps != null)
        {
            foreach (var name in manifest.ManagedApps)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "app",
                    SourceManifest = sourceManifest
                });
            }
        }

        return items;
    }

    public void SetItemSource(string itemName, string sourceManifest, string sourceType)
    {
        var key = itemName.ToLowerInvariant();
        _itemSources[key] = $"{sourceManifest}:{sourceType}";
    }

    public (string SourceManifest, string SourceType) GetItemSource(string itemName)
    {
        var key = itemName.ToLowerInvariant();
        if (_itemSources.TryGetValue(key, out var source))
        {
            var parts = source.Split(':');
            return (parts[0], parts.Length > 1 ? parts[1] : "unknown");
        }
        return ("Unknown", "unknown");
    }

    public void ClearItemSources()
    {
        _itemSources.Clear();
    }

    /// <summary>
    /// Deduplicates manifest items, keeping the first occurrence
    /// </summary>
    public List<ManifestItem> DeduplicateItems(List<ManifestItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ManifestItem>();

        foreach (var item in items)
        {
            var key = $"{item.Name}:{item.Action}";
            if (!seen.Contains(key))
            {
                seen.Add(key);
                result.Add(item);
            }
        }

        return result;
    }
}
