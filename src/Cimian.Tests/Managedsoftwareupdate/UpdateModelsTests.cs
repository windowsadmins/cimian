using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for UpdateModels - core model classes for managedsoftwareupdate.
/// </summary>
public class UpdateModelsTests
{
    #region CimianConfig Tests

    [Fact]
    public void CimianConfig_DefaultValues_AreCorrect()
    {
        var config = new CimianConfig();

        Assert.Equal(@"C:\ProgramData\ManagedInstalls\Cache", config.CachePath);
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\catalogs", config.CatalogsPath);
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\manifests", config.ManifestsPath);
        Assert.Equal("INFO", config.LogLevel);
        Assert.Equal(900, config.InstallerTimeout);
        Assert.False(config.Verbose);
        Assert.False(config.Debug);
        Assert.False(config.NoPreflight);
        Assert.False(config.NoPostflight);
        Assert.False(config.CheckOnly);
    }

    [Fact]
    public void CimianConfig_ConfigPath_IsStatic()
    {
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\Config.yaml", CimianConfig.ConfigPath);
    }

    [Fact]
    public void CimianConfig_Catalogs_InitializesAsEmptyList()
    {
        var config = new CimianConfig();

        Assert.NotNull(config.Catalogs);
        Assert.Empty(config.Catalogs);
    }

    #endregion

    #region ManifestFile Tests

    [Fact]
    public void ManifestFile_DefaultValues_AreEmpty()
    {
        var manifest = new ManifestFile();

        Assert.NotNull(manifest.Catalogs);
        Assert.NotNull(manifest.IncludedManifests);
        Assert.NotNull(manifest.ManagedInstalls);
        Assert.NotNull(manifest.ManagedUninstalls);
        Assert.NotNull(manifest.ManagedUpdates);
        Assert.NotNull(manifest.OptionalInstalls);
        Assert.NotNull(manifest.ManagedProfiles);
        Assert.NotNull(manifest.ManagedApps);
        Assert.NotNull(manifest.ConditionalItems);
        Assert.Empty(manifest.Catalogs);
        Assert.Empty(manifest.ManagedInstalls);
    }

    [Fact]
    public void ManifestFile_CanSetProperties()
    {
        var manifest = new ManifestFile
        {
            Name = "site-manifest",
            Catalogs = ["production", "testing"],
            ManagedInstalls = ["app1", "app2"],
            ManagedUninstalls = ["oldapp"]
        };

        Assert.Equal("site-manifest", manifest.Name);
        Assert.Equal(2, manifest.Catalogs.Count);
        Assert.Equal(2, manifest.ManagedInstalls.Count);
        Assert.Single(manifest.ManagedUninstalls);
    }

    #endregion

    #region CatalogItem Tests

    [Fact]
    public void CatalogItem_DefaultValues_AreCorrect()
    {
        var item = new CatalogItem();

        Assert.Equal(string.Empty, item.Name);
        Assert.Equal(string.Empty, item.Version);
        Assert.Null(item.DisplayName);
        Assert.Null(item.Description);
        Assert.NotNull(item.Installer);
        Assert.NotNull(item.Uninstaller);
        Assert.NotNull(item.SupportedArch);
        Assert.NotNull(item.Requires);
        Assert.NotNull(item.UpdateFor);
        Assert.NotNull(item.BlockingApps);
        Assert.True(item.Uninstallable);
    }

    [Fact]
    public void CatalogItem_IsUninstallable_WithUninstaller_ReturnsTrue()
    {
        var item = new CatalogItem
        {
            Uninstallable = true,
            Uninstaller = [new UninstallerInfo { Type = "msi", ProductCode = "{1234}" }]
        };

        Assert.True(item.IsUninstallable());
    }

    [Fact]
    public void CatalogItem_IsUninstallable_WhenFalse_ReturnsFalse()
    {
        var item = new CatalogItem
        {
            Uninstallable = false,
            Uninstaller = [new UninstallerInfo { Type = "msi" }]
        };

        Assert.False(item.IsUninstallable());
    }

