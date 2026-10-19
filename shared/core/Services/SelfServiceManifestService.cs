// SelfServiceManifestService.cs - Manages user's self-service software requests
// Port of pkg/selfservice/selfservice.go
// Handles user-writable manifest for requesting installs/removals without admin privileges

using System.Security.AccessControl;
using System.Security.Principal;
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

    /// <summary>
    /// Names already offered via manifest <c>default_installs</c> (Munki 7 parity).
    /// Once recorded here, the client will not re-seed the name into
    /// <see cref="ManagedInstalls"/> — so a user who removes the item in MSC
    /// keeps it removed.
    /// </summary>
    [YamlMember(Alias = "default_installs")]
    public List<string> DefaultInstalls { get; set; } = [];
}

/// <summary>
/// Interface for self-service manifest operations
/// </summary>
public interface ISelfServiceManifestService
{
    /// <summary>
    /// Raised whenever the manifest changes on disk via this service (an install
    /// or removal request added, or a request cancelled). Lets viewmodels that
    /// derive their list from the self-serve manifest (e.g. the Updates tab's
    /// install-requested cross-reference) refresh immediately instead of waiting
    /// for the next InstallInfo rewrite. May be raised on a background thread —
    /// subscribers that touch UI must marshal.
    /// </summary>
    event EventHandler? RequestsChanged;

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
    private readonly string SelfServiceManifestPath;

    // Set for a signed-in user (Managed Software Center, manifestutil): their choices go to
    // a drop file of their own, never to SelfServeManifest.yaml. Null when running as SYSTEM.
    private readonly string? _dropDir;
    private readonly string? _userSid;
    private string? _ownDropFile;
    
    private readonly ILogger<SelfServiceManifestService>? _logger;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <inheritdoc />
    public event EventHandler? RequestsChanged;

    /// <summary>
    /// The protected copy, read and written directly. For managedsoftwareupdate (SYSTEM).
    /// </summary>
    public SelfServiceManifestService(ILogger<SelfServiceManifestService>? logger = null)
        : this(logger, CimianPaths.SelfServeManifestYaml, null, null)
    {
    }

