// SelfServiceSeenStore.cs - When SYSTEM first saw each Self Service drop file.

using System.Text.Json;

namespace Cimian.Core.Services;

/// <summary>
/// Keeps, across runs, when managedsoftwareupdate first saw each drop file in
/// selfservice\. That time orders the files instead of their write times, which belong to
/// the files' owners. Written by SYSTEM at the ManagedInstalls root (Users read only); a
/// copy anyone else could have written is ignored.
/// </summary>
public static class SelfServiceSeenStore
{
    public static Dictionary<string, DateTime> Load()
    {
        var store = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var path = CimianPaths.SelfServiceSeenFile;
        try
        {
            if (!File.Exists(path) || !ManagedInstallsSecurity.IsSafeToExecute(path, out _)) return store;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path));
            foreach (var (name, seen) in loaded ?? new())
            {
                store[name] = seen;
            }
        }
        catch
        {
            // Start over: the only cost is that files already waiting tie on first-seen time.
        }
        return store;
    }

    public static void Save(Dictionary<string, DateTime> store)
    {
        try
        {
            File.WriteAllText(CimianPaths.SelfServiceSeenFile, JsonSerializer.Serialize(store));
        }
        catch
        {
            // Unelevated runs cannot write it; nothing they do with drop files persists anyway.
        }
    }
}
