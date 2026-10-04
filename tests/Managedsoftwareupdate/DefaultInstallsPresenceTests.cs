using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Models;
using Cimian.Core.Services;
using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// default_installs means "install once via SelfServe, then the user owns presence"
/// (Munki 7). These tests cover the seed, the SelfServe merge and deduplication, and
/// IdentifyActions skipping leftover Action=default markers.
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

        var changed = ManifestService.SeedDefaultInstallsInto(items, selfServe, out var seeded);

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

        var changed = ManifestService.SeedDefaultInstallsInto(items, selfServe, out var seeded);

        Assert.False(changed);
        Assert.Empty(seeded);
        Assert.DoesNotContain(name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Single(selfServe.DefaultInstalls);
    }

    [Fact]
    public void Seed_WithoutOptional_StillSeeds_LikeMunki()
    {
        // Munki 7 processDefaultInstalls does not require optional_installs.
        var name = Unique("OrphanDefault");
        var items = new List<ManifestItem>
        {
            Entry(name, "default"),
        };
        var selfServe = EmptySelfServe();

        var changed = ManifestService.SeedDefaultInstallsInto(items, selfServe, out var seeded);

        Assert.True(changed);
        Assert.Equal(new[] { name }, seeded);
        Assert.Contains(name, selfServe.DefaultInstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seed_DoesNotClearPendingUninstall_LikeMunki()
    {
        // Munki 7 processDefaultInstalls never touches managed_uninstalls.
        var name = Unique("Browser");
        var items = new List<ManifestItem>
        {
            Entry(name, "default"),
        };
        var selfServe = EmptySelfServe();
        selfServe.ManagedUninstalls.Add(name);

        var changed = ManifestService.SeedDefaultInstallsInto(items, selfServe, out _);

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
        // After seed + merge, the winning action is SelfServe install.
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
        Assert.True(ManifestService.SeedDefaultInstallsInto(items, selfServe, out _));

        // User removes in MSC: drop from managed_installs, keep default_installs,
        // optionally queue uninstall (seed must not put it back on managed_installs).
        selfServe.ManagedInstalls.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        selfServe.ManagedUninstalls.Add(name);

        var changed = ManifestService.SeedDefaultInstallsInto(items, selfServe, out var seeded);

        Assert.False(changed);
        Assert.Empty(seeded);
        Assert.DoesNotContain(name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(name, selfServe.DefaultInstalls, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the manifest side of an update check over <paramref name="items"/>: the
    /// default_installs seed, the SelfServe merge and deduplication, then
    /// IdentifyActions and the InstallInfo that Managed Software Center reads.
    /// </summary>
    private (List<ManifestItem> Items, List<CatalogItem> ToInstall, List<CatalogItem> ToUninstall, InstallInfoFile Info)
        RunCheck(List<ManifestItem> items, SelfServiceManifest selfServe, Dictionary<string, CatalogItem> catalog)
    {
        var service = new ManifestService(_config);
        ManifestService.SeedDefaultInstallsInto(items, selfServe, out _);
        service.MergeSelfServeInto(items, selfServe);
        var deduped = service.DeduplicateItems(items);
        var (toInstall, toUpdate, toUninstall, _) = _engine.IdentifyActions(deduped, catalog);
        var info = _engine.BuildInstallInfo(deduped, toInstall, toUpdate, toUninstall, catalog);
        return (deduped, toInstall, toUninstall, info);
    }

    [Fact]
    public void DefaultAndOptional_AfterUserRemoval_StaysOfferedAsOptional()
    {
        // Listed in both default_installs and optional_installs. It was seeded once,
        // then the user removed it in Managed Software Center and the removal finished,
        // so SelfServe holds only the default_installs record. As in Munki 7 it must
        // still be an optional install the user can pick again.
        var item = AbsentItem(Unique("Editor"));
        var items = new List<ManifestItem>
        {
            Entry(item.Name, "default"),
            Entry(item.Name, "optional"),
        };
        var selfServe = EmptySelfServe();
        selfServe.DefaultInstalls.Add(item.Name);

        var (deduped, toInstall, toUninstall, info) = RunCheck(items, selfServe, Catalog(item));

        Assert.Equal("optional", Assert.Single(deduped).Action);
        Assert.Empty(toInstall);
        Assert.Empty(toUninstall);
        var offered = Assert.Single(info.OptionalInstalls);
        Assert.Equal(item.Name, offered.Name, ignoreCase: true);
        Assert.Equal("not-installed", offered.Status);
        Assert.DoesNotContain(selfServe.ManagedInstalls, n => n.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultAndOptional_UserReinstallsAfterRemoval_InstallsAsOptional()
    {
        // After the removal the user picks the item again in Managed Software Center.
        var item = AbsentItem(Unique("Editor"));
        var items = new List<ManifestItem>
        {
            Entry(item.Name, "optional"),
            Entry(item.Name, "default"),
        };
        var selfServe = EmptySelfServe();
        selfServe.DefaultInstalls.Add(item.Name);
        selfServe.ManagedInstalls.Add(item.Name);

        var (deduped, toInstall, _, info) = RunCheck(items, selfServe, Catalog(item));

        var entry = Assert.Single(deduped);
        Assert.Equal("install", entry.Action);
        Assert.True(entry.PromotedFromOptional);
        Assert.Single(toInstall, item);
        Assert.Equal("will-be-installed", Assert.Single(info.OptionalInstalls).Status);
    }

    [Fact]
    public void DefaultAndOptional_FirstEncounter_InstallsOnceAsOptional()
    {
        var item = AbsentItem(Unique("Editor"));
        var items = new List<ManifestItem>
        {
            Entry(item.Name, "default"),
            Entry(item.Name, "optional"),
        };
        var selfServe = EmptySelfServe();

        var (deduped, toInstall, _, info) = RunCheck(items, selfServe, Catalog(item));

        Assert.True(Assert.Single(deduped).PromotedFromOptional);
        Assert.Single(toInstall, item);
        Assert.Equal("will-be-installed", Assert.Single(info.OptionalInstalls).Status);
        Assert.Contains(item.Name, selfServe.DefaultInstalls, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultOnly_NotInOptionalInstalls_IsSeededButNeverInstalled()
    {
        // Munki 7 seeds the SelfServe manifest regardless, but processSelfServeManifest
        // only installs SelfServe managed_installs that are available optional installs,
        // so an item listed only under default_installs is neither installed nor offered.
        var item = AbsentItem(Unique("DefaultOnly"));
        var items = new List<ManifestItem> { Entry(item.Name, "default") };
        var selfServe = EmptySelfServe();

        var (_, toInstall, _, info) = RunCheck(items, selfServe, Catalog(item));

        Assert.Contains(item.Name, selfServe.ManagedInstalls, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(toInstall);
        Assert.Empty(info.ManagedInstalls);
        Assert.Empty(info.OptionalInstalls);
    }

    [Fact]
    public void DefaultAndOptional_PendingUninstall_IsReversedWhenSeeded()
    {
        // The seed leaves managed_uninstalls alone, and the seeded install request
        // then outranks the pending removal.
        var item = AbsentItem(Unique("Editor"));
        var items = new List<ManifestItem>
        {
            Entry(item.Name, "default"),
            Entry(item.Name, "optional"),
        };
        var selfServe = EmptySelfServe();
        selfServe.ManagedUninstalls.Add(item.Name);

        var (deduped, toInstall, toUninstall, _) = RunCheck(items, selfServe, Catalog(item));

        Assert.Equal("install", Assert.Single(deduped).Action);
        Assert.Single(toInstall, item);
        Assert.Empty(toUninstall);
    }

    /// <summary>A SelfServe manifest held in memory instead of on disk.</summary>
    private sealed class InMemorySelfServe(SelfServiceManifest manifest) : ISelfServiceManifestService
    {
        public SelfServiceManifest Manifest { get; private set; } = manifest;
        public int Saves { get; private set; }

        public event EventHandler? RequestsChanged { add { } remove { } }

        public Task<SelfServiceManifest> LoadAsync() => Task.FromResult(Manifest);

        public Task SaveAsync(SelfServiceManifest manifest)
        {
            Manifest = manifest;
            Saves++;
            return Task.CompletedTask;
        }

        public Task AddInstallRequestAsync(string itemName) => throw new NotSupportedException();
        public Task AddRemovalRequestAsync(string itemName) => throw new NotSupportedException();
        public Task RemoveRequestAsync(string itemName) => throw new NotSupportedException();
        public Task<bool> IsInstallRequestedAsync(string itemName) => throw new NotSupportedException();
        public Task<bool> IsRemovalRequestedAsync(string itemName) => throw new NotSupportedException();
        public Task<SelfServiceManifest> GetAllRequestsAsync() => Task.FromResult(Manifest);
    }
}
