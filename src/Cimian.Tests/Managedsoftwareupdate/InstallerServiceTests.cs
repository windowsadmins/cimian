using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for InstallerService - package installation with timeout protection.
/// </summary>
public class InstallerServiceTests
{
    private readonly CimianConfig _testConfig;
    private readonly string _testDir;
    private readonly InstallerService _service;

    public InstallerServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "Installer", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);

        _testConfig = new CimianConfig
        {
            InstallerTimeout = 30, // Short timeout for tests
            CachePath = _testDir
        };

        _service = new InstallerService(_testConfig);
    }

    #region Blocking App Detection Tests

    [Fact]
    public void CheckBlockingApps_NoBlockingApps_ReturnsFalse()
    {
        var item = new CatalogItem
        {
            Name = "TestApp",
            BlockingApps = []
        };

        var hasBlocking = _service.CheckBlockingApps(item, out var runningApps);

        Assert.False(hasBlocking);
        Assert.Empty(runningApps);
    }

    [Fact]
    public void CheckBlockingApps_NonRunningApps_ReturnsFalse()
    {
        var item = new CatalogItem
        {
            Name = "TestApp",
            BlockingApps = ["FakeApp12345.exe", "NonExistentApp67890.exe"]
        };

        var hasBlocking = _service.CheckBlockingApps(item, out var runningApps);

        Assert.False(hasBlocking);
        Assert.Empty(runningApps);
    }

    #endregion

    #region NeedsUpdate Tests

    [Fact]
    public void NeedsUpdate_PackageNotInCatalog_ReturnsFalse()
    {
        var manifestItem = new ManifestItem { Name = "NonExistent" };
        var catalogMap = new Dictionary<string, CatalogItem>();

        var needsUpdate = _service.NeedsUpdate(manifestItem, catalogMap);

        Assert.False(needsUpdate);
    }

    #endregion

    #region Install Method Tests

    [Fact]
    public async Task InstallAsync_ScriptOnlyItem_Succeeds()
    {
        var item = new CatalogItem
        {
            Name = "ScriptOnlyPackage",
            Version = "1.0.0",
            Installer = new InstallerInfo { Type = "script" }
        };

        var (success, output) = await _service.InstallAsync(item, null!);

        Assert.True(success);
        Assert.Contains("Script-only", output);
    }

    [Fact]
    public async Task InstallAsync_WithPreinstallScript_RunsScript()
    {
        var item = new CatalogItem
        {
            Name = "ScriptPackage",
            Version = "1.0.0",
            Installer = new InstallerInfo { Type = "script" },
            PreinstallScript = "Write-Output 'Preinstall ran'"
        };

        var (success, _) = await _service.InstallAsync(item, null!);

        Assert.True(success);
    }

    [Fact]
    public async Task InstallAsync_PreinstallScriptFails_ReturnsFailure()
    {
        var item = new CatalogItem
        {
            Name = "FailingPackage",
            Version = "1.0.0",
            Installer = new InstallerInfo { Type = "script" },
            PreinstallScript = "throw 'Intentional failure'"
        };

        var (success, output) = await _service.InstallAsync(item, null!);

        Assert.False(success);
        Assert.Contains("Preinstall script failed", output);
    }

    #endregion

    #region Uninstall Method Tests

    [Fact]
    public async Task UninstallAsync_NoUninstaller_ReturnsFailure()
    {
        var item = new CatalogItem
        {
            Name = "NoUninstallerPackage",
            Version = "1.0.0",
            Uninstaller = []
        };

        var (success, output) = await _service.UninstallAsync(item);

        Assert.False(success);
        Assert.Contains("No uninstaller defined", output);
    }

    [Fact]
    public async Task UninstallAsync_MsiWithNoProductCode_ReturnsFailure()
    {
        var item = new CatalogItem
        {
            Name = "BadMsiPackage",
            Version = "1.0.0",
            Uninstaller = [
                new UninstallerInfo { Type = "msi", ProductCode = null }
            ]
        };

        var (success, output) = await _service.UninstallAsync(item);

        Assert.False(success);
        Assert.Contains("No product code", output);
    }

    [Fact]
    public async Task UninstallAsync_ExeWithNoCommand_ReturnsFailure()
    {
        var item = new CatalogItem
        {
            Name = "BadExePackage",
            Version = "1.0.0",
            Uninstaller = [
                new UninstallerInfo { Type = "exe", Command = null }
            ]
        };

        var (success, output) = await _service.UninstallAsync(item);

        Assert.False(success);
        Assert.Contains("No uninstall command", output);
    }

    [Fact]
    public async Task UninstallAsync_PowerShellWithNoCommand_ReturnsFailure()
    {
        var item = new CatalogItem
        {
            Name = "BadPSPackage",
            Version = "1.0.0",
            Uninstaller = [
                new UninstallerInfo { Type = "powershell", Command = null }
            ]
        };

        var (success, output) = await _service.UninstallAsync(item);

        Assert.False(success);
        Assert.Contains("No uninstall script", output);
    }

    [Fact]
    public async Task UninstallAsync_WithPreuninstallScript_RunsScript()
    {
        var item = new CatalogItem
        {
            Name = "ScriptUninstall",
            Version = "1.0.0",
            PreuninstallScript = "Write-Output 'Preuninstall ran'",
            Uninstaller = [
                new UninstallerInfo { Type = "powershell", Command = "Write-Output 'Uninstalled'" }
            ]
        };

        var (success, _) = await _service.UninstallAsync(item);

        Assert.True(success);
    }

    #endregion
}
