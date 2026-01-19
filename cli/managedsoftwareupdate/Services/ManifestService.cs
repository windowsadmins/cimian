using System.Net.Http.Headers;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;
using Cimian.Engine.Predicates;
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

                    // Process included manifests (Munki-like behavior)
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
    /// Evaluates complex condition strings with support for OR/AND operators and DOES_NOT_CONTAIN
    /// Examples:
    ///   - "catalogs == Staging"
    ///   - "arch == x64"  
    ///   - "hostname CONTAINS Cintiq"
    ///   - "hostname DOES_NOT_CONTAIN Camera"
    ///   - "hostname CONTAINS Cintiq-1 OR hostname CONTAINS Cintiq-0"
    ///   - "hostname DOES_NOT_CONTAIN Camera AND hostname DOES_NOT_CONTAIN ANIM-CAM"
    /// </summary>
    private bool EvaluateCondition(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true; // No condition means always true
        }

        var conditionUpper = condition.ToUpperInvariant();
        
        // Check for OR operator (case insensitive) - evaluate as ANY match
        if (conditionUpper.Contains(" OR "))
        {
            var parts = SplitOnLogicalOperator(condition, " OR ");
            ConsoleLogger.Debug($"    Evaluating OR condition with {parts.Count} parts: {condition}");
            foreach (var part in parts)
            {
                if (EvaluateSingleCondition(part.Trim()))
                {
                    ConsoleLogger.Debug($"    OR condition matched on: {part.Trim()}");
                    return true; // Short-circuit on first true
                }
            }
            ConsoleLogger.Debug($"    OR condition did not match any part");
            return false;
        }
        
        // Check for AND operator (case insensitive) - evaluate as ALL must match
        if (conditionUpper.Contains(" AND "))
        {
            var parts = SplitOnLogicalOperator(condition, " AND ");
            ConsoleLogger.Debug($"    Evaluating AND condition with {parts.Count} parts: {condition}");
            foreach (var part in parts)
            {
                if (!EvaluateSingleCondition(part.Trim()))
                {
                    ConsoleLogger.Debug($"    AND condition failed on: {part.Trim()}");
                    return false; // Short-circuit on first false
                }
            }
            ConsoleLogger.Debug($"    AND condition matched all parts");
            return true;
        }
        
        // Single condition
        return EvaluateSingleCondition(condition);
    }
    
    /// <summary>
    /// Splits a condition string on a logical operator while preserving quoted strings
    /// </summary>
    private List<string> SplitOnLogicalOperator(string condition, string logicalOp)
    {
        var results = new List<string>();
        var opUpper = logicalOp.ToUpperInvariant();
        var conditionUpper = condition.ToUpperInvariant();
        
        var startIndex = 0;
        var index = conditionUpper.IndexOf(opUpper, StringComparison.Ordinal);
        
        while (index != -1)
        {
            results.Add(condition.Substring(startIndex, index - startIndex));
            startIndex = index + opUpper.Length;
            index = conditionUpper.IndexOf(opUpper, startIndex, StringComparison.Ordinal);
        }
        
        if (startIndex < condition.Length)
        {
            results.Add(condition.Substring(startIndex));
        }
        
        return results;
    }
    
    /// <summary>
    /// Evaluates a single condition (no OR/AND operators)
    /// </summary>
    private bool EvaluateSingleCondition(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }
        
        var conditionUpper = condition.ToUpperInvariant();
        
        // Determine the operator - check in order of specificity (longer operators first)
        string operatorUsed;
        bool isDoesNotContain = false;
        bool isContains = false;
        bool isEquals = false;
        bool isNotEquals = false;
        bool isBeginsWith = false;
        bool isEndsWith = false;
        
        if (conditionUpper.Contains("DOES_NOT_CONTAIN"))
        {
            operatorUsed = "DOES_NOT_CONTAIN";
            isDoesNotContain = true;
        }
        else if (conditionUpper.Contains("BEGINSWITH"))
        {
            operatorUsed = "BEGINSWITH";
            isBeginsWith = true;
        }
        else if (conditionUpper.Contains("ENDSWITH"))
        {
            operatorUsed = "ENDSWITH";
            isEndsWith = true;
        }
        else if (conditionUpper.Contains("CONTAINS"))
        {
            operatorUsed = "CONTAINS";
            isContains = true;
        }
        else if (condition.Contains("!="))
        {
            operatorUsed = "!=";
            isNotEquals = true;
        }
        else if (condition.Contains("=="))
        {
            operatorUsed = "==";
            isEquals = true;
        }
        else if (conditionUpper.Contains("LIKE"))
        {
            operatorUsed = "LIKE";
            isContains = true; // LIKE is treated similar to CONTAINS for simple cases
        }
        else
        {
            ConsoleLogger.Warn($"Unknown operator in condition: {condition}");
            return false;
        }
        
        // Split on the operator (case insensitive for text operators)
        int splitIndex;
        if (operatorUsed == "==" || operatorUsed == "!=")
        {
            splitIndex = condition.IndexOf(operatorUsed, StringComparison.Ordinal);
        }
        else
        {
            splitIndex = conditionUpper.IndexOf(operatorUsed, StringComparison.OrdinalIgnoreCase);
        }
        
        if (splitIndex == -1)
        {
            ConsoleLogger.Warn($"Could not find operator in condition: {condition}");
            return false;
        }
        
        var key = condition.Substring(0, splitIndex).Trim().ToLowerInvariant();
        var expectedValue = condition.Substring(splitIndex + operatorUsed.Length).Trim();
        
        // Strip surrounding quotes from expected value
        if ((expectedValue.StartsWith('"') && expectedValue.EndsWith('"')) ||
            (expectedValue.StartsWith('\'') && expectedValue.EndsWith('\'')))
        {
            expectedValue = expectedValue[1..^1];
        }
        
        // Get the actual value from facts
        object? actualValue = key switch
        {
            "catalogs" => _config.Catalogs,
            "arch" or "architecture" => GetSystemArchitecture(),
            "hostname" => Environment.MachineName,
            "os" or "operatingsystem" => "Windows",
            "machine_type" => "desktop", // Simplified - could be enhanced
            "machine_model" => GetMachineModel(),
            _ => null
        };
        
        if (actualValue == null)
        {
            ConsoleLogger.Debug($"    Unknown fact key: {key} (condition may be unsupported)");
            return false;
        }
        
        ConsoleLogger.Debug($"    Evaluating: {key} {operatorUsed} '{expectedValue}' (actual: {actualValue})");
        
        // Handle array comparison (like catalogs)
        if (actualValue is List<string> list)
        {
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
            if (isDoesNotContain)
            {
                return !list.Any(v => v.Contains(expectedValue, StringComparison.OrdinalIgnoreCase));
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
        if (isDoesNotContain)
        {
            return !actualStr.Contains(expectedValue, StringComparison.OrdinalIgnoreCase);
        }
        if (isBeginsWith)
        {
            return actualStr.StartsWith(expectedValue, StringComparison.OrdinalIgnoreCase);
        }
        if (isEndsWith)
        {
            return actualStr.EndsWith(expectedValue, StringComparison.OrdinalIgnoreCase);
        }
        
        return false;
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
