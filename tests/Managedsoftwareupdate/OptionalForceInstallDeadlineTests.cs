using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// force_install_after_date on a title listed only under optional_installs must not
/// queue an install or surface a deadline on the optional Software-tab record.
/// Matches Munki: deadlines apply after SelfServe opt-in / managed install queues.
/// </summary>
public class OptionalForceInstallDeadlineTests : IDisposable
{
    private readonly string _testDir;
    private readonly CimianConfig _config;
    private readonly UpdateEngine _engine;
    private readonly string _suffix = Guid.NewGuid().ToString("N");

    public OptionalForceInstallDeadlineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "OptionalForceDeadline", _suffix);
        Directory.CreateDirectory(_testDir);

        _config = new CimianConfig { CachePath = Path.Combine(_testDir, "Cache") };
        Directory.CreateDirectory(_config.CachePath);
        _engine = new UpdateEngine(_config);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* Ignore cleanup errors */ }
    }

    private string Unique(string name) => $"{name}{_suffix}";

    /// <summary>Catalog item whose installs file is missing: not installed, needs action.</summary>
    private CatalogItem AbsentWithPastDeadline(string name) => new()
    {
        Name = name,
        Version = "2.0.0",
        ForceInstallAfterDate = DateTime.Now.AddDays(-1),
        Installs = new List<InstallCheckItem>
        {
            new() { Type = "file", Path = Path.Combine(_testDir, name + ".missing") }
        }
    };

    private static Dictionary<string, CatalogItem> Catalog(params CatalogItem[] items) =>
        items.ToDictionary(i => i.Name.ToLowerInvariant());

    private static ManifestItem Entry(string name, string action) =>
        new() { Name = name, Action = action, SourceManifest = "test" };

    [Fact]
    public void OptionalOnly_PastForceDeadline_IsNotQueued()
    {
        var item = AbsentWithPastDeadline(Unique("optional"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "optional") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
    }

    [Fact]
    public void ManagedInstall_PastForceDeadline_StillQueuesInstall()
    {
        var item = AbsentWithPastDeadline(Unique("managed"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "install") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(toUpdate);
    }

    [Fact]
    public void OptionalInstallRecord_OmitsForceInstallAfterDate()
    {
        var item = AbsentWithPastDeadline(Unique("banner"));
        var record = _engine.BuildOptionalInstallRecord(item.Name, item, null);

        Assert.Null(record.ForceInstallAfterDate);
    }

    [Fact]
    public void ManagedInstallInfoItem_KeepsForceInstallAfterDate()
    {
        var item = AbsentWithPastDeadline(Unique("managedbanner"));
        var record = UpdateEngine.BuildInstallInfoItem(item.Name, item);

        Assert.Equal(item.ForceInstallAfterDate, record.ForceInstallAfterDate);
    }

    [Fact]
    public void SelfServePromotedOptional_PastDeadline_StillQueuesInstall()
    {
        var item = AbsentWithPastDeadline(Unique("selfserve"));
        var manifest = new List<ManifestItem>
        {
            new()
            {
                Name = item.Name,
                Action = "install",
                SourceManifest = "SelfServeManifest",
                PromotedFromOptional = true
            }
        };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(toUpdate);
    }
}
