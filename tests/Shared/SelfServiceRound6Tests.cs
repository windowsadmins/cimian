using System.Security.AccessControl;
using System.Security.Principal;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Self Service drop files from many users: none can block another, grow the protected
/// manifest without bound, or pick their place in the order.
/// </summary>
public class SelfServiceRound6Tests : IDisposable
{
    private readonly string _dir;
    private readonly string _drop;
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly List<string> _log = new();

    public SelfServiceRound6Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "SelfServiceR6", Guid.NewGuid().ToString());
        _drop = Path.Combine(_dir, "selfservice");
        Directory.CreateDirectory(_drop);
    }

    public void Dispose()
    {
        foreach (var f in Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*", SearchOption.AllDirectories) : [])
        {
            try { var i = new FileInfo(f); var a = i.GetAccessControl(); a.SetAccessRuleProtection(false, false);
                  foreach (FileSystemAccessRule r in a.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                      if (r.AccessControlType == AccessControlType.Deny) a.RemoveAccessRule(r);
                  i.SetAccessControl(a); } catch { }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Requests(params (string Item, string Action)[] ops) =>
        "requests:\n" + string.Concat(ops.Select(o => $"- item: {o.Item}\n  action: {o.Action}\n"));

    private static HashSet<string> Offered(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    // ── 1. One user cannot block another's clicks ────────────────────────────

    [Fact]
    public async Task AClickStillLands_WhenTheUsualFileNameCannotBeRead()
    {
        // Stands in for a name another user took first: it exists and cannot be read.
        var squatted = Path.Combine(_drop, $"{_sid}.yaml");
        File.WriteAllText(squatted, Requests(("Other", "install")));
        var info = new FileInfo(squatted);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(acl);

        var svc = new SelfServiceManifestService(null, Path.Combine(_dir, "SelfServeManifest.yaml"), _drop, _sid);
        await svc.AddInstallRequestAsync("Firefox");

        Assert.True(await svc.IsInstallRequestedAsync("Firefox"));
        Assert.Contains(Directory.GetFiles(_drop), f => Path.GetFileName(f).StartsWith(_sid + ".") && f != squatted);
    }

    [Fact]
    public async Task EachWindowCreatesItsOwnFile_WithANameNobodyCanPredict()
    {
        var manifest = Path.Combine(_dir, "SelfServeManifest.yaml");
        await new SelfServiceManifestService(null, manifest, _drop, _sid).AddInstallRequestAsync("Firefox");

        var file = Path.GetFileName(Assert.Single(Directory.GetFiles(_drop)));
        Assert.Matches($"^{_sid}\\.[0-9a-f]{{32}}\\.yaml$", file);
    }

    // ── 2. The protected manifest cannot be grown without bound ──────────────

    [Fact]
    public void RequestsForItemsNotOffered_AreDroppedAtIntake()
    {
        File.WriteAllText(Path.Combine(_drop, "u.yaml"),
            Requests(("Firefox", "install"), ("Junk1", "install"), ("Junk2", "remove"), ("Junk3", "cancel")));
        var manifest = new SelfServiceManifest();

        SelfServiceRequests.Ingest(_drop, manifest, Offered("Firefox"), new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.Equal(new[] { "Firefox" }, manifest.ManagedInstalls);
        Assert.Empty(manifest.ManagedUninstalls);
        Assert.Contains(_log, l => l.Contains("2") && l.Contains("not offered"));
        Assert.DoesNotContain(_log, l => l.Contains("Junk"));
    }

    [Fact]
    public void ADropFileWithTooManyRequests_IsRefusedWhole()
    {
        var ops = Enumerable.Range(0, SelfServiceRequests.MaxRequestsPerFile + 1).Select(i => ($"App{i}", "install")).ToArray();
        File.WriteAllText(Path.Combine(_drop, "u.yaml"), Requests(ops));
        var manifest = new SelfServiceManifest();

        SelfServiceRequests.Ingest(_drop, manifest, Offered(ops.Select(o => o.Item1).ToArray()),
            new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.Empty(manifest.ManagedInstalls);
        Assert.Empty(Directory.GetFiles(_drop));
    }

    [Fact]
    public void OneOwnersFilesBeyondThePerRunCap_WaitForTheNextRun()
    {
        for (var i = 0; i < SelfServiceRequests.MaxFilesPerOwnerPerRun + 3; i++)
        {
            File.WriteAllText(Path.Combine(_drop, $"f{i:D2}.yaml"), Requests(($"App{i}", "install")));
        }
        var offered = Offered(Enumerable.Range(0, 20).Select(i => $"App{i}").ToArray());
        var manifest = new SelfServiceManifest();

        var seen = new Dictionary<string, DateTime>();
        SelfServiceRequests.Ingest(_drop, manifest, offered, seen, DateTime.UtcNow, _log.Add);

        Assert.Equal(SelfServiceRequests.MaxFilesPerOwnerPerRun, manifest.ManagedInstalls.Count);
        Assert.Equal(3, Directory.GetFiles(_drop).Length);
        Assert.Equal(3, seen.Count);

        SelfServiceRequests.Ingest(_drop, manifest, offered, seen, DateTime.UtcNow, _log.Add);

        Assert.Equal(SelfServiceRequests.MaxFilesPerOwnerPerRun + 3, manifest.ManagedInstalls.Count);
        Assert.Empty(Directory.GetFiles(_drop));
        Assert.Empty(seen);
    }

    [Fact]
    public void CancellationBeyondThePerRunCap_IsAppliedOnTheNextRun()
    {
        var now = DateTime.UtcNow;
        var seen = new Dictionary<string, DateTime>();
        for (var i = 0; i <= SelfServiceRequests.MaxFilesPerOwnerPerRun; i++)
        {
            var name = $"f{i:D2}.yaml";
            var action = i == SelfServiceRequests.MaxFilesPerOwnerPerRun ? "cancel" : "install";
            File.WriteAllText(Path.Combine(_drop, name), Requests(("Firefox", action)));
            seen[name] = now.AddSeconds(i);
        }
        var manifest = new SelfServiceManifest();

        SelfServiceRequests.Ingest(_drop, manifest, Offered("Firefox"), seen, now.AddMinutes(1), _log.Add);
        Assert.Contains("Firefox", manifest.ManagedInstalls);
        Assert.Single(Directory.GetFiles(_drop));

        SelfServiceRequests.Ingest(_drop, manifest, Offered("Firefox"), seen, now.AddMinutes(2), _log.Add);
        Assert.Empty(manifest.ManagedInstalls);
        Assert.Empty(Directory.GetFiles(_drop));
        Assert.Empty(seen);
    }

    [Fact]
    public void TheManifestStopsGrowingAtItsCap()
    {
        var manifest = new SelfServiceManifest
        {
            ManagedInstalls = Enumerable.Range(0, SelfServiceRequests.MaxManifestEntries).Select(i => $"Old{i}").ToList(),
        };
        File.WriteAllText(Path.Combine(_drop, "u.yaml"), Requests(("Firefox", "install")));

        SelfServiceRequests.Ingest(_drop, manifest, Offered("Firefox"), new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.DoesNotContain("Firefox", manifest.ManagedInstalls);
        Assert.Equal(SelfServiceRequests.MaxManifestEntries, manifest.ManagedInstalls.Count);
    }

    // ── 4. Order is SYSTEM's, not the file times users control ─────────────

    [Fact]
    public void FilesAreTakenInTheOrderSystemFirstSawThem_NotByWriteTime()
    {
        var t = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var first = Path.Combine(_drop, "first.yaml");
        var second = Path.Combine(_drop, "second.yaml");
        File.WriteAllText(first, Requests(("Firefox", "install")));
        File.WriteAllText(second, Requests(("Firefox", "remove")));
        // The second user backdates their file to go first; the remove must still win.
        File.SetLastWriteTimeUtc(second, t.AddYears(-5));
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["first.yaml"] = t, ["second.yaml"] = t.AddSeconds(30),
        };
        var manifest = new SelfServiceManifest();

        SelfServiceRequests.Ingest(_drop, manifest, Offered("Firefox"), seen, t.AddMinutes(1), _log.Add);

        Assert.Equal(new[] { "Firefox" }, manifest.ManagedUninstalls);
        Assert.Empty(seen);
    }

    // ── 5. Exclusive create never follows a link ────────────────────────────

    [Fact]
    public void ExclusiveCreate_FailsWhenAnythingHasTheName()
    {
        var existing = Path.Combine(_dir, "taken.yaml");
        File.WriteAllText(existing, "keep");

        Assert.False(ManagedInstallsSecurity.TryWriteNewFile(existing, "x"u8.ToArray()));
        Assert.Equal("keep", File.ReadAllText(existing));

        var fresh = Path.Combine(_dir, "fresh.yaml");
        Assert.True(ManagedInstallsSecurity.TryWriteNewFile(fresh, "new"u8.ToArray()));
        Assert.Equal("new", File.ReadAllText(fresh));
    }

    [Fact]
    public void ExclusiveCreate_DoesNotWriteThroughASymbolicLink()
    {
        var target = Path.Combine(_dir, "target.txt");
        File.WriteAllText(target, "original");
        var link = Path.Combine(_dir, "SelfServeManifest.yaml");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

        Assert.False(ManagedInstallsSecurity.TryWriteNewFile(link, "evil"u8.ToArray()));
        Assert.Equal("original", File.ReadAllText(target));
    }
}