    [Fact]
    public void CatalogItem_IsUninstallable_NoUninstallerNoRegistry_ReturnsFalse()
    {
        var item = new CatalogItem
        {
            Uninstallable = true,
            Uninstaller = []
        };

        Assert.False(item.IsUninstallable());
    }

    #endregion

    #region InstallerInfo Tests

    [Fact]
    public void InstallerInfo_DefaultValues()
    {
        var installer = new InstallerInfo();

        Assert.Equal(string.Empty, installer.Location);
        Assert.Equal(string.Empty, installer.Type);
        Assert.NotNull(installer.Args);
        Assert.Empty(installer.Args);
        Assert.Null(installer.Hash);
        Assert.Null(installer.Size);
    }

    [Fact]
    public void InstallerInfo_CanSetAllProperties()
    {
        var installer = new InstallerInfo
        {
            Location = "apps/myapp/myapp-1.0.0.msi",
            Type = "msi",
            Hash = "abc123def456",
            Size = 1024000,
            Args = ["/qn", "/norestart"]
        };

        Assert.Equal("apps/myapp/myapp-1.0.0.msi", installer.Location);
        Assert.Equal("msi", installer.Type);
        Assert.Equal("abc123def456", installer.Hash);
        Assert.Equal(1024000, installer.Size);
        Assert.Equal(2, installer.Args.Count);
    }

    #endregion

    #region UninstallerInfo Tests

    [Fact]
    public void UninstallerInfo_DefaultValues()
    {
        var uninstaller = new UninstallerInfo();

        Assert.Equal(string.Empty, uninstaller.Type);
        Assert.Null(uninstaller.ProductCode);
        Assert.Null(uninstaller.Command);
        Assert.NotNull(uninstaller.Args);
        Assert.Empty(uninstaller.Args);
    }

    [Fact]
    public void UninstallerInfo_MsiType_HasProductCode()
    {
        var uninstaller = new UninstallerInfo
        {
            Type = "msi",
            ProductCode = "{12345678-1234-1234-1234-123456789012}"
        };

        Assert.Equal("msi", uninstaller.Type);
        Assert.NotNull(uninstaller.ProductCode);
    }

    [Fact]
    public void UninstallerInfo_ExeType_HasCommand()
    {
        var uninstaller = new UninstallerInfo
        {
            Type = "exe",
            Command = @"C:\Program Files\App\uninstall.exe",
            Args = ["/S"]
        };

        Assert.Equal("exe", uninstaller.Type);
        Assert.NotNull(uninstaller.Command);
        Assert.Single(uninstaller.Args);
    }

    #endregion

    #region CheckInfo Tests

    [Fact]
    public void CheckInfo_DefaultValues()
    {
        var check = new CheckInfo();

        Assert.NotNull(check.Registry);
        Assert.Null(check.File);
        Assert.Null(check.Script);
    }

    [Fact]
    public void RegistryCheck_CanSetProperties()
    {
        var registry = new RegistryCheck
        {
            Name = "My Application",
            Version = "1.0.0",
            Path = @"HKLM\SOFTWARE\MyApp",
            Value = "Version"
        };

        Assert.Equal("My Application", registry.Name);
        Assert.Equal("1.0.0", registry.Version);
        Assert.Equal(@"HKLM\SOFTWARE\MyApp", registry.Path);
    }

    [Fact]
    public void FileCheck_CanSetProperties()
    {
        var file = new FileCheck
        {
            Path = @"C:\Program Files\App\app.exe",
            Version = "1.0.0",
            Hash = "sha256hash"
        };

        Assert.Equal(@"C:\Program Files\App\app.exe", file.Path);
        Assert.Equal("1.0.0", file.Version);
        Assert.Equal("sha256hash", file.Hash);
    }

    #endregion

    #region ConditionalItem Tests

