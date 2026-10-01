using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// managed_updates means "patch if present": an item listed only there is updated
/// where it is installed and left alone where it is not. These tests drive the real
/// status checks against files in a temp directory, with item names no machine has
/// a ManagedInstalls registry entry for.
/// </summary>
public class ManagedUpdatesPresenceTests : IDisposable
{
    private const string WrongHash = "00000000000000000000000000000000";

    private readonly string _testDir;
    private readonly CimianConfig _config;
    private readonly UpdateEngine _engine;
    private readonly string _suffix = Guid.NewGuid().ToString("N");

    public ManagedUpdatesPresenceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "ManagedUpdates", _suffix);
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
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    // No hyphen: a requires entry is split on one into name and version.
    private string Unique(string name) => $"{name}{_suffix}";

    /// <summary>An item whose installs file is not on disk: not installed.</summary>
    private CatalogItem AbsentItem(string name) => new()
    {
        Name = name,
        Version = "2.0.0",
        Installs = new List<InstallCheckItem>
        {
            new() { Type = "file", Path = Path.Combine(_testDir, name + ".missing") }
        }
    };

    /// <summary>An item whose installs file exists with another hash: installed, out of date.</summary>
    private CatalogItem OutdatedItem(string name)
    {
        var path = Path.Combine(_testDir, name + ".bin");
        File.WriteAllText(path, "old build");
        return new CatalogItem
        {
            Name = name,
            Version = "2.0.0",
            Installs = new List<InstallCheckItem>
            {
                new() { Type = "file", Path = path, Md5Checksum = WrongHash }
            }
        };
    }

    private static Dictionary<string, CatalogItem> Catalog(params CatalogItem[] items) =>
        items.ToDictionary(i => i.Name.ToLowerInvariant());

    private static ManifestItem Entry(string name, string action) =>
        new() { Name = name, Action = action, SourceManifest = "test" };

    [Fact]
    public void ManagedUpdateOnly_NotInstalled_IsLeftAlone()
    {
        var item = AbsentItem(Unique("absent"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_InstallcheckScriptSaysInstallNeeded_IsLeftAlone()
    {
        var item = new CatalogItem
        {
            Name = Unique("scripted"),
            Version = "2.0.0",
            InstallcheckScript = "exit 0"
        };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_InstalledAndOutOfDate_StillUpdates()
    {
        var item = OutdatedItem(Unique("outdated"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Single(toUpdate, item);
        Assert.Empty(_engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedInstall_NotInstalled_StillInstalls()
    {
        var item = AbsentItem(Unique("install"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "install") };

        var (toInstall, _, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(_engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void InBothManagedInstallsAndManagedUpdates_NotInstalled_StillInstalls()
    {
        var item = AbsentItem(Unique("both"));
        var manifest = new ManifestService(_config).DeduplicateItems(new List<ManifestItem>
        {
            Entry(item.Name, "update"),
            Entry(item.Name, "install")
        });

        var (toInstall, _, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(_engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_NotInstalled_DoesNotPullInItsRequires()
    {
        var dep = AbsentItem(Unique("dep"));
        var item = AbsentItem(Unique("absent"));
        item.Requires = new List<string> { dep.Name };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };
        var catalog = Catalog(item, dep);

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, catalog);
        _engine.ResolveDependencies(manifest, catalog, toUpdate);

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.DoesNotContain(manifest, m => m.Name == dep.Name);
    }

    [Fact]
    public void ManagedUpdateOnly_InstalledAndOutOfDate_StillPullsInItsRequires()
    {
        var dep = AbsentItem(Unique("dep"));
        var item = OutdatedItem(Unique("outdated"));
        item.Requires = new List<string> { dep.Name };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };
        var catalog = Catalog(item, dep);

        var (_, toUpdate, _, _) = _engine.IdentifyActions(manifest, catalog);
        _engine.ResolveDependencies(manifest, catalog, toUpdate);

        Assert.Contains(toUpdate, i => i.Name == item.Name);
        Assert.Contains(toUpdate, i => i.Name == dep.Name);
        Assert.Contains(manifest, m => m.Name == dep.Name);
    }
}
