using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Reporting reads the same layers managedsoftwareupdate uses, so a report names the
/// repo and manifest that policy set rather than the file's.
/// </summary>
public class EffectiveSettingsReportingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public EffectiveSettingsReportingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "Config.yaml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void PolicyBeatsSettingsBeatsConfigYaml()
    {
        File.WriteAllText(_configPath,
            "SoftwareRepoURL: https://file.example\nClientIdentifier: file-id\nCatalogs:\n  - Production\n");
        var settings = new Dictionary<string, object> { ["ClientIdentifier"] = "settings-id" };
        var policy = new Dictionary<string, object> { ["SoftwareRepoURL"] = "https://policy.example" };

        var effective = DataExporter.LoadEffectiveSettings(_configPath, settings, policy, requireTrustedFile: false);

        Assert.NotNull(effective);
        Assert.Equal("https://policy.example", effective!.SoftwareRepoURL);
        Assert.Equal("settings-id", effective.ClientIdentifier);
        Assert.Equal(new[] { "Production" }, effective.Catalogs);
    }

    [Fact]
    public void NothingAnywhere_ReturnsNull()
    {
        Assert.Null(DataExporter.LoadEffectiveSettings(_configPath, null, null, requireTrustedFile: false));
    }
}
