using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Recovery of sessions that were killed before they reached EndSession.
/// </summary>
/// <remarks>
/// The regression these guard: a run that dies mid-session leaves session.json at
/// "running" with an empty summary, and because the reports are only regenerated
/// from EndSession, the reports directory keeps advertising the last session that
/// finished. The host then looks healthy while nothing it was asked to install
/// actually ran - and the only symptom that reaches the fleet is a loop warning
/// blaming the pkginfo detection criteria.
/// </remarks>
public class AbandonedSessionTests : IDisposable
{
    private readonly string _root;

    public AbandonedSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cimian-abandoned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteSession(string id, string status, int? processId, DateTime startedAt, string? lastEventPackage = null)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);

        var environment = new Dictionary<string, object>();
        if (processId.HasValue)
        {
            environment["process_id"] = processId.Value;
        }

        var session = new
        {
            session_id = id,
            start_time = startedAt.ToString("o"),
            run_type = "auto",
            status,
            environment
        };
        File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(session));

        if (lastEventPackage != null)
        {
            var evt = new
            {
                event_id = id + "-1",
                session_id = id,
                timestamp = startedAt.AddSeconds(21).ToString("o"),
                level = "DEBUG",
                event_type = "status_check",
                package_name = lastEventPackage,
                action = "",
                status = "installed",
                message = ""
            };
            File.WriteAllText(Path.Combine(dir, "events.jsonl"), JsonSerializer.Serialize(evt) + Environment.NewLine);
        }

        return dir;
    }

    private static JsonElement ReadSession(string dir)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "session.json"))).RootElement;

    [Fact]
    public void AbandonedRunningSession_IsMarkedAbortedAndNamesWhereItStopped()
    {
        // A dead pid: nothing can still be writing this session.
        var dir = WriteSession("2026-09-01-0206", "running", processId: 0x7FFFFFFF,
            startedAt: DateTime.Now.AddHours(-6), lastEventPackage: "DisableKillerNetworkService");

        var recovered = SessionLogger.ReapAbandonedSessions(new[] { dir }, currentSessionDir: "", currentSessionId: "2026-09-01-1631");

        Assert.Single(recovered);
        var session = ReadSession(dir);
        Assert.Equal("aborted", session.GetProperty("status").GetString());
        Assert.True(session.TryGetProperty("end_time", out _));
        Assert.Contains("DisableKillerNetworkService",
            session.GetProperty("environment").GetProperty("aborted_reason").GetString());
        Assert.Equal("2026-09-01-1631",
            session.GetProperty("environment").GetProperty("aborted_detected_by").GetString());
    }

    [Fact]
    public void CompletedSession_IsLeftAlone()
    {
        var dir = WriteSession("2026-09-01-1531", "completed", processId: null, startedAt: DateTime.Now.AddHours(-2));

        var recovered = SessionLogger.ReapAbandonedSessions(new[] { dir }, "", "2026-09-01-1631");

        Assert.Empty(recovered);
        Assert.Equal("completed", ReadSession(dir).GetProperty("status").GetString());
    }

    [Fact]
    public void LiveSession_OfARunningProcess_IsNotClobbered()
    {
        // This test's own process is alive and started before the session, which is
        // exactly the shape of a genuinely concurrent run.
        var dir = WriteSession("2026-09-01-1631", "running",
            processId: Environment.ProcessId, startedAt: DateTime.Now);

        var recovered = SessionLogger.ReapAbandonedSessions(new[] { dir }, "", "2026-09-01-1731");

        Assert.Empty(recovered);
        Assert.Equal("running", ReadSession(dir).GetProperty("status").GetString());
    }

    [Fact]
    public void CurrentSessionDirectory_IsNeverReaped()
    {
        var dir = WriteSession("2026-09-01-1631", "running", processId: 0x7FFFFFFF, startedAt: DateTime.Now);

        var recovered = SessionLogger.ReapAbandonedSessions(new[] { dir }, currentSessionDir: dir, currentSessionId: "2026-09-01-1631");

        Assert.Empty(recovered);
        Assert.Equal("running", ReadSession(dir).GetProperty("status").GetString());
    }

    [Fact]
    public void SessionWithNoEvents_StillGetsAbortedWithAGenericReason()
    {
        var dir = WriteSession("2026-09-01-0415", "running", processId: 0x7FFFFFFF, startedAt: DateTime.Now.AddHours(-9));

        var recovered = SessionLogger.ReapAbandonedSessions(new[] { dir }, "", "2026-09-01-1631");

        Assert.Single(recovered);
        var session = ReadSession(dir);
        Assert.Equal("aborted", session.GetProperty("status").GetString());
        Assert.Equal("session ended without reaching EndSession",
            session.GetProperty("environment").GetProperty("aborted_reason").GetString());
    }
}
