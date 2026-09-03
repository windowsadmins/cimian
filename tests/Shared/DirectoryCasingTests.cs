using System;
using System.IO;
using Cimian.Core;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// The logs directory must be spelled exactly "logs" on disk.
/// </summary>
/// <remarks>
/// The regression this guards: the convention was applied by changing the path string in
/// CimianPaths, which renamed nothing. NTFS is case-insensitive, so every device that already
/// had ManagedInstalls\Logs kept that name and went on reporting it upward, on the newest
/// client, indefinitely.
/// </remarks>
public class DirectoryCasingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cimian-casing-" + Guid.NewGuid().ToString("N"));

    public DirectoryCasingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string LeafOnDisk(string parent, string name)
        => Path.GetFileName(Directory.GetDirectories(parent, name)[0]);

    [Fact]
    public void AWrongCasedDirectoryIsRenamedAndItsContentsSurvive()
    {
        var upper = Path.Combine(_root, "Logs");
        Directory.CreateDirectory(upper);
        File.WriteAllText(Path.Combine(upper, "run.log"), "history worth keeping");

        var moved = CimianPaths.NormalizeDirectoryCasing(Path.Combine(_root, "logs"));

        Assert.True(moved);
        Assert.Equal("logs", LeafOnDisk(_root, "logs"));
        Assert.Equal("history worth keeping", File.ReadAllText(Path.Combine(_root, "logs", "run.log")));
    }

    [Fact]
    public void AnAlreadyCorrectDirectoryIsLeftAlone()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));

        Assert.False(CimianPaths.NormalizeDirectoryCasing(Path.Combine(_root, "logs")));
        Assert.Equal("logs", LeafOnDisk(_root, "logs"));
    }

    [Fact]
    public void AMissingDirectoryIsNotCreated()
    {
        Assert.False(CimianPaths.NormalizeDirectoryCasing(Path.Combine(_root, "logs")));
        Assert.False(Directory.Exists(Path.Combine(_root, "logs")));
    }

    [Fact]
    public void RunningTwiceIsHarmless()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Logs"));

        Assert.True(CimianPaths.NormalizeDirectoryCasing(Path.Combine(_root, "logs")));
        Assert.False(CimianPaths.NormalizeDirectoryCasing(Path.Combine(_root, "logs")));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public void NoStagingDirectoryIsLeftBehindWhenTheRenameSucceeds()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Logs"));

        CimianPaths.NormalizeDirectoryCasing(Path.Combine(_root, "logs"));

        Assert.DoesNotContain(Directory.GetDirectories(_root), d => Path.GetFileName(d).Contains(".casing-"));
    }

    [Fact]
    public void TheConventionDirsAllSpellTheirLeafInLowercase()
    {
        foreach (var dir in CimianPaths.ConventionDirs)
        {
            var leaf = Path.GetFileName(dir);
            Assert.Equal(leaf.ToLowerInvariant(), leaf);
        }
    }
}
