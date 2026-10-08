using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for ConfigurationService - loads and saves Cimian configuration.
/// </summary>
public class ConfigurationServiceTests : IDisposable
{
    private readonly string _testConfigDir;
    private readonly string _testConfigPath;
    private readonly ConfigurationService _service;

    public ConfigurationServiceTests()
    {
        _testConfigDir = Path.Combine(Path.GetTempPath(), "CimianTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testConfigDir);
        _testConfigPath = Path.Combine(_testConfigDir, "Config.yaml");
        // No policy key: a machine with Cimian policy set (HKLM\SOFTWARE\Policies\Cimian)
        // would otherwise override the values these tests load.
        _service = new ConfigurationService(policyRegistryPath: null);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testConfigDir))
            {
                Directory.Delete(_testConfigDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    #region LoadConfig Tests

    [Fact]
    public void LoadConfig_WhenFileDoesNotExist_ReturnsDefaultConfig()
    {
        var nonExistentPath = Path.Combine(_testConfigDir, "nonexistent.yaml");

        var config = _service.LoadConfig(nonExistentPath);

        Assert.NotNull(config);
        Assert.NotNull(config.SoftwareRepoURL);
        Assert.NotNull(config.CachePath);
    }

    [Fact]
    public void LoadConfig_InvalidYaml_ReturnsDefaultConfig()
    {
        File.WriteAllText(_testConfigPath, "this is: [not: valid yaml content");

        var config = _service.LoadConfig(_testConfigPath);

        Assert.NotNull(config);
    }

    [Fact]
    public void LoadConfig_EmptyFile_ReturnsDefaultConfig()
    {
        File.WriteAllText(_testConfigPath, "");

        var config = _service.LoadConfig(_testConfigPath);

        Assert.NotNull(config);
    }

    [Fact]
    public void LoadConfig_PartialConfig_LoadsAvailableValues()
    {
        var partialYaml = @"
SoftwareRepoURL: https://partial.example.com
ClientIdentifier: partial-client
";
        File.WriteAllText(_testConfigPath, partialYaml);

        var config = _service.LoadConfig(_testConfigPath);

        Assert.Equal("https://partial.example.com", config.SoftwareRepoURL);
        Assert.Equal("partial-client", config.ClientIdentifier);
    }

    [Fact]
    public void LoadConfig_Catalogs_ArePreservedAsList()
    {
        var yamlWithCatalogs = @"
SoftwareRepoURL: https://test.example.com
Catalogs:
  - production
  - testing
  - staging
";
        File.WriteAllText(_testConfigPath, yamlWithCatalogs);

        var config = _service.LoadConfig(_testConfigPath);

        Assert.NotNull(config.Catalogs);
        Assert.Equal(3, config.Catalogs.Count);
        Assert.Contains("production", config.Catalogs);
        Assert.Contains("testing", config.Catalogs);
        Assert.Contains("staging", config.Catalogs);
    }

    [Fact]
    public void LoadConfig_AdditionalHttpHeaders_ArePreservedInOrder()
    {
        var yamlWithHeaders = @"
SoftwareRepoURL: https://test.example.com
AdditionalHttpHeaders:
  - ""X-Client-Serial: ABC123""
  - ""X-Client-Id: 9F1E0B6C""
";
        File.WriteAllText(_testConfigPath, yamlWithHeaders);

        var config = _service.LoadConfig(_testConfigPath);

        Assert.Equal(["X-Client-Serial: ABC123", "X-Client-Id: 9F1E0B6C"], config.AdditionalHttpHeaders);
    }

    [Fact]
    public void LoadConfig_AdditionalHttpHeaders_UnquotedEntries_AreReadAsStrings()
    {
        // Without quotes, "Name: value" is a one-key mapping in YAML, not a string.
        var yamlWithHeaders = @"
SoftwareRepoURL: https://test.example.com
AdditionalHttpHeaders:
  - X-Client-Serial: ABC123
  - X-Client-Count: 5
  - X-Client-Flag:
";
        File.WriteAllText(_testConfigPath, yamlWithHeaders);

        var config = _service.LoadConfig(_testConfigPath);

        Assert.Equal(["X-Client-Serial: ABC123", "X-Client-Count: 5", "X-Client-Flag: "], config.AdditionalHttpHeaders);
    }

    [Fact]
    public void LoadConfig_AdditionalHttpHeaders_QuotedAndUnquotedEntries_KeepTheirOrder()
    {
        var yamlWithHeaders = @"
AdditionalHttpHeaders:
  - ""X-Client-Serial: ABC123""
  - X-Client-Id: 9F1E0B6C
  - ""X-Client-Time: 10:30:15""
";
        File.WriteAllText(_testConfigPath, yamlWithHeaders);

        var config = _service.LoadConfig(_testConfigPath);

        Assert.Equal(["X-Client-Serial: ABC123", "X-Client-Id: 9F1E0B6C", "X-Client-Time: 10:30:15"],
            config.AdditionalHttpHeaders);
    }

    [Fact]
    public void LoadConfig_AdditionalHttpHeaders_BlockMapping_ReadsEachPairInOrder()
    {
        var yamlWithHeaders = @"
SoftwareRepoURL: https://test.example.com
AdditionalHttpHeaders:
  X-Client-Serial: ABC123
  X-Client-Time: ""10:30:15""
  X-Client-Flag:
  X-Client-Id: 9F1E0B6C
";
        File.WriteAllText(_testConfigPath, yamlWithHeaders);

        var config = _service.LoadConfig(_testConfigPath);

        Assert.Equal(["X-Client-Serial: ABC123", "X-Client-Time: 10:30:15", "X-Client-Flag: ", "X-Client-Id: 9F1E0B6C"],
            config.AdditionalHttpHeaders);
    }

    [Theory]
    [InlineData("AdditionalHttpHeaders:\n  - X-Client-Serial: ABC123")]
    [InlineData("AdditionalHttpHeaders:\n  X-Client-Serial: ABC123")]
    [InlineData("AdditionalHttpHeaders: 'X-Client-Serial: ABC123'")]
    public void LoadConfig_AdditionalHttpHeaders_OtherSpellings_ReadTheEntryAndKeepTheRestOfTheConfig(string headers)
    {
        // An unquoted entry, a mapping and a lone string are not a list of strings in YAML.
        var yamlWithHeaders = $@"
SoftwareRepoURL: https://test.example.com
AuthToken: token
{headers}
Catalogs:
  - production
  - testing
";
        File.WriteAllText(_testConfigPath, yamlWithHeaders);

        var config = _service.LoadConfig(_testConfigPath);

        Assert.Equal(["X-Client-Serial: ABC123"], config.AdditionalHttpHeaders);
        Assert.Equal("https://test.example.com", config.SoftwareRepoURL);
        Assert.Equal("token", config.AuthToken);
        Assert.Equal(["production", "testing"], config.Catalogs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("~")]
    [InlineData("null")]
    public void LoadConfig_AdditionalHttpHeaders_NullKey_IsAnEmptyList(string value)
    {
        var yamlWithHeaders = $@"
SoftwareRepoURL: https://test.example.com
AdditionalHttpHeaders: {value}
";
        File.WriteAllText(_testConfigPath, yamlWithHeaders);

        var config = _service.LoadConfig(_testConfigPath);

        Assert.Equal("https://test.example.com", config.SoftwareRepoURL);
        Assert.Empty(config.AdditionalHttpHeaders);
    }

    #endregion

    #region GetDefaultConfig Tests

    [Fact]
    public void GetDefaultConfig_ReturnsValidDefaults()
    {
        var config = _service.GetDefaultConfig();

        Assert.NotNull(config);
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\Cache", config.CachePath);
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\catalogs", config.CatalogsPath);
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\manifests", config.ManifestsPath);
        Assert.Equal("INFO", config.LogLevel);
        Assert.Equal(900, config.InstallerTimeout);
        Assert.NotEmpty(config.Catalogs);
    }

    [Fact]
    public void GetDefaultConfig_ClientIdentifier_IsMachineName()
    {
        var config = _service.GetDefaultConfig();

        Assert.Equal(Environment.MachineName, config.ClientIdentifier);
    }

    #endregion

    #region SaveConfig Tests

    [Fact]
    public void SaveConfig_CreatesValidYamlFile()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "https://test.example.com/repo",
            ClientIdentifier = "test-client",
            CachePath = @"C:\TestCache",
            LogLevel = "DEBUG"
        };

        _service.SaveConfig(config, _testConfigPath);

        Assert.True(File.Exists(_testConfigPath));
        var content = File.ReadAllText(_testConfigPath);
        Assert.Contains("SoftwareRepoURL", content);
        Assert.Contains("https://test.example.com/repo", content);
    }

    [Fact]
    public void SaveConfig_CreatesDirectoryIfNotExists()
    {
        var nestedPath = Path.Combine(_testConfigDir, "nested", "deep", "Config.yaml");
        var config = _service.GetDefaultConfig();

        _service.SaveConfig(config, nestedPath);

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void SaveConfig_RoundTrip_PreservesValues()
    {
        var originalConfig = new CimianConfig
        {
            SoftwareRepoURL = "https://roundtrip.example.com",
            ClientIdentifier = "roundtrip-client",
            CachePath = @"C:\RoundtripCache",
            LogLevel = "DEBUG",
            InstallerTimeout = 1200,
            NoPreflight = true,
            Catalogs = ["production", "testing"]
        };

        _service.SaveConfig(originalConfig, _testConfigPath);
        var loadedConfig = _service.LoadConfig(_testConfigPath);

        Assert.Equal(originalConfig.SoftwareRepoURL, loadedConfig.SoftwareRepoURL);
        Assert.Equal(originalConfig.ClientIdentifier, loadedConfig.ClientIdentifier);
        Assert.Equal(originalConfig.CachePath, loadedConfig.CachePath);
        Assert.Equal(originalConfig.LogLevel, loadedConfig.LogLevel);
        Assert.Equal(originalConfig.InstallerTimeout, loadedConfig.InstallerTimeout);
        Assert.Equal(originalConfig.NoPreflight, loadedConfig.NoPreflight);
    }

    [Fact]
    public void SaveConfig_AdditionalHttpHeaders_RoundTrip_PreservesEntries()
    {
        var config = new CimianConfig
        {
            AdditionalHttpHeaders = ["X-Client-Serial: ABC123", "X-Client-Time: 10:30:15"]
        };

        _service.SaveConfig(config, _testConfigPath);
        var loadedConfig = _service.LoadConfig(_testConfigPath);

        Assert.Equal(config.AdditionalHttpHeaders, loadedConfig.AdditionalHttpHeaders);
    }

    #endregion

    #region ValidateConfig Tests

    [Fact]
    public void ValidateConfig_ValidConfig_ReturnsNoErrors()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "https://valid.example.com",
            CachePath = @"C:\Cache",
            InstallerTimeout = 900
        };

        var errors = _service.ValidateConfig(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateConfig_MissingSoftwareRepoURL_ReturnsError()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "",
            CachePath = @"C:\Cache",
            InstallerTimeout = 900
        };

        var errors = _service.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("SoftwareRepoURL"));
    }

    [Fact]
    public void ValidateConfig_InvalidURL_ReturnsError()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "not-a-valid-url",
            CachePath = @"C:\Cache",
            InstallerTimeout = 900
        };

        var errors = _service.ValidateConfig(config);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ValidateConfig_MissingCachePath_ReturnsError()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "https://valid.example.com",
            CachePath = "",
            InstallerTimeout = 900
        };

        var errors = _service.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("CachePath"));
    }

    [Fact]
    public void ValidateConfig_TimeoutTooLow_ReturnsError()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "https://valid.example.com",
            CachePath = @"C:\Cache",
            InstallerTimeout = 30 // Below 60 minimum
        };

        var errors = _service.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("InstallerTimeout"));
    }

    #endregion

    #region EnsureDirectoriesExist Tests

    [Fact]
    public void EnsureDirectoriesExist_CreatesDirectories()
    {
        var config = new CimianConfig
        {
            CachePath = Path.Combine(_testConfigDir, "cache"),
            CatalogsPath = Path.Combine(_testConfigDir, "catalogs"),
            ManifestsPath = Path.Combine(_testConfigDir, "manifests")
        };

        _service.EnsureDirectoriesExist(config);

        Assert.True(Directory.Exists(config.CachePath));
        Assert.True(Directory.Exists(config.CatalogsPath));
        Assert.True(Directory.Exists(config.ManifestsPath));
    }

    #endregion
}
