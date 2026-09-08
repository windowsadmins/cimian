using System;
using System.IO;
using System.Linq;
using Cimian.Core.Services;
using Xunit;

/// <summary>
/// A run's own install events must reach events.json while the run is still writing them.
/// </summary>
/// <remarks>
/// The report is generated before EndSession disposes the events.jsonl writer, so the
/// current session's file is open for writing when the report reads it. File.ReadLines
/// opens with FileShare.Read, which the OS refuses against an open writer; the sharing
/// violation was swallowed per file, and events.json was written without the session that
/// was writing it. Every installing run therefore handed postflight a report with none of
/// its own installs, and the fleet showed nothing for a package that had just installed.
/// </remarks>
public class LiveSessionEventsTests : IDisposable
{
    private readonly string _root;

    public LiveSessionEventsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cimian-live-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string OpenLiveEventsFile(out StreamWriter writer)
    {
        var path = Path.Combine(_root, "events.jsonl");
        // Exactly how SessionLogger opens it: append, AutoFlush, default share mode.
        writer = new StreamWriter(path, append: true) { AutoFlush = true };
        writer.WriteLine("{\"event_type\":\"install\",\"package_name\":\"Rhino\",\"status\":\"started\"}");
        writer.WriteLine("{\"event_type\":\"install\",\"package_name\":\"Rhino\",\"status\":\"completed\"}");
        return path;
    }

    [Fact]
    public void ReadLinesShared_ReadsAFileAnotherWriterStillHoldsOpen()
    {
        var path = OpenLiveEventsFile(out var writer);
        using (writer)
        {
            var lines = SessionLogger.ReadLinesShared(path).ToList();
            Assert.Equal(2, lines.Count);
            Assert.Contains("\"status\":\"completed\"", lines[1]);
        }
    }

    [Fact]
    public void ReadLinesShared_SeesLinesWrittenAfterAnEarlierRead()
    {
        // AutoFlush puts each completed line on disk; a later read must see it.
        var path = OpenLiveEventsFile(out var writer);
        using (writer)
        {
            Assert.Equal(2, SessionLogger.ReadLinesShared(path).Count());
            writer.WriteLine("{\"event_type\":\"install\",\"package_name\":\"Rhino\",\"status\":\"completed\",\"message\":\"v8\"}");
            Assert.Equal(3, SessionLogger.ReadLinesShared(path).Count());
        }
    }

    [Fact]
    public void TheDefaultReadIsRefusedAgainstAnOpenWriter_OnWindows()
    {
        // The premise. Share modes are enforced by the OS only on Windows, which is the
        // only platform this client runs on; elsewhere .NET emulates nothing and the old
        // read succeeds, so this fact is meaningful only there.
        if (!OperatingSystem.IsWindows()) return;

        var path = OpenLiveEventsFile(out var writer);
        using (writer)
        {
            Assert.Throws<IOException>(() => File.ReadLines(path).ToList());
        }
    }

    [Fact]
    public void ATornFinalLine_CostsThatLineNotTheSession()
    {
        // The file is read while it is still being written, so the last line can be
        // incomplete. The two good events must survive it.
        var path = OpenLiveEventsFile(out var writer);
        using (writer)
        {
            writer.Write("{\"event_type\":\"install\",\"package_name\":\"Rhi");
            var events = SessionLogger.ParseEventLines(SessionLogger.ReadLinesShared(path)).ToList();
            Assert.Equal(2, events.Count);
            Assert.All(events, e => Assert.Equal("Rhino", e.PackageName));
        }
    }

    [Fact]
    public void ReadLinesShared_StillReadsAClosedFile()
    {
        var path = OpenLiveEventsFile(out var writer);
        writer.Dispose();
        Assert.Equal(2, SessionLogger.ReadLinesShared(path).Count());
    }
}
