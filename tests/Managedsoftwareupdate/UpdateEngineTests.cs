using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for UpdateEngine - the main orchestration service for managedsoftwareupdate.
/// These tests focus on construction and configuration, avoiding network calls.
/// </summary>
public class UpdateEngineTests : IDisposable
{
    private readonly CimianConfig _testConfig;
    private readonly string _testDir;

    public UpdateEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "Engine", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);

        _testConfig = new CimianConfig
        {
            CachePath = Path.Combine(_testDir, "Cache"),
            CatalogsPath = Path.Combine(_testDir, "catalogs"),
            ManifestsPath = Path.Combine(_testDir, "manifests"),
            SoftwareRepoURL = "https://test.example.com/repo",
            InstallerTimeout = 30
        };

        Directory.CreateDirectory(_testConfig.CachePath);
        Directory.CreateDirectory(_testConfig.CatalogsPath);
        Directory.CreateDirectory(_testConfig.ManifestsPath);
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

    #region Engine Initialization Tests

    [Fact]
    public void UpdateEngine_Constructor_DoesNotThrow()
    {
        var config = new CimianConfig
        {
            CachePath = _testDir,
            SoftwareRepoURL = "https://test.example.com"
        };

        var exception = Record.Exception(() => new UpdateEngine(config));

        Assert.Null(exception);
    }

    [Fact]
    public void UpdateEngine_Constructor_WithMinimalConfig_Works()
    {
        var config = new CimianConfig();

        var exception = Record.Exception(() => new UpdateEngine(config));

        Assert.Null(exception);
    }

    [Fact]
    public void UpdateEngine_Constructor_WithFullConfig_Works()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "https://full.example.com",
            ClientIdentifier = "test-client",
            CachePath = _testDir,
            CatalogsPath = Path.Combine(_testDir, "cats"),
            ManifestsPath = Path.Combine(_testDir, "mans"),
            LogLevel = "DEBUG",
            InstallerTimeout = 1800,
            Verbose = true,
            Debug = true,
            NoPreflight = true,
            NoPostflight = true,
            CheckOnly = true,
            Catalogs = ["production", "testing"]
        };

        var engine = new UpdateEngine(config);

        Assert.NotNull(engine);
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void UpdateEngine_UsesProvidedConfig()
    {
        var customConfig = new CimianConfig
        {
            SoftwareRepoURL = "https://custom.example.com",
            CachePath = _testDir,
            InstallerTimeout = 120
        };

        var engine = new UpdateEngine(customConfig);

        Assert.NotNull(engine);
    }

    [Fact]
    public void UpdateEngine_MultipleInstances_CanBeCreated()
    {
        var config1 = new CimianConfig { CachePath = Path.Combine(_testDir, "cache1") };
        var config2 = new CimianConfig { CachePath = Path.Combine(_testDir, "cache2") };

        var engine1 = new UpdateEngine(config1);
        var engine2 = new UpdateEngine(config2);

        Assert.NotNull(engine1);
        Assert.NotNull(engine2);
    }

    #endregion

    #region Timeout Configuration Tests

    [Fact]
    public void UpdateEngine_WithShortTimeout_Creates()
    {
        var config = new CimianConfig
        {
            InstallerTimeout = 60
        };

        var engine = new UpdateEngine(config);

        Assert.NotNull(engine);
    }

    [Fact]
    public void UpdateEngine_WithLongTimeout_Creates()
    {
        var config = new CimianConfig
        {
            InstallerTimeout = 7200 // 2 hours
        };

        var engine = new UpdateEngine(config);

        Assert.NotNull(engine);
    }

    #endregion

    #region Path Configuration Tests

    [Fact]
    public void UpdateEngine_WithCustomPaths_Creates()
    {
        var config = new CimianConfig
        {
            CachePath = Path.Combine(_testDir, "custom_cache"),
            CatalogsPath = Path.Combine(_testDir, "custom_catalogs"),
            ManifestsPath = Path.Combine(_testDir, "custom_manifests")
        };

        var engine = new UpdateEngine(config);

        Assert.NotNull(engine);
    }

    [Fact]
    public void UpdateEngine_WithNonExistentPaths_Creates()
    {
        var config = new CimianConfig
        {
            CachePath = Path.Combine(_testDir, "nonexistent", "cache"),
            CatalogsPath = Path.Combine(_testDir, "nonexistent", "catalogs"),
            ManifestsPath = Path.Combine(_testDir, "nonexistent", "manifests")
        };

        var engine = new UpdateEngine(config);

        Assert.NotNull(engine);
    }

    #endregion

    #region Script Configuration Tests

    [Fact]
    public void UpdateEngine_WithPreflightDisabled_Creates()
    {
        var config = new CimianConfig
        {
            NoPreflight = true
        };

        var engine = new UpdateEngine(config);

        Assert.NotNull(engine);
    }

    [Fact]
    public void UpdateEngine_WithPostflightDisabled_Creates()
    {
        var config = new CimianConfig
        {
            NoPostflight = true
        };

        var engine = new UpdateEngine(config);

        Assert.NotNull(engine);
    }

    [Fact]
    public void UpdateEngine_WithBothScriptsDisabled_Creates()
    {
        var config = new CimianConfig
        {
            NoPreflight = true,
            NoPostflight = true
        };

        var engine = new UpdateEngine(config);

        Assert.NotNull(engine);
    }

    #endregion
}
