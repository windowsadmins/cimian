using Cimian.CLI.Manifestutil.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Manifestutil.Services;

/// <summary>
/// Service for managing package deployment manifests
/// Migrated from Go: cmd/manifestutil/main.go
/// </summary>
public class ManifestService
{
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;

    public ManifestService()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .Build();
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
        var manifest = _deserializer.Deserialize<PackageManifest>(yaml);

        // Normalize included_manifests paths to forward slashes
        if (manifest.IncludedManifests != null)
        {
            manifest.IncludedManifests = manifest.IncludedManifests
                .Select(path => path.Replace('\\', '/'))
                .ToList();
        }

        return manifest;
    }

    /// <summary>
    /// Saves a manifest to a YAML file
    /// </summary>
    public void SaveManifest(string manifestPath, PackageManifest manifest)
    {
        // Normalize included_manifests paths to forward slashes before saving
        if (manifest.IncludedManifests != null)
        {
            manifest.IncludedManifests = manifest.IncludedManifests
                .Select(path => path.Replace('\\', '/'))
                .ToList();
        }

        var yaml = _serializer.Serialize(manifest);
        
        // Ensure directory exists
        var directory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(manifestPath, yaml);
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
        return _deserializer.Deserialize<CimianConfig>(yaml);
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
