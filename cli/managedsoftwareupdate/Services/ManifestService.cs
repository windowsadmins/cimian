using System.Net.Http.Headers;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;
using Cimian.Engine.Predicates;
using SystemFacts = Cimian.Core.Models.SystemFacts;
// Use the Predicate expression types from Cimian.Engine
using ParsedExpression = Cimian.Engine.Predicates.ParsedExpression;
using LiteralExpression = Cimian.Engine.Predicates.LiteralExpression;
using ComparisonExpression = Cimian.Engine.Predicates.ComparisonExpression;
using LogicalExpression = Cimian.Engine.Predicates.LogicalExpression;
using NotExpression = Cimian.Engine.Predicates.NotExpression;
using AnyExpression = Cimian.Engine.Predicates.AnyExpression;

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
    private readonly PredicateEngine _predicateEngine;
    private readonly List<string> _featuredItems = new();

    /// <summary>
    /// Featured items collected across all processed manifests
    /// </summary>
    public IReadOnlyList<string> FeaturedItems => _featuredItems;
    private SystemFacts? _systemFacts;

    public ManifestService(CimianConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? CimianHttpClientFactory.CreateHttpClient(config);
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        _predicateEngine = new PredicateEngine(new Microsoft.Extensions.Logging.Abstractions.NullLogger<PredicateEngine>());
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
        // If UseClientCertificateCNAsClientIdentifier is set, use the certificate CN
        var clientIdentifier = CimianHttpClientFactory.GetClientCertificateCN(_config);
        if (string.IsNullOrEmpty(clientIdentifier))
        {
            clientIdentifier = _config.ClientIdentifier;
        }
        if (string.IsNullOrEmpty(clientIdentifier))
        {
            clientIdentifier = Environment.MachineName;
        }

        // PASS 1: Process all manifests, collecting catalogs and deferring conditional items
        await ProcessManifestAsync(clientIdentifier, items, processedManifests, pendingConditionals);

        // Log collected catalogs before processing conditionals
        ConsoleLogger.Info($"    Collected catalogs for conditional evaluation: [{string.Join(", ", _config.Catalogs)}]");

        // PASS 2: Now process all conditional items with full catalog context
        foreach (var (conditionalItems, sourceManifest) in pendingConditionals)
        {
            ConsoleLogger.Info($"    Processing {conditionalItems.Count} conditional items from {sourceManifest}");
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
            ConsoleLogger.Debug($"Skipping already-processed manifest: {manifestName}");
            return;
        }
        processedManifests.Add(manifestName);
        ConsoleLogger.Debug($"Processing manifest originalName: {manifestName} processedName: {manifestName}.yaml");

        // Try to download the manifest
        var manifestUrl = $"{_config.SoftwareRepoURL.TrimEnd('/')}/manifests/{manifestName}.yaml";
        var localPath = Path.Combine(_config.ManifestsPath, $"{manifestName}.yaml");
        ConsoleLogger.Debug($"Downloading manifest url: {manifestUrl} localPath: {localPath}");

        try
        {
            ConsoleLogger.Debug($"Starting download url: {manifestUrl} destination: {localPath}");
            var response = await _httpClient.GetAsync(manifestUrl);
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
                ConsoleLogger.Debug($"Successfully downloaded manifest url: {manifestUrl}");
                ConsoleLogger.Debug($"Processed manifest: {Path.GetFileNameWithoutExtension(manifestName)}");

                var manifest = _deserializer.Deserialize<ManifestFile>(content);
                if (manifest != null)
                {
                    // Add catalogs to config FIRST (before processing anything else)
                    if (manifest.Catalogs != null && manifest.Catalogs.Count > 0)
                    {
                        ConsoleLogger.Debug($"Processing catalogs for manifest manifest: {Path.GetFileNameWithoutExtension(manifestName)} catalogs: [{string.Join(", ", manifest.Catalogs)}]");
                        foreach (var catalog in manifest.Catalogs)
                        {
                            if (!_config.Catalogs.Contains(catalog))
                            {
                                ConsoleLogger.Debug($"Added catalog to collection catalog: {catalog}");
                                _config.Catalogs.Add(catalog);
                            }
                        }
                    }
                    else
                    {
                        ConsoleLogger.Debug($"Processing catalogs for manifest manifest: {Path.GetFileNameWithoutExtension(manifestName)} catalogs: []");
                    }

                    // Process included manifests
                    if (manifest.IncludedManifests != null)
                    {
                        ConsoleLogger.Debug($"Processing included manifests from {manifestName} count: {manifest.IncludedManifests.Count}");
                        foreach (var include in manifest.IncludedManifests)
                        {
                            // Clean up the include path - normalize slashes and remove .yaml extension
                            var includeName = include.Replace(".yaml", "").Replace("\\", "/");
                            ConsoleLogger.Debug($"Processing included manifest: {includeName}");
                            
                            // Include paths are relative or absolute manifest references
                            // They should be passed as-is to ProcessManifestAsync
                            await ProcessManifestAsync(includeName, items, processedManifests, pendingConditionals);
                        }
                    }

                    // Collect featured_items from this manifest
                    if (manifest.FeaturedItems != null && manifest.FeaturedItems.Count > 0)
                    {
                        foreach (var fi in manifest.FeaturedItems)
                        {
                            if (!_featuredItems.Contains(fi, StringComparer.OrdinalIgnoreCase))
                                _featuredItems.Add(fi);
                        }
                        ConsoleLogger.Debug($"Collected {manifest.FeaturedItems.Count} featured items from {manifestName}");
                    }

                    // Convert to manifest items (excluding conditional items - they're deferred)
                    var manifestItems = ConvertToManifestItems(manifest, manifestName);
                    ConsoleLogger.Debug($"Processed manifest: {manifestName} itemCount: {manifestItems.Count}");
                    items.AddRange(manifestItems);
                    
                    // DEFER conditional items processing until all manifests are loaded
                    // This ensures catalogs are fully populated before conditional evaluation
                    if (manifest.ConditionalItems != null && manifest.ConditionalItems.Count > 0)
                    {
                        ConsoleLogger.Info($"    Deferring {manifest.ConditionalItems.Count} conditional items from {manifestName}");
                        pendingConditionals.Add((manifest.ConditionalItems, manifestName));
                    }
                }
            }
            else
            {
                ConsoleLogger.Warn($"Failed to download manifest {manifestName}: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Error processing manifest {manifestName}: {ex.Message}");
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
                ConsoleLogger.Info($"    Conditional item matched: {conditional.Condition}");
                
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
                ConsoleLogger.Info($"    Conditional item did not match: {conditional.Condition}");
            }
        }
        
        return items;
    }

    /// <summary>
    /// Ensures SystemFacts are populated for predicate evaluation
    /// </summary>
    private void EnsureSystemFacts()
    {
        if (_systemFacts != null) return;
        
        // Get OS version info for conditional evaluations
        var osVersion = Environment.OSVersion.Version;
        
        _systemFacts = new Cimian.Core.Models.SystemFacts
        {
            Hostname = Environment.MachineName,
            Architecture = GetSystemArchitecture(),
            OperatingSystem = "Windows",
            OperatingSystemVersion = osVersion.ToString(), // e.g., "10.0.26200.7623"
            OSVersMajor = osVersion.Major,                  // e.g., 10
            OSVersMinor = osVersion.Minor,                  // e.g., 0
            OSBuildNumber = osVersion.Build,                // e.g., 26200
            Catalogs = _config.Catalogs,
            MachineType = GetMachineType(),
            MachineModel = GetMachineModel(),
            CollectedAt = DateTime.UtcNow
        };
    }
    
    /// <summary>
    /// Evaluates a condition using the PredicateEngine for proper NSPredicate-style parsing
    /// Supports complex expressions with OR/AND/NOT operators, parentheses, and nested conditions
    /// </summary>
    private bool EvaluateCondition(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true; // No condition means always true
        }

        EnsureSystemFacts();
        
        try
        {
            // Use the sophisticated PredicateEngine for proper parsing and evaluation
            var result = _predicateEngine.EvaluateConditionAsync(condition, _systemFacts!).GetAwaiter().GetResult();
            
            if (result)
            {
                ConsoleLogger.Debug($"    Condition matched: {condition}");
            }
            else
            {
                ConsoleLogger.Debug($"    Condition did not match: {condition}");
            }
            
            return result;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Error evaluating condition '{condition}': {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Gets the system architecture in a format matching Go behavior
    /// </summary>
    private static string GetSystemArchitecture()
    {
        // Check PROCESSOR_IDENTIFIER first to detect native ARM64 hardware
        var procId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        if (procId.ToUpperInvariant().Contains("ARM"))
        {
            return "arm64";
        }
        
        // Check for WoW64 native architecture
        var nativeArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? "";
        if (!string.IsNullOrEmpty(nativeArch))
        {
            return nativeArch.ToUpperInvariant() switch
            {
                "AMD64" or "X86_64" => "x64",
                "ARM64" => "arm64",
                _ => nativeArch.ToLowerInvariant()
            };
        }
        
        // Fall back to PROCESSOR_ARCHITECTURE
        var arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
        return arch.ToUpperInvariant() switch
        {
            "AMD64" or "X86_64" => "x64",
            "X86" or "386" => "x86",
            "ARM64" => "arm64",
            _ => Environment.Is64BitOperatingSystem ? "x64" : "x86"
        };
    }
    
    /// <summary>
    /// Gets the machine model (for ARM64 Surface detection, etc.)
    /// </summary>
    private static string GetMachineModel()
    {
        // This would ideally use WMI Win32_ComputerSystem.Model
        // For now, return a placeholder - can be enhanced later
        return "Unknown";
    }
    
    /// <summary>
    /// Detects machine type: laptop, desktop, virtual, or server
    /// Uses battery presence as primary laptop indicator
    /// </summary>
    private static string GetMachineType()
    {
        try
        {
            // Check for virtual machine first
            var manufacturer = Environment.GetEnvironmentVariable("COMPUTERNAME") ?? "";
            if (manufacturer.StartsWith("VM", StringComparison.OrdinalIgnoreCase))
            {
                return "virtual";
            }
            
            // Check for battery - presence indicates laptop
            // Use PowerShell to query battery status
            var batteryCheck = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"(Get-WmiObject Win32_Battery).Count\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(batteryCheck);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                
                if (int.TryParse(output, out int batteryCount) && batteryCount > 0)
                {
                    return "laptop";
                }
            }
        }
        catch
        {
            // Fall through to default
        }
        
        return "desktop";
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

        // Add default_installs — treated like managed_installs but only on first encounter
        // (not enforced if user has previously removed the item)
        if (manifest.DefaultInstalls != null)
        {
            foreach (var name in manifest.DefaultInstalls)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "default",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "default_installs");
            }
        }

        return items;
    }

    public void SetItemSource(string itemName, string sourceManifest, string sourceType)
    {
        var key = itemName.ToLowerInvariant();
        _itemSources[key] = $"{sourceManifest}:{sourceType}";
        ConsoleLogger.Debug($"Setting item source item: {itemName} sourceManifest: {sourceManifest} sourceType: {sourceType}");
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
    /// Deduplicates manifest items, keeping the highest version for each item name.
    /// Go parity: pkg/status.DeduplicateManifestItems - uses just name as key,
    /// keeps highest version if duplicate found, preserves original order.
    /// </summary>
    public List<ManifestItem> DeduplicateItems(List<ManifestItem> items)
    {
        var dedup = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
        var orderedKeys = new List<string>();

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Name))
                continue;

            var key = item.Name.ToLowerInvariant();
            
            if (dedup.TryGetValue(key, out var existing))
            {
                // If we find a newer version, update it but keep the original position
                if (IsOlderVersion(existing.Version, item.Version))
                {
                    dedup[key] = item;
                }
            }
            else
            {
                // First time seeing this item - track its order
                orderedKeys.Add(key);
                dedup[key] = item;
            }
        }

        // Build result in the original order items were first encountered
        var result = new List<ManifestItem>();
        foreach (var key in orderedKeys)
        {
            result.Add(dedup[key]);
        }
        return result;
    }

    /// <summary>
    /// Compare versions to determine if v1 is older than v2.
    /// Go parity: pkg/status.IsOlderVersion
    /// </summary>
    private static bool IsOlderVersion(string? v1, string? v2)
    {
        if (string.IsNullOrEmpty(v1)) return true;
        if (string.IsNullOrEmpty(v2)) return false;
        
        // Try to parse as Version objects first
        if (Version.TryParse(v1.Replace("-", "."), out var ver1) && 
            Version.TryParse(v2.Replace("-", "."), out var ver2))
        {
            return ver1 < ver2;
        }
        
        // Fall back to string comparison
        return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase) < 0;
    }
}
