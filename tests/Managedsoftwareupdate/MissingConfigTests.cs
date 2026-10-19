using Xunit;
using Cimian.CLI.managedsoftwareupdate.Services;
using Microsoft.Win32;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Under ManagedInstalls a missing Config.yaml (never written, or quarantined by the
/// permissions repair) must not send the device to the built-in defaults, which point at an
/// example repository. MDM policy can still supply the repository on its own.
/// </summary>
public class MissingConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _policyKey = $@"Software\CimianTests\Policy-{Guid.NewGuid():N}";

    public MissingConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "MissingConfig", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(_policyKey, throwOnMissingSubKey: false); } catch { }
    }

    [Fact]
    public void MissingConfig_UnderManagedInstalls_WithoutPolicy_StopsTheRun()
    {
        var path = Path.Combine(_dir, "Config.yaml");

        var ex = Assert.Throws<ConfigurationRefusedException>(
            () => new ConfigurationService(_policyKey, Registry.CurrentUser).LoadConfig(path, requireTrusted: true));
        Assert.Contains("built-in defaults", ex.Message);
        Assert.Contains("quarantine", ex.Message);
    }

    [Fact]
    public void MissingConfig_UnderManagedInstalls_WithPolicyRepository_RunsOnPolicy()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_policyKey))
        {
            key.SetValue("SoftwareRepoURL", "https://policy.example.org/repo");
            key.SetValue("ClientIdentifier", "PolicyClient");
        }
        var path = Path.Combine(_dir, "Config.yaml");

        var config = new ConfigurationService(_policyKey, Registry.CurrentUser).LoadConfig(path, requireTrusted: true);

        Assert.Equal("https://policy.example.org/repo", config.SoftwareRepoURL);
        Assert.Equal("PolicyClient", config.ClientIdentifier);
    }

    [Fact]
    public void MissingConfig_UnderManagedInstalls_WithBlankPolicyRepository_StopsTheRun()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_policyKey))
        {
            key.SetValue("SoftwareRepoURL", "   ");
        }
        var path = Path.Combine(_dir, "Config.yaml");

        Assert.Throws<ConfigurationRefusedException>(
            () => new ConfigurationService(_policyKey, Registry.CurrentUser).LoadConfig(path, requireTrusted: true));
    }

    [Fact]
    public void MissingConfig_OutsideManagedInstalls_KeepsTheDefaults()
    {
        // A --config path a test or an administrator names is not the hardened tree.
        var path = Path.Combine(_dir, "Config.yaml");

        var config = new ConfigurationService(null).LoadConfig(path, requireTrusted: false);

        Assert.NotNull(config);
    }
}
