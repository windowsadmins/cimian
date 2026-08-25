using System;
using System.IO;
using System.Linq;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Collection of the script output that MSI custom actions leave in sidecar files.
/// </summary>
/// <remarks>
/// The behaviour these pin down: a package's install scripts run inside msiexec, so
/// their output reaches this client only through a file. That file is a handoff, not a
/// log — once its contents are in the session log (which is what ReportMate ingests)
/// nothing should be left behind. Anything still sitting under the logs root afterwards
/// is either not ours or is state, and both must survive untouched.
/// </remarks>
public class PackageScriptLogDrainTests : IDisposable
{
    private readonly string _logs;

    public PackageScriptLogDrainTests()
    {
        _logs = Path.Combine(Path.GetTempPath(), "cimian-drain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); } catch { }
    }

    private string Write(string relativePath, params string[] lines)
    {
        var full = Path.Combine(_logs, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllLines(full, lines);
        return full;
    }

    private SessionLogger.PackageScriptOutput[] Drain()
        => SessionLogger.CollectPackageScriptLogs(_logs).ToArray();

    [Fact]
    public void PerPackageSidecarIsReturnedAndRemoved()
    {
        var file = Write(Path.Combine("packages", "ExampleAgent", "postinstall.log"),
            "Registry key exists", "Configuration applied");

        var drained = Drain();

        Assert.False(File.Exists(file));
        var one = Assert.Single(drained);
        Assert.Equal("ExampleAgent", one.Package);
        Assert.Equal("postinstall", one.Phase);
        Assert.Equal(new[] { "Registry key exists", "Configuration applied" }, one.Lines);
        Assert.False(one.Truncated);
    }

    [Theory]
    [InlineData("preinstall")]
    [InlineData("postinstall")]
    [InlineData("uninstall")]
    public void EveryPhaseIsCollected(string phase)
    {
        Write(Path.Combine("packages", "App", $"{phase}.log"), "output");

        var one = Assert.Single(Drain());

        Assert.Equal(phase, one.Phase);
    }

    [Fact]
    public void HandRolledSidecarAtTheLogsRootIsCollectedToo()
    {
        // Scripts that opened a file themselves instead of printing to stdout. This is
        // what is actually sitting on already-deployed endpoints, so the drain has to
        // cover it without waiting for every package to be rebuilt.
        var file = Write("LocalAccountSetup-postinstall.log", "All verification checks passed.");

        var drained = Drain();

        Assert.False(File.Exists(file));
        var one = Assert.Single(drained);
        Assert.Equal("LocalAccountSetup", one.Package);
        Assert.Equal("postinstall", one.Phase);
    }

    [Theory]
    [InlineData("vendor-state-marker.log")]          // a state marker: deleting it reinstalls forever
    [InlineData("installers.log")]               // third-party, not ours to consume
    [InlineData("cimiwatcher20260824.log")]      // Serilog owns it
    [InlineData("ManagedSoftwareUpdate.log")]
    [InlineData("postinstall.log")]              // no package name in front of it
    public void FilesAtTheRootThatAreNotOursAreLeftAlone(string name)
    {
        var file = Write(name, "content");

        var drained = Drain();

        Assert.Empty(drained);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void SessionDirectoriesAreNeverTouched()
    {
        var nested = Write(Path.Combine("2026-08-24", "1408", "install.log"), "the session log itself");

        Assert.Empty(Drain());
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void BlankOutputIsRemovedButNotReported()
    {
        // The script ran and printed nothing. There is nothing to say, and leaving an
        // empty file behind would defeat the point.
        var file = Write(Path.Combine("packages", "Quiet", "preinstall.log"), "", "   ");

        Assert.Empty(Drain());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void RunawayOutputIsCappedAndSaysSo()
    {
        var lines = Enumerable.Range(1, SessionLogger.MaxPackageScriptLogLines + 50)
            .Select(i => $"line {i}").ToArray();
        Write(Path.Combine("packages", "Chatty", "postinstall.log"), lines);

        var one = Assert.Single(Drain());

        Assert.True(one.Truncated);
        Assert.Equal(SessionLogger.MaxPackageScriptLogLines, one.Lines.Count);
        Assert.Equal("line 1", one.Lines[0]);
    }

    [Fact]
    public void EmptiedPackageDirectoryIsCleanedUp()
    {
        Write(Path.Combine("packages", "Gone", "postinstall.log"), "done");

        Drain();

        Assert.False(Directory.Exists(Path.Combine(_logs, "packages", "Gone")));
    }

    [Fact]
    public void DrainingTwiceIsSafeAndTheSecondPassFindsNothing()
    {
        Write(Path.Combine("packages", "App", "postinstall.log"), "first run");

        Assert.Single(Drain());
        Assert.Empty(Drain());
    }

    [Fact]
    public void IsSilentWhenNothingHasBeenWritten()
    {
        Assert.Empty(Drain());
    }
}
