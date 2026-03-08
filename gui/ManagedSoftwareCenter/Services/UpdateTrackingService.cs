// UpdateTrackingService.cs - Tracks when updates were first discovered

using System.Text.Json;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Persists first-seen dates for pending items to %LocalAppData%\Cimian\SoftwareCenter\UpdateTracking.json
/// </summary>
public class UpdateTrackingService : IUpdateTrackingService
{
    private static readonly string TrackingDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cimian", "SoftwareCenter");
    private static readonly string TrackingFile = Path.Combine(TrackingDir, "UpdateTracking.json");

    private Dictionary<string, DateTime>? _cache;

    public async Task TrackItemAsync(string itemName)
    {
        var data = await LoadAsync();
        if (!data.ContainsKey(itemName))
        {
            data[itemName] = DateTime.UtcNow;
            await SaveAsync(data);
        }
    }

    public async Task<int?> GetDaysPendingAsync(string itemName)
    {
        var data = await LoadAsync();
        if (data.TryGetValue(itemName, out var firstSeen))
        {
            return (int)(DateTime.UtcNow - firstSeen).TotalDays;
        }
        return null;
    }

    public async Task RemoveItemAsync(string itemName)
    {
        var data = await LoadAsync();
        if (data.Remove(itemName))
        {
            await SaveAsync(data);
        }
    }

    public async Task PruneAsync(IEnumerable<string> currentPendingItems)
    {
        var data = await LoadAsync();
        var pendingSet = new HashSet<string>(currentPendingItems, StringComparer.OrdinalIgnoreCase);
        var toRemove = data.Keys.Where(k => !pendingSet.Contains(k)).ToList();
        if (toRemove.Count > 0)
        {
            foreach (var key in toRemove) data.Remove(key);
            await SaveAsync(data);
        }
    }

    private async Task<Dictionary<string, DateTime>> LoadAsync()
    {
        if (_cache != null) return _cache;

        if (File.Exists(TrackingFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(TrackingFile);
                _cache = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? [];
            }
            catch
            {
                _cache = [];
            }
        }
        else
        {
            _cache = [];
        }
        return _cache;
    }

    private async Task SaveAsync(Dictionary<string, DateTime> data)
    {
        _cache = data;
        Directory.CreateDirectory(TrackingDir);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(TrackingFile, json);
    }
}
