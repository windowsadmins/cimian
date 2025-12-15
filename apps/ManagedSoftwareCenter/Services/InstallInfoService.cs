// InstallInfoService.cs - Reads and caches InstallInfo.yaml (Munki-style)
// Watches for changes and notifies subscribers

using System.IO;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for reading and caching InstallInfo data
/// Implements Munki-style catalog caching with FileSystemWatcher for reactive updates
/// </summary>
public class InstallInfoService : IInstallInfoService, IDisposable
{
    private const string InstallInfoPath = @"C:\ProgramData\ManagedInstalls\InstallInfo.yaml";
    private const string CacheDirectory = @"%LocalAppData%\Cimian\SoftwareCenter";

    private readonly ILogger<InstallInfoService>? _logger;
    private readonly IDeserializer _deserializer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private InstallInfo? _cachedInfo;
    private DateTime _cacheTime;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _debounceCts;

    public event EventHandler<InstallInfo>? InstallInfoChanged;

    public InstallInfoService(ILogger<InstallInfoService>? logger = null)
    {
        _logger = logger;

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <inheritdoc />
    public async Task<InstallInfo> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Return cached if recent (within 5 seconds)
            if (_cachedInfo != null && DateTime.Now - _cacheTime < TimeSpan.FromSeconds(5))
            {
                return _cachedInfo;
            }

            if (!File.Exists(InstallInfoPath))
            {
                _logger?.LogDebug("InstallInfo.yaml does not exist, returning empty info");
                _cachedInfo = new InstallInfo();
                _cacheTime = DateTime.Now;
                return _cachedInfo;
            }

            var content = await File.ReadAllTextAsync(InstallInfoPath);
            System.Diagnostics.Debug.WriteLine($"[InstallInfoService] Read {content.Length} bytes from InstallInfo.yaml");
            var info = _deserializer.Deserialize<InstallInfo>(content);
            System.Diagnostics.Debug.WriteLine($"[InstallInfoService] Deserialized, OptionalInstalls = {info?.OptionalInstalls?.Count ?? -1}");

            if (info == null)
            {
                info = new InstallInfo();
            }

            // Ensure collections are initialized
            info.OptionalInstalls ??= [];
            info.ManagedInstalls ??= [];
            info.Removals ??= [];
            info.ManagedUpdates ??= [];
            info.FeaturedItems ??= [];
            info.ProblemItems ??= [];

            _cachedInfo = info;
            _cacheTime = DateTime.Now;

            _logger?.LogDebug("Loaded InstallInfo with {OptionalCount} optional, {ManagedCount} managed, {UpdateCount} updates",
                info.OptionalInstalls.Count, info.ManagedInstalls.Count, info.ManagedUpdates.Count);

            return info;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load InstallInfo.yaml");
            _cachedInfo = new InstallInfo();
            _cacheTime = DateTime.Now;
            return _cachedInfo;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstallableItem>> GetOptionalInstallsAsync()
    {
        var info = await LoadAsync();
        System.Diagnostics.Debug.WriteLine($"[InstallInfoService] GetOptionalInstallsAsync: {info.OptionalInstalls.Count} items");
        return info.OptionalInstalls.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstallableItem>> GetManagedInstallsAsync()
    {
        var info = await LoadAsync();
        return info.ManagedInstalls.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstallableItem>> GetRemovalsAsync()
    {
        var info = await LoadAsync();
        return info.Removals.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstallableItem>> GetManagedUpdatesAsync()
    {
        var info = await LoadAsync();
        return info.ManagedUpdates.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProblemItem>> GetProblemItemsAsync()
    {
        var info = await LoadAsync();
        return info.ProblemItems.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetCategoriesAsync()
    {
        var info = await LoadAsync();

        var categories = info.OptionalInstalls
            .Where(x => !string.IsNullOrWhiteSpace(x.Category))
            .Select(x => x.Category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        return categories.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstallableItem>> GetItemsByCategoryAsync(string category)
    {
        var info = await LoadAsync();

        var items = info.OptionalInstalls
            .Where(x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.GetDisplayName())
            .ToList();

        return items.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<InstallableItem?> GetItemByNameAsync(string name)
    {
        var info = await LoadAsync();

        // Search all collections
        var item = info.OptionalInstalls.FirstOrDefault(x => 
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        
        if (item != null) return item;

        item = info.ManagedInstalls.FirstOrDefault(x => 
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        
        if (item != null) return item;

        item = info.ManagedUpdates.FirstOrDefault(x => 
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        return item;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLastCheckTimeAsync()
    {
        var info = await LoadAsync();
        return info.LastCheck == default ? null : info.LastCheck;
    }

    /// <inheritdoc />
    public void StartWatching()
    {
        if (_watcher != null) return;

        var directory = Path.GetDirectoryName(InstallInfoPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _logger?.LogWarning("Cannot watch InstallInfo - directory does not exist: {Directory}", directory);
            return;
        }

        _watcher = new FileSystemWatcher(directory)
        {
            Filter = Path.GetFileName(InstallInfoPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;

        _logger?.LogInformation("Started watching InstallInfo.yaml for changes");
    }

    /// <inheritdoc />
    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;

            _logger?.LogInformation("Stopped watching InstallInfo.yaml");
        }
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce rapid changes
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(_debounceDelay, _debounceCts.Token);

            // Clear cache and reload
            _cachedInfo = null;
            var info = await LoadAsync();

            _logger?.LogDebug("InstallInfo.yaml changed, notifying subscribers");
            InstallInfoChanged?.Invoke(this, info);
        }
        catch (TaskCanceledException)
        {
            // Debounce cancelled, another change came in
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling InstallInfo file change");
        }
    }

    public void Dispose()
    {
        StopWatching();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
