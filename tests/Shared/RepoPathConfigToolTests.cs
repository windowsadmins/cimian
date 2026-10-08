using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Each admin tool must find the repo in a Config.yaml written by <c>cimiimport --config</c>,
/// which uses <c>RepoPath</c>, and in an older one that uses <c>repo_path</c>.
/// </summary>
public class RepoPathConfigToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"repopath_{Guid.NewGuid():N}");

    public RepoPathConfigToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteConfig(string yaml)
    {
        var path = Path.Combine(_dir, "Config.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private const string CimiimportConfig =
        "SoftwareRepoURL: https://cimian.example.com/deployment\n" +
        "RepoPath: D:\\deployment\n" +
        "CloudProvider: none\n" +
        "DefaultCatalog: Development\n";

    private const string LegacyConfig =
        "SoftwareRepoURL: https://cimian.example.com/deployment\n" +
        "repo_path: D:\\legacy\n";

    [Theory]
    [InlineData(CimiimportConfig, "D:\\deployment")]
    [InlineData(LegacyConfig, "D:\\legacy")]
    public void Makepkginfo_ReadsTheRepoPath(string yaml, string expected)
    {
        var config = new Cimian.CLI.Makepkginfo.Services.PkgInfoBuilder().LoadConfig(WriteConfig(yaml));

        Assert.Equal(expected, config.RepoPath);
    }

    [Theory]
    [InlineData(CimiimportConfig, "D:\\deployment")]
    [InlineData(LegacyConfig, "D:\\legacy")]
    public void Manifestutil_ReadsTheRepoPath(string yaml, string expected)
    {
        var config = new Cimian.CLI.Manifestutil.Services.ManifestService().LoadConfig(WriteConfig(yaml));

        Assert.Equal(expected, config.RepoPath);
    }
}
