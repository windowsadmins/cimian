using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using InstallInfoFile = Cimian.Core.Models.InstallInfoFile;
using InstallInfoItem = Cimian.Core.Models.InstallInfoItem;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// force_install_after_date on a title listed only under optional_installs must not
/// queue an install or surface a deadline on the optional Software-tab record.
/// Matches Munki: deadlines apply after SelfServe opt-in / managed install queues.
/// </summary>
public class OptionalForceInstallDeadlineTests : IDisposable
{
    private readonly string _testDir;
    private readonly CimianConfig _config;
    private readonly UpdateEngine _engine;
    private readonly string _suffix = Guid.NewGuid().ToString("N");

    public OptionalForceInstallDeadlineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "OptionalForceDeadline", _suffix);
        Directory.CreateDirectory(_testDir);

        _config = new CimianConfig { CachePath = Path.Combine(_testDir, "Cache") };
        Directory.CreateDirectory(_config.CachePath);
        _engine = new UpdateEngine(_config);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* Ignore cleanup errors */ }
    }

    private string Unique(string name) => $"{name}{_suffix}";

    /// <summary>Catalog item whose installs file is missing: not installed, needs action.</summary>
    private CatalogItem AbsentWithPastDeadline(string name) => new()
    {
        Name = name,
        Version = "2.0.0",
        ForceInstallAfterDate = DateTime.Now.AddDays(-1),
        Installs = new List<InstallCheckItem>
        {
            new() { Type = "file", Path = Path.Combine(_testDir, name + ".missing") }
        }
    };

    /// <summary>Catalog item whose installs file exists with another hash: installed, out of date.</summary>
    private CatalogItem OutdatedWithPastDeadline(string name)
    {
        var path = Path.Combine(_testDir, name + ".bin");
        File.WriteAllText(path, "old build");
        return new CatalogItem
        {
            Name = name,
            Version = "2.0.0",
            ForceInstallAfterDate = DateTime.Now.AddDays(-1),
            Installs = new List<InstallCheckItem>
            {
                new() { Type = "file", Path = path, Md5Checksum = "00000000000000000000000000000000" }
            }
        };
    }

    private static ManifestItem SelfServeRequest(string name) => new()
    {
        Name = name,
        Action = "install",
        SourceManifest = "SelfServeManifest",
        IsSelfServe = true,
        PromotedFromOptional = true
    };

    private static Dictionary<string, CatalogItem> Catalog(params CatalogItem[] items) =>
        items.ToDictionary(i => i.Name.ToLowerInvariant());

    private static ManifestItem Entry(string name, string action) =>
        new() { Name = name, Action = action, SourceManifest = "test" };

    [Fact]
    public void OptionalOnly_PastForceDeadline_IsNotQueued()
    {
        var item = AbsentWithPastDeadline(Unique("optional"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "optional") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
    }

    [Fact]
    public void ManagedInstall_PastForceDeadline_StillQueuesInstall()
    {
        var item = AbsentWithPastDeadline(Unique("managed"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "install") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(toUpdate);
    }

    [Fact]
    public void OptionalInstallRecord_OmitsForceInstallAfterDate()
    {
        var item = AbsentWithPastDeadline(Unique("banner"));
        var record = _engine.BuildOptionalInstallRecord(item.Name, item, null);

        Assert.Null(record.ForceInstallAfterDate);
    }

    [Fact]
    public void ManagedInstallInfoItem_KeepsForceInstallAfterDate()
    {
        var item = AbsentWithPastDeadline(Unique("managedbanner"));
        var record = UpdateEngine.BuildInstallInfoItem(item.Name, item);

        Assert.Equal(item.ForceInstallAfterDate, record.ForceInstallAfterDate);
    }

    [Fact]
    public void SelfServePromotedOptional_PastDeadline_StillQueuesInstall()
    {
        var item = AbsentWithPastDeadline(Unique("selfserve"));
        var manifest = new List<ManifestItem>
        {
            new()
            {
                Name = item.Name,
                Action = "install",
                SourceManifest = "SelfServeManifest",
                PromotedFromOptional = true
            }
        };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(toUpdate);
    }

    [Fact]
    public void SelfServePromotedOptional_NotInstalled_DeadlineDoesNotOverrideInstallWindow()
    {
        var item = AbsentWithPastDeadline(Unique("selfservewindow"));
        var manifest = new List<ManifestItem> { SelfServeRequest(item.Name) };

        var (toInstall, _, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.False(_engine.ForceDeadlineOverridesInstallWindow(item, DateTime.Now));
    }

    [Fact]
    public void SelfServePromotedOptional_NotInstalled_InstallInfoOmitsDeadline()
    {
        var item = AbsentWithPastDeadline(Unique("selfserveinfo"));
        var manifest = new List<ManifestItem> { SelfServeRequest(item.Name) };
        var catalog = Catalog(item);

        var (toInstall, toUpdate, toUninstall, _) = _engine.IdentifyActions(manifest, catalog);
        var info = _engine.BuildInstallInfo(manifest, toInstall, toUpdate, toUninstall, catalog);

        var pending = Assert.Single(info.ManagedInstalls);
        Assert.Equal(item.Name, pending.Name);
        Assert.Null(pending.ForceInstallAfterDate);
    }

    [Fact]
    public void SelfServePromotedOptional_InstalledAndOutOfDate_KeepsDeadline()
    {
        var item = OutdatedWithPastDeadline(Unique("selfserveoutdated"));
        var manifest = new List<ManifestItem> { SelfServeRequest(item.Name) };
        var catalog = Catalog(item);

        var (toInstall, toUpdate, toUninstall, _) = _engine.IdentifyActions(manifest, catalog);
        var info = _engine.BuildInstallInfo(manifest, toInstall, toUpdate, toUninstall, catalog);

        Assert.Single(toUpdate, item);
        Assert.True(_engine.ForceDeadlineOverridesInstallWindow(item, DateTime.Now));
        var pending = Assert.Single(info.ManagedInstalls);
        Assert.Equal(item.ForceInstallAfterDate, pending.ForceInstallAfterDate);
    }

    [Fact]
    public void ManagedInstall_NotInstalled_DeadlineStillOverridesInstallWindow()
    {
        var item = AbsentWithPastDeadline(Unique("managedwindow"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "install") };

        _engine.IdentifyActions(manifest, Catalog(item));

        Assert.True(_engine.ForceDeadlineOverridesInstallWindow(item, DateTime.Now));
    }

    /// <summary>Runs the action pass and dependency resolution the way a session does.</summary>
    private InstallInfoFile Resolve(List<ManifestItem> manifest, Dictionary<string, CatalogItem> catalog)
    {
        var (toInstall, toUpdate, toUninstall, _) = _engine.IdentifyActions(manifest, catalog);
        _engine.ResolveDependencies(manifest, catalog, toUpdate);
        return _engine.BuildInstallInfo(manifest, toInstall, toUpdate, toUninstall, catalog);
    }

    private static InstallInfoItem Pending(InstallInfoFile info, string name) =>
        Assert.Single(info.ManagedInstalls, i => i.Name == name);

    [Fact]
    public void SelfServeRequest_NotInstalledRequiresDependency_DependencyDeadlineNotEnforced()
    {
        var dependency = AbsentWithPastDeadline(Unique("requireddep"));
        var parent = AbsentWithPastDeadline(Unique("requiringparent"));
        parent.Requires = new List<string> { dependency.Name };
        var manifest = new List<ManifestItem> { SelfServeRequest(parent.Name) };

        var info = Resolve(manifest, Catalog(parent, dependency));

        Assert.False(_engine.ForceDeadlineOverridesInstallWindow(dependency, DateTime.Now));
        Assert.Null(Pending(info, dependency.Name).ForceInstallAfterDate);
    }

    [Fact]
    public void SelfServeRequest_NotInstalledUpdateForItem_DeadlineNotEnforced()
    {
        var parent = AbsentWithPastDeadline(Unique("updatedparent"));
        var update = AbsentWithPastDeadline(Unique("updatefordep"));
        update.UpdateFor = new List<string> { parent.Name };
        var manifest = new List<ManifestItem> { SelfServeRequest(parent.Name) };

        var info = Resolve(manifest, Catalog(parent, update));

        Assert.False(_engine.ForceDeadlineOverridesInstallWindow(update, DateTime.Now));
        Assert.Null(Pending(info, update.Name).ForceInstallAfterDate);
    }

    [Fact]
    public void SelfServeRequest_DependencyAlsoManagedInstall_KeepsDeadline()
    {
        var dependency = AbsentWithPastDeadline(Unique("manageddep"));
        var parent = AbsentWithPastDeadline(Unique("sharedparent"));
        parent.Requires = new List<string> { dependency.Name };
        var manifest = new List<ManifestItem>
        {
            SelfServeRequest(parent.Name),
            Entry(dependency.Name, "install")
        };

        var info = Resolve(manifest, Catalog(parent, dependency));

        Assert.True(_engine.ForceDeadlineOverridesInstallWindow(dependency, DateTime.Now));
        Assert.Equal(dependency.ForceInstallAfterDate, Pending(info, dependency.Name).ForceInstallAfterDate);
    }

    [Fact]
    public void SelfServeRequest_DependencyOfManagedInstall_KeepsDeadline()
    {
        var dependency = AbsentWithPastDeadline(Unique("shareddep"));
        var requested = AbsentWithPastDeadline(Unique("requestedparent"));
        var managed = AbsentWithPastDeadline(Unique("managedparent"));
        requested.Requires = new List<string> { dependency.Name };
        managed.Requires = new List<string> { dependency.Name };
        var manifest = new List<ManifestItem>
        {
            SelfServeRequest(requested.Name),
            Entry(managed.Name, "install")
        };

        var info = Resolve(manifest, Catalog(requested, managed, dependency));

        Assert.True(_engine.ForceDeadlineOverridesInstallWindow(dependency, DateTime.Now));
        Assert.Equal(dependency.ForceInstallAfterDate, Pending(info, dependency.Name).ForceInstallAfterDate);
    }

    [Fact]
    public void SelfServeRequest_DependencyInstalledAndOutOfDate_KeepsDeadline()
    {
        var dependency = OutdatedWithPastDeadline(Unique("outdateddep"));
        var parent = AbsentWithPastDeadline(Unique("outdatedparent"));
        parent.Requires = new List<string> { dependency.Name };
        var manifest = new List<ManifestItem> { SelfServeRequest(parent.Name) };

        var info = Resolve(manifest, Catalog(parent, dependency));

        Assert.True(_engine.ForceDeadlineOverridesInstallWindow(dependency, DateTime.Now));
        Assert.Equal(dependency.ForceInstallAfterDate, Pending(info, dependency.Name).ForceInstallAfterDate);
    }
}
