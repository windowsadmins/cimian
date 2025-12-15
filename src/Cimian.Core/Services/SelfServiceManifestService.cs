// SelfServiceManifestService.cs - Manages user's self-service software requests
// Port of pkg/selfservice/selfservice.go
// Handles user-writable manifest for requesting installs/removals without admin privileges

using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.Core.Services;

/// <summary>
/// Represents the self-service manifest structure
/// User-writable file for requesting software installs/removals
/// </summary>
public class SelfServiceManifest
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "SelfServeManifest";

    [YamlMember(Alias = "managed_installs")]
    public List<string> ManagedInstalls { get; set; } = [];

    [YamlMember(Alias = "managed_uninstalls")]
    public List<string> ManagedUninstalls { get; set; } = [];

    [YamlMember(Alias = "optional_installs")]
    public List<string> OptionalInstalls { get; set; } = [];
}

/// <summary>
/// Interface for self-service manifest operations
/// </summary>
public interface ISelfServiceManifestService
{
    /// <summary>
    /// Load the self-service manifest from disk
    /// </summary>
    Task<SelfServiceManifest> LoadAsync();

    /// <summary>
    /// Save the self-service manifest to disk
    /// </summary>
    Task SaveAsync(SelfServiceManifest manifest);

    /// <summary>
    /// Add an item to managed_installs (user requests install)
    /// </summary>
    Task AddInstallRequestAsync(string itemName);

    /// <summary>
    /// Add an item to managed_uninstalls (user requests removal)
    /// </summary>
    Task AddRemovalRequestAsync(string itemName);

    /// <summary>
    /// Remove an item from the manifest (cancel pending request)
    /// </summary>
    Task RemoveRequestAsync(string itemName);

    /// <summary>
    /// Check if an item is currently requested for install
    /// </summary>
    Task<bool> IsInstallRequestedAsync(string itemName);

    /// <summary>
    /// Check if an item is currently requested for removal
    /// </summary>
    Task<bool> IsRemovalRequestedAsync(string itemName);

    /// <summary>
    /// Get all items in the self-service manifest
    /// </summary>
    Task<SelfServiceManifest> GetAllRequestsAsync();
}

/// <summary>
/// Service for managing the self-service manifest
/// Mirrors Go implementation in pkg/selfservice/selfservice.go
/// </summary>
public class SelfServiceManifestService : ISelfServiceManifestService
{
    private const string SelfServiceManifestPath = @"C:\ProgramData\ManagedInstalls\SelfServeManifest.yaml";
    
    private readonly ILogger<SelfServiceManifestService>? _logger;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SelfServiceManifestService(ILogger<SelfServiceManifestService>? logger = null)
    {
        _logger = logger;
        
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitEmptyCollections)
            .Build();
    }

    /// <inheritdoc />
    public async Task<SelfServiceManifest> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(SelfServiceManifestPath))
            {
                _logger?.LogDebug("Self-service manifest does not exist, returning empty manifest");
                return new SelfServiceManifest();
            }

            var content = await File.ReadAllTextAsync(SelfServiceManifestPath);
            var manifest = _deserializer.Deserialize<SelfServiceManifest>(content);

            if (manifest == null)
            {
                return new SelfServiceManifest();
            }

            // Ensure name is set
            if (string.IsNullOrEmpty(manifest.Name))
            {
                manifest.Name = "SelfServeManifest";
            }

            // Ensure collections are initialized
            manifest.ManagedInstalls ??= [];
            manifest.ManagedUninstalls ??= [];
            manifest.OptionalInstalls ??= [];

            _logger?.LogDebug("Loaded self-service manifest with {InstallCount} install requests and {UninstallCount} uninstall requests",
                manifest.ManagedInstalls.Count, manifest.ManagedUninstalls.Count);

            return manifest;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(SelfServiceManifest manifest)
    {
        await _lock.WaitAsync();
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(SelfServiceManifestPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var yaml = _serializer.Serialize(manifest);
            await File.WriteAllTextAsync(SelfServiceManifestPath, yaml);

            _logger?.LogDebug("Saved self-service manifest to {Path}", SelfServiceManifestPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task AddInstallRequestAsync(string itemName)
    {
        _logger?.LogInformation("Adding install request for {ItemName}", itemName);

        var manifest = await LoadAsync();

        // Check if already in managed_installs
        if (manifest.ManagedInstalls.Any(x => x.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger?.LogDebug("Item {ItemName} already in install requests", itemName);
            return;
        }

        // Remove from managed_uninstalls if present (cancel removal)
        manifest.ManagedUninstalls = manifest.ManagedUninstalls
            .Where(x => !x.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Add to managed_installs
        manifest.ManagedInstalls.Add(itemName);

        await SaveAsync(manifest);
        _logger?.LogInformation("Successfully added install request for {ItemName}", itemName);
    }

    /// <inheritdoc />
    public async Task AddRemovalRequestAsync(string itemName)
    {
        _logger?.LogInformation("Adding removal request for {ItemName}", itemName);

        var manifest = await LoadAsync();

        // Check if already in managed_uninstalls
        if (manifest.ManagedUninstalls.Any(x => x.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger?.LogDebug("Item {ItemName} already in removal requests", itemName);
            return;
        }

        // Remove from managed_installs if present (cancel install)
        manifest.ManagedInstalls = manifest.ManagedInstalls
            .Where(x => !x.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Remove from optional_installs as well
        manifest.OptionalInstalls = manifest.OptionalInstalls
            .Where(x => !x.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Add to managed_uninstalls
        manifest.ManagedUninstalls.Add(itemName);

        await SaveAsync(manifest);
        _logger?.LogInformation("Successfully added removal request for {ItemName}", itemName);
    }

    /// <inheritdoc />
    public async Task RemoveRequestAsync(string itemName)
    {
        _logger?.LogInformation("Removing request for {ItemName}", itemName);

        var manifest = await LoadAsync();

        var originalInstallCount = manifest.ManagedInstalls.Count;
        var originalUninstallCount = manifest.ManagedUninstalls.Count;
        var originalOptionalCount = manifest.OptionalInstalls.Count;

        // Remove from all lists
        manifest.ManagedInstalls = manifest.ManagedInstalls
            .Where(x => !x.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        manifest.ManagedUninstalls = manifest.ManagedUninstalls
            .Where(x => !x.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        manifest.OptionalInstalls = manifest.OptionalInstalls
            .Where(x => !x.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var wasRemoved = manifest.ManagedInstalls.Count != originalInstallCount ||
                         manifest.ManagedUninstalls.Count != originalUninstallCount ||
                         manifest.OptionalInstalls.Count != originalOptionalCount;

        if (wasRemoved)
        {
            await SaveAsync(manifest);
            _logger?.LogInformation("Successfully removed request for {ItemName}", itemName);
        }
        else
        {
            _logger?.LogDebug("Item {ItemName} was not in self-service manifest", itemName);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsInstallRequestedAsync(string itemName)
    {
        var manifest = await LoadAsync();
        return manifest.ManagedInstalls.Any(x => x.Equals(itemName, StringComparison.OrdinalIgnoreCase)) ||
               manifest.OptionalInstalls.Any(x => x.Equals(itemName, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<bool> IsRemovalRequestedAsync(string itemName)
    {
        var manifest = await LoadAsync();
        return manifest.ManagedUninstalls.Any(x => x.Equals(itemName, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<SelfServiceManifest> GetAllRequestsAsync()
    {
        return await LoadAsync();
    }
}
