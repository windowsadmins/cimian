using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core;
using Cimian.Core.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Files under ManagedInstalls that SYSTEM takes instructions from are checked when they
/// are loaded, not only by the permissions repair: a handle opened before the repair keeps
/// its rights, so something can still be planted afterwards. The test process is an
/// ordinary account, so the files it writes look exactly like planted ones.
/// </summary>
public class LoadTimeTrustTests : IDisposable
{
    private readonly string _dir;

    public LoadTimeTrustTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "LoadTimeTrust", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ConfigAnyUserCouldHaveWritten_StopsTheRun()
    {
        // Never fall back to defaults: without MDM policy they point at a dead example
        // repository, after the run has already cleared the local catalogs.
        var path = Path.Combine(_dir, "Config.yaml");
        File.WriteAllText(path, "SoftwareRepoURL: https://attacker.example.net\n");

        var ex = Assert.Throws<ConfigurationRefusedException>(
            () => new ConfigurationService().LoadConfig(path, requireTrusted: true));
        Assert.Contains("Refusing", ex.Message);
    }

    [Fact]
    public void ConfigSomeoneHoldsOpen_StopsTheRun_AndSaysItCouldNotBeRead()
    {
        // Config.yaml is readable by Users, so any user can hold it open without sharing.
        var path = Path.Combine(_dir, "Config.yaml");
        File.WriteAllText(path, "SoftwareRepoURL: https://repo.example.org\n");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<ConfigurationRefusedException>(
            () => new ConfigurationService().LoadConfig(path, requireTrusted: true));
        Assert.Contains("could not read", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalCatalogSomeoneHoldsOpen_StopsTheRunInsteadOfRunningWithAnEmptyCatalog()
    {
        var path = Path.Combine(_dir, "Production.yaml");
        File.WriteAllText(path, "items:\n  - name: Firefox\n    version: 1.0\n");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<CatalogUnavailableException>(
            () => new CatalogService(new CimianConfig()).LoadLocalCatalog(path, requireTrusted: false));
    }

    [Fact]
    public void CatalogsFolder_IsClosedToUsers()
    {
        Assert.True(ManagedInstallsSecurity.IsAdminOnlyLocation(CimianPaths.CatalogsDir));
    }

    [Fact]
    public void LocalOnlyManifestAnyUserCouldHaveWritten_IsRefused()
    {
        var path = Path.Combine(_dir, "local.yaml");
        File.WriteAllText(path, "managed_installs:\n  - Planted\n");

        Assert.Throws<UnauthorizedAccessException>(
            () => new ManifestService(new CimianConfig()).LoadLocalOnlyManifest(path, requireTrusted: true));
    }

    [Fact]
    public void ConditionScriptSomeoneHoldsOpen_StopsTheRun()
    {
        // Skipping it would change what conditional items evaluate to; fail closed instead.
        var script = Path.Combine(_dir, "facts.ps1");
        File.WriteAllText(script, "Write-Output 'role=lab'");
        using var held = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<InvalidOperationException>(() => ManifestService.ConditionScripts(_dir));
        Assert.Contains("could not read", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FlightScriptSomeoneHoldsOpen_FailsAsUnreadableNotUntrusted()
    {
        var script = Path.Combine(_dir, "preflight.ps1");
        File.WriteAllText(script, "exit 0");
        using var held = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.None);

        var (success, output) = await new ScriptService().RunFlightScriptAsync(
            "preflight", new[] { script }, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.False(success);
        Assert.Contains("could not read", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigOutsideManagedInstalls_IsLoadedAsBefore()
    {
        // An administrator's explicit --config path is their call; only the files under
        // ManagedInstalls that users could once write are held to the stricter rule.
        var path = Path.Combine(_dir, "Config.yaml");
        File.WriteAllText(path, "SoftwareRepoURL: https://repo.example.org\n");

        Assert.Equal("https://repo.example.org", new ConfigurationService().LoadConfig(path).SoftwareRepoURL);
    }

    [Fact]
    public void LocalCatalogAnyUserCouldHaveWritten_IsNotUsed()
    {
        var path = Path.Combine(_dir, "Production.yaml");
        File.WriteAllText(path, "items:\n  - name: Planted\n    version: 1.0\n");

        var items = new CatalogService(new CimianConfig()).LoadLocalCatalog(path, requireTrusted: true);

        Assert.Empty(items);
    }

    [Fact]
    public void UserFilesTheRepairAdopts_AreOnlyTheOnesUsersWriteByDesign()
    {
        // Since the Munki 7 copy model, users never write SelfServeManifest.yaml.
        Assert.False(ManagedInstallsSecurity.IsExpectedUserOwned(CimianPaths.SelfServeManifestYaml));
        Assert.True(ManagedInstallsSecurity.IsExpectedUserOwned(CimianPaths.LastRunTimeFile));
        Assert.False(ManagedInstallsSecurity.IsExpectedUserOwned(CimianPaths.ConfigYaml));
        Assert.False(ManagedInstallsSecurity.IsExpectedUserOwned(CimianPaths.BootstrapFlagFile));
        Assert.False(ManagedInstallsSecurity.IsExpectedUserOwned(Path.Combine(CimianPaths.ManagedInstallsRoot, "anything.yaml")));
    }
}
