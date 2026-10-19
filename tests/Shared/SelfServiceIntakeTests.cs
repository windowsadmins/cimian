using System.Runtime.InteropServices;
using System.Security.Principal;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// The Munki 7 copy model on Windows. Users drop requests in ManagedInstalls\selfservice\;
/// SYSTEM reads each drop file once through a checked handle, merges it into the
/// protected SelfServeManifest.yaml and deletes it.
/// </summary>
public class SelfServiceIntakeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _drop;
    private readonly List<string> _log = new();

    public SelfServiceIntakeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "SelfServiceIntake", Guid.NewGuid().ToString());
        _drop = Path.Combine(_dir, "selfservice");
        Directory.CreateDirectory(_drop);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr security);

    private string Drop(string name, string yaml, DateTime? written = null)
    {
        var path = Path.Combine(_drop, name);
        File.WriteAllText(path, yaml);
        if (written != null) File.SetLastWriteTimeUtc(path, written.Value);
        return path;
    }

    private static string Requests(params (string Item, string Action)[] ops) =>
        "requests:\n" + string.Concat(ops.Select(o => $"- item: {o.Item}\n  action: {o.Action}\n"));

    // Everything these tests request is offered to the "device".
    private static readonly HashSet<string> Everything =
        new(new[] { "Firefox", "Zoom", "Gimp", "SECRET-NAME" }, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ValidDropFile_IsMergedAndDeleted()
    {
        var path = Drop("user.yaml", Requests(("Firefox", "install"), ("Zoom", "remove")));
        var manifest = new SelfServiceManifest();

        var changed = SelfServiceRequests.Ingest(_drop, manifest, Everything, new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.True(changed);
        Assert.Equal(new[] { "Firefox" }, manifest.ManagedInstalls);
        Assert.Equal(new[] { "Zoom" }, manifest.ManagedUninstalls);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CancelRemovesAnEarlierRequest()
    {
        var manifest = new SelfServiceManifest { ManagedInstalls = ["Firefox"], ManagedUninstalls = ["Zoom"] };
        Drop("user.yaml", Requests(("Firefox", "cancel"), ("Zoom", "cancel")));

        SelfServiceRequests.Ingest(_drop, manifest, Everything, new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.Empty(manifest.ManagedInstalls);
        Assert.Empty(manifest.ManagedUninstalls);
    }

    [Fact]
    public void TwoUsers_AreTakenInOrder_AndTheLatestRequestForAnItemWins()
    {
        var t = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Drop("b.yaml", Requests(("Firefox", "remove")));
        Drop("a.yaml", Requests(("Firefox", "install"), ("Gimp", "install")));
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["a.yaml"] = t, ["b.yaml"] = t.AddMinutes(1) };
        var manifest = new SelfServiceManifest();

        SelfServiceRequests.Ingest(_drop, manifest, Everything, seen, t.AddMinutes(2), _log.Add);

        Assert.Equal(new[] { "Gimp" }, manifest.ManagedInstalls);
        Assert.Equal(new[] { "Firefox" }, manifest.ManagedUninstalls);
        Assert.Empty(Directory.GetFiles(_drop));
    }

    [Fact]
    public void OversizedDropFile_IsRefusedDeletedAndNotEchoed()
    {
        var path = Drop("big.yaml", Requests(("SECRET-NAME", "install")) + new string('#', TriggerRequests.MaxRequestBytes));
        var manifest = new SelfServiceManifest();

        var changed = SelfServiceRequests.Ingest(_drop, manifest, Everything, new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.False(changed);
        Assert.Empty(manifest.ManagedInstalls);
        Assert.False(File.Exists(path));
        Assert.Contains(_log, l => l.Contains("Refused", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_log, l => l.Contains("SECRET-NAME"));
    }

    [Theory]
    [InlineData("requests: [")]
    [InlineData("requests:\n- item: Firefox\n  action: format-c\n")]
    [InlineData("requests:\n- item: \"\"\n  action: install\n")]
    [InlineData("requests:\n- item: \"bad\\u0001name\"\n  action: install\n")]
    public void MalformedDropFile_IsRefusedWhole(string yaml)
    {
        var path = Drop("bad.yaml", yaml);
        var manifest = new SelfServiceManifest();

        SelfServiceRequests.Ingest(_drop, manifest, Everything, new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.Empty(manifest.ManagedInstalls);
        Assert.Empty(manifest.ManagedUninstalls);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void HardLinkedDropFile_IsRefused_AndItsTargetLeftAlone()
    {
        var target = Path.Combine(_dir, "elsewhere.yaml");
        File.WriteAllText(target, Requests(("Firefox", "install")));
        Assert.True(CreateHardLink(Path.Combine(_drop, "link.yaml"), target, IntPtr.Zero));
        var manifest = new SelfServiceManifest();

        SelfServiceRequests.Ingest(_drop, manifest, Everything, new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.Empty(manifest.ManagedInstalls);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void SymbolicLinkDropFile_IsRefused_AndItsTargetLeftAlone()
    {
        var target = Path.Combine(_dir, "elsewhere.yaml");
        File.WriteAllText(target, Requests(("Firefox", "install")));
        var link = Path.Combine(_drop, "link.yaml");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // No Developer Mode or SeCreateSymbolicLinkPrivilege here: a standard user on a
            // managed machine cannot make one either. The hard-link test covers the rest.
            return;
        }
        var manifest = new SelfServiceManifest();

        SelfServiceRequests.Ingest(_drop, manifest, Everything, new Dictionary<string, DateTime>(), DateTime.UtcNow, _log.Add);

        Assert.Empty(manifest.ManagedInstalls);
        Assert.Equal(Requests(("Firefox", "install")), File.ReadAllText(target));
    }

    [Fact]
    public void UserCopyFromBeforeTheCopyModel_IsImportedAsRequestsOnly()
    {
        // Upgrades find a SelfServeManifest.yaml the user wrote. Its content is kept, as
        // requests that are then filtered like any other; the file itself is not trusted.
        var parsed = SelfServiceRequests.ParseLegacyManifest(
            "name: SelfServeManifest\nmanaged_installs:\n- Firefox\nmanaged_uninstalls:\n- Zoom\n", out var reason);

        Assert.NotNull(parsed);
        Assert.Equal(new[] { "Firefox" }, parsed!.ManagedInstalls);
        Assert.Equal(new[] { "Zoom" }, parsed.ManagedUninstalls);
        Assert.Null(SelfServiceRequests.ParseLegacyManifest("managed_installs:\n- \"bad\\u0001\"\n", out reason));
    }
}

public class SelfServiceUserModeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _system;
    private readonly string _drop;
    private readonly string _sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    public SelfServiceUserModeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "SelfServiceUser", Guid.NewGuid().ToString());
        _drop = Path.Combine(_dir, "selfservice");
        Directory.CreateDirectory(_drop);
        _system = Path.Combine(_dir, "SelfServeManifest.yaml");
        File.WriteAllText(_system, "name: SelfServeManifest\nmanaged_installs:\n- Gimp\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SelfServiceManifestService UserService() => new(null, _system, _drop, _sid);

    [Fact]
    public async Task UserRequests_GoToTheirDropFile_NotTheSystemCopy()
    {
        var before = File.ReadAllText(_system);

        await UserService().AddInstallRequestAsync("Firefox");

        Assert.Equal(before, File.ReadAllText(_system));
        Assert.Single(Directory.GetFiles(_drop));
    }

    [Fact]
    public async Task PendingRequests_ShowTheSystemCopyPlusTheUsersOwnDropFile()
    {
        var svc = UserService();
        await svc.AddInstallRequestAsync("Firefox");
        await svc.AddRemovalRequestAsync("Gimp");

        Assert.True(await svc.IsInstallRequestedAsync("Firefox"));
        Assert.True(await svc.IsRemovalRequestedAsync("Gimp"));
        Assert.False(await svc.IsInstallRequestedAsync("Gimp"));

        // A fresh instance (another MSC window, or after a restart) sees the same.
        Assert.True(await UserService().IsInstallRequestedAsync("Firefox"));
    }

    [Fact]
    public async Task CancellingAPendingRequest_IsAlsoARequest()
    {
        var svc = UserService();
        await svc.AddInstallRequestAsync("Firefox");
        await svc.RemoveRequestAsync("Firefox");
        await svc.RemoveRequestAsync("Gimp");

        var all = await svc.GetAllRequestsAsync();
        Assert.DoesNotContain("Firefox", all.ManagedInstalls);
        Assert.DoesNotContain("Gimp", all.ManagedInstalls);

        // What SYSTEM will do with the drop file matches what the user sees.
        var system = new SelfServiceManifest { ManagedInstalls = ["Gimp"] };
        SelfServiceRequests.Ingest(_drop, system, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gimp", "Firefox" }, new Dictionary<string, DateTime>(), DateTime.UtcNow, _ => { });
        Assert.Empty(system.ManagedInstalls);
    }

    [Fact]
    public async Task NewRequest_IsOwnedByTheIndividualUser_EvenWhenElevated()
    {
        await UserService().AddInstallRequestAsync("Firefox");

        var path = Assert.Single(Directory.GetFiles(_drop));
        Assert.Equal(_sid, ManagedInstallsSecurity.TryGetOwner(path)!.Value);
    }

    [Fact]
    public async Task RepeatedChoicesAndCancellation_ReuseOneFile_AndSurviveIntake()
    {
        var svc = UserService();
        for (var i = 0; i < SelfServiceRequests.MaxFilesPerOwnerPerRun + 1; i++)
        {
            await svc.AddInstallRequestAsync("Firefox");
        }
        await svc.RemoveRequestAsync("Firefox");

        Assert.Single(Directory.GetFiles(_drop));
        Assert.False(await UserService().IsInstallRequestedAsync("Firefox"));
        var manifest = new SelfServiceManifest();
        SelfServiceRequests.Ingest(_drop, manifest, new HashSet<string> { "Firefox" },
            new Dictionary<string, DateTime>(), DateTime.UtcNow, _ => { });
        Assert.Empty(manifest.ManagedInstalls);
    }

    [Fact]
    public async Task UserMode_NeverWritesTheSystemCopyDirectly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UserService().SaveAsync(new SelfServiceManifest()));
    }
}
