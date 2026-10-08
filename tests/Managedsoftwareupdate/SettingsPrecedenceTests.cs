using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Precedence, highest first: command-line flag, policy, machine settings, Config.yaml,
/// default. Every Config.yaml key can come from either registry layer, read by its type.
/// </summary>
public class SettingsPrecedenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public SettingsPrecedenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "Config.yaml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Dictionary<string, object> Values(params (string Name, object Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static ConfigurationService Service(
        Dictionary<string, object>? policy = null,
        Dictionary<string, object>? settings = null,
        bool requireTrustedFile = false) =>
        new(() => policy, () => settings, requireTrustedFile);

    [Fact]
    public void DefaultApplies_WhenNoSourceSetsIt()
    {
        var config = Service().LoadConfig(_configPath);

        Assert.Equal(900, config.InstallerTimeout);
        Assert.Equal("INFO", config.LogLevel);
    }

    [Fact]
    public void ConfigYaml_BeatsDefault()
    {
        File.WriteAllText(_configPath, "InstallerTimeout: 1200\n");

        var config = Service().LoadConfig(_configPath);

        Assert.Equal(1200, config.InstallerTimeout);
    }

    [Fact]
    public void MachineSettings_BeatConfigYaml()
    {
        File.WriteAllText(_configPath, "InstallerTimeout: 1200\nSoftwareRepoURL: https://file.example\n");
        var service = Service(settings: Values(("InstallerTimeout", 1800)));

        var config = service.LoadConfig(_configPath);

        Assert.Equal(1800, config.InstallerTimeout);
        Assert.Equal("https://file.example", config.SoftwareRepoURL);
        Assert.Equal(SettingsLayers.MachineSettingsSource, service.LastLoadSources["InstallerTimeout"]);
    }

    [Fact]
    public void Policy_BeatsMachineSettings()
    {
        File.WriteAllText(_configPath, "SoftwareRepoURL: https://file.example\n");
        var service = Service(
            policy: Values(("SoftwareRepoURL", "https://policy.example")),
            settings: Values(("SoftwareRepoURL", "https://settings.example")));

        var config = service.LoadConfig(_configPath);

        Assert.Equal("https://policy.example", config.SoftwareRepoURL);
        Assert.Equal(SettingsLayers.PolicySource, service.LastLoadSources["SoftwareRepoURL"]);
    }

    [Fact]
    public void CommandLineFlag_BeatsPolicy()
    {
        var config = Service(policy: Values(("LogLevel", "WARN"), ("Debug", 0))).LoadConfig(_configPath);
        Assert.Equal("WARN", config.LogLevel);

        ConfigurationService.ApplyCommandLineVerbosity(config, 3);

        Assert.Equal("DEBUG", config.LogLevel);
        Assert.True(config.Debug);
    }

    [Fact]
    public void Policy_AppliesWhenConfigYamlIsMissing()
    {
        var config = Service(policy: Values(("ClientIdentifier", "lab-a"))).LoadConfig(_configPath);

        Assert.Equal("lab-a", config.ClientIdentifier);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("1", true)]
    [InlineData("no", false)]
    public void Booleans_ReadFromDwordOrString(object raw, bool expected)
    {
        File.WriteAllText(_configPath, $"UseCache: {(!expected).ToString().ToLowerInvariant()}\n");

        var config = Service(policy: Values(("UseCache", raw))).LoadConfig(_configPath);

        Assert.Equal(expected, config.UseCache);
    }

    [Theory]
    [InlineData(45)]
    [InlineData("45")]
    [InlineData(45L)]
    public void Numbers_ReadFromDwordQwordOrString(object raw)
    {
        var config = Service(policy: Values(("CacheRetentionDays", raw))).LoadConfig(_configPath);

        Assert.Equal(45, config.CacheRetentionDays);
    }

    [Fact]
    public void Lists_ReadFromMultiString()
    {
        File.WriteAllText(_configPath, "Catalogs:\n  - Production\n");

        var config = Service(policy: Values(("Catalogs", new[] { "Testing", " ", "Production " })))
            .LoadConfig(_configPath);

        Assert.Equal(new[] { "Testing", "Production" }, config.Catalogs);
    }

    [Fact]
    public void Lists_ReadFromStringWithOneEntryPerLine()
    {
        var config = Service(policy: Values(("Catalogs", "Testing\r\nProduction"))).LoadConfig(_configPath);

        Assert.Equal(new[] { "Testing", "Production" }, config.Catalogs);
    }

    [Fact]
    public void Strings_AreTrimmed()
    {
        var config = Service(policy: Values(("ClientCertificateThumbprint", "  ABC123  "))).LoadConfig(_configPath);

        Assert.Equal("ABC123", config.ClientCertificateThumbprint);
    }

    [Fact]
    public void BlankPolicyValue_LeavesLowerLayer()
    {
        var config = Service(
                policy: Values(("ClientIdentifier", "   "), ("Catalogs", Array.Empty<string>())),
                settings: Values(("ClientIdentifier", "from-settings"), ("Catalogs", new[] { "Testing" })))
            .LoadConfig(_configPath);

        Assert.Equal("from-settings", config.ClientIdentifier);
        Assert.Equal(new[] { "Testing" }, config.Catalogs);
    }

    [Theory]
    [InlineData("InstallerTimeout", 30)]
    [InlineData("CacheRetentionDays", -1)]
    [InlineData("UseCache", "maybe")]
    [InlineData("InstallerTimeout", "soon")]
    public void InvalidPolicyValue_LeavesLowerLayer(string name, object raw)
    {
        File.WriteAllText(_configPath, "InstallerTimeout: 1200\nCacheRetentionDays: 10\nUseCache: false\n");

        var config = Service(policy: Values((name, raw))).LoadConfig(_configPath);

        Assert.Equal(1200, config.InstallerTimeout);
        Assert.Equal(10, config.CacheRetentionDays);
        Assert.False(config.UseCache);
    }

    [Fact]
    public void RegistryNames_AreCaseInsensitive()
    {
        var policy = new Dictionary<string, object> { ["softwarerepourl"] = "https://policy.example" };

        var config = new ConfigurationService(() => policy, () => null, requireTrustedFile: false)
            .LoadConfig(_configPath);

        Assert.Equal("https://policy.example", config.SoftwareRepoURL);
    }

    /// <summary>
    /// Every public setting on CimianConfig must be one the registry layers can set; a
    /// new property of a type they cannot read fails here instead of silently being
    /// file-only.
    /// </summary>
    [Fact]
    public void EveryConfigKey_CanBeSetByPolicy()
    {
        var properties = typeof(CimianConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        var settable = SettingsLayers.SettingNames<CimianConfig>()
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(properties, settable);
    }

    [Fact]
    public void EveryConfigKey_IsAppliedFromPolicy()
    {
        var policy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in SettingsLayers.SettingNames<CimianConfig>())
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            policy[name] = underlying == typeof(bool) ? 1
                : underlying == typeof(int) ? 120
                : underlying == typeof(List<string>) ? new[] { "x" }
                : "value";
        }
        var service = Service(policy: policy);

        service.LoadConfig(_configPath);

        Assert.Equal(policy.Keys.OrderBy(k => k), service.LastLoadSources.Keys.OrderBy(k => k));
    }

    [Fact]
    public void ConfigYaml_IgnoredWhenNonAdminCanWriteIt()
    {
        File.WriteAllText(_configPath, "SoftwareRepoURL: https://file.example\nInstallerTimeout: 1200\n");
        var file = new FileInfo(_configPath);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, AccessControlType.Allow));
        file.SetAccessControl(security);

        var config = Service(policy: Values(("ClientIdentifier", "lab-a")), requireTrustedFile: true)
            .LoadConfig(_configPath);

        Assert.Equal(900, config.InstallerTimeout);
        Assert.NotEqual("https://file.example", config.SoftwareRepoURL);
        // Policy still applies when the file is ignored.
        Assert.Equal("lab-a", config.ClientIdentifier);
    }
}
