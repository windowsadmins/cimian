using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.RepresentationModel;

namespace Cimian.CLI.Repoclean.Services;

public class PkgInfoAnalyzer : IPkgInfoAnalyzer
{
    private readonly ILogger<PkgInfoAnalyzer> _logger;
    private readonly IDeserializer _yamlDeserializer;

    public PkgInfoAnalyzer(ILogger<PkgInfoAnalyzer> logger)
    {
        _logger = logger;
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
    }

    public async Task<(Dictionary<string, Dictionary<string, List<PackageInfo>>> pkgInfoDb, HashSet<(string name, string version)> requiredItems, HashSet<string> referencedPackages, int pkgInfoCount)> AnalyzePkgInfoAsync(IFileRepository repository, HashSet<string> manifestItems)
    {
        Console.WriteLine("Analyzing pkginfo files...");
        
        var pkgInfoDb = new Dictionary<string, Dictionary<string, List<PackageInfo>>>();
        var requiredItems = new HashSet<(string name, string version)>();
        var referencedPackages = new HashSet<string>();
        var pkgInfoCount = 0;

        try
        {
            var pkgInfoList = await repository.GetItemListAsync("pkgsinfo");
            
            foreach (var pkgInfoName in pkgInfoList)
            {
                try
                {
                    var pkgInfoPath = Path.Combine("pkgsinfo", pkgInfoName);
                    var data = await repository.GetContentAsync(pkgInfoPath);
                    var pkgInfo = ParsePkgInfoData(data, pkgInfoName);

                    if (pkgInfo == null) continue;

                    if (!pkgInfo.TryGetValue("name", out var nameObj) || nameObj is not string name ||
                        !pkgInfo.TryGetValue("version", out var versionObj) || versionObj is not string version)
                    {
                        _logger.LogWarning("Missing 'name' or 'version' keys in {PkgInfoName}", pkgInfoName);
                        continue;
                    }

                    var packageInfo = CreatePackageInfo(pkgInfo, name, version, pkgInfoPath, data.Length);
                    
                    // Track referenced packages
                    if (!string.IsNullOrEmpty(packageInfo.PackagePath))
                        referencedPackages.Add(packageInfo.PackagePath);
                    if (!string.IsNullOrEmpty(packageInfo.UninstallPackagePath))
                        referencedPackages.Add(packageInfo.UninstallPackagePath);

                    // Process requirements
                    ProcessRequirements(pkgInfo, name, manifestItems, requiredItems);

                    // Process update_for
                    ProcessUpdateFor(pkgInfo, name, manifestItems);

                    // Generate metadata key
                    var metakey = GenerateMetakey(pkgInfo);

                    // Store in database
                    if (!pkgInfoDb.ContainsKey(metakey))
                        pkgInfoDb[metakey] = new Dictionary<string, List<PackageInfo>>();
                    
                    if (!pkgInfoDb[metakey].ContainsKey(version))
                        pkgInfoDb[metakey][version] = new List<PackageInfo>();
                    
                    pkgInfoDb[metakey][version].Add(packageInfo);
                    pkgInfoCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing pkginfo {PkgInfoName}", pkgInfoName);
                    Console.WriteLine($"Error processing pkginfo {pkgInfoName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pkginfo list");
            Console.WriteLine($"Error getting pkginfo list: {ex.Message}");
        }

        return (pkgInfoDb, requiredItems, referencedPackages, pkgInfoCount);
    }

    private PackageInfo CreatePackageInfo(Dictionary<string, object> pkgInfo, string name, string version, string resourceIdentifier, int dataLength)
    {
        var packageInfo = new PackageInfo
        {
            Name = name,
            Version = version,
            ResourceIdentifier = resourceIdentifier,
            ItemSize = dataLength
        };

        // Handle installer section (Cimian format)
        if (pkgInfo.TryGetValue("installer", out var installerObj))
        {
            Dictionary<string, object>? installer = null;
            
            // Handle different types that YamlDotNet might return
            if (installerObj is Dictionary<string, object> directDict)
            {
                installer = directDict;
            }
            else if (installerObj is IDictionary<object, object> objectDict)
            {
                installer = objectDict.ToDictionary(
                    kvp => kvp.Key?.ToString() ?? "",
                    kvp => kvp.Value ?? new object()
                );
            }
            
            if (installer != null)
            {
                if (installer.TryGetValue("location", out var locationObj) && locationObj is string location)
                {
                    packageInfo.PackagePath = NormalizePath(location);
                }

                if (installer.TryGetValue("size", out var sizeObj) && IsNumeric(sizeObj))
                {
                    packageInfo.PackageSize = Convert.ToInt64(sizeObj);
                }
            }
        }
        // Fallback to Munki format
        else if (pkgInfo.TryGetValue("installer_item_location", out var packagePath) && packagePath is string pkgPath)
        {
            packageInfo.PackagePath = NormalizePath(pkgPath);
        }

        if (pkgInfo.TryGetValue("installer_item_size", out var packageSize) && IsNumeric(packageSize))
        {
            packageInfo.PackageSize = Convert.ToInt64(packageSize) * 1024; // Convert KB to bytes
        }

        // Handle uninstaller section (Cimian format)
        if (pkgInfo.TryGetValue("uninstaller", out var uninstallerObj) && uninstallerObj is Dictionary<string, object> uninstaller)
        {
            if (uninstaller.TryGetValue("location", out var uninstallLocationObj) && uninstallLocationObj is string uninstallLocation)
            {
                packageInfo.UninstallPackagePath = NormalizePath(uninstallLocation);
            }
        }
        // Fallback to Munki format
        else if (pkgInfo.TryGetValue("uninstaller_item_location", out var uninstallPath) && uninstallPath is string uninstallPkgPath)
        {
            packageInfo.UninstallPackagePath = NormalizePath(uninstallPkgPath);
        }

        if (pkgInfo.TryGetValue("uninstaller_item_size", out var uninstallSize) && IsNumeric(uninstallSize))
        {
            packageInfo.UninstallPackageSize = Convert.ToInt64(uninstallSize) * 1024; // Convert KB to bytes
        }

        // Parse catalogs
        if (pkgInfo.TryGetValue("catalogs", out var catalogsObj) && catalogsObj is Newtonsoft.Json.Linq.JArray catalogsArray)
        {
            packageInfo.Catalogs = catalogsArray.Select(c => c.ToString()).ToList();
        }

        // Parse other fields as needed
        if (pkgInfo.TryGetValue("minimum_munki_version", out var minMunkiVersion) && minMunkiVersion is string minMunki)
        {
            packageInfo.MinimumMunkiVersion = minMunki;
        }

        if (pkgInfo.TryGetValue("minimum_os_version", out var minOsVersion) && minOsVersion is string minOs)
        {
            packageInfo.MinimumOsVersion = minOs;
        }

        if (pkgInfo.TryGetValue("maximum_os_version", out var maxOsVersion) && maxOsVersion is string maxOs)
        {
            packageInfo.MaximumOsVersion = maxOs;
        }

        if (pkgInfo.TryGetValue("supported_architectures", out var archObj) && archObj is Newtonsoft.Json.Linq.JArray archArray)
        {
            packageInfo.SupportedArchitectures = archArray.Select(a => a.ToString()).ToList();
        }

        if (pkgInfo.TryGetValue("installable_condition", out var installableCondition) && installableCondition is string installCond)
        {
            packageInfo.InstallableCondition = installCond;
        }

        if (pkgInfo.TryGetValue("uninstall_method", out var uninstallMethod) && uninstallMethod is string uninstallMeth)
        {
            packageInfo.UninstallMethod = uninstallMeth;
        }

        // Parse receipts if uninstall_method is removepackages
        if (packageInfo.UninstallMethod == "removepackages" && pkgInfo.TryGetValue("receipts", out var receiptsObj) && receiptsObj is Newtonsoft.Json.Linq.JArray receiptsArray)
        {
            packageInfo.Receipts = receiptsArray
                .Select(r => r.ToObject<Dictionary<string, object>>())
                .Where(r => r != null && r.ContainsKey("packageid"))
                .Select(r => new Receipt { PackageId = r!["packageid"]?.ToString() ?? string.Empty })
                .ToList();
        }

        return packageInfo;
    }

    private void ProcessRequirements(Dictionary<string, object> pkgInfo, string name, HashSet<string> manifestItems, HashSet<(string name, string version)> requiredItems)
    {
        if (!pkgInfo.TryGetValue("requires", out var requiresObj)) return;

        List<string> dependencies;
        
        if (requiresObj is string singleDependency)
        {
            dependencies = new List<string> { singleDependency };
        }
        else if (requiresObj is Newtonsoft.Json.Linq.JArray dependenciesArray)
        {
            dependencies = dependenciesArray.Select(d => d.ToString()).ToList();
        }
        else
        {
            return;
        }

        foreach (var dependency in dependencies)
        {
            var (requiredName, requiredVersion) = ParseNameAndVersion(dependency);
            
            if (!string.IsNullOrEmpty(requiredVersion))
            {
                requiredItems.Add((requiredName, requiredVersion));
            }

            // If this item is in a manifest, then anything it requires should be treated as if it, too, is in a manifest
            if (manifestItems.Contains(name))
            {
                manifestItems.Add(requiredName);
            }
        }
    }

    private void ProcessUpdateFor(Dictionary<string, object> pkgInfo, string name, HashSet<string> manifestItems)
    {
        if (!pkgInfo.TryGetValue("update_for", out var updateForObj)) return;

        List<string> updateItems;
        
        if (updateForObj is string singleUpdateItem)
        {
            updateItems = new List<string> { singleUpdateItem };
        }
        else if (updateForObj is Newtonsoft.Json.Linq.JArray updateItemsArray)
        {
            updateItems = updateItemsArray.Select(u => u.ToString()).ToList();
        }
        else
        {
            return;
        }

        foreach (var updateItem in updateItems)
        {
            var (updateItemName, _) = ParseNameAndVersion(updateItem);
            
            if (manifestItems.Contains(updateItemName))
            {
                manifestItems.Add(name);
            }
        }
    }

    private string GenerateMetakey(Dictionary<string, object> pkgInfo)
    {
        var metakey = new StringBuilder();
        var keysToHash = new[] { "name", "catalogs", "minimum_munki_version", "minimum_os_version", "maximum_os_version", "supported_architectures", "installable_condition" };

        // Add receipts to hash if uninstall_method is removepackages
        var includeReceipts = pkgInfo.TryGetValue("uninstall_method", out var uninstallMethod) && uninstallMethod.ToString() == "removepackages";

        foreach (var key in keysToHash)
        {
            if (pkgInfo.TryGetValue(key, out var value) && value != null)
            {
                string valueString;
                
                if (key == "catalogs" && value is Newtonsoft.Json.Linq.JArray catalogsArray)
                {
                    valueString = string.Join(", ", catalogsArray.Select(c => c.ToString()).OrderBy(c => c));
                }
                else if (key == "supported_architectures" && value is Newtonsoft.Json.Linq.JArray archArray)
                {
                    valueString = string.Join(", ", archArray.Select(a => a.ToString()).OrderBy(a => a));
                }
                else
                {
                    valueString = value.ToString() ?? string.Empty;
                }

                if (!string.IsNullOrEmpty(valueString))
                {
                    metakey.AppendLine($"{key}: {valueString}");
                }
            }
        }

        if (includeReceipts && pkgInfo.TryGetValue("receipts", out var receiptsObj) && receiptsObj is Newtonsoft.Json.Linq.JArray receiptsArray)
        {
            var receiptIds = receiptsArray
                .Select(r => r.ToObject<Dictionary<string, object>>())
                .Where(r => r != null && r.ContainsKey("packageid"))
                .Select(r => r!["packageid"]?.ToString() ?? string.Empty)
                .OrderBy(id => id);

            var receiptsString = string.Join(", ", receiptIds);
            if (!string.IsNullOrEmpty(receiptsString))
            {
                metakey.AppendLine($"receipts: {receiptsString}");
            }
        }

        return metakey.ToString().TrimEnd('\r', '\n');
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

    private Dictionary<string, object>? ParsePkgInfoData(string data, string pkgInfoName)
    {
        try
        {
            // First try YAML parsing
            if (pkgInfoName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || 
                pkgInfoName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                // Always try the simplified approach first for YAML files
                var result = new Dictionary<string, object>();
                var lines = data.Split('\n');
                bool foundName = false, foundVersion = false;
                
                // Extract basic fields
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("name:") && !foundName)
                    {
                        var name = trimmed.Substring(5).Trim();
                        // Remove quotes if present
                        if ((name.StartsWith("\"") && name.EndsWith("\"")) || (name.StartsWith("'") && name.EndsWith("'")))
                            name = name.Substring(1, name.Length - 2);
                        result["name"] = name;
                        foundName = true;
                    }
                    else if (trimmed.StartsWith("version:") && !foundVersion)
                    {
                        var version = trimmed.Substring(8).Trim();
                        // Remove quotes if present
                        if ((version.StartsWith("\"") && version.EndsWith("\"")) || (version.StartsWith("'") && version.EndsWith("'")))
                            version = version.Substring(1, version.Length - 2);
                        result["version"] = version;
                        foundVersion = true;
                    }
                }
                
                // Extract installer location
                var installerLocation = ExtractInstallerLocationFromYaml(data);
                if (!string.IsNullOrEmpty(installerLocation))
                {
                    var installer = new Dictionary<string, object>
                    {
                        ["location"] = installerLocation
                    };
                    result["installer"] = installer;
                }
                
                // If we found at least name and version, return the simplified result
                if (foundName && foundVersion)
                {
                    return result;
                }
                
                // Fallback to full YAML parsing if simplified approach didn't work
                try
                {
                    var yamlStream = new YamlStream();
                    yamlStream.Load(new StringReader(data));
                    
                    if (yamlStream.Documents.Count > 0 && yamlStream.Documents[0].RootNode is YamlMappingNode rootNode)
                    {
                        return ConvertYamlMappingToDict(rootNode);
                    }
                }
                catch
                {
                    // If full YAML parsing fails, return the partial result if we have name/version
                    if (foundName && foundVersion)
                    {
                        return result;
                    }
                    throw;
                }
            }

            // Fall back to JSON parsing
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing pkginfo file {PkgInfoName}", pkgInfoName);
            return null;
        }
    }

    private string ExtractInstallerLocationFromYaml(string yamlContent)
    {
        var lines = yamlContent.Split('\n');
        bool inInstaller = false;
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            
            // Check for installer section start
            if (trimmed == "installer:")
            {
                inInstaller = true;
                continue;
            }
            
            if (inInstaller)
            {
                // If we hit another top-level key (no indentation), we're out of installer section
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith(" ") && !line.StartsWith("\t") && line.Contains(":"))
                {
                    inInstaller = false;
                    continue;
                }
                
                // Look for location within installer section
                if (trimmed.StartsWith("location:"))
                {
                    var location = trimmed.Substring(9).Trim();
                    // Remove quotes if present
                    location = location.Trim('"', '\'');
                    return location;
                }
            }
        }
        
