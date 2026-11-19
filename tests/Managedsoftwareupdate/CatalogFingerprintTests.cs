using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// The fallback LoopGuard fingerprint, used when a catalog has no loop_fingerprint,
/// moves when only the installer's arguments change, so a fix that adds a quiet
/// switch releases the item's loop suppression (#101).
/// </summary>
public class CatalogFingerprintTests
{
    private static CatalogItem Item(Action<InstallerInfo>? edit = null)
    {
        var installer = new InstallerInfo
        {
            Type = "exe",
            Location = "apps/tool/tool-1.0.exe",
            Hash = "abc",
            Switches = ["/VERYSILENT"],
            Flags = ["NORESTART"],
            Args = ["--quiet"]
        };
        edit?.Invoke(installer);
        return new CatalogItem { Name = "Tool", Version = "1.0", Installer = installer };
    }

    public static TheoryData<string, Action<InstallerInfo>> ArgumentEdits => new()
    {
        { "switch added", i => i.Switches.Add("/SUPPRESSMSGBOXES") },
        { "flag added", i => i.Flags.Add("ALLUSERS") },
        { "arg changed", i => i.Args[0] = "--silent" },
        { "subcommand set", i => i.Subcommand = "install" },
        { "success code declared", i => i.SuccessCodes = [2] },
    };

    [Theory]
    [MemberData(nameof(ArgumentEdits))]
    public void InstallerArgumentChange_MovesFallbackFingerprint(string change, Action<InstallerInfo> edit)
    {
        var before = UpdateEngine.ComputeCatalogFingerprint(Item());
        var after = UpdateEngine.ComputeCatalogFingerprint(Item(edit));

        Assert.True(before != after, $"fingerprint did not move when {change}");
    }

    [Fact]
    public void SameItem_SameFingerprint()
    {
        Assert.Equal(UpdateEngine.ComputeCatalogFingerprint(Item()), UpdateEngine.ComputeCatalogFingerprint(Item()));
    }

    [Fact]
    public void SwitchesMovedBetweenLists_MovesFingerprint()
    {
        var asSwitch = Item(i => { i.Switches = ["X"]; i.Flags = []; });
        var asFlag = Item(i => { i.Switches = []; i.Flags = ["X"]; });

        Assert.NotEqual(UpdateEngine.ComputeCatalogFingerprint(asSwitch), UpdateEngine.ComputeCatalogFingerprint(asFlag));
    }

    [Fact]
    public void StampedLoopFingerprint_StillWins()
    {
        var a = Item(); a.LoopFingerprint = "stamp";
        var b = Item(i => i.Switches.Add("/X")); b.LoopFingerprint = "stamp";

        Assert.Equal(UpdateEngine.ComputeCatalogFingerprint(a), UpdateEngine.ComputeCatalogFingerprint(b));
    }
}