    /// <summary>
    /// For the signed-in user: reads show the protected copy plus their own pending drop
    /// file; changes go to that drop file for managedsoftwareupdate to take in.
    /// </summary>
    public static SelfServiceManifestService ForSignedInUser(ILogger<SelfServiceManifestService>? logger = null)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new SelfServiceManifestService(logger, CimianPaths.SelfServeManifestYaml,
            CimianPaths.SelfServiceDropDir, identity.User!.Value);
    }

    /// <summary>
    /// Reads and writes <paramref name="manifestPath"/> instead of the system file, so tests
    /// do not touch the machine's SelfServeManifest.yaml.
    /// </summary>
    internal SelfServiceManifestService(string manifestPath, ILogger<SelfServiceManifestService>? logger = null)
        : this(logger, manifestPath, null, null)
    {
    }

    internal SelfServiceManifestService(ILogger<SelfServiceManifestService>? logger, string manifestPath,
        string? dropDir, string? userSid)
    {
        _logger = logger;
        SelfServiceManifestPath = manifestPath;
        _dropDir = dropDir;
        _userSid = userSid;
        
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
                var empty = new SelfServiceManifest();
                if (_userSid != null)
                {
                    SelfServiceRequests.Apply(empty, ReadOwnPendingRequests());
                }
                return empty;
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
            manifest.DefaultInstalls ??= [];

            if (_userSid != null)
            {
                SelfServiceRequests.Apply(manifest, ReadOwnPendingRequests());
            }

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
        if (_userSid != null)
        {
            throw new InvalidOperationException(
                "SelfServeManifest.yaml is written only by managedsoftwareupdate; a signed-in user's choices go to their drop file.");
        }

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
        if (_userSid != null)
        {
            await QueueAsync(itemName, SelfServiceRequests.Install);
            return;
        }

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
        RequestsChanged?.Invoke(this, EventArgs.Empty);
        _logger?.LogInformation("Successfully added install request for {ItemName}", itemName);
    }

    /// <inheritdoc />
    public async Task AddRemovalRequestAsync(string itemName)
    {
        _logger?.LogInformation("Adding removal request for {ItemName}", itemName);
        if (_userSid != null)
        {
            await QueueAsync(itemName, SelfServiceRequests.Remove);
            return;
        }

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
        RequestsChanged?.Invoke(this, EventArgs.Empty);
        _logger?.LogInformation("Successfully added removal request for {ItemName}", itemName);
    }

    /// <inheritdoc />
    public async Task RemoveRequestAsync(string itemName)
    {
        _logger?.LogInformation("Removing request for {ItemName}", itemName);
        if (_userSid != null)
        {
            await QueueAsync(itemName, SelfServiceRequests.Cancel);
            return;
        }

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
            RequestsChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>The signed-in user's drop files, oldest first: only files they own.</summary>
    private List<string> OwnDropFiles()
    {
        if (_dropDir == null || !Directory.Exists(_dropDir)) return [];
        return new DirectoryInfo(_dropDir).EnumerateFiles("*.yaml")
            .Where(f => string.Equals(ManagedInstallsSecurity.TryGetOwner(f.FullName)?.Value, _userSid,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .ToList();
    }

    private List<SelfServiceRequest> ReadOwnPendingRequests()
    {
        var requests = new List<SelfServiceRequest>();
        foreach (var file in OwnDropFiles())
        {
            try
            {
                if (SelfServiceRequests.Parse(File.ReadAllText(file), out _) is { } parsed)
                {
                    requests.AddRange(parsed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being taken in by managedsoftwareupdate right now; the protected copy has it next.
            }
        }
        return requests;
    }

    /// <summary>
    /// Records a choice in this instance's drop file, replacing any earlier choice for the
    /// item. The file is created new, under a name nobody can predict, so no other user can
    /// have taken it or be holding it; an existing file is only reopened after checking it
    /// is still this user's.
    /// </summary>
    private async Task QueueAsync(string itemName, string action)
    {
        if (!SelfServiceRequests.IsValidItemName(itemName))
        {
            throw new ArgumentException("Invalid item name", nameof(itemName));
        }

        await _lock.WaitAsync();
        try
        {
            var file = new SelfServiceRequestFile();
            var target = _ownDropFile;
            if (target != null && !IsOwnFile(target))
            {
                target = null;
            }
            if (target != null)
            {
                try
                {
                    file.Requests = SelfServiceRequests.Parse(await File.ReadAllTextAsync(target), out _) ?? [];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Taken in, or not readable: start a new file.
                    target = null;
                }
            }

            file.Requests.RemoveAll(r => r.Item.Equals(itemName, StringComparison.OrdinalIgnoreCase));
            file.Requests.Add(new SelfServiceRequest { Item = itemName, Action = action });
            var bytes = System.Text.Encoding.UTF8.GetBytes(SelfServiceRequests.Serialize(file));

            if (target != null)
            {
                try
                {
                    await File.WriteAllBytesAsync(target, bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    target = null;
                }
            }
            if (target == null)
            {
                target = Path.Combine(_dropDir!, $"{_userSid}.{Guid.NewGuid():N}.yaml");
                // Elevated tokens default to group ownership. Set the individual user as owner
                // at creation so pending reads, rewrites and intake limits use the same SID.
                var security = new FileSecurity();
                var owner = new SecurityIdentifier(_userSid!);
                security.SetOwner(owner);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.Modify, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, AccessControlType.Allow));
                using var stream = new FileInfo(target).Create(FileMode.CreateNew, FileSystemRights.Write,
                    FileShare.None, 4096, FileOptions.None, security);
                await stream.WriteAsync(bytes);
            }
            _ownDropFile = target;
        }
        finally
        {
            _lock.Release();
        }

        RequestsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsOwnFile(string path) =>
        File.Exists(path)
        && string.Equals(ManagedInstallsSecurity.TryGetOwner(path)?.Value, _userSid, StringComparison.OrdinalIgnoreCase);
}
