using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// default_installs means "install once via SelfServe, then the user owns presence"
/// (Munki 6.1). Pure seed helper coverage plus IdentifyActions skipping leftover
/// Action=default markers. Seed logic lives on <see cref="InstallInfoAnalyzer"/>
/// (relocated from ManifestService — see windowsadmins/cimian#188 / #189).
/// </summary>
public class DefaultInstallsPresenceTests : IDisposable
{
    private readonly string _testDir;
    private readonly CimianConfig _config;
    private readonly UpdateEngine _engine;
    private readonly string _suffix = Guid.NewGuid().ToString("N");

    public DefaultInstallsPresenceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "DefaultInstalls", _suffix);
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

    private static Dictionary<string, CatalogItem> Catalog(params CatalogItem[] items) =>
        items.ToDictionary(i => i.Name.ToLowerInvariant());

    private static ManifestItem Entry(string name, string action) =>
        new() { Name = name, Action = action, SourceManifest = "test" };

    private static SelfServiceManifest EmptySelfServe() => new()
    {
        Name = "SelfServeManifest",
        ManagedInstalls = [],
        ManagedUninstalls = [],
        OptionalInstalls = [],
        DefaultInstalls = []
    };

    [Fact]
    public void Seed_FirstEncounter_AddsDefaultAndManagedInstalls()
    {
        var name = Unique("Browser");
        var items = new List<ManifestItem>
        {
            Entry(name, "default"),
        };
        var selfServe = EmptySelfServe();

        var changed = InstallInfoAnalyzer.SeedDefaultInstallsInto(items, selfServe, out var seeded);

        Assert.True(changed);
        Assert.Equal(new[] { name }, seeded);
        Assert.Contains(name, selfServe.DefaultInstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seed_AlreadyInSelfServeDefaultInstalls_DoesNotReseed()
    {
        var name = Unique("Browser");
        var items = new List<ManifestItem>
        {
            Entry(name, "default"),
        };
        var selfServe = EmptySelfServe();
        selfServe.DefaultInstalls.Add(name);
        // User removed: not in managed_installs.

        var changed = InstallInfoAnalyzer.SeedDefaultInstallsInto(items, selfServe, out var seeded);

        Assert.False(changed);
        Assert.Empty(seeded);
        Assert.DoesNotContain(name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Single(selfServe.DefaultInstalls);
    }

    [Fact]
    public void Seed_WithoutOptional_StillSeeds_LikeMunki()
    {
        // Munki process_default_installs does not require optional_installs.
        var name = Unique("OrphanDefault");
        var items = new List<ManifestItem>
        {
            Entry(name, "default"),
        };
        var selfServe = EmptySelfServe();

        var changed = InstallInfoAnalyzer.SeedDefaultInstallsInto(items, selfServe, out var seeded);

        Assert.True(changed);
        Assert.Equal(new[] { name }, seeded);
        Assert.Contains(name, selfServe.DefaultInstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seed_DoesNotClearPendingUninstall_LikeMunki()
    {
        // Munki process_default_installs never touches managed_uninstalls.
        var name = Unique("Browser");
        var items = new List<ManifestItem>
        {
            Entry(name, "default"),
        };
        var selfServe = EmptySelfServe();
        selfServe.ManagedUninstalls.Add(name);

        var changed = InstallInfoAnalyzer.SeedDefaultInstallsInto(items, selfServe, out _);

        Assert.True(changed);
        Assert.Contains(name, selfServe.ManagedUninstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentifyActions_LeftoverDefault_NotInstalled_IsLeftAlone()
    {
        var item = AbsentItem(Unique("leftover"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "default") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
    }

    [Fact]
    public void IdentifyActions_SelfServePromotedInstall_NotInstalled_StillInstalls()
    {
        var item = AbsentItem(Unique("seeded"));
        // After seed + SelfServe, the winning action is SelfServe install.
        var manifest = new List<ManifestItem>
        {
            new()
            {
                Name = item.Name,
                Action = "install",
                SourceManifest = "SelfServeManifest",
                IsSelfServe = true,
                PromotedFromOptional = true
            }
        };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(toUpdate);
    }

    [Fact]
    public void Deduplicate_ManagedInstallStillSupersedesDefault()
    {
        var name = Unique("Browser");
        var service = new ManifestService(_config);
        var result = service.DeduplicateItems(new List<ManifestItem>
        {
            Entry(name, "default"),
            Entry(name, "install"),
        });

        var entry = Assert.Single(result);
        Assert.Equal("install", entry.Action);
    }

    [Fact]
    public void UserRemoval_AfterSeed_DoesNotReseedOnNextPass()
    {
        var name = Unique("Browser");
        var items = new List<ManifestItem>
        {
            Entry(name, "default"),
        };

        var selfServe = EmptySelfServe();
        Assert.True(InstallInfoAnalyzer.SeedDefaultInstallsInto(items, selfServe, out _));

        // User removes in MSC: drop from managed_installs, keep default_installs,
        // optionally queue uninstall (seed must not put it back on managed_installs).
        selfServe.ManagedInstalls.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        selfServe.ManagedUninstalls.Add(name);

        var changed = InstallInfoAnalyzer.SeedDefaultInstallsInto(items, selfServe, out var seeded);

        Assert.False(changed);
        Assert.Empty(seeded);
        Assert.DoesNotContain(name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(name, selfServe.DefaultInstalls, StringComparer.OrdinalIgnoreCase);
    }
}
