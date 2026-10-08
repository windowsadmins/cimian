using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cimian.CLI.Repoclean.Services;

public class ManifestAnalyzer : IManifestAnalyzer
{
    private readonly ILogger<ManifestAnalyzer> _logger;

    public ManifestAnalyzer(ILogger<ManifestAnalyzer> logger)
    {
        _logger = logger;
    }

    // Every manifest key that names items. default_installs and featured_items are
    // Cimian keys; an item listed only there is still in use.
    private static readonly string[] ItemKeys =
    {
        "managed_installs", "managed_uninstalls", "managed_updates",
        "optional_installs", "default_installs", "featured_items"
    };

    public async Task<ManifestAnalysis> AnalyzeManifestsAsync(IFileRepository repository)
    {
        Console.WriteLine("Analyzing manifest files...");

        var analysis = new ManifestAnalysis();
        var manifestsList = await repository.GetItemListAsync("manifests");

        foreach (var manifestName in manifestsList)
        {
            var manifestPath = Path.Combine("manifests", manifestName);
            try
            {
                var data = await repository.GetContentAsync(manifestPath);
                ProcessManifest(ParseManifestData(data, manifestName), analysis);
            }
            catch (Exception ex)
            {
                // The items this manifest names are now unprotected, so this is not a
                // warning to scroll past: the caller refuses to delete anything.
                _logger.LogDebug(ex, "Error processing manifest {ManifestName}", manifestName);
                analysis.ParseErrors.Add($"{manifestPath}: {ex.Message}");
            }
        }

        return analysis;
    }

    private void ProcessManifest(IReadOnlyDictionary<string, object> manifest, ManifestAnalysis analysis)
    {
        foreach (var key in ItemKeys)
        {
            foreach (var itemString in YamlValues.AsStringList(manifest.GetValueOrDefault(key)))
            {
                var (itemName, itemVersion) = ParseNameAndVersion(itemString);
                analysis.Items.Add(itemName);

                if (!string.IsNullOrEmpty(itemVersion))
                {
                    analysis.ItemsWithVersions.Add((itemName, itemVersion));
                }
            }
        }

        // conditional_items can nest, so recurse
        foreach (var conditional in YamlValues.AsMappingList(manifest.GetValueOrDefault("conditional_items")))
        {
            ProcessManifest(conditional, analysis);
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

    private static IReadOnlyDictionary<string, object> ParseManifestData(string data, string manifestName)
    {
        if (manifestName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(data)
                ?? throw new InvalidDataException("the document is empty");
        }

        // Cimian manifests are YAML, with or without an extension
        return YamlValues.ParseYamlMapping(data);
    }
}
