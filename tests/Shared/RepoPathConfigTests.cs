using Cimian.CLI.Cimiimport.Models;
using Cimian.CLI.Cimiimport.Services;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

public class RepoPathConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"repopath_{Guid.NewGuid():N}");
    private readonly string _configPath;

    public RepoPathConfigTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "Config.yaml");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("RepoPath: D:\\deployment\n", "D:\\deployment")]
    [InlineData("repo_path: D:\\legacy\n", "D:\\legacy")]
    [InlineData("RepoPath: D:\\deployment\nrepo_path: D:\\legacy\n", "D:\\deployment")]
    [InlineData("RepoPath: ''\nrepo_path: D:\\legacy\n", "D:\\legacy")]
    [InlineData("SoftwareRepoURL: https://cimian.example.com\n", null)]
    [InlineData("", null)]
    public void Read_PrefersRepoPathAndFallsBackToRepoPathSnakeCase(string yaml, string? expected)
    {
        Assert.Equal(expected, RepoPathConfig.Read(yaml));
    }

    [Fact]
    public void ReadFile_ReturnsNullForAMissingFile()
    {
        Assert.Null(RepoPathConfig.ReadFile(_configPath));
    }

    [Fact]
    public void Cimiimport_ReadsALegacyRepoPath()
    {
        File.WriteAllText(_configPath, "repo_path: D:\\legacy\nCloudProvider: none\n");

        var config = new ConfigurationService(_configPath).LoadOrCreateConfig();

        Assert.Equal("D:\\legacy", config.RepoPath);
    }

    [Fact]
    public void Cimiimport_SaveWritesRepoPathOnly_AndKeepsOtherSettings()
    {
        File.WriteAllText(_configPath,
            "SoftwareRepoURL: https://cimian.example.com/deployment\n" +
            "repo_path: D:\\legacy\n");

        var service = new ConfigurationService(_configPath);
        service.SaveConfig(new ImportConfiguration { RepoPath = "D:\\deployment" });

        var yaml = File.ReadAllText(_configPath);
        Assert.Equal("D:\\deployment", RepoPathConfig.Read(yaml));
        Assert.DoesNotContain("repo_path", yaml);
        Assert.Contains("SoftwareRepoURL: https://cimian.example.com/deployment", yaml);
    }
}
