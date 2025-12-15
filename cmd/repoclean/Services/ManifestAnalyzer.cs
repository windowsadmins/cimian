using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RepoClean.Services;

public class ManifestAnalyzer : IManifestAnalyzer
{
    private readonly ILogger<ManifestAnalyzer> _logger;
    private readonly IDeserializer _yamlDeserializer;

    public ManifestAnalyzer(ILogger<ManifestAnalyzer> logger)
    {
        _logger = logger;
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
    }

    public async Task<(HashSet<string> manifestItems, HashSet<(string name, string version)> manifestItemsWithVersions)> AnalyzeManifestsAsync(IFileRepository repository)
    {
        Console.WriteLine("Analyzing manifest files...");
        
        var manifestItems = new HashSet<string>();
        var manifestItemsWithVersions = new HashSet<(string name, string version)>();

        try
        {
            var manifestsList = await repository.GetItemListAsync("manifests");
            
            foreach (var manifestName in manifestsList)
            {
                try
                {
                    var manifestPath = Path.Combine("manifests", manifestName);
                    var data = await repository.GetContentAsync(manifestPath);
                    var manifest = ParseManifestData(data, manifestName);

                    if (manifest == null) continue;

                    // Process standard manifest keys
                    var keysToProcess = new[] { "managed_installs", "managed_uninstalls", "managed_updates", "optional_installs" };
                    
                    foreach (var key in keysToProcess)
                    {
                        ProcessManifestItems(manifest, key, manifestItems, manifestItemsWithVersions);
                    }

                    // Process conditional items
                    if (manifest.ContainsKey("conditional_items") && manifest["conditional_items"] is Newtonsoft.Json.Linq.JArray conditionalItems)
                    {
                        foreach (var conditionalItem in conditionalItems)
                        {
                            if (conditionalItem is Newtonsoft.Json.Linq.JObject conditionalItemObj)
                            {
                                var conditionalDict = conditionalItemObj.ToObject<Dictionary<string, object>>();
                                if (conditionalDict != null)
                                {
                                    foreach (var key in keysToProcess)
                                    {
                                        ProcessManifestItems(conditionalDict, key, manifestItems, manifestItemsWithVersions);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing manifest {ManifestName}", manifestName);
                    Console.WriteLine($"Error processing manifest {manifestName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting manifest list");
            Console.WriteLine($"Error getting manifest list: {ex.Message}");
        }

        return (manifestItems, manifestItemsWithVersions);
    }

    private void ProcessManifestItems(
        Dictionary<string, object> manifest,
        string key,
        HashSet<string> manifestItems,
        HashSet<(string name, string version)> manifestItemsWithVersions)
    {
        if (!manifest.ContainsKey(key)) return;

        if (manifest[key] is Newtonsoft.Json.Linq.JArray items)
        {
            foreach (var item in items)
            {
                if (item is Newtonsoft.Json.Linq.JValue itemValue && itemValue.Value is string itemString)
                {
                    var (itemName, itemVersion) = ParseNameAndVersion(itemString);
                    manifestItems.Add(itemName);
                    
                    if (!string.IsNullOrEmpty(itemVersion))
                    {
                        manifestItemsWithVersions.Add((itemName, itemVersion));
                    }
                }
            }
        }
    }

    private (string name, string version) ParseNameAndVersion(string itemString)
    {
        // Split on '--' first, then on '-'
        var delimiters = new[] { "--", "-" };
        
        foreach (var delimiter in delimiters)
        {
            if (itemString.Contains(delimiter))
            {
                var lastIndex = itemString.LastIndexOf(delimiter);
                var potentialVersion = itemString.Substring(lastIndex + delimiter.Length);
                
                // Check if the potential version starts with a digit
                if (!string.IsNullOrEmpty(potentialVersion) && char.IsDigit(potentialVersion[0]))
                {
                    var name = itemString.Substring(0, lastIndex);
                    return (name, potentialVersion);
                }
            }
        }

        return (itemString, string.Empty);
    }

    private Dictionary<string, object>? ParseManifestData(string data, string manifestName)
    {
        try
        {
            // First try YAML parsing
            if (manifestName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || 
                manifestName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                return _yamlDeserializer.Deserialize<Dictionary<string, object>>(data);
            }

            // Fall back to JSON parsing
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing manifest {ManifestName}", manifestName);
            return null;
        }
    }
}
