using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cimian.CLI.Repoclean.Services;

public class PkgInfoAnalyzer : IPkgInfoAnalyzer
{
    private readonly ILogger<PkgInfoAnalyzer> _logger;

    public PkgInfoAnalyzer(ILogger<PkgInfoAnalyzer> logger)
    {
        _logger = logger;
    }

    public async Task<PkgInfoAnalysis> AnalyzePkgInfoAsync(IFileRepository repository, HashSet<string> manifestItems)
    {
        Console.WriteLine("Analyzing pkginfo files...");

        var analysis = new PkgInfoAnalysis();
        var pkgInfoList = await repository.GetItemListAsync("pkgsinfo");

        foreach (var pkgInfoName in pkgInfoList.Where(IsPkgInfoFile))
        {
            var pkgInfoPath = Path.Combine("pkgsinfo", pkgInfoName);
            Dictionary<string, object> pkgInfo;
            string data;
            try
            {
                data = await repository.GetContentAsync(pkgInfoPath);
                pkgInfo = ParsePkgInfoData(data, pkgInfoName);
            }
            catch (Exception ex)
            {
                // An unreadable pkgsinfo still owns a payload. Leaving it out silently
                // would list that payload as orphaned, so the caller must know.
                _logger.LogDebug(ex, "Error parsing pkginfo {PkgInfoName}", pkgInfoName);
                analysis.ParseErrors.Add($"{pkgInfoPath}: {ex.Message}");
                continue;
            }

            var name = YamlValues.AsString(pkgInfo.GetValueOrDefault("name"));
            var version = YamlValues.AsString(pkgInfo.GetValueOrDefault("version"));
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
            {
                analysis.ParseErrors.Add($"{pkgInfoPath}: missing 'name' or 'version'");
                continue;
            }

            var packageInfo = CreatePackageInfo(pkgInfo, name, version, pkgInfoPath, data.Length);

            foreach (var payload in packageInfo.PayloadPaths)
            {
                analysis.ReferencedPackages.Add(payload);
            }

            ProcessRequirements(packageInfo, manifestItems, analysis.RequiredItems);
            ProcessUpdateFor(packageInfo, manifestItems);

            var metakey = GenerateMetakey(pkgInfo);

            if (!analysis.PkgInfoDb.TryGetValue(metakey, out var versions))
            {
                versions = new Dictionary<string, List<PackageInfo>>();
                analysis.PkgInfoDb[metakey] = versions;
            }

            if (!versions.TryGetValue(version, out var items))
            {
                items = new List<PackageInfo>();
                versions[version] = items;
            }

            items.Add(packageInfo);
            analysis.PkgInfoCount++;
        }

        return analysis;
    }

    private static PackageInfo CreatePackageInfo(Dictionary<string, object> pkgInfo, string name, string version, string resourceIdentifier, int dataLength)
    {
        var packageInfo = new PackageInfo
        {
            Name = name,
            Version = version,
            ResourceIdentifier = resourceIdentifier,
            ItemSize = dataLength,
            Requires = YamlValues.AsStringList(pkgInfo.GetValueOrDefault("requires")),
            UpdateFor = YamlValues.AsStringList(pkgInfo.GetValueOrDefault("update_for")),
            Catalogs = YamlValues.AsStringList(pkgInfo.GetValueOrDefault("catalogs")),
            SupportedArchitectures = YamlValues.AsStringList(pkgInfo.GetValueOrDefault("supported_architectures")),
            MinimumCimianVersion = YamlValues.AsString(pkgInfo.GetValueOrDefault("minimum_cimian_version")) ?? string.Empty,
            MinimumOsVersion = YamlValues.AsString(pkgInfo.GetValueOrDefault("minimum_os_version")) ?? string.Empty,
            MaximumOsVersion = YamlValues.AsString(pkgInfo.GetValueOrDefault("maximum_os_version")) ?? string.Empty,
            InstallableCondition = YamlValues.AsString(pkgInfo.GetValueOrDefault("installable_condition")) ?? string.Empty,
            UninstallMethod = YamlValues.AsString(pkgInfo.GetValueOrDefault("uninstall_method")) ?? string.Empty
        };

        // installer: (Cimian), with the Munki-style installer_item_location as fallback
        if (YamlValues.AsMapping(pkgInfo.GetValueOrDefault("installer")) is { } installer)
        {
            if (YamlValues.AsString(installer.GetValueOrDefault("location")) is { Length: > 0 } location)
            {
                packageInfo.PackagePath = YamlValues.NormalizeRepoPath(location);
            }
            if (TryGetLong(installer.GetValueOrDefault("size"), out var size))
            {
                packageInfo.PackageSize = size;
            }
        }
        else if (YamlValues.AsString(pkgInfo.GetValueOrDefault("installer_item_location")) is { Length: > 0 } pkgPath)
        {
            packageInfo.PackagePath = YamlValues.NormalizeRepoPath(pkgPath);
        }

        if (TryGetLong(pkgInfo.GetValueOrDefault("installer_item_size"), out var itemSizeKb))
        {
            packageInfo.PackageSize = itemSizeKb * 1024;
        }

        // uninstaller: is a list in Cimian pkgsinfo (one entry per uninstaller); a single
        // mapping is accepted too. Each entry with a location owns a payload in pkgs/.
        foreach (var uninstaller in YamlValues.AsMappingList(pkgInfo.GetValueOrDefault("uninstaller")))
        {
            if (YamlValues.AsString(uninstaller.GetValueOrDefault("location")) is { Length: > 0 } location)
            {
                packageInfo.UninstallPackagePaths.Add(YamlValues.NormalizeRepoPath(location));
            }
            if (TryGetLong(uninstaller.GetValueOrDefault("size"), out var size))
            {
                packageInfo.UninstallPackageSize += size;
            }
        }

        if (YamlValues.AsString(pkgInfo.GetValueOrDefault("uninstaller_item_location")) is { Length: > 0 } uninstallPath)
        {
            packageInfo.UninstallPackagePaths.Add(YamlValues.NormalizeRepoPath(uninstallPath));
        }

        if (TryGetLong(pkgInfo.GetValueOrDefault("uninstaller_item_size"), out var uninstallSizeKb))
        {
            packageInfo.UninstallPackageSize = uninstallSizeKb * 1024;
        }

        if (packageInfo.UninstallMethod == "removepackages")
        {
            packageInfo.Receipts = YamlValues.AsMappingList(pkgInfo.GetValueOrDefault("receipts"))
                .Select(r => YamlValues.AsString(r.GetValueOrDefault("packageid")))
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => new Receipt { PackageId = id! })
                .ToList();
        }

