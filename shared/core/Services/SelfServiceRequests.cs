// SelfServiceRequests.cs - The Munki 7 Self Service copy model on Windows.

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.Core.Services;

/// <summary>One Self Service choice: install, remove, or cancel an earlier choice.</summary>
public class SelfServiceRequest
{
    [YamlMember(Alias = "item")]
    public string Item { get; set; } = string.Empty;

    [YamlMember(Alias = "action")]
    public string Action { get; set; } = string.Empty;
}

/// <summary>A user's drop file: their choices, oldest first.</summary>
public class SelfServiceRequestFile
{
    [YamlMember(Alias = "requests")]
    public List<SelfServiceRequest> Requests { get; set; } = [];
}

/// <summary>
/// Self Service the way Munki 7 does it (selfservice.swift updateSelfServeManifest).
/// Users never write SelfServeManifest.yaml: it is SYSTEM and Administrators write, Users
/// read. Managed Software Center and manifestutil put each user's choices in a drop file
/// under <see cref="CimianPaths.SelfServiceDropDir"/>. At the start of the Self Service
/// step, managedsoftwareupdate (SYSTEM) reads each one once through a checked handle,
/// merges it into the protected copy and deletes it.
/// </summary>
public static class SelfServiceRequests
{
    public const string Install = "install";
    public const string Remove = "remove";
    public const string Cancel = "cancel";

    private const int MaxItemNameLength = 256;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>Applies choices in order, so the latest choice for an item wins.</summary>
    public static void Apply(SelfServiceManifest manifest, IEnumerable<SelfServiceRequest> requests) =>
        Apply(manifest, requests, int.MaxValue);

    /// <summary>
    /// Applies choices in order. An install or remove that would take the manifest past
    /// <paramref name="maxEntries"/> names is not applied; returns how many were not.
    /// </summary>
    public static int Apply(SelfServiceManifest manifest, IEnumerable<SelfServiceRequest> requests, int maxEntries)
    {
        var capped = 0;
        foreach (var r in requests)
        {
            manifest.ManagedInstalls.RemoveAll(n => n.Equals(r.Item, StringComparison.OrdinalIgnoreCase));
            manifest.ManagedUninstalls.RemoveAll(n => n.Equals(r.Item, StringComparison.OrdinalIgnoreCase));
            if (r.Action != Install)
            {
                manifest.OptionalInstalls.RemoveAll(n => n.Equals(r.Item, StringComparison.OrdinalIgnoreCase));
            }
            if (r.Action == Cancel) continue;

            if (manifest.ManagedInstalls.Count + manifest.ManagedUninstalls.Count >= maxEntries)
            {
                capped++;
                continue;
            }
            if (r.Action == Install) manifest.ManagedInstalls.Add(r.Item);
            else manifest.ManagedUninstalls.Add(r.Item);
        }
        return capped;
    }

    /// <summary>
    /// Parses a drop file. Any bad entry (unknown action, empty or oversized name, control
    /// characters) refuses the whole file. The reason never repeats the content.
    /// </summary>
    public static List<SelfServiceRequest>? Parse(string content, out string? reason)
    {
        SelfServiceRequestFile? file;
        try
        {
            file = Deserializer.Deserialize<SelfServiceRequestFile>(content);
        }
        catch (Exception)
        {
            reason = "it is not a valid request file";
            return null;
        }

        var requests = file?.Requests ?? [];
        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            r.Action = (r.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (r.Action is not (Install or Remove or Cancel))
            {
                reason = $"request {i + 1} has an unknown action";
                return null;
            }
            if (!IsValidItemName(r.Item))
            {
                reason = $"request {i + 1} has an invalid item name";
                return null;
            }
        }

        reason = null;
        return requests;
    }

    /// <summary>
    /// Parses a SelfServeManifest.yaml that a user wrote before the copy model, so an
    /// upgrade keeps users' choices. The result is only ever used as requests, filtered like
    /// any others; the file itself is quarantined.
    /// </summary>
    public static SelfServiceManifest? ParseLegacyManifest(string content, out string? reason)
    {
        SelfServiceManifest? manifest;
        try
        {
            manifest = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<SelfServiceManifest>(content) ?? new SelfServiceManifest();
        }
        catch (Exception)
        {
            reason = "it is not a valid Self Service manifest";
            return null;
        }

        manifest.ManagedInstalls ??= [];
        manifest.ManagedUninstalls ??= [];
        manifest.OptionalInstalls ??= [];
        if (!manifest.ManagedInstalls.Concat(manifest.ManagedUninstalls).Concat(manifest.OptionalInstalls).All(IsValidItemName))
        {
            reason = "it has an invalid item name";
            return null;
        }

        reason = null;
        return manifest;
    }

    public static bool IsValidItemName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxItemNameLength && !name.Any(char.IsControl);

    public static string Serialize(SelfServiceRequestFile file) => Serializer.Serialize(file);

