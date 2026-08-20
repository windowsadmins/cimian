using System;
using System.IO;
using System.Linq;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Retention over the parts of the logs tree that are not the dated session directories.
/// </summary>
/// <remarks>
/// The regression these guard: retention enumerated directories only. Loose files at the
/// logs root - package script output, msiexec verbose logs, self-update installer logs -
/// were never candidates for deletion, so they accumulated for the life of the machine.
/// </remarks>
public class SessionLogRetentionTests : IDisposable
{
    private readonly string _root;

    public SessionLogRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cimian-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string relativePath, DateTime lastWrite)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        File.SetLastWriteTime(full, lastWrite);
        return full;
    }

    private static DateTime Cutoff => DateTime.Now.AddDays(-30);
    private static DateTime Expired => DateTime.Now.AddDays(-45);
    private static DateTime Fresh => DateTime.Now.AddDays(-2);

    [Fact]
    public void SweepExpiredFiles_RemovesOldLooseFilesAndKeepsRecentOnes()
    {
        var old = WriteFile("selfupdate-20250101-000000.log", Expired);
        var recent = WriteFile("selfupdate-20260801-000000.log", Fresh);

        SessionLogger.SweepExpiredFiles(_root, Cutoff);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void SweepExpiredFiles_DoesNotDescendIntoSubdirectories()
    {
        // Session directories are aged as a unit by their own date-named rule. An old
        // file inside one must not be picked off individually, or a session's log set
        // ends up half deleted.
        var nested = WriteFile(Path.Combine("2026-01-02", "0800", "install.log"), Expired);

        SessionLogger.SweepExpiredFiles(_root, Cutoff);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void SweepExpiredFiles_IsSilentWhenTheDirectoryDoesNotExist()
    {
        var missing = Path.Combine(_root, "no-such-directory");

        SessionLogger.SweepExpiredFiles(missing, Cutoff);

        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void SweepExpiredSubdirectories_DropsAPackageNothingHasTouchedInTheWindow()
    {
        WriteFile(Path.Combine("packages", "RetiredApp", "postinstall.log"), Expired);
        WriteFile(Path.Combine("packages", "CurrentApp", "postinstall.log"), Fresh);

        SessionLogger.SweepExpiredSubdirectories(Path.Combine(_root, "packages"), Cutoff);

        Assert.False(Directory.Exists(Path.Combine(_root, "packages", "RetiredApp")));
        Assert.True(Directory.Exists(Path.Combine(_root, "packages", "CurrentApp")));
    }

    [Fact]
    public void SweepExpiredSubdirectories_KeepsAPackageWithAnyRecentFile()
    {
        // A package installed once long ago and reinstalled last week is live. The
        // newest file decides, not the oldest.
        WriteFile(Path.Combine("packages", "App", "preinstall.log"), Expired);
        WriteFile(Path.Combine("packages", "App", "postinstall.log"), Fresh);

        SessionLogger.SweepExpiredSubdirectories(Path.Combine(_root, "packages"), Cutoff);

        Assert.True(Directory.Exists(Path.Combine(_root, "packages", "App")));
        Assert.True(File.Exists(Path.Combine(_root, "packages", "App", "preinstall.log")));
    }

    [Fact]
    public void SweepExpiredSubdirectories_DropsAnEmptyLeftover()
    {
        var empty = Path.Combine(_root, "packages", "Empty");
        Directory.CreateDirectory(empty);

        SessionLogger.SweepExpiredSubdirectories(Path.Combine(_root, "packages"), Cutoff);

        Assert.False(Directory.Exists(empty));
    }

    [Fact]
    public void SweepExpiredSubdirectories_LeavesFilesAtItsOwnRootAlone()
    {
        var loose = WriteFile(Path.Combine("packages", "stray.log"), Expired);

        SessionLogger.SweepExpiredSubdirectories(Path.Combine(_root, "packages"), Cutoff);

        Assert.True(File.Exists(loose));
    }
}
