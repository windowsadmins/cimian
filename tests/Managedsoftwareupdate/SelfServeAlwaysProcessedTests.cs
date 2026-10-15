using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Models;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// The Self Service manifest is processed on every run, as in Munki 7's
/// processSelfServeManifest. A Config.yaml that still sets the removed skip key
/// must still load, and must not turn Self Service off.
/// </summary>
public class SelfServeAlwaysProcessedTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _selfServePath;

    public SelfServeAlwaysProcessedTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "SelfServe", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
        _selfServePath = Path.Combine(_testDir, "SelfServeManifest.yaml");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    private CimianConfig LoadLegacyConfig()
    {
        var configPath = Path.Combine(_testDir, "Config.yaml");
        File.WriteAllText(configPath, """
            SoftwareRepoURL: https://test.example.com
            ClientIdentifier: test-client
            SkipSelfService: true
            """);
        return new ConfigurationService(policyRegistryPath: null).LoadConfig(configPath);
    }

    [Fact]
    public void LoadConfig_WithRemovedSkipKey_LoadsOtherSettings()
    {
        var config = LoadLegacyConfig();

        Assert.Equal("https://test.example.com", config.SoftwareRepoURL);
        Assert.Equal("test-client", config.ClientIdentifier);
    }

    [Fact]
    public async Task MergeSelfServeManifest_WithRemovedSkipKey_StillMergesRequests()
    {
        File.WriteAllText(_selfServePath, """
            managed_installs:
              - Firefox
            managed_uninstalls:
              - VLC
            """);
        var service = new ManifestService(LoadLegacyConfig()) { CreateSelfServeManifestService = () => new Cimian.Core.Services.SelfServiceManifestService(_selfServePath) };
        var items = new List<ManifestItem>
        {
            new() { Name = "Firefox", Action = "optional", SourceManifest = "Staff" },
            new() { Name = "VLC", Action = "optional", SourceManifest = "Staff" },
        };

        await service.MergeSelfServeManifestAsync(items);

        Assert.Equal("install", items.Single(i => i.Name == "Firefox").Action);
        Assert.Equal("uninstall", items.Single(i => i.Name == "VLC").Action);
    }

    [Fact]
    public async Task CleanUpSelfServeUninstalls_WithRemovedSkipKey_StillConsumesCompletedRemovals()
    {
        File.WriteAllText(_selfServePath, """
            managed_uninstalls:
              - VLC
              - Zoom
            """);
        var engine = new UpdateEngine(LoadLegacyConfig()) { CreateSelfServeManifestService = () => new Cimian.Core.Services.SelfServiceManifestService(_selfServePath) };
        var outcomes = new List<ItemOutcome>
        {
            new("VLC", "3.0.0", "remove", true, null, DateTime.UtcNow),
            new("Zoom", "6.0.0", "remove", false, "uninstaller failed", DateTime.UtcNow),
        };

        await engine.CleanUpSelfServeUninstallsAsync(outcomes);

        var remaining = await new Cimian.Core.Services.SelfServiceManifestService(_selfServePath).LoadAsync();
        Assert.Equal(["Zoom"], remaining.ManagedUninstalls);
    }
}
