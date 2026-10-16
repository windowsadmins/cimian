using Xunit;
using Cimian.Core.Services;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// A CIMIAN-WARNING marker becomes the install's warning, which the run records as
/// Warning with last_warning in items.json, wherever the script ran: a packaged
/// script inside the installer, a nopkg item's install_script, or its
/// postinstall_script (#100).
/// </summary>
public class ScriptWarningOutcomeTests : IDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "CimianTests", "ScriptWarning", Guid.NewGuid().ToString());

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); } catch { }
    }

    private void WritePackageLog(string package, string phase, params string[] lines)
    {
        var dir = Path.Combine(_logs, "packages", package);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, $"{phase}.log"), lines);
    }

    [Fact]
    public void PackagedPostinstallMarker_IsTheWarning()
    {
        WritePackageLog("PrefsPackage", "postinstall",
            "Applying preferences",
            "CIMIAN-WARNING: precondition-unmet",
            "exiting 2");

        var warning = InstallerService.FindPackageScriptWarning(SessionLogger.CollectPackageScriptLogs(_logs));

        Assert.Equal("precondition-unmet", warning);
    }

    [Fact]
    public void PackagedPostinstallMarker_WinsOverPreinstallMarker()
    {
        WritePackageLog("PrefsPackage", "preinstall", "CIMIAN-WARNING: from-preinstall");
        WritePackageLog("PrefsPackage", "postinstall", "CIMIAN-WARNING: from-postinstall");

        var warning = InstallerService.FindPackageScriptWarning(SessionLogger.CollectPackageScriptLogs(_logs));

        Assert.Equal("from-postinstall", warning);
    }

    [Fact]
    public void PackagedScriptWithoutMarker_HasNoWarning()
    {
        WritePackageLog("PrefsPackage", "postinstall", "Applying preferences", "done");

        Assert.Null(InstallerService.FindPackageScriptWarning(SessionLogger.CollectPackageScriptLogs(_logs)));
    }

    // OnDemand keeps these installs from writing a ManagedInstalls receipt.
    private static CatalogItem Nopkg(string? installScript = null, string? postinstallScript = null) => new()
    {
        Name = "WarningScriptApp",
        Version = "1.0",
        OnDemand = true,
        Installer = new InstallerInfo { Type = "nopkg" },
        InstallScript = installScript,
        PostinstallScript = postinstallScript
    };

    [Fact]
    public async Task NopkgInstallScriptMarkerAndExit2_IsInstalledWithWarning()
    {
        var service = new InstallerService(new CimianConfig());

        var (success, _, warning) = await service.InstallAsync(
            Nopkg(installScript: "Write-Output 'CIMIAN-WARNING: precondition-unmet'; exit 2"), "");

        Assert.True(success);
        Assert.Equal("precondition-unmet", warning);
    }

    [Fact]
    public async Task NopkgInstallScriptExit1WithoutMarker_StillFails()
    {
        var service = new InstallerService(new CimianConfig());

        var (success, _, warning) = await service.InstallAsync(Nopkg(installScript: "exit 1"), "");

        Assert.False(success);
        Assert.Null(warning);
    }

    [Fact]
    public async Task PostinstallMarkerAndExit2_IsInstalledWithWarning()
    {
        var service = new InstallerService(new CimianConfig());

        var (success, _, warning) = await service.InstallAsync(
            Nopkg(installScript: "exit 0", postinstallScript: "Write-Output 'CIMIAN-WARNING: needs-followup'; exit 2"), "");

        Assert.True(success);
        Assert.Equal("needs-followup", warning);
    }

    [Fact]
    public async Task WarningFromOneInstall_DoesNotCarryIntoTheNext()
    {
        var service = new InstallerService(new CimianConfig());
        await service.InstallAsync(Nopkg(installScript: "Write-Output 'CIMIAN-WARNING: first'; exit 2"), "");

        var (_, _, warning) = await service.InstallAsync(Nopkg(installScript: "exit 0"), "");

        Assert.Null(warning);
    }
}
