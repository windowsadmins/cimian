using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Post-install verification decides the same way the status check does: a
/// version_script, when the item has one and no installcheck_script, decides
/// whether the install worked, not the installs array (#210).
/// </summary>
public class InstallVerificationTests
{
    private static readonly string MissingFile =
        Path.Combine(Path.GetTempPath(), "CimianTests", Guid.NewGuid().ToString(), "absent.exe");

    private static CatalogItem Item(string? versionScript, string? installcheckScript = null) => new()
    {
        Name = "VersionScriptApp",
        Version = "2.0.0",
        Installer = new InstallerInfo { Type = "exe" },
        VersionScript = versionScript,
        InstallcheckScript = installcheckScript,
        Installs = [new InstallCheckItem { Type = "file", Path = MissingFile }]
    };

    private static InstallerService Service() => new(new CimianConfig());

    [Fact]
    public void VersionScriptAtCatalogVersion_PassesDespiteStaleInstallsArray()
    {
        var (ok, reason) = Service().VerifyInstallationBeforeRegistry(Item("Write-Output '2.0.0'"));

        Assert.True(ok, reason);
    }

    [Fact]
    public void VersionScriptBelowCatalogVersion_Fails()
    {
        var (ok, reason) = Service().VerifyInstallationBeforeRegistry(Item("Write-Output '1.0.0'"));

        Assert.False(ok);
        Assert.Contains("version_script", reason);
    }

    [Fact]
    public void VersionScriptPrintingNothing_Fails()
    {
        var (ok, reason) = Service().VerifyInstallationBeforeRegistry(Item("exit 0"));

        Assert.False(ok);
        Assert.Contains("version_script", reason);
    }

    [Fact]
    public void NoVersionScript_StillChecksInstallsArray()
    {
        var (ok, reason) = Service().VerifyInstallationBeforeRegistry(Item(null));

        Assert.False(ok);
        Assert.Contains("expected file not found", reason);
    }

    [Fact]
    public void InstallcheckScriptOutranksVersionScript_InstallsArrayStillChecked()
    {
        var (ok, reason) = Service().VerifyInstallationBeforeRegistry(
            Item("Write-Output '2.0.0'", installcheckScript: "exit 1"));

        Assert.False(ok);
        Assert.Contains("expected file not found", reason);
    }
}
