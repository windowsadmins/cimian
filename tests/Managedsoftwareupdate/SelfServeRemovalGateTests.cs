using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// The engine's half of the Self Service removal gate (Munki 7 processRemoval): the item
/// must be in the catalog, removable, and actually installed.
/// </summary>
public class SelfServeRemovalGateTests
{
    private static CatalogItem Removable(string name) => new()
    {
        Name = name,
        Uninstaller = { new UninstallerInfo() },
    };

    [Fact]
    public void NotInTheCatalog_IsRefused()
    {
        var decision = UpdateEngine.DecideSelfServeRemoval(null, () => true, out var reason);

        Assert.Equal(SelfServeRemovalDecision.Refused, decision);
        Assert.Contains("catalog", reason);
    }

    [Fact]
    public void UninstallableFalse_IsRefused()
    {
        var item = Removable("Agent");
        item.Uninstallable = false;

        Assert.Equal(SelfServeRemovalDecision.Refused, UpdateEngine.DecideSelfServeRemoval(item, () => true, out var reason));
        Assert.Contains("uninstallable", reason);
    }

    [Fact]
    public void NotInstalled_IsNothingToDo()
    {
        Assert.Equal(SelfServeRemovalDecision.NothingToDo,
            UpdateEngine.DecideSelfServeRemoval(Removable("Gimp"), () => false, out _));
    }

    [Fact]
    public void OfferedRemovableAndInstalled_IsRemoved()
    {
        Assert.Equal(SelfServeRemovalDecision.Remove,
            UpdateEngine.DecideSelfServeRemoval(Removable("Firefox"), () => true, out _));
    }
}
