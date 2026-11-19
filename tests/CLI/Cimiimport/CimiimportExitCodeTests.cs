using Xunit;

namespace Cimian.Tests.CLI.Cimiimport;

/// <summary>
/// cimiimport runs unattended in pipelines (--nointeractive), so input it cannot use has
/// to fail the run, not exit 0 with nothing imported or with a script silently left out.
/// These go through Main, the way a pipeline calls it.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public class CimiimportExitCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cimiimport_exit_{Guid.NewGuid():N}");
    private readonly string _repo;
    private readonly string _installer;

    public CimiimportExitCodeTests()
    {
        _repo = Path.Combine(_dir, "repo");
        foreach (var sub in new[] { "pkgsinfo", "pkgs", "catalogs", "manifests" })
        {
            Directory.CreateDirectory(Path.Combine(_repo, sub));
        }

        _installer = Path.Combine(_dir, "Tool-1.0.exe");
        File.WriteAllText(_installer, "not a real installer");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static Task<int> RunCimiimport(params string[] args) =>
        Cimian.CLI.Cimiimport.Program.Main(args);

    private string Missing(string name) => Path.Combine(_dir, "missing", name);

    [Fact]
    public async Task MissingInstaller_ExitsNonZero()
    {
        var exit = await RunCimiimport(Missing("Tool-2.0.exe"), "--repo_path", _repo, "--nointeractive");

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task MissingRepository_ExitsNonZero()
    {
        var exit = await RunCimiimport(_installer, "--repo_path", Missing("repo"), "--nointeractive");

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task MissingScript_ExitsNonZeroAndImportsNothing()
    {
        var exit = await RunCimiimport(_installer, "--repo_path", _repo, "--nointeractive",
            "--postinstall-script", Missing("postinstall.ps1"));

        Assert.Equal(1, exit);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_repo, "pkgsinfo"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingUninstaller_ExitsNonZeroAndImportsNothing()
    {
        var exit = await RunCimiimport(_installer, "--repo_path", _repo, "--nointeractive",
            "--uninstaller", Missing("uninstall.exe"));

        Assert.Equal(1, exit);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_repo, "pkgsinfo"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ValidateInputs_AcceptsGoodInputsAndTemplateScripts()
    {
        var script = Path.Combine(_dir, "postinstall.ps1");
        File.WriteAllText(script, "Write-Host done");
        var config = new Cimian.CLI.Cimiimport.Models.ImportConfiguration { RepoPath = _repo };
        var scripts = new Cimian.CLI.Cimiimport.Models.ScriptPaths { Postinstall = script, Preinstall = "template" };

        var errors = Cimian.CLI.Cimiimport.Services.ImportService.ValidateInputs(_installer, config, scripts, uninstallerPath: null);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateInputs_ReportsEveryProblemAtOnce()
    {
        var config = new Cimian.CLI.Cimiimport.Models.ImportConfiguration { RepoPath = "" };
        var scripts = new Cimian.CLI.Cimiimport.Models.ScriptPaths { InstallCheck = Missing("check.ps1") };

        var errors = Cimian.CLI.Cimiimport.Services.ImportService.ValidateInputs(Missing("Tool.exe"), config, scripts, Missing("uninstall.exe"));

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("--install-check-script"));
        Assert.Contains(errors, e => e.Contains("--uninstaller"));
        Assert.Contains(errors, e => e.Contains("No repo path"));
    }
}
