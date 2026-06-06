using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for <see cref="CatalogService.BuildDependencyClosure"/>.
/// Covers the regression where stale <c>requires:</c> deps and chained
/// <c>update_for</c> deps were not surfaced when their seed was already current.
/// </summary>
public class CatalogServiceDependencyTests
{
    private static Dictionary<string, CatalogItem> Catalog(params CatalogItem[] items)
    {
        var map = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            map[item.Name.ToLowerInvariant()] = item;
        }
        return map;
    }

    private static CatalogItem Item(string name, List<string>? requires = null, List<string>? updateFor = null)
        => new() { Name = name, Version = "1.0.0", Requires = requires ?? new(), UpdateFor = updateFor ?? new() };

    [Fact]
    public void Closure_RequiresFlat_IncludesDep()
    {
        // Regression: ManageUsersPrefs requires ManageUsers. Seed is the prefs;
        // we must surface ManageUsers so its CheckStatus can run.
        var catalog = Catalog(
            Item("ManageUsersPrefs", requires: new() { "ManageUsers" }),
            Item("ManageUsers"));

        var deps = CatalogService.BuildDependencyClosure(new[] { "ManageUsersPrefs" }, catalog);

        Assert.Contains("ManageUsers", deps);
        Assert.DoesNotContain("ManageUsersPrefs", deps); // seeds excluded
    }

    [Fact]
    public void Closure_UpdateForChain_FollowsBeyondDepthOne()
    {
        // Regression: prior code iterated only the seed list, so AUpdate2 was missed.
        // A is the seed; AUpdate1 update_for A; AUpdate2 update_for AUpdate1.
        var catalog = Catalog(
            Item("A"),
            Item("AUpdate1", updateFor: new() { "A" }),
            Item("AUpdate2", updateFor: new() { "AUpdate1" }));

        var deps = CatalogService.BuildDependencyClosure(new[] { "A" }, catalog);

        Assert.Contains("AUpdate1", deps);
        Assert.Contains("AUpdate2", deps);
    }

    [Fact]
    public void Closure_MixedRelations_WalksBothDirections()
    {
        // A requires B; C update_for A. Seed A → expect B and C.
        var catalog = Catalog(
            Item("A", requires: new() { "B" }),
            Item("B"),
            Item("C", updateFor: new() { "A" }));

        var deps = CatalogService.BuildDependencyClosure(new[] { "A" }, catalog);

        Assert.Contains("B", deps);
        Assert.Contains("C", deps);
    }

    [Fact]
    public void Closure_RequiresChain_FollowsDeepRequires()
    {
        // A requires B; B requires C. Seed A → expect both B and C.
        var catalog = Catalog(
            Item("A", requires: new() { "B" }),
            Item("B", requires: new() { "C" }),
            Item("C"));

        var deps = CatalogService.BuildDependencyClosure(new[] { "A" }, catalog);

        Assert.Contains("B", deps);
        Assert.Contains("C", deps);
    }

    [Fact]
    public void Closure_RequiresCycle_Terminates()
    {
        // A requires B; B requires A. Must not infinite-loop.
        var catalog = Catalog(
            Item("A", requires: new() { "B" }),
            Item("B", requires: new() { "A" }));

        var deps = CatalogService.BuildDependencyClosure(new[] { "A" }, catalog);

        Assert.Contains("B", deps);
        Assert.DoesNotContain("A", deps); // seed excluded
    }

    [Fact]
    public void Closure_UnknownRequires_IsSkipped()
    {
        // A requires Phantom which isn't in the catalog — must not throw or add it.
        var catalog = Catalog(Item("A", requires: new() { "Phantom" }));

        var deps = CatalogService.BuildDependencyClosure(new[] { "A" }, catalog);

        Assert.Empty(deps);
    }

    [Fact]
    public void Closure_VersionedRequires_StripsVersion()
    {
        // requires: ["Foo-1.2.3"] should resolve to the catalog entry "Foo".
        var catalog = Catalog(
            Item("A", requires: new() { "Foo-1.2.3" }),
            Item("Foo"));

        var deps = CatalogService.BuildDependencyClosure(new[] { "A" }, catalog);

        Assert.Contains("Foo", deps);
    }

    [Fact]
    public void Closure_SeedAlreadyHasDepInList_DoesNotReAdd()
    {
        // If a dep is itself in the seed set, don't re-add it.
        var catalog = Catalog(
            Item("A", requires: new() { "B" }),
            Item("B"));

        var deps = CatalogService.BuildDependencyClosure(new[] { "A", "B" }, catalog);

        Assert.Empty(deps);
    }

    [Fact]
    public void Closure_DuplicateDepsAcrossSeeds_DedupedAndOrdered()
    {
        // Both A and B require C. Closure should list C exactly once.
        var catalog = Catalog(
            Item("A", requires: new() { "C" }),
            Item("B", requires: new() { "C" }),
            Item("C"));

        var deps = CatalogService.BuildDependencyClosure(new[] { "A", "B" }, catalog);

        Assert.Single(deps);
        Assert.Equal("C", deps[0]);
    }

    [Fact]
    public void Closure_PreservesCatalogCasing()
    {
        // Seed uses lowercase; catalog has canonical casing. Closure returns canonical.
        var catalog = Catalog(
            Item("ManageUsersPrefs", requires: new() { "manageusers" }),
            Item("ManageUsers"));

        var deps = CatalogService.BuildDependencyClosure(new[] { "ManageUsersPrefs" }, catalog);

        Assert.Single(deps);
        Assert.Equal("ManageUsers", deps[0]);
    }

    [Fact]
    public void Closure_EmptySeeds_ReturnsEmpty()
    {
        var catalog = Catalog(Item("A", requires: new() { "B" }), Item("B"));

        var deps = CatalogService.BuildDependencyClosure(Array.Empty<string>(), catalog);

        Assert.Empty(deps);
    }

    [Fact]
    public void Closure_LargeCatalog_DoesNotRescanPerNode()
    {
        // Regression check for the O(closure * catalog) blowup: 2000 unrelated
        // items + a 5-node requires chain. With the update_for index, total work
        // is roughly linear in catalog size. Generous budget — we just want to
        // catch a regression to per-node full scans, not benchmark.
        var items = new List<CatalogItem>
        {
            Item("A", requires: new() { "B" }),
            Item("B", requires: new() { "C" }),
            Item("C", requires: new() { "D" }),
            Item("D", requires: new() { "E" }),
            Item("E"),
        };
        for (int i = 0; i < 2000; i++)
        {
            items.Add(Item($"Noise{i}"));
        }
        var catalog = Catalog(items.ToArray());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deps = CatalogService.BuildDependencyClosure(new[] { "A" }, catalog);
        sw.Stop();

        Assert.Equal(4, deps.Count);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Closure walk took {sw.ElapsedMilliseconds}ms on 2k-item catalog — likely regressed to per-node full scan");
    }
}