        return "";
    }

    private string ExtractFieldFromYaml(string yamlContent, string fieldName)
    {
        var lines = yamlContent.Split('\n');
        var pattern = $"{fieldName}:";
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(pattern) && !trimmed.StartsWith($"{fieldName}_"))
            {
                var value = trimmed.Substring(pattern.Length).Trim();
                // Remove quotes if present
                value = value.Trim('"', '\'');
                return value;
            }
        }
        
        return "";
    }

    private Dictionary<string, object> ConvertYamlMappingToDict(YamlMappingNode mappingNode)
    {
        var result = new Dictionary<string, object>();
        
        foreach (var kvp in mappingNode.Children)
        {
            if (kvp.Key is YamlScalarNode keyNode)
            {
                string key = keyNode.Value ?? "";
                object value = ConvertYamlNode(kvp.Value);
                result[key] = value;
            }
        }
        
        return result;
    }

    private object ConvertYamlNode(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => scalar.Value ?? "",
            YamlSequenceNode sequence => sequence.Children.Select(ConvertYamlNode).Cast<object>().ToList(),
            YamlMappingNode mapping => ConvertYamlMappingToDict(mapping),
            _ => ""
        };
    }

    private string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Remove leading backslash/slash and keep Windows path separators for consistency with file system
        var normalizedPath = path.TrimStart('\\', '/');
        
        return normalizedPath;
    }

    private static bool IsNumeric(object value)
    {
        return value is byte || value is sbyte ||
               value is short || value is ushort ||
               value is int || value is uint ||
               value is long || value is ulong ||
               value is float || value is double ||
               value is decimal ||
               (value is string str && double.TryParse(str, out _));
    }
}
