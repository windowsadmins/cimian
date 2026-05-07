using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cimian.Core.Models;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests;

/// <summary>
/// Acceptance tests for items.json per-run finalization.
///
/// Three invariants under test:
///   1. last_seen_in_session == sessionId iff the run produced a terminal
///      install/update/remove outcome for the item.
///   2. current_status reflects the actual outcome, not the pre-install plan.
///   3. install_count / failure_count accumulate from events.jsonl rather than
///      defaulting to 0 because of the package vs package_name field-name bug.
/// </summary>
public class ItemsJsonFinalizationTests
{
    // ── SessionItemStatusResolver: outcome vs pending plan ──────────────────

    [Fact]
    public void Resolve_SuccessfulInstallOutcome_OverridesPendingInstall()
    {
        var outcome = new ItemOutcome("Foo", "1.0", "install", true, null, DateTime.UtcNow);

        var status = SessionItemStatusResolver.Resolve(
            outcome,
            isPendingInstall: true,   // plan said it was pending
            isPendingUpdate: false,
            isPendingUninstall: false,
            manifestAction: "install");

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void Resolve_FailedInstallOutcome_ReturnsFailed()
    {
        var outcome = new ItemOutcome("Bar", "1.0", "install", false, "boom", DateTime.UtcNow);

        var status = SessionItemStatusResolver.Resolve(
            outcome,
            isPendingInstall: true,
            isPendingUpdate: false,
            isPendingUninstall: false,
            manifestAction: "install");

        Assert.Equal("Failed", status);
    }

    [Fact]
    public void Resolve_SuccessfulRemoveOutcome_ReturnsRemoved()
    {
        var outcome = new ItemOutcome("Baz", "1.0", "remove", true, null, DateTime.UtcNow);

        var status = SessionItemStatusResolver.Resolve(
            outcome,
            isPendingInstall: false,
            isPendingUpdate: false,
            isPendingUninstall: true,
            manifestAction: "uninstall");

        Assert.Equal("Removed", status);
    }

    [Fact]
    public void Resolve_NoOutcome_FallsBackToPendingInstallPlan()
    {
        var status = SessionItemStatusResolver.Resolve(
            outcome: null,
            isPendingInstall: true,
            isPendingUpdate: false,
            isPendingUninstall: false,
            manifestAction: "install");

        Assert.Equal("Pending Install", status);
    }

    [Fact]
    public void Resolve_NoOutcome_AlreadyInstalledItem_ReturnsInstalled()
    {
        var status = SessionItemStatusResolver.Resolve(
            outcome: null,
            isPendingInstall: false,
            isPendingUpdate: false,
            isPendingUninstall: false,
            manifestAction: "install");

        Assert.Equal("Installed", status);
    }

    // ── DataExporter: events.jsonl reads + LastSeenInSession stamp ──────────

    [Fact]
    public void GenerateCurrentItems_PackageNameKey_CountsInstallsAndFailures()
    {
        using var fixture = new SessionsFixture();
        fixture.WriteSession("2026-04-27-1000",
            EventLine(action: "install", status: "completed", packageName: "Foo", packageVersion: "1.0"),
            EventLine(action: "install", status: "failed",    packageName: "Foo", packageVersion: "1.0"),
            EventLine(action: "install", status: "completed", packageName: "Foo", packageVersion: "1.1"));

        var exporter = new DataExporter(fixture.BaseDir);
        var items = exporter.GenerateCurrentItemsFromPackagesInfo(
            new List<SessionPackageInfo>
            {
                new() { Name = "Foo", Version = "1.1", Status = "Installed", ItemType = "managed_installs", DisplayName = "Foo" }
            },
            currentSessionId: "2026-04-27-1000");

        var foo = items.Single();
        Assert.Equal(3, foo.InstallCount);
        Assert.Equal(1, foo.FailureCount);
    }

    [Fact]
    public void GenerateCurrentItems_LegacyPackageKey_StillCountsForBackwardCompat()
    {
        using var fixture = new SessionsFixture();
        // Pre-rename events.jsonl format with `package` / `version` instead of
        // `package_name` / `package_version`.
        fixture.WriteSession("2026-04-26-0900",
            "{\"action\":\"install\",\"status\":\"completed\",\"package\":\"Bar\",\"version\":\"2.0\"}",
            "{\"action\":\"install\",\"status\":\"failed\",\"package\":\"Bar\",\"version\":\"2.0\"}");

        var exporter = new DataExporter(fixture.BaseDir);
        var items = exporter.GenerateCurrentItemsFromPackagesInfo(
            new List<SessionPackageInfo>
            {
                new() { Name = "Bar", Version = "2.0", Status = "Installed", ItemType = "managed_installs", DisplayName = "Bar" }
            },
            currentSessionId: "2026-04-26-0900");

        var bar = items.Single();
        Assert.Equal(2, bar.InstallCount);
        Assert.Equal(1, bar.FailureCount);
    }

    [Fact]
    public void GenerateCurrentItems_ActedOn_StampsLastSeenInSession()
    {
        using var fixture = new SessionsFixture();
        var exporter = new DataExporter(fixture.BaseDir);

        var items = exporter.GenerateCurrentItemsFromPackagesInfo(
            new List<SessionPackageInfo>
            {
                new()
                {
                    Name = "Touched", Version = "1.0", Status = "Installed",
                    ItemType = "managed_installs", DisplayName = "Touched",
                    ActionPerformed = "install",
                    OutcomeTimestamp = DateTime.UtcNow
                },
                new()
                {
                    Name = "OnlyChecked", Version = "1.0", Status = "Installed",
                    ItemType = "managed_installs", DisplayName = "OnlyChecked"
                    // ActionPerformed left null — only status-checked
                }
            },
            currentSessionId: "2026-04-28-1545");

        var touched = items.Single(i => i.ItemName == "Touched");
        var onlyChecked = items.Single(i => i.ItemName == "OnlyChecked");

        Assert.Equal("2026-04-28-1545", touched.LastSeenInSession);
        Assert.Equal("", onlyChecked.LastSeenInSession);
    }

    [Fact]
    public void GenerateCurrentItems_NoEventHistory_StillReportsZeroCountsNotMissing()
    {
        using var fixture = new SessionsFixture();
        var exporter = new DataExporter(fixture.BaseDir);

        var items = exporter.GenerateCurrentItemsFromPackagesInfo(
            new List<SessionPackageInfo>
            {
                new() { Name = "Fresh", Version = "1.0", Status = "Pending", ItemType = "managed_installs", DisplayName = "Fresh" }
            },
            currentSessionId: "2026-04-28-1545");

        var fresh = items.Single();
        Assert.Equal(0, fresh.InstallCount);
        Assert.Equal(0, fresh.FailureCount);
        Assert.NotNull(fresh.RecentAttempts);
        Assert.Empty(fresh.RecentAttempts);
    }

    // ── LoopGuard suppression surfacing ─────────────────────────────────────

    [Fact]
    public void DataExporter_LoopSuppressedSessionItem_PopulatesWarningFields()
    {
        // Given a loop-suppressed item produced by CollectSessionItems, DataExporter
        // must propagate WarningMessage into LastWarning + WarningCount and stamp
        // last_seen_in_session because ActionPerformed is set.
        using var fixture = new SessionsFixture();
        var exporter = new DataExporter(fixture.BaseDir);

        const string reason = "LOOP SUPPRESSED: WinAdminsAccount — suppressed for 6h 0m " +
                              "(Rapid-fire loop). Clear with: managedsoftwareupdate --clear-loop WinAdminsAccount";

        var items = exporter.GenerateCurrentItemsFromPackagesInfo(
            new List<SessionPackageInfo>
            {
                new()
                {
                    Name = "WinAdminsAccount",
                    Version = "1.0",
                    Status = "Warning",
                    ItemType = "managed_installs",
                    DisplayName = "WinAdminsAccount",
                    WarningMessage = reason,
                    StatusReason = reason,
                    StatusReasonCode = StatusReasonCode.LoopSuppressed,
                    DetectionMethod = Cimian.Core.Models.DetectionMethod.None,
                    ActionPerformed = "loop_suppressed",
                    OutcomeTimestamp = DateTime.UtcNow
                }
            },
            currentSessionId: "2026-05-06-1532");

        var record = items.Single();
        Assert.Equal("Warning", record.CurrentStatus);
        Assert.Equal(reason, record.LastWarning);
        Assert.Equal(1, record.WarningCount);
        Assert.Equal("2026-05-06-1532", record.LastSeenInSession);
    }

    [Fact]
    public void LoopGuard_GetSuppressedReport_ReturnsActiveSuppressionsWithClearCommand()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "CimianTests", "LoopGuard", Guid.NewGuid().ToString());
        Directory.CreateDirectory(stateDir);
        var statePath = Path.Combine(stateDir, "state.json");
        var logsDir   = Path.Combine(stateDir, "logs");
        Directory.CreateDirectory(logsDir);