    /// <summary>Requests one drop file may hold; more refuses the file.</summary>
    public const int MaxRequestsPerFile = 100;

    /// <summary>Drop files taken from one owner per run; extras wait for the next run.</summary>
    public const int MaxFilesPerOwnerPerRun = 5;

    /// <summary>Names the protected manifest may hold across installs and removals.</summary>
    public const int MaxManifestEntries = 500;

    /// <summary>
    /// Merges the drop files in <paramref name="dropDir"/> into <paramref name="manifest"/>
    /// and deletes the files it processes. Files beyond the per-owner limit stay queued.
    /// </summary>
    /// <remarks>
    /// <para>Order is SYSTEM's: when a file was first seen (<paramref name="firstSeen"/>,
    /// kept by the caller across runs), then its NTFS file ID. File times belong to the
    /// file's owner and could be backdated. Across users the latest choice for an item wins.</para>
    /// <para>Bounded: a file whose owner cannot be read waits; one owner contributes at most
    /// <see cref="MaxFilesPerOwnerPerRun"/> files per run; a file with more than
    /// <see cref="MaxRequestsPerFile"/> requests, or that is a link, has a second name, is
    /// larger than <see cref="TriggerRequests.MaxRequestBytes"/> or does not parse, is
    /// refused whole. Install and remove requests for items not in <paramref name="offered"/>
    /// (this device's optional_installs) are dropped, and the manifest stops growing at
    /// <see cref="MaxManifestEntries"/>. Cancels always apply. What was dropped is logged
    /// by count and owner, never by content.</para>
    /// </remarks>
    /// <returns>True when the manifest changed.</returns>
    public static bool Ingest(string dropDir, SelfServiceManifest manifest, ISet<string> offered,
        IDictionary<string, DateTime> firstSeen, DateTime now, Action<string> log)
    {
        if (!Directory.Exists(dropDir)) return false;

        var before = Fingerprint(manifest);
        var present = new DirectoryInfo(dropDir).EnumerateFiles("*.yaml").ToList();
        var names = new HashSet<string>(present.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in firstSeen.Keys.Where(k => !names.Contains(k)).ToList())
        {
            firstSeen.Remove(gone);
        }
        foreach (var f in present)
        {
            firstSeen.TryAdd(f.Name, now);
        }

        var ordered = present
            .Select(f => (File: f, Seen: firstSeen[f.Name], Id: ManagedInstallsSecurity.TryGetFileId(f.FullName) ?? ulong.MaxValue))
            .OrderBy(x => x.Seen).ThenBy(x => x.Id).ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.File)
            .ToList();

        var perOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ordered)
        {
            var owner = ManagedInstallsSecurity.TryGetOwner(file.FullName)?.Value;
            if (owner == null)
            {
                log("Left a Self Service request file whose owner could not be read; trying again next run");
                continue;
            }

            perOwner[owner] = perOwner.GetValueOrDefault(owner) + 1;
            if (perOwner[owner] > MaxFilesPerOwnerPerRun)
            {
                log($"Left an extra Self Service request file from {owner} for the next run (more than {MaxFilesPerOwnerPerRun} this run)");
                continue;
            }
            else if (!ManagedInstallsSecurity.TryReadRequest(file.FullName, requireTrustedOwner: false, out var content, out var problem))
            {
                log($"Refused Self Service request file from {owner}: {problem}");
            }
            else if (Parse(content!, out problem) is not { } requests)
            {
                log($"Refused Self Service request file from {owner}: {problem}");
            }
            else if (requests.Count > MaxRequestsPerFile)
            {
                log($"Refused Self Service request file from {owner}: more than {MaxRequestsPerFile} requests");
            }
            else
            {
                var kept = requests.Where(r => r.Action == Cancel || offered.Contains(r.Item)).ToList();
                if (kept.Count < requests.Count)
                {
                    log($"Dropped {requests.Count - kept.Count} Self Service request(s) from {owner} for items not offered to this device");
                }
                var capped = Apply(manifest, kept, MaxManifestEntries);
                if (capped > 0)
                {
                    log($"Dropped {capped} Self Service request(s) from {owner}: the manifest is at its {MaxManifestEntries}-name limit");
                }
                log($"Took {kept.Count - capped} Self Service request(s) from {owner}");
            }

            try
            {
                // Removing a link removes the link, never what it points at.
                file.Delete();
                firstSeen.Remove(file.Name);
            }
            catch (Exception ex)
            {
                log($"Could not delete Self Service request file from {owner}: {ex.Message}");
            }
        }

        return Fingerprint(manifest) != before;

        static string Fingerprint(SelfServiceManifest m) =>
            string.Join("|", m.ManagedInstalls) + "#" + string.Join("|", m.ManagedUninstalls) + "#" + string.Join("|", m.OptionalInstalls);
    }
}
