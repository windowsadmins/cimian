using Xunit;

namespace Cimian.Tests.Repoclean;

/// <summary>
/// Runs repoclean end to end, through its real entry point, against a scratch repository
/// on disk. These go through <c>Main</c> rather than the services so they exercise the
/// same path an operator does, exit code included.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public class RepocleanRunTests : IDisposable
{
    private readonly string _repo;

    public RepocleanRunTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"repoclean_test_{Guid.NewGuid():N}");
        foreach (var dir in new[] { "pkgsinfo", "pkgs", "catalogs", "manifests" })
        {
            Directory.CreateDirectory(Path.Combine(_repo, dir));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_repo))
        {
            Directory.Delete(_repo, true);
        }
    }

    private static Task<int> RunRepoclean(params string[] args) =>
        Cimian.CLI.Repoclean.Program.Main(args);

    private string RepoFile(string relativePath) =>
        Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relativePath, string content)
    {
        var path = RepoFile(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WritePayload(string relativePath) =>
        Write("pkgs/" + relativePath, $"payload {relativePath} {Guid.NewGuid()}");

    private void WritePkgInfo(string name, string version, string extra = "")
    {
        WritePayload($"apps/{name}-{version}.msi");
        Write($"pkgsinfo/apps/{name}-{version}.yaml", $"""
            name: {name}
            version: "{version}"
            catalogs:
              - Testing
            installer:
              type: msi
              location: apps/{name}-{version}.msi
            {extra}
            """);
    }

    /// <summary>
    /// The repository from the data-loss report: an app in a manifest that requires an
    /// old version of a library and carries its own uninstaller payload, alongside an
    /// item with more versions than --keep allows and one payload nothing points at.
    /// </summary>
    private void SeedRepository()
    {
        WritePkgInfo("Lib", "1.0");
        WritePkgInfo("Lib", "2.0");
        WritePkgInfo("Lib", "3.0");

        WritePkgInfo("Old", "1.0");
        WritePkgInfo("Old", "2.0");
        WritePkgInfo("Old", "3.0");

        WritePayload("apps/App-uninstall.exe");
        WritePkgInfo("App", "5.0", """
            requires:
              - Lib-1.0
            uninstaller:
              - type: exe
                location: apps/App-uninstall.exe
            """);

        WritePayload("apps/Stray.msi");

        Write("manifests/site.yaml", """
            name: site
            managed_installs:
              - App
              - Old
            """);
    }

    private Dictionary<string, byte[]> Snapshot(params string[] relativePaths) =>
        relativePaths.ToDictionary(p => p, p => File.ReadAllBytes(RepoFile(p)));

    [Fact]
    public async Task RemoveAuto_KeepsEveryPayloadAKeptItemReferences_ByteForByte()
    {
        SeedRepository();
        var kept = Snapshot(
            "pkgs/apps/App-5.0.msi",
            "pkgs/apps/App-uninstall.exe",
            "pkgs/apps/Lib-1.0.msi",
            "pkgs/apps/Lib-2.0.msi",
            "pkgs/apps/Lib-3.0.msi",
            "pkgs/apps/Old-2.0.msi",
            "pkgs/apps/Old-3.0.msi",
            "pkgsinfo/apps/App-5.0.yaml",
            "pkgsinfo/apps/Lib-1.0.yaml",
            "pkgsinfo/apps/Old-3.0.yaml");

        var exit = await RunRepoclean("--repo-url", _repo, "--keep", "2", "--remove", "--auto");

        Assert.Equal(0, exit);
        foreach (var (path, bytes) in kept)
        {
            Assert.True(File.Exists(RepoFile(path)), $"{path} was deleted");
            Assert.Equal(bytes, File.ReadAllBytes(RepoFile(path)));
        }
    }

    [Fact]
    public async Task RemoveAuto_DeletesOnlyTheOldVersionAndTheTrueOrphan()
    {
        SeedRepository();

        await RunRepoclean("--repo-url", _repo, "--keep", "2", "--remove", "--auto");

        Assert.False(File.Exists(RepoFile("pkgsinfo/apps/Old-1.0.yaml")));
        Assert.False(File.Exists(RepoFile("pkgs/apps/Old-1.0.msi")));
        Assert.False(File.Exists(RepoFile("pkgs/apps/Stray.msi")));

        var remaining = Directory.EnumerateFiles(Path.Combine(_repo, "pkgs"), "*", SearchOption.AllDirectories).Count();
        Assert.Equal(7, remaining);
    }

    [Fact]
    public async Task RemoveAuto_KeepsAVersionPinnedByAManifest()
    {
        SeedRepository();
        Write("manifests/lab.yaml", """
            name: lab
            conditional_items:
              - condition: hostname BEGINSWITH "LAB"
                managed_installs:
                  - Old-1.0
            """);

        await RunRepoclean("--repo-url", _repo, "--keep", "2", "--remove", "--auto");

        Assert.True(File.Exists(RepoFile("pkgsinfo/apps/Old-1.0.yaml")));
        Assert.True(File.Exists(RepoFile("pkgs/apps/Old-1.0.msi")));
    }

    [Fact]
    public async Task RemoveAuto_RebuildsCatalogsWithoutTheDeletedItems()
    {
        SeedRepository();

        await RunRepoclean("--repo-url", _repo, "--keep", "2", "--remove", "--auto");

        var all = File.ReadAllText(RepoFile("catalogs/All.yaml"));
        Assert.Contains("name: App", all);
        Assert.DoesNotContain("location: apps/Old-1.0.msi", all);
        Assert.Contains("location: apps/Old-3.0.msi", all);
    }

    [Fact]
    public async Task DryRun_DeletesNothing()
    {
        SeedRepository();
        var before = Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories).Order().ToList();

        var exit = await RunRepoclean("--repo-url", _repo, "--keep", "2");

        Assert.Equal(0, exit);
        Assert.Equal(before, Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories).Order().ToList());
    }

    [Fact]
    public async Task RemoveAuto_RefusesToDeleteWhenAPkgsinfoCannotBeRead()
    {
        SeedRepository();
        WritePayload("apps/Broken-1.0.msi");
        Write("pkgsinfo/apps/Broken-1.0.yaml", "name: Broken\nversion: [unclosed\n");
        var before = Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories).Order().ToList();

        var exit = await RunRepoclean("--repo-url", _repo, "--keep", "2", "--remove", "--auto");

        Assert.Equal(1, exit);
        Assert.Equal(before, Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories).Order().ToList());
    }

    [Fact]
    public async Task MissingRepositoryPath_ExitsNonZero()
    {
        var missing = Path.Combine(_repo, "not-mounted");

        var exit = await RunRepoclean("--repo-url", missing, "--remove", "--auto");

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task RepositoryWithoutPkgsinfo_ExitsNonZero()
    {
        Directory.Delete(Path.Combine(_repo, "pkgsinfo"));

        var exit = await RunRepoclean("--repo-url", _repo, "--remove", "--auto");

        Assert.Equal(1, exit);
    }
}