        return packageInfo;
    }

    private static void ProcessRequirements(PackageInfo item, HashSet<string> manifestItems, HashSet<(string name, string version)> requiredItems)
    {
        foreach (var dependency in item.Requires)
        {
            var (requiredName, requiredVersion) = ParseNameAndVersion(dependency);

            if (!string.IsNullOrEmpty(requiredVersion))
            {
                requiredItems.Add((requiredName, requiredVersion));
            }

            // If this item is in a manifest, then anything it requires should be treated as if it, too, is in a manifest
            if (manifestItems.Contains(item.Name))
            {
                manifestItems.Add(requiredName);
            }
        }
    }

    private static void ProcessUpdateFor(PackageInfo item, HashSet<string> manifestItems)
    {
        foreach (var updateItem in item.UpdateFor)
        {
            var (updateItemName, _) = ParseNameAndVersion(updateItem);
            if (manifestItems.Contains(updateItemName))
            {
                manifestItems.Add(item.Name);
            }
        }
    }

    private static string GenerateMetakey(Dictionary<string, object> pkgInfo)
    {
        var metakey = new StringBuilder();
        var keysToHash = new[] { "name", "catalogs", "minimum_cimian_version", "minimum_os_version", "maximum_os_version", "supported_architectures", "installable_condition" };

        // Add receipts to hash if uninstall_method is removepackages
        var includeReceipts = YamlValues.AsString(pkgInfo.GetValueOrDefault("uninstall_method")) == "removepackages";

        foreach (var key in keysToHash)
        {
            if (!pkgInfo.TryGetValue(key, out var value) || value == null)
                continue;

            var valueString = key is "catalogs" or "supported_architectures"
                ? string.Join(", ", YamlValues.AsStringList(value).OrderBy(v => v, StringComparer.Ordinal))
                : YamlValues.AsString(value) ?? string.Empty;

            if (!string.IsNullOrEmpty(valueString))
            {
                metakey.AppendLine($"{key}: {valueString}");
            }
        }

        if (includeReceipts)
        {
            var receiptIds = YamlValues.AsMappingList(pkgInfo.GetValueOrDefault("receipts"))
                .Select(r => YamlValues.AsString(r.GetValueOrDefault("packageid")) ?? string.Empty)
                .Where(id => id.Length > 0)
                .OrderBy(id => id, StringComparer.Ordinal);

            var receiptsString = string.Join(", ", receiptIds);
            if (!string.IsNullOrEmpty(receiptsString))
            {
                metakey.AppendLine($"receipts: {receiptsString}");
            }
        }

        return metakey.ToString().TrimEnd('\r', '\n');
    }

    private static (string name, string version) ParseNameAndVersion(string itemString)
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

    // makecatalogs reads YAML pkgsinfo; JSON is accepted as before. Anything else in
    // pkgsinfo/ (a README, an editor backup) is not a pkgsinfo and owns no payload.
    private static bool IsPkgInfoFile(string name) =>
        name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object> ParsePkgInfoData(string data, string pkgInfoName)
    {
        // Parse the whole document. A line scan that picked out name, version and
        // installer.location used to stand in for this, and it never saw requires,
        // uninstaller or catalogs -- the fields that decide what is safe to delete.
        if (pkgInfoName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
            pkgInfoName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            return YamlValues.ParseYamlMapping(data);
        }

        return JsonConvert.DeserializeObject<Dictionary<string, object>>(data)
            ?? throw new InvalidDataException("the document is empty");
    }

    private static bool TryGetLong(object? value, out long result)
    {
        result = 0;
        return value switch
        {
            string s => long.TryParse(s, out result),
            Newtonsoft.Json.Linq.JValue j when j.Value is not null => long.TryParse(j.ToString(), out result),
            _ => false
        };
    }
}