        try
        {
            var guard = new LoopGuard(statePath, logsDir);

            // Three rapid attempts within 2h trigger the rapid-fire loop policy.
            for (var i = 0; i < 3; i++)
            {
                guard.RecordAttempt("WinAdminsAccount", "1.0", success: false, catalogFingerprint: "fp1");
            }

            var report = guard.GetSuppressedReport();
            var entry = Assert.Single(report);
            Assert.Equal("WinAdminsAccount", entry.Name);
            Assert.Equal("1.0", entry.Version);
            // Stored reason is the policy-level cause (LoopGuard composes the
            // operator-facing "LOOP SUPPRESSED: <name> ..." string at ShouldSuppress
            // time, not at storage time).
            Assert.Contains("Rapid-fire", entry.Reason);
            Assert.Equal("managedsoftwareupdate --clear-loop WinAdminsAccount", entry.ClearCommand);
        }
        finally
        {
            try { Directory.Delete(stateDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string EventLine(string action, string status, string packageName, string packageVersion) =>
        "{\"action\":\"" + action + "\"," +
        "\"status\":\"" + status + "\"," +
        "\"package_name\":\"" + packageName + "\"," +
        "\"package_version\":\"" + packageVersion + "\"," +
        "\"timestamp\":\"" + DateTime.UtcNow.ToString("o") + "\"}";

    private sealed class SessionsFixture : IDisposable
    {
        public string BaseDir { get; }

        public SessionsFixture()
        {
            BaseDir = Path.Combine(Path.GetTempPath(), "CimianTests", "ItemsJson", Guid.NewGuid().ToString());
            Directory.CreateDirectory(BaseDir);
        }

        public void WriteSession(string sessionId, params string[] eventLines)
        {
            var dir = Path.Combine(BaseDir, sessionId);
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "events.jsonl"), eventLines);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(BaseDir)) Directory.Delete(BaseDir, recursive: true); }
            catch { /* cleanup best-effort */ }
        }
    }
}