    [Fact]
    public void ConditionalItem_DefaultValues()
    {
        var item = new ConditionalItem();

        Assert.Equal(string.Empty, item.Condition);
        Assert.NotNull(item.ManagedInstalls);
        Assert.NotNull(item.ManagedUninstalls);
        Assert.NotNull(item.ManagedUpdates);
        Assert.NotNull(item.OptionalInstalls);
        Assert.Empty(item.ManagedInstalls);
    }

    [Fact]
    public void ConditionalItem_WithCondition()
    {
        var item = new ConditionalItem
        {
            Condition = "machine_type == 'laptop'",
            ManagedInstalls = ["laptop-tools", "vpn-client"]
        };

        Assert.Equal("machine_type == 'laptop'", item.Condition);
        Assert.Equal(2, item.ManagedInstalls.Count);
    }

    #endregion

    #region ManifestItem Tests

    [Fact]
    public void ManifestItem_DefaultValues()
    {
        var item = new ManifestItem();

        Assert.Equal(string.Empty, item.Name);
        Assert.Equal(string.Empty, item.Version);
        Assert.Equal(string.Empty, item.Action);
        Assert.Equal(string.Empty, item.SourceManifest);
        Assert.NotNull(item.SupportedArch);
        Assert.NotNull(item.Catalogs);
    }

    #endregion

    #region SessionSummary Tests

    [Fact]
    public void SessionSummary_DefaultValues()
    {
        var summary = new SessionSummary();

        Assert.Equal(0, summary.TotalActions);
        Assert.Equal(0, summary.Installs);
        Assert.Equal(0, summary.Updates);
        Assert.Equal(0, summary.Removals);
        Assert.Equal(0, summary.Successes);
        Assert.Equal(0, summary.Failures);
        Assert.NotNull(summary.PackagesHandled);
        Assert.Empty(summary.PackagesHandled);
    }

    [Fact]
    public void SessionSummary_CanTrackResults()
    {
        var summary = new SessionSummary
        {
            TotalActions = 5,
            Installs = 3,
            Updates = 1,
            Removals = 1,
            Successes = 4,
            Failures = 1,
            PackagesHandled = ["app1", "app2", "app3", "app4", "app5"]
        };

        Assert.Equal(5, summary.TotalActions);
        Assert.Equal(4, summary.Successes);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(5, summary.PackagesHandled.Count);
    }

    #endregion

    #region StatusCheckResult Tests

    [Fact]
    public void StatusCheckResult_DefaultValues()
    {
        var result = new StatusCheckResult();

        Assert.Equal(string.Empty, result.Status);
        Assert.Equal(string.Empty, result.Reason);
        Assert.False(result.NeedsAction);
        Assert.Null(result.Error);
    }

    [Fact]
    public void StatusCheckResult_CanSetValues()
    {
        var result = new StatusCheckResult
        {
            Status = "needs_install",
            Reason = "Package not found",
            NeedsAction = true
        };

        Assert.Equal("needs_install", result.Status);
        Assert.Equal("Package not found", result.Reason);
        Assert.True(result.NeedsAction);
    }

    #endregion

    #region CatalogWrapper Tests

    [Fact]
    public void CatalogWrapper_DefaultValues()
    {
        var wrapper = new CatalogWrapper();

        Assert.NotNull(wrapper.Items);
        Assert.Empty(wrapper.Items);
    }

    [Fact]
    public void CatalogWrapper_CanHoldMultipleItems()
    {
        var wrapper = new CatalogWrapper
        {
            Items = [
                new CatalogItem { Name = "app1", Version = "1.0.0" },
                new CatalogItem { Name = "app2", Version = "2.0.0" }
            ]
        };

        Assert.Equal(2, wrapper.Items.Count);
        Assert.Equal("app1", wrapper.Items[0].Name);
        Assert.Equal("app2", wrapper.Items[1].Name);
    }

    #endregion
}
