using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for <see cref="CatalogService.OrderByRequires"/> and
/// <see cref="CatalogService.FindFailedRequirement"/>: an item must be installed
/// after the items it requires when both are installed in the same run.
/// </summary>
public class CatalogServiceInstallOrderTests
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

    private static CatalogItem Item(string name, params string[] requires)
        => new() { Name = name, Version = "1.0", Requires = requires.ToList() };

    private static readonly CatalogItem Top = Item("Top", "Mid");
    private static readonly CatalogItem Mid = Item("Mid", "Base");
    private static readonly CatalogItem Base = Item("Base");
    private static readonly Dictionary<string, CatalogItem> Chain = Catalog(Top, Mid, Base);

    /// <summary>
    /// The run list is <c>toInstall.Concat(toUpdate)</c>; this builds it the same way.
    /// </summary>
    private static string[] Order(CatalogItem[] toInstall, CatalogItem[] toUpdate, Dictionary<string, CatalogItem> catalog)
        => CatalogService.OrderByRequires(toInstall.Concat(toUpdate), catalog).Select(i => i.Name).ToArray();

    [Fact]
    public void Order_OnlyTopInManifest_InstallsBaseThenMidThenTop()
    {
        // Top comes from the manifest; Mid and Base are appended to toUpdate by
        // dependency resolution, in discovery order (parent before requirement).
        var order = Order(new[] { Top }, new[] { Mid, Base }, Chain);

        Assert.Equal(new[] { "Base", "Mid", "Top" }, order);
    }

    [Fact]
    public void Order_ManifestListsTopMidBase_InstallsBaseThenMidThenTop()
    {
        var order = Order(new[] { Top, Mid, Base }, Array.Empty<CatalogItem>(), Chain);

        Assert.Equal(new[] { "Base", "Mid", "Top" }, order);
    }

    [Fact]
    public void Order_ManifestListsBaseMidTop_IsUnchanged()
    {
        var order = Order(new[] { Base, Mid, Top }, Array.Empty<CatalogItem>(), Chain);

        Assert.Equal(new[] { "Base", "Mid", "Top" }, order);
    }

    [Fact]
    public void Order_RequirementInToUpdate_StillInstallsFirst()
    {
        // Base was installed by Cimian before and needs action again, so it is
        // classified as an update and stands after the installs.
        var order = Order(new[] { Mid, Top }, new[] { Base }, Chain);

        Assert.Equal(new[] { "Base", "Mid", "Top" }, order);
    }

    [Fact]
    public void Order_UnrelatedItems_KeepTheirPlaces()
    {
        var a = Item("A");
        var b = Item("B");
        var catalog = Catalog(Top, Mid, Base, a, b);

        var order = Order(new[] { a, Top, b }, new[] { Mid, Base }, catalog);

        Assert.Equal(new[] { "A", "Base", "Mid", "Top", "B" }, order);
    }

    [Fact]
    public void Order_RequirementBehindItemNotInRun_StillInstallsFirst()
    {
        // Mid is already installed and not part of the run, but Base, which Mid
        // requires, needs action again.
        var order = Order(new[] { Top }, new[] { Base }, Chain);

        Assert.Equal(new[] { "Base", "Top" }, order);
    }

    [Fact]
    public void Order_VersionedAndDifferentlyCasedRequires_AreResolved()
    {
        var app = Item("App", "runtime-1.2.3");
        var runtime = Item("Runtime");

        var order = Order(new[] { app, runtime }, Array.Empty<CatalogItem>(), Catalog(app, runtime));

        Assert.Equal(new[] { "Runtime", "App" }, order);
    }

    [Fact]
    public void Order_SharedRequirement_AppearsOnce()
    {
        var a = Item("A", "C");
        var b = Item("B", "C");
        var c = Item("C");

        var order = Order(new[] { a, b }, new[] { c }, Catalog(a, b, c));

        Assert.Equal(new[] { "C", "A", "B" }, order);
    }

    [Fact]
    public void Order_RequiresCycle_TerminatesAndReturnsEveryItemOnce()
    {
        var a = Item("A", "B");
        var b = Item("B", "C");
        var c = Item("C", "A");

        var order = Order(new[] { a, b, c }, Array.Empty<CatalogItem>(), Catalog(a, b, c));

        Assert.Equal(new[] { "A", "B", "C" }, order.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Order_SelfRequire_Terminates()
    {
        var a = Item("A", "A");

        var order = Order(new[] { a }, Array.Empty<CatalogItem>(), Catalog(a));

        Assert.Equal(new[] { "A" }, order);
    }

    [Fact]
    public void Order_UnknownRequirement_IsIgnored()
    {
        var a = Item("A", "Phantom");

        var order = Order(new[] { a }, Array.Empty<CatalogItem>(), Catalog(a));

        Assert.Equal(new[] { "A" }, order);
    }

    [Fact]
    public void FailedRequirement_Found_IgnoringCaseAndVersion()
    {
        var app = Item("App", "Other", "runtime-1.2.3");

        var failed = CatalogService.FindFailedRequirement(app, new List<string> { "Runtime" });

        Assert.Equal("runtime-1.2.3", failed);
    }

    [Fact]
    public void FailedRequirement_NoneFailed_ReturnsNull()
    {
        Assert.Null(CatalogService.FindFailedRequirement(Mid, new List<string> { "Top" }));
        Assert.Null(CatalogService.FindFailedRequirement(Base, new List<string> { "Mid" }));
    }

    [Fact]
    public void FailedRequirement_PropagatesDownTheChainInRunOrder()
    {
        // The install loop walks the ordered list and marks an item failed when
        // one of its requirements failed; this is that walk with Base failing.
        var failed = new List<string>();
        var attempted = new List<string>();

        foreach (var item in CatalogService.OrderByRequires(new[] { Top, Mid, Base }, Chain))
        {
            if (CatalogService.FindFailedRequirement(item, failed) != null)
            {
                failed.Add(item.Name);
                continue;
            }

            attempted.Add(item.Name);
            if (item.Name == "Base") failed.Add(item.Name);
        }

        Assert.Equal(new[] { "Base" }, attempted);
        Assert.Equal(new[] { "Base", "Mid", "Top" }, failed);
    }
}
