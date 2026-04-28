using Cimian.CLI.Manifestutil.Models;
using Cimian.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Manifestutil.Services;

/// <summary>
/// Service for managing self-service manifests (user-requested installs)
/// Migrated from Go: pkg/selfservice/selfservice.go
/// </summary>
public class SelfServiceManifestService
{
    /// <summary>
    /// Default path to the self-service manifest
    /// </summary>
    public static readonly string DefaultManifestPath = CimianPaths.SelfServeManifestYaml;

    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly string _manifestPath;

    public SelfServiceManifestService(string? manifestPath = null)
    {
        _manifestPath = manifestPath ?? DefaultManifestPath;

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
    /// Loads the self-service manifest from disk.
    /// Returns an empty manifest if the file doesn't exist.
    /// </summary>
    public SelfServiceManifest Load()
    {
        if (!File.Exists(_manifestPath))
        {
            return new SelfServiceManifest
            {
                Name = "SelfServeManifest",
                ManagedInstalls = new List<string>(),
                ManagedUninstalls = new List<string>(),
                OptionalInstalls = new List<string>()
            };
        }

        var yaml = File.ReadAllText(_manifestPath);
        var manifest = _deserializer.Deserialize<SelfServiceManifest>(yaml);

        // Ensure the manifest has a name
        if (string.IsNullOrEmpty(manifest.Name))
        {
            manifest.Name = "SelfServeManifest";
        }

        // Initialize null lists
        manifest.ManagedInstalls ??= new List<string>();
        manifest.ManagedUninstalls ??= new List<string>();
        manifest.OptionalInstalls ??= new List<string>();

        return manifest;
    }

    /// <summary>
    /// Saves the self-service manifest to disk
    /// </summary>
    public void Save(SelfServiceManifest manifest)
    {
        // Ensure the directory exists
        var directory = Path.GetDirectoryName(_manifestPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var yaml = _serializer.Serialize(manifest);
        File.WriteAllText(_manifestPath, yaml);
    }

    /// <summary>
    /// Adds a package to the self-service manifest for installation
    /// </summary>
    public bool AddToInstalls(string packageName)
    {
        var manifest = Load();

        // Check if already present (case-insensitive)
        if (manifest.ManagedInstalls.Any(p => p.Equals(packageName, StringComparison.OrdinalIgnoreCase)))
        {
            return false; // Already exists
        }

        manifest.ManagedInstalls.Add(packageName);
        Save(manifest);
        return true;
    }

    /// <summary>
    /// Removes a package from the self-service manifest
    /// </summary>
    public bool RemoveFromInstalls(string packageName)
    {
        var manifest = Load();
        var removedFromManaged = RemoveFromList(manifest.ManagedInstalls, packageName);
        var removedFromOptional = RemoveFromList(manifest.OptionalInstalls, packageName);

        if (removedFromManaged || removedFromOptional)
        {
            Save(manifest);
            return true;
        }

        return false;
    }

    private static bool RemoveFromList(List<string> list, string item)
    {
        var existing = list.FirstOrDefault(p => p.Equals(item, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            list.Remove(existing);
            return true;
        }
        return false;
    }
}

/// <summary>
/// Self-service manifest structure
/// </summary>
public class SelfServiceManifest
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "SelfServeManifest";

    [YamlMember(Alias = "managed_installs")]
    public List<string> ManagedInstalls { get; set; } = new();

    [YamlMember(Alias = "managed_uninstalls")]
    public List<string> ManagedUninstalls { get; set; } = new();

    [YamlMember(Alias = "optional_installs")]
    public List<string> OptionalInstalls { get; set; } = new();
}
