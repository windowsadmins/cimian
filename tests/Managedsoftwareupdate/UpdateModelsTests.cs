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

    [Fact]
    public void CatalogItem_IsUninstallable_MsiInstallerWithProductCode_ReturnsTrue()
    {
        // Self-uninstallable MSI: no uninstaller block, but installer.product_code is
        // enough — UninstallAsync synthesizes msiexec /x from it.
        var item = new CatalogItem
        {
            Uninstallable = true,
            Uninstaller = [],
            Installer = new InstallerInfo
            {
                Type = "msi",
                ProductCode = "{12345678-1234-1234-1234-123456789012}"
            }
        };

        Assert.True(item.IsUninstallable());
    }

    [Fact]
    public void CatalogItem_IsUninstallable_MsiInstallerWithoutProductCode_ReturnsFalse()
    {
        // An MSI installer with no product_code can't be uninstalled — we have nothing
        // to feed msiexec /x.
        var item = new CatalogItem
        {
            Uninstallable = true,
            Uninstaller = [],
            Installer = new InstallerInfo { Type = "msi" }
        };

        Assert.False(item.IsUninstallable());
    }

    [Fact]
    public void CatalogItem_IsUninstallable_NonMsiWithProductCode_ReturnsFalse()
    {
        // The MSI synthesis clause is gated on installer.type == "msi". A pkg/exe
        // pkginfo that happens to carry a product_code (unusual) does not qualify.
        var item = new CatalogItem
        {
            Uninstallable = true,
            Uninstaller = [],
            Installer = new InstallerInfo
            {
                Type = "pkg",
                ProductCode = "{12345678-1234-1234-1234-123456789012}"
            }
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
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.UpgradeCode);
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
            Args = ["/qn", "/norestart"],
            ProductCode = "{12345678-1234-1234-1234-123456789012}",
            UpgradeCode = "{abcdef01-2345-6789-abcd-ef0123456789}"
        };

        Assert.Equal("apps/myapp/myapp-1.0.0.msi", installer.Location);
        Assert.Equal("msi", installer.Type);
        Assert.Equal("abc123def456", installer.Hash);
        Assert.Equal(1024000, installer.Size);
        Assert.Equal(2, installer.Args.Count);
        Assert.Equal("{12345678-1234-1234-1234-123456789012}", installer.ProductCode);
        Assert.Equal("{abcdef01-2345-6789-abcd-ef0123456789}", installer.UpgradeCode);
    }

    [Fact]
    public void InstallerInfo_DeserializesProductCodeAndUpgradeCodeFromYaml()
    {
        var yaml = """
            type: msi
            location: apps/myapp/myapp-1.0.0.msi
            product_code: '{12345678-1234-1234-1234-123456789012}'
            upgrade_code: '{abcdef01-2345-6789-abcd-ef0123456789}'
            """;

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        var installer = deserializer.Deserialize<InstallerInfo>(yaml);

        Assert.Equal("msi", installer.Type);
        Assert.Equal("{12345678-1234-1234-1234-123456789012}", installer.ProductCode);
        Assert.Equal("{abcdef01-2345-6789-abcd-ef0123456789}", installer.UpgradeCode);
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

    #region InstallWindow Tests

    [Fact]
    public void InstallWindow_NormalWindow_WithinReturnsTrue()
    {
        var window = new InstallWindow { Start = "04:00", End = "06:00" };
        // 5:00 AM is within 04:00-06:00
        var inside = new DateTime(2026, 2, 23, 5, 0, 0); // Monday
        Assert.True(window.IsWithinWindow(inside));
    }

    [Fact]
    public void InstallWindow_NormalWindow_OutsideReturnsFalse()
    {
        var window = new InstallWindow { Start = "04:00", End = "06:00" };
        // 8:00 AM is outside 04:00-06:00
        var outside = new DateTime(2026, 2, 23, 8, 0, 0);
        Assert.False(window.IsWithinWindow(outside));
    }

    [Fact]
    public void InstallWindow_ExactStart_IsInclusive()
    {
        var window = new InstallWindow { Start = "04:00", End = "06:00" };
        var atStart = new DateTime(2026, 2, 23, 4, 0, 0);
        Assert.True(window.IsWithinWindow(atStart));
    }

    [Fact]
    public void InstallWindow_ExactEnd_IsExclusive()
    {
        var window = new InstallWindow { Start = "04:00", End = "06:00" };
        var atEnd = new DateTime(2026, 2, 23, 6, 0, 0);
        Assert.False(window.IsWithinWindow(atEnd));
    }

    [Fact]
    public void InstallWindow_OvernightWrap_LateNightReturnsTrue()
    {
        var window = new InstallWindow { Start = "22:00", End = "06:00" };
        // 23:00 is within 22:00-06:00
        var lateNight = new DateTime(2026, 2, 23, 23, 0, 0);
        Assert.True(window.IsWithinWindow(lateNight));
    }

    [Fact]
    public void InstallWindow_OvernightWrap_EarlyMorningReturnsTrue()
    {
        var window = new InstallWindow { Start = "22:00", End = "06:00" };
        // 3:00 AM is within 22:00-06:00
        var earlyMorning = new DateTime(2026, 2, 24, 3, 0, 0);
        Assert.True(window.IsWithinWindow(earlyMorning));
    }

    [Fact]
    public void InstallWindow_OvernightWrap_MiddayReturnsFalse()
    {
        var window = new InstallWindow { Start = "22:00", End = "06:00" };
        // 12:00 PM is outside 22:00-06:00
        var midday = new DateTime(2026, 2, 23, 12, 0, 0);
        Assert.False(window.IsWithinWindow(midday));
    }

    [Fact]
    public void InstallWindow_WeekdayFilter_MatchingDayReturnsTrue()
    {
        var window = new InstallWindow
        {
            Start = "04:00", End = "06:00",
            Weekdays = new List<string> { "Mon", "Tue", "Wed", "Thu", "Fri" }
        };
        // Monday 5:00 AM
        var monday = new DateTime(2026, 2, 23, 5, 0, 0); // Feb 23, 2026 is Monday
        Assert.True(window.IsWithinWindow(monday));
    }

    [Fact]
    public void InstallWindow_WeekdayFilter_NonMatchingDayReturnsFalse()
    {
        var window = new InstallWindow
        {
            Start = "04:00", End = "06:00",
            Weekdays = new List<string> { "Mon", "Tue", "Wed", "Thu", "Fri" }
        };
        // Saturday 5:00 AM — day doesn't match
        var saturday = new DateTime(2026, 2, 28, 5, 0, 0); // Feb 28, 2026 is Saturday
        Assert.False(window.IsWithinWindow(saturday));
    }

    [Fact]
    public void InstallWindow_NoWeekdays_AllDaysAllowed()
    {
        var window = new InstallWindow { Start = "04:00", End = "06:00" };
        // Saturday 5:00 AM — no weekday filter, so allowed
        var saturday = new DateTime(2026, 2, 28, 5, 0, 0);
        Assert.True(window.IsWithinWindow(saturday));
    }

    [Fact]
    public void InstallWindow_WeekdaysCaseInsensitive()
    {
        var window = new InstallWindow
        {
            Start = "04:00", End = "06:00",
            Weekdays = new List<string> { "mon", "TUE" }
        };
        var monday = new DateTime(2026, 2, 23, 5, 0, 0);
        Assert.True(window.IsWithinWindow(monday));
    }

    [Fact]
    public void InstallWindow_InvalidTimes_FailsOpen()
    {
        var window = new InstallWindow { Start = "invalid", End = "also-invalid" };
        // Invalid config should fail-open (return true = no restriction)
        Assert.True(window.IsWithinWindow(DateTime.Now));
    }

    [Fact]
    public void InstallWindow_Null_CatalogItemHasNoRestriction()
    {
        var item = new CatalogItem { Name = "Test", Version = "1.0" };
        Assert.Null(item.InstallWindow);
    }

    [Fact]
    public void InstallWindow_ToString_FormatsCorrectly()
    {
        var window = new InstallWindow { Start = "04:00", End = "06:00" };
        Assert.Equal("04:00-06:00", window.ToString());
    }

    [Fact]
    public void InstallWindow_OvernightWrap_AfterMidnight_ChecksPreviousDay()
    {
        // Window is Mon 22:00 – Tue 02:00, restricted to Mondays.
        // At 01:00 Tue the current day is Tuesday, but we're in Monday's overnight window.
        var window = new InstallWindow
        {
            Start = "22:00", End = "02:00",
            Weekdays = new List<string> { "Mon" }
        };
        // Feb 23 2026 is Monday. 01:00 on Feb 24 (Tuesday) is still inside the Mon-night window.
        var tuesdayEarlyMorning = new DateTime(2026, 2, 24, 1, 0, 0);
        Assert.True(window.IsWithinWindow(tuesdayEarlyMorning));
    }

    [Fact]
    public void InstallWindow_OvernightWrap_AfterMidnight_WrongPreviousDay_ReturnsFalse()
    {
        // Window is Fri 22:00 – Sat 02:00, restricted to Fridays.
        // At 01:00 on a Tuesday (Monday night) it should not match.
        var window = new InstallWindow
        {
            Start = "22:00", End = "02:00",
            Weekdays = new List<string> { "Fri" }
        };
        // Feb 24 2026 is Tuesday. Previous day is Monday — not Friday.
        var tuesdayEarlyMorning = new DateTime(2026, 2, 24, 1, 0, 0);
        Assert.False(window.IsWithinWindow(tuesdayEarlyMorning));
    }

    #endregion
}
