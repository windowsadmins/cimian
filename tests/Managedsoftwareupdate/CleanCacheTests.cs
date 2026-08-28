using System;
using System.IO;
using Cimian.CLI.managedsoftwareupdate;
using Cimian.Core;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// The --clean-cache command.
/// </summary>
/// <remarks>
/// The regression these pin down: the command resolved its own
/// %ProgramData%\Cimian\Cache rather than the cache root everything else uses. That
/// directory has never existed, so the command reported "nothing to clean" and exited
/// successfully no matter how large the real cache had grown.
/// </remarks>
public class CleanCacheTests : IDisposable
{
    private readonly string _root;

    public CleanCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cimian-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void TargetsTheCacheDirectoryTheRestOfTheClientUses()
    {
        Assert.Equal(
            Path.Combine(CimianPaths.ManagedInstallsRoot, "Cache"),
            CimianPaths.CacheDir);
    }

    [Fact]
    public void RemovesCachedPayloadsIncludingNestedOnes()
    {
        var top = Path.Combine(_root, "payload.msi");
        var nested = Path.Combine(_root, "SomePackage", "payload.pkg");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(top, "x");
        File.WriteAllText(nested, "x");

        var exit = Program.CleanCacheDirectory(_root);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(top));
        Assert.False(File.Exists(nested));
    }

    [Fact]
    public void RemovesTheDirectoriesLeftEmptyBehindIt()
    {
        var nested = Path.Combine(_root, "SomePackage", "payload.pkg");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "x");

        Program.CleanCacheDirectory(_root);

        Assert.False(Directory.Exists(Path.Combine(_root, "SomePackage")));
    }

    [Fact]
    public void SucceedsQuietlyWhenThereIsNoCacheDirectory()
    {
        var exit = Program.CleanCacheDirectory(Path.Combine(_root, "not-there"));

        Assert.Equal(0, exit);
    }
}
