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


/// <summary>
/// Relocation of artifacts written to the pre-move locations.
/// </summary>
/// <remarks>
/// These exist because age is not a sufficient signal for this class of file. An MSI
/// already installed on the machine rewrites its sidecar log every time it runs, so the
/// file is permanently younger than any retention window and an age-based sweep never
/// reaches it. On a surveyed endpoint, 36 of the 50 loose files at the logs root were
/// inside the 30-day window for exactly that reason. Waiting for every package on every
/// endpoint to be rebuilt is not a plan, so the client relocates on sight and the whole
/// pass becomes a no-op once they are.
/// </remarks>
public class LegacyArtifactRelocationTests : IDisposable
{
    private readonly string _root;
    private readonly string _logs;
    private readonly string _cache;

    public LegacyArtifactRelocationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cimian-relocate-" + Guid.NewGuid().ToString("N"));
        _logs = Path.Combine(_root, "logs");
        _cache = Path.Combine(_root, "Cache");
        Directory.CreateDirectory(_logs);
        Directory.CreateDirectory(_cache);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string root, string relativePath, string content = "x")
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private void Relocate() => SessionLogger.RelocateLegacyArtifacts(_logs, _cache);

    [Theory]
    [InlineData("cimipkg-ReportMate-CimianPostinstall.log", "ReportMate", "postinstall.log")]
    [InlineData("cimipkg-CimianAuth-CimianPreinstall.log", "CimianAuth", "preinstall.log")]
    [InlineData("cimipkg-ReportMatePrefs-CimianUninstall.log", "ReportMatePrefs", "uninstall.log")]
    public void SidecarLogsMoveIntoTheirPackageDirectory(string name, string product, string expected)
    {
        var source = Write(_logs, name, "attempt output");

        Relocate();

        Assert.False(File.Exists(source));
        var moved = Path.Combine(_logs, "packages", product, expected);
        Assert.True(File.Exists(moved));
        Assert.Equal("attempt output", File.ReadAllText(moved));
    }

    [Fact]
    public void ProductNamesContainingHyphensSurvive()
    {
        // Keying off the "cimipkg-" prefix would split "Forti-Client-Prefs" at its first
        // hyphen. The action suffix is the only unambiguous anchor.
        Write(_logs, "cimipkg-Forti-Client-Prefs-CimianPostinstall.log");

        Relocate();

        Assert.True(File.Exists(Path.Combine(_logs, "packages", "Forti-Client-Prefs", "postinstall.log")));
    }

    [Fact]
    public void SelfUpdateLogsMoveIntoTheirSubdirectory()
    {
        var source = Write(_logs, "selfupdate-20260816-231113.log");

        Relocate();

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(_logs, "selfupdate", "selfupdate-20260816-231113.log")));
    }

    [Theory]
    [InlineData("Chrome_install.1.log")]
    [InlineData("AzureCLI_install.log")]
    [InlineData("Teams_msix_install.log")]
    public void InstallerLogsComeOutOfTheDownloadCache(string name)
    {
        var source = Write(_cache, Path.Combine("browsers", name));

        Relocate();

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(_logs, "installs", name)));
    }

    [Fact]
    public void PayloadsInTheCacheAreNotTouched()
    {
        var payload = Write(_cache, Path.Combine("design", "Suite-2026.8.pkg"));
        var marker = Write(_cache, Path.Combine("design", "Suite-2026.8.pkg.verified"));

        Relocate();

        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(marker));
    }

    [Theory]
    [InlineData("cimiwatcher20260820.log")]   // Serilog owns it and caps it at 7
    [InlineData("installers.log")]            // third-party; expires on age instead
    [InlineData("verifiedHarmony.log")]       // a state marker, not a log at all
    public void FilesThisClientDoesNotOwnAreLeftWhereTheyAre(string name)
    {
        var source = Write(_logs, name);

        Relocate();

        Assert.True(File.Exists(source));
    }

    [Fact]
    public void RelocationIsIdempotentAndSurvivesACollision()
    {
        // Runs every session. The second pass must not fail because the destination
        // already holds the previous attempt's copy.
        Write(_logs, "cimipkg-App-CimianPostinstall.log", "first");
        Relocate();
        Write(_logs, "cimipkg-App-CimianPostinstall.log", "second");
        Relocate();

        var moved = Path.Combine(_logs, "packages", "App", "postinstall.log");
        Assert.Equal("second", File.ReadAllText(moved));
        Assert.Empty(Directory.GetFiles(_logs));
    }

    [Fact]
    public void IsSilentWhenNothingHasAccumulated()
    {
        Relocate();

        Assert.Empty(Directory.GetFiles(_logs));
    }
}
