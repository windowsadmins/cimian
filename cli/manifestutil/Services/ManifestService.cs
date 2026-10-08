using Cimian.CLI.Manifestutil.Models;
using Cimian.Core.Services;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Cimian.CLI.Manifestutil.Services;

/// <summary>
/// Service for managing package deployment manifests
/// Migrated from Go: cmd/manifestutil/main.go
/// </summary>
public class ManifestService
{
    public ManifestService()
    {
    }

    /// <summary>
    /// Lists all available manifests from the manifest directory
    /// </summary>
    public IEnumerable<string> ListManifests(string manifestDir)
    {
        if (!Directory.Exists(manifestDir))
        {
            throw new DirectoryNotFoundException($"Manifest directory not found: {manifestDir}");
        }

        return Directory.GetFiles(manifestDir, "*.yaml")
            .Select(Path.GetFileName)
            .Where(name => name != null)
            .Cast<string>()
            .OrderBy(name => name);
    }

    /// <summary>
    /// Loads a manifest from a YAML file
    /// </summary>
    public PackageManifest GetManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest file not found: {manifestPath}");
        }

        var yaml = File.ReadAllText(manifestPath);
        // YamlUtils.DeserializeManifest does the included_manifests \ → /
        // normalization for us — every manifest consumer (manifestutil,
        // CimianStudio) gets the same path shape without duplicating the loop.
        var manifest = YamlUtils.DeserializeManifest<PackageManifest>(yaml)
            ?? throw new InvalidDataException($"Manifest is empty or malformed: {manifestPath}");
        manifest.Source = ParseMapping(yaml);
        return manifest;
    }

    /// <summary>
    /// Saves a manifest to a YAML file
    /// </summary>
    public void SaveManifest(string manifestPath, PackageManifest manifest)
    {
        // SerializeManifest performs included_manifests path normalization
        // before emit.
        var yaml = MergeWithSource(YamlUtils.SerializeManifest(manifest), manifest.Source);

        var directory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(manifestPath, yaml);
    }

    private static YamlMappingNode? ParseMapping(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
    }

    /// <summary>
    /// Rebuilds the manifest in the order of the file it was read from. A key the model
    /// declares takes the model's value, so edits apply, and is left out if the model
    /// no longer has it (a section emptied by --remove-pkg). Any other key is written
    /// back exactly as it was read, which is what keeps conditional_items, default_installs,
    /// featured_items, managed_profiles and managed_apps. Keys the model adds go last.
    /// </summary>
    private static string MergeWithSource(string modelYaml, YamlMappingNode? source)
    {
        if (source == null || ParseMapping(modelYaml) is not { } model)
            return modelYaml;

        var declared = typeof(PackageManifest).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(YamlIgnoreAttribute), false).Length == 0)
            .Select(p => (p.GetCustomAttributes(typeof(YamlMemberAttribute), false).FirstOrDefault() as YamlMemberAttribute)?.Alias ?? p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var merged = new YamlMappingNode();
        foreach (var (key, value) in source.Children)
        {
            var name = (key as YamlScalarNode)?.Value;
            if (name == null || !declared.Contains(name))
            {
                merged.Add(key, value);
            }
            else if (model.Children.TryGetValue(key, out var modelValue))
            {
                merged.Add(key, modelValue);
            }
        }

        foreach (var (key, value) in model.Children)
        {
            if (!merged.Children.ContainsKey(key))
                merged.Add(key, value);
        }

        return YamlUtils.Serializer.Serialize(merged);
    }

    /// <summary>
    /// Creates a new empty manifest file
    /// </summary>
    public void CreateNewManifest(string manifestPath, string name)
    {
        var manifest = new PackageManifest
        {
            Name = name,
            ManagedInstalls = null,
            ManagedUninstalls = null,
            ManagedUpdates = null,
            OptionalInstalls = null,
            IncludedManifests = null,
            Catalogs = null
        };

        SaveManifest(manifestPath, manifest);
    }

    /// <summary>
    /// Adds a package to the specified section of a manifest
    /// </summary>
    public void AddPackageToManifest(PackageManifest manifest, string package, ManifestSection section)
    {
        var list = GetOrCreateSection(manifest, section);
        
        if (!list.Contains(package, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(package);
        }
    }

    /// <summary>
    /// Removes a package from the specified section of a manifest
    /// </summary>
    public bool RemovePackageFromManifest(PackageManifest manifest, string package, ManifestSection section)
    {
        var list = GetSection(manifest, section);
        if (list == null)
        {
            return false;
        }

        var item = list.FirstOrDefault(p => p.Equals(package, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            list.Remove(item);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Loads the Cimian configuration from the default path
    /// </summary>
    public CimianConfig LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Config file not found: {configPath}");
        }

        var yaml = File.ReadAllText(configPath);
        return YamlUtils.Deserializer.Deserialize<CimianConfig>(yaml);
    }

    private List<string> GetOrCreateSection(PackageManifest manifest, ManifestSection section)
    {
        switch (section)
        {
            case ManifestSection.ManagedInstalls:
                manifest.ManagedInstalls ??= new List<string>();
                return manifest.ManagedInstalls;
            case ManifestSection.ManagedUninstalls:
                manifest.ManagedUninstalls ??= new List<string>();
                return manifest.ManagedUninstalls;
            case ManifestSection.ManagedUpdates:
                manifest.ManagedUpdates ??= new List<string>();
                return manifest.ManagedUpdates;
            case ManifestSection.OptionalInstalls:
                manifest.OptionalInstalls ??= new List<string>();
                return manifest.OptionalInstalls;
            default:
                throw new ArgumentException($"Invalid section: {section}", nameof(section));
        }
    }

    private List<string>? GetSection(PackageManifest manifest, ManifestSection section)
    {
        return section switch
        {
            ManifestSection.ManagedInstalls => manifest.ManagedInstalls,
            ManifestSection.ManagedUninstalls => manifest.ManagedUninstalls,
            ManifestSection.ManagedUpdates => manifest.ManagedUpdates,
            ManifestSection.OptionalInstalls => manifest.OptionalInstalls,
            _ => throw new ArgumentException($"Invalid section: {section}", nameof(section))
        };
    }
}

/// <summary>
/// Manifest sections that can contain packages
/// </summary>
public enum ManifestSection
{
    ManagedInstalls,
    ManagedUninstalls,
    ManagedUpdates,
    OptionalInstalls
}

/// <summary>
/// Extension methods for parsing section strings
/// </summary>
public static class ManifestSectionExtensions
{
    public static ManifestSection Parse(string section)
    {
        return section.ToLowerInvariant() switch
        {
            "managed_installs" => ManifestSection.ManagedInstalls,
            "managed_uninstalls" => ManifestSection.ManagedUninstalls,
            "managed_updates" => ManifestSection.ManagedUpdates,
            "optional_installs" => ManifestSection.OptionalInstalls,
            _ => throw new ArgumentException($"Invalid section: {section}. Valid sections: managed_installs, managed_uninstalls, managed_updates, optional_installs")
        };
    }

    public static string ToYamlName(this ManifestSection section)
    {
        return section switch
        {
            ManifestSection.ManagedInstalls => "managed_installs",
            ManifestSection.ManagedUninstalls => "managed_uninstalls",
            ManifestSection.ManagedUpdates => "managed_updates",
            ManifestSection.OptionalInstalls => "optional_installs",
            _ => throw new ArgumentException($"Invalid section: {section}")
        };
    }
}
