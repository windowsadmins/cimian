using Xunit;
using FluentAssertions;
using Cimian.Core.Services;
using Cimian.Core.Models;
using System.Text.Json;

namespace Cimian.Tests.Core.Services;

/// <summary>
/// Tests for LoopGuard install loop prevention service
/// </summary>
public class LoopGuardTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statePath;
    private readonly string _logsDir;
    private readonly string _cacheDir;

    public LoopGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"loopguard_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _statePath = Path.Combine(_tempDir, "state.json");
        _logsDir = Path.Combine(_tempDir, "logs");
        _cacheDir = Path.Combine(_tempDir, "Cache");
        Directory.CreateDirectory(_logsDir);
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* cleanup best-effort */ }
    }

    private LoopGuard CreateGuard(bool isBootstrap = false, bool disabled = false)
    {
        return new LoopGuard(_statePath, _logsDir, isBootstrap, _cacheDir, disabled);
    }

    #region Basic Behavior

    [Fact]
    public void NewGuard_ShouldNotSuppressAnything()
    {
        var guard = CreateGuard();
        var (suppress, _) = guard.ShouldSuppress("SomePackage", "1.0.0");
        suppress.Should().BeFalse();
    }

    [Fact]
    public void ShouldSuppress_NullOrEmptyPackage_ReturnsFalse()
    {
        var guard = CreateGuard();
        guard.ShouldSuppress("", "1.0.0").Suppress.Should().BeFalse();
        guard.ShouldSuppress(null!, "1.0.0").Suppress.Should().BeFalse();
    }

    [Fact]
    public void RecordAttempt_TracksAttempts()
    {
        var guard = CreateGuard();
        guard.RecordAttempt("TestPkg", "1.0.0", success: true);
        guard.RecordAttempt("TestPkg", "1.0.0", success: true);

        var state = guard.GetPackageState("TestPkg");
        state.Should().NotBeNull();
        state!.AttemptCount.Should().Be(2);
        state.LastVersion.Should().Be("1.0.0");
        state.LastSuccess.Should().BeTrue();
    }

    [Fact]
    public void RecordAttempt_IsCaseInsensitive()
    {
        var guard = CreateGuard();
        guard.RecordAttempt("TestPkg", "1.0.0", success: true);
        guard.RecordAttempt("testpkg", "1.0.0", success: true);

        var state = guard.GetPackageState("TESTPKG");
        state.Should().NotBeNull();
        state!.AttemptCount.Should().Be(2);
    }

    #endregion

    #region Bootstrap Mode

    [Fact]
    public void Bootstrap_NeverSuppresses()
    {
        var guard = CreateGuard(isBootstrap: true);

        // Simulate a package that would normally be suppressed
        guard.RecordAttempt("LoopPkg", "1.0.0", true);
        guard.RecordAttempt("LoopPkg", "1.0.0", true);
        guard.RecordAttempt("LoopPkg", "1.0.0", true);

        // Even with 3 attempts, bootstrap mode should never suppress
        var (suppress, _) = guard.ShouldSuppress("LoopPkg", "1.0.0");
        suppress.Should().BeFalse();
    }

    #endregion

    #region Globally Disabled (LoopGuardEnabled: false)

    [Fact]
    public void Disabled_NeverSuppresses()
    {
        var guard = CreateGuard(disabled: true);

        // Simulate a package that would normally trip rapid-fire suppression
        guard.RecordAttempt("LoopPkg", "1.0.0", true);
        guard.RecordAttempt("LoopPkg", "1.0.0", true);
        guard.RecordAttempt("LoopPkg", "1.0.0", true);

        // With LoopGuard globally disabled, suppression is off entirely
        var (suppress, reason) = guard.ShouldSuppress("LoopPkg", "1.0.0");
        suppress.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_IgnoresExistingSuppressionState()
    {
        // First, a guard that actually suppresses the package and persists state.
        var enforcing = CreateGuard();
        enforcing.RecordAttempt("StuckPkg", "1.0.0", true);
        enforcing.RecordAttempt("StuckPkg", "1.0.0", true);
        enforcing.RecordAttempt("StuckPkg", "1.0.0", true);
        enforcing.ShouldSuppress("StuckPkg", "1.0.0").Suppress.Should().BeTrue();

        // A disabled guard loading the same state must not act on it.
        var disabled = CreateGuard(disabled: true);
        disabled.ShouldSuppress("StuckPkg", "1.0.0").Suppress.Should().BeFalse();
    }

    [Fact]
    public void Disabled_DoesNotAccumulateSuppressionState()
    {
        // Record enough attempts to normally trip rapid-fire suppression, but while
        // globally disabled — no suppression state should be persisted.
        var disabled = CreateGuard(disabled: true);
        disabled.RecordAttempt("LaterPkg", "1.0.0", true);
        disabled.RecordAttempt("LaterPkg", "1.0.0", true);
        disabled.RecordAttempt("LaterPkg", "1.0.0", true);

        // Re-enable LoopGuard (fresh guard loading the same state file). Because
        // nothing was recorded while disabled, it behaves as if no loop history
        // exists and does not instantly suppress the package.
        var reEnabled = CreateGuard();
        reEnabled.ShouldSuppress("LaterPkg", "1.0.0").Suppress.Should().BeFalse();
    }

    #endregion

    #region Self-Reported Warning Carve-Out

    [Fact]
    public void SelfReportedWarning_DoesNotIncrementAttemptCount()
    {
        var guard = CreateGuard();

        guard.RecordAttempt("WarnPkg", "1.0.0", success: true, selfReportedWarning: true);
        guard.RecordAttempt("WarnPkg", "1.0.0", success: true, selfReportedWarning: true);
        guard.RecordAttempt("WarnPkg", "1.0.0", success: true, selfReportedWarning: true);

        // Self-reported warnings must not pollute loop counters
        var state = guard.GetPackageState("WarnPkg");
        state.Should().BeNull("self-reported warnings should not create per-package loop state");
    }

    [Fact]
    public void SelfReportedWarning_DoesNotTripRapidFireSuppression()
    {
        var guard = CreateGuard();

        // Same pattern that trips suppression for normal attempts: 3 in 2 hours
        guard.RecordAttempt("FirmwarePkg", "2026.06.10", success: true, selfReportedWarning: true);
        guard.RecordAttempt("FirmwarePkg", "2026.06.10", success: true, selfReportedWarning: true);
        guard.RecordAttempt("FirmwarePkg", "2026.06.10", success: true, selfReportedWarning: true);

        var (suppress, _) = guard.ShouldSuppress("FirmwarePkg", "2026.06.10");
        suppress.Should().BeFalse("CIMIAN-WARNING markers signal awaiting external remediation, not install loops");
    }

    [Fact]
    public void NormalAttempts_StillSuppress_WhenInterleavedWithSelfReportedWarnings()
    {
        var guard = CreateGuard();

        // Mix: 2 normal attempts + 1 self-reported warning → only 2 count
        guard.RecordAttempt("MixedPkg", "1.0.0", success: true);
        guard.RecordAttempt("MixedPkg", "1.0.0", success: true, selfReportedWarning: true);
        guard.RecordAttempt("MixedPkg", "1.0.0", success: true);

        var (suppress, _) = guard.ShouldSuppress("MixedPkg", "1.0.0");
        suppress.Should().BeFalse("two real attempts should not trip the 3-in-2h threshold");

        // Third real attempt should trip suppression
        guard.RecordAttempt("MixedPkg", "1.0.0", success: true);
        var (suppress2, reason) = guard.ShouldSuppress("MixedPkg", "1.0.0");
        suppress2.Should().BeTrue("loop guard must still catch real install loops");
        reason.Should().Contain("LOOP SUPPRESSED");
    }

    [Fact]
    public void SelfReportedWarning_DefaultsToFalse_PreservesExistingBehavior()
    {
        var guard = CreateGuard();

        // Call without selfReportedWarning (existing call sites) — must behave as before
        guard.RecordAttempt("LegacyPkg", "1.0.0", success: true);
        guard.RecordAttempt("LegacyPkg", "1.0.0", success: true);
        guard.RecordAttempt("LegacyPkg", "1.0.0", success: true);

        var (suppress, _) = guard.ShouldSuppress("LegacyPkg", "1.0.0");
        suppress.Should().BeTrue("default selfReportedWarning=false must preserve rapid-fire suppression");
    }

    #endregion

    #region Rapid-Fire Detection

    [Fact]
    public void RapidFire_ThreeInstallsInTwoHours_Suppresses()
    {
        var guard = CreateGuard();

        // Record 3 rapid-fire installs (timestamps are "now")
        guard.RecordAttempt("RapidPkg", "2.0.0", true);
        guard.RecordAttempt("RapidPkg", "2.0.0", true);
        guard.RecordAttempt("RapidPkg", "2.0.0", true);

        // Should now be suppressed
        var (suppress, reason) = guard.ShouldSuppress("RapidPkg", "2.0.0");
        suppress.Should().BeTrue();
        reason.Should().Contain("LOOP SUPPRESSED");
        reason.Should().Contain("--clear-loop");
    }

    #endregion

    #region Escalating Backoff

    [Fact]
    public void SameVersion_ThreeAttempts_ThreeSessions_SixHourSuppression()
    {
        // Spread events across days to avoid rapid-fire detection (2-hour window)
        CreateEventsFile("session1", "EscPkg", "3.0.0", "completed", DateTime.UtcNow.AddDays(-3));
        CreateEventsFile("session2", "EscPkg", "3.0.0", "completed", DateTime.UtcNow.AddDays(-2));
        CreateEventsFile("session3", "EscPkg", "3.0.0", "completed", DateTime.UtcNow.AddDays(-1));

        var guard = CreateGuard();

        var (suppress, reason) = guard.ShouldSuppress("EscPkg", "3.0.0");
        suppress.Should().BeTrue();
        reason.Should().Contain("6h");
    }

    [Fact]
    public void SameVersion_FiveAttempts_TwentyFourHourSuppression()
    {
        for (int i = 1; i <= 5; i++)
            CreateEventsFile($"session{i}", "EscPkg5", "3.0.0", "completed", DateTime.UtcNow.AddDays(-6 + i));

        var guard = CreateGuard();

        var (suppress, reason) = guard.ShouldSuppress("EscPkg5", "3.0.0");
        suppress.Should().BeTrue();
        reason.Should().Contain("24h");
    }

    [Fact]
    public void SameVersion_EightAttempts_CappedSuppression()
    {
        for (int i = 1; i <= 8; i++)
            CreateEventsFile($"session{i}", "EscPkg8", "3.0.0", "completed", DateTime.UtcNow.AddDays(-8 + i));

        var guard = CreateGuard();

        // Top tier is now a finite cap (default 7 days), not indefinite — so a package
        // stranded by a transient failure retries automatically instead of being blacklisted.
        var (suppress, reason) = guard.ShouldSuppress("EscPkg8", "3.0.0");
        suppress.Should().BeTrue();
        reason.Should().Contain("7-day");
        reason.Should().NotContain("indefinite");
    }

    #endregion

    #region Clear Loop

    [Fact]
    public void ClearLoop_RemovesSuppression()
    {
        var guard = CreateGuard();

        // Get it suppressed (rapid-fire)
        guard.RecordAttempt("ClearMe", "1.0.0", true);
        guard.RecordAttempt("ClearMe", "1.0.0", true);
        guard.RecordAttempt("ClearMe", "1.0.0", true);

        guard.ShouldSuppress("ClearMe", "1.0.0").Suppress.Should().BeTrue();

        // Clear it
        guard.ClearLoop("ClearMe").Should().BeTrue();

        // Should no longer be suppressed
        guard.ShouldSuppress("ClearMe", "1.0.0").Suppress.Should().BeFalse();
    }

    [Fact]
    public void ClearLoop_NonexistentPackage_ReturnsFalse()
    {
        var guard = CreateGuard();
        guard.ClearLoop("DoesNotExist").Should().BeFalse();
    }

    [Fact]
    public void ClearAll_ClearsAllSuppressions()
    {
        var guard = CreateGuard();

        guard.RecordAttempt("Pkg1", "1.0.0", true);
        guard.RecordAttempt("Pkg1", "1.0.0", true);
        guard.RecordAttempt("Pkg1", "1.0.0", true);

        guard.RecordAttempt("Pkg2", "1.0.0", true);
        guard.RecordAttempt("Pkg2", "1.0.0", true);
        guard.RecordAttempt("Pkg2", "1.0.0", true);

        var count = guard.ClearAll();
        count.Should().BeGreaterThan(0);

        guard.ShouldSuppress("Pkg1", "1.0.0").Suppress.Should().BeFalse();
        guard.ShouldSuppress("Pkg2", "1.0.0").Suppress.Should().BeFalse();
    }

    #endregion

    #region State Persistence

    [Fact]
    public void State_PersistsAcrossInstances()
    {
        // First instance — record attempts to trigger suppression
        var guard1 = CreateGuard();
        guard1.RecordAttempt("PersistPkg", "1.0.0", true);
        guard1.RecordAttempt("PersistPkg", "1.0.0", true);
        guard1.RecordAttempt("PersistPkg", "1.0.0", true);

        guard1.ShouldSuppress("PersistPkg", "1.0.0").Suppress.Should().BeTrue();

        // Second instance — should still be suppressed from persisted state
        var guard2 = CreateGuard();
        guard2.ShouldSuppress("PersistPkg", "1.0.0").Suppress.Should().BeTrue();
    }

    [Fact]
    public void State_CorruptFile_StartsClean()
    {
        File.WriteAllText(_statePath, "{{{{not json}}}}");

        var guard = CreateGuard();
        guard.ShouldSuppress("AnyPkg", "1.0.0").Suppress.Should().BeFalse();
    }

    #endregion

    #region GetSuppressedPackages

    [Fact]
    public void GetSuppressedPackages_ReturnsSuppressed()
    {
        var guard = CreateGuard();

        guard.RecordAttempt("SuppPkg", "1.0.0", true);
        guard.RecordAttempt("SuppPkg", "1.0.0", true);
        guard.RecordAttempt("SuppPkg", "1.0.0", true);

        var suppressed = guard.GetSuppressedPackages();
        suppressed.Should().ContainSingle();
        suppressed[0].Name.Should().Be("SuppPkg");
    }

    [Fact]
    public void GetSuppressedPackages_Empty_WhenNoneActive()
    {
        var guard = CreateGuard();
        guard.GetSuppressedPackages().Should().BeEmpty();
    }

    #endregion

    #region History from events.jsonl

    [Fact]
    public void BuildsHistory_FromEventsFiles()
    {
        CreateEventsFile("session1", "HistPkg", "2.0.0", "completed");
        CreateEventsFile("session2", "HistPkg", "2.0.0", "completed");

        var guard = CreateGuard();

        var state = guard.GetPackageState("HistPkg");
        state.Should().NotBeNull();
        state!.AttemptCount.Should().Be(2);
        state.SessionCount.Should().Be(2);
    }

    [Fact]
    public void BuildsHistory_IgnoresNonInstallEvents()
    {
        // Create events with "status_check" action instead of "install"
        var dayDir = Path.Combine(_logsDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var sessionDir = Path.Combine(dayDir, "check_session");
        Directory.CreateDirectory(sessionDir);

        var events = new[]
        {
            new { package = "CheckPkg", action = "status_check", status = "installed", version = "1.0.0",
                  timestamp = DateTime.UtcNow.ToString("o") }
        };

        var eventsPath = Path.Combine(sessionDir, "events.jsonl");
        File.WriteAllLines(eventsPath, events.Select(e => JsonSerializer.Serialize(e)));

        var guard = CreateGuard();
        guard.GetPackageState("CheckPkg").Should().BeNull();
    }

    #endregion

    #region Auto-Clear on Catalog Change

    [Fact]
    public void VersionChange_AutoClearsSuppression()
    {
        var guard = CreateGuard();

        // Get suppressed on version 1.0.0 (rapid-fire)
        guard.RecordAttempt("VerPkg", "1.0.0", true);
        guard.RecordAttempt("VerPkg", "1.0.0", true);
        guard.RecordAttempt("VerPkg", "1.0.0", true);

        guard.ShouldSuppress("VerPkg", "1.0.0").Suppress.Should().BeTrue();

        // Catalog now has version 2.0.0 — pkgsinfo was updated
        var (suppress, reason) = guard.ShouldSuppress("VerPkg", "2.0.0");
        suppress.Should().BeFalse();
        reason.Should().Contain("Auto-cleared");
        reason.Should().Contain("2.0.0");
    }

    [Fact]
    public void VersionChange_ResetsHistory()
    {
        var guard = CreateGuard();

        guard.RecordAttempt("ResetPkg", "1.0.0", true);
        guard.RecordAttempt("ResetPkg", "1.0.0", true);
        guard.RecordAttempt("ResetPkg", "1.0.0", true);

        // Suppressed on 1.0.0
        guard.ShouldSuppress("ResetPkg", "1.0.0").Suppress.Should().BeTrue();

        // Auto-clear on version change
        guard.ShouldSuppress("ResetPkg", "2.0.0");

        // History should be reset — state should show 0 attempts
        var state = guard.GetPackageState("ResetPkg");
        state.Should().NotBeNull();
        state!.AttemptCount.Should().Be(0);
        state.LastVersion.Should().Be("2.0.0");
    }

    [Fact]
    public void SameVersion_DoesNotAutoClear()
    {
        var guard = CreateGuard();

        guard.RecordAttempt("SamePkg", "1.0.0", true);
        guard.RecordAttempt("SamePkg", "1.0.0", true);
        guard.RecordAttempt("SamePkg", "1.0.0", true);

        // Same version — should stay suppressed
        guard.ShouldSuppress("SamePkg", "1.0.0").Suppress.Should().BeTrue();
    }

    [Fact]
    public void FingerprintChange_AutoClearsSuppression_SameVersion()
    {
        var guard = CreateGuard();
        var fingerprint1 = LoopGuard.ComputeFingerprint("1.0.0|old_script|");
        var fingerprint2 = LoopGuard.ComputeFingerprint("1.0.0|new_fixed_script|");

        // Get suppressed with fingerprint1
        guard.RecordAttempt("FpPkg", "1.0.0", true, fingerprint1);
        guard.RecordAttempt("FpPkg", "1.0.0", true, fingerprint1);
        guard.RecordAttempt("FpPkg", "1.0.0", true, fingerprint1);

        guard.ShouldSuppress("FpPkg", "1.0.0", fingerprint1).Suppress.Should().BeTrue();

        // Same version but different fingerprint (installcheck_script changed)
        var (suppress, reason) = guard.ShouldSuppress("FpPkg", "1.0.0", fingerprint2);
        suppress.Should().BeFalse();
        reason.Should().Contain("Auto-cleared");
        reason.Should().Contain("pkgsinfo fields updated");
    }

    [Fact]
    public void SameFingerprint_StaysSuppressed()
    {
        var guard = CreateGuard();
        var fingerprint = LoopGuard.ComputeFingerprint("1.0.0|some_script|hash123");

        guard.RecordAttempt("StayPkg", "1.0.0", true, fingerprint);
        guard.RecordAttempt("StayPkg", "1.0.0", true, fingerprint);
        guard.RecordAttempt("StayPkg", "1.0.0", true, fingerprint);

        // Same fingerprint — should stay suppressed
        guard.ShouldSuppress("StayPkg", "1.0.0", fingerprint).Suppress.Should().BeTrue();
    }

    [Fact]
    public void FingerprintChange_ResetsHistory()
    {
        var guard = CreateGuard();
        var fingerprint1 = LoopGuard.ComputeFingerprint("1.0.0||old_hash");
        var fingerprint2 = LoopGuard.ComputeFingerprint("1.0.0||new_hash");

        guard.RecordAttempt("FpResetPkg", "1.0.0", true, fingerprint1);
        guard.RecordAttempt("FpResetPkg", "1.0.0", true, fingerprint1);
        guard.RecordAttempt("FpResetPkg", "1.0.0", true, fingerprint1);

        guard.ShouldSuppress("FpResetPkg", "1.0.0", fingerprint1).Suppress.Should().BeTrue();

        // Auto-clear via fingerprint change
        guard.ShouldSuppress("FpResetPkg", "1.0.0", fingerprint2);

        var state = guard.GetPackageState("FpResetPkg");
        state.Should().NotBeNull();
        state!.AttemptCount.Should().Be(0);
        state.CatalogFingerprint.Should().Be(fingerprint2);
    }

    [Fact]
    public void FingerprintVersionChange_ShowsVersionInReason()
    {
        var guard = CreateGuard();
        var fingerprint1 = LoopGuard.ComputeFingerprint("1.0.0|script|hash1");
        var fingerprint2 = LoopGuard.ComputeFingerprint("2.0.0|script|hash2");

        guard.RecordAttempt("FpVerPkg", "1.0.0", true, fingerprint1);
        guard.RecordAttempt("FpVerPkg", "1.0.0", true, fingerprint1);
        guard.RecordAttempt("FpVerPkg", "1.0.0", true, fingerprint1);

        guard.ShouldSuppress("FpVerPkg", "1.0.0", fingerprint1).Suppress.Should().BeTrue();

        // Both version and fingerprint changed
        var (suppress, reason) = guard.ShouldSuppress("FpVerPkg", "2.0.0", fingerprint2);
        suppress.Should().BeFalse();
        reason.Should().Contain("Auto-cleared");
        reason.Should().Contain("version");
        reason.Should().Contain("2.0.0");
    }

    [Fact]
    public void FingerprintFallback_VersionOnlyWhenNoFingerprint()
    {
        var guard = CreateGuard();

        // Record without fingerprint (backward compatibility)
        guard.RecordAttempt("FallbackPkg", "1.0.0", true);
        guard.RecordAttempt("FallbackPkg", "1.0.0", true);
        guard.RecordAttempt("FallbackPkg", "1.0.0", true);

        guard.ShouldSuppress("FallbackPkg", "1.0.0").Suppress.Should().BeTrue();

        // Version change without fingerprint — should still auto-clear
        var (suppress, reason) = guard.ShouldSuppress("FallbackPkg", "2.0.0");
        suppress.Should().BeFalse();
        reason.Should().Contain("Auto-cleared");
    }

    [Fact]
    public void FingerprintChange_ClearsHistory_EvenWhenNotYetSuppressed()
    {
        // A package can carry loop history without being suppressed yet. If the pkgsinfo
        // is fixed at that point, the history must go too — otherwise the first install
        // after the fix is judged on pre-fix evidence and trips immediately.
        var guard = CreateGuard();
        var before = LoopGuard.ComputeFingerprint("1.0.0|old");
        var after = LoopGuard.ComputeFingerprint("1.0.0|fixed");

        guard.RecordAttempt("NotYetPkg", "1.0.0", true, before);
        guard.RecordAttempt("NotYetPkg", "1.0.0", true, before);
        guard.ShouldSuppress("NotYetPkg", "1.0.0", before).Suppress.Should().BeFalse();
        guard.GetPackageState("NotYetPkg")!.AttemptCount.Should().Be(2);

        var (suppress, reason) = guard.ShouldSuppress("NotYetPkg", "1.0.0", after);

        suppress.Should().BeFalse();
        reason.Should().Contain("Auto-cleared");
        var state = guard.GetPackageState("NotYetPkg")!;
        state.AttemptCount.Should().Be(0);
        state.VersionAttempts.Should().BeEmpty();
        state.CatalogFingerprint.Should().Be(after);
    }

    [Fact]
    public void FingerprintChange_ClearsProcessedSessions()
    {
        // SessionCount is recomputed from ProcessedSessions by the events rebuild, so
        // leaving the set behind made the session gate on every threshold permanently
        // satisfied and the package re-suppressed on ~3 installs instead of 3 sessions.
        var guard = CreateGuard();
        var before = LoopGuard.ComputeFingerprint("1.0.0|old");
        var after = LoopGuard.ComputeFingerprint("1.0.0|fixed");

        foreach (var session in new[] { "0900", "1000", "1100" })
        {
            guard.SetCurrentSession(Path.Combine(_logsDir, "2026-01-01", session));
            guard.RecordAttempt("SessPkg", "1.0.0", true, before);
        }

        guard.GetPackageState("SessPkg")!.ProcessedSessions.Should().HaveCount(3);

        guard.ShouldSuppress("SessPkg", "1.0.0", after);

        var state = guard.GetPackageState("SessPkg")!;
        state.ProcessedSessions.Should().BeEmpty();
        state.SessionCount.Should().Be(0);
        state.ClearedAt.Should().NotBeNull();
    }

    [Fact]
    public void FingerprintChange_ResetsSuppressionCycles()
    {
        // A fix starts the backoff over: the escalation floor earned by earlier windows
        // must not keep a fixed package on a 24h/7d leash.
        var guard = CreateGuard();
        var before = LoopGuard.ComputeFingerprint("1.0.0|old");
        var after = LoopGuard.ComputeFingerprint("1.0.0|fixed");

        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("CyclePkg", "1.0.0", true, before);

        var state = guard.GetPackageState("CyclePkg")!;
        state.SuppressedUntil = DateTime.UtcNow.AddMinutes(-1);
        guard.ShouldSuppress("CyclePkg", "1.0.0", before).Suppress.Should().BeFalse();
        guard.GetPackageState("CyclePkg")!.SuppressionCycles.Should().Be(1);

        guard.ShouldSuppress("CyclePkg", "1.0.0", after);

        guard.GetPackageState("CyclePkg")!.SuppressionCycles.Should().Be(0);
    }

    #endregion

    #region Suppression Expiry

    [Fact]
    public void ExpiredSuppression_RetriesInsteadOfReSuppressingOnTheSameHistory()
    {
        // The counters never decay, so an expired window used to fall straight back into
        // the thresholds it was created by and re-suppress without a single retry — a
        // permanent blacklist by the back door.
        var guard = CreateGuard();

        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("ExpirePkg", "1.0.0", true);

        var state = guard.GetPackageState("ExpirePkg")!;
        state.SuppressedUntil.Should().NotBeNull();
        state.SuppressedUntil = DateTime.UtcNow.AddMinutes(-1);

        guard.ShouldSuppress("ExpirePkg", "1.0.0").Suppress.Should().BeFalse();

        // ...and still not suppressed on the next look: the history went with the window.
        guard.ShouldSuppress("ExpirePkg", "1.0.0").Suppress.Should().BeFalse();
        var cleared = guard.GetPackageState("ExpirePkg")!;
        cleared.AttemptCount.Should().Be(0);
        cleared.RecentTimestamps.Should().BeEmpty();
        cleared.SuppressionCycles.Should().Be(1);
    }

    [Fact]
    public void ExpiredSuppression_EscalatesTheNextWindow()
    {
        // Retrying after every window must not mean looping every 12 hours forever: a
        // served cycle floors the next window at 24h, two or more at the 7-day cap.
        var guard = CreateGuard();

        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("EscalatePkg", "1.0.0", true);
        guard.GetPackageState("EscalatePkg")!.SuppressedUntil = DateTime.UtcNow.AddMinutes(-1);
        guard.ShouldSuppress("EscalatePkg", "1.0.0");

        // Still broken — it loops again on the fresh history.
        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("EscalatePkg", "1.0.0", true);

        var state = guard.GetPackageState("EscalatePkg")!;
        state.SuppressedUntil.Should().NotBeNull();
        // Rapid-fire alone is 12h; the served cycle floors it at 24h.
        state.SuppressedUntil!.Value.Should().BeAfter(DateTime.UtcNow.AddHours(23));
        state.SuppressionReason.Should().Contain("escalated");

        state.SuppressedUntil = DateTime.UtcNow.AddMinutes(-1);
        guard.ShouldSuppress("EscalatePkg", "1.0.0");
        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("EscalatePkg", "1.0.0", true);

        // Second served cycle — floored at the 7-day cap.
        guard.GetPackageState("EscalatePkg")!.SuppressedUntil!.Value
            .Should().BeAfter(DateTime.UtcNow.AddDays(6));
    }

    #endregion

    #region Cache Analysis

    [Fact]
    public void Cache_HitWhenFileExists()
    {
        // Create a cached file
        File.WriteAllText(Path.Combine(_cacheDir, "CachePkg.exe"), "fake-installer");

        var guard = CreateGuard();
        var (hasCache, path) = guard.CheckCacheForPackage("CachePkg");
        hasCache.Should().BeTrue();
        path.Should().Contain("CachePkg.exe");
    }

    [Fact]
    public void Cache_HitInSubdirectory()
    {
        var pkgDir = Path.Combine(_cacheDir, "SubPkg");
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, "installer.msi"), "fake-msi");

        var guard = CreateGuard();
        var (hasCache, path) = guard.CheckCacheForPackage("SubPkg");
        hasCache.Should().BeTrue();
        path.Should().Contain("installer.msi");
    }

    [Fact]
    public void Cache_MissWhenNoFile()
    {
        var guard = CreateGuard();
        var (hasCache, _) = guard.CheckCacheForPackage("NoCachePkg");
        hasCache.Should().BeFalse();
    }

    [Fact]
    public void DiagnosticInfo_IncludesCacheStatus()
    {
        File.WriteAllText(Path.Combine(_cacheDir, "DiagPkg.exe"), "fake");

        var guard = CreateGuard();
        guard.RecordAttempt("DiagPkg", "1.0.0", true);
        guard.RecordAttempt("DiagPkg", "1.0.0", true);
        guard.RecordAttempt("DiagPkg", "1.0.0", true);

        var diag = guard.GetDiagnosticInfo("DiagPkg");
        diag.Should().Contain("Cache: HIT");
        diag.Should().Contain("install/status-check issue");
    }

    #endregion

    #region State File Format

    [Fact]
    public void StateFile_UsesNestedCimianStateFormat()
    {
        var guard = CreateGuard();
        guard.RecordAttempt("FmtPkg", "1.0.0", true);

        // Read the state file and verify it has the nested structure
        var json = File.ReadAllText(_statePath);
        var doc = JsonDocument.Parse(json);

        // Should have a "loop_guard" wrapper
        doc.RootElement.TryGetProperty("loop_guard", out var loopGuard).Should().BeTrue();
        loopGuard.TryGetProperty("packages", out var packages).Should().BeTrue();
        packages.TryGetProperty("fmtpkg", out _).Should().BeTrue();
    }

    [Fact]
    public void StateFile_PersistsCatalogFingerprint()
    {
        var fingerprint = LoopGuard.ComputeFingerprint("1.0.0|script|hash");
        var guard1 = CreateGuard();
        guard1.RecordAttempt("FpPersistPkg", "1.0.0", true, fingerprint);

        // Second instance should load the fingerprint
        var guard2 = CreateGuard();
        var state = guard2.GetPackageState("FpPersistPkg");
        state.Should().NotBeNull();
        state!.CatalogFingerprint.Should().Be(fingerprint);
    }

    #endregion

    #region ComputeFingerprint

    [Fact]
    public void ComputeFingerprint_DeterministicForSameInput()
    {
        var fp1 = LoopGuard.ComputeFingerprint("1.0.0|script_here|hash123");
        var fp2 = LoopGuard.ComputeFingerprint("1.0.0|script_here|hash123");
        fp1.Should().Be(fp2);
    }

    [Fact]
    public void ComputeFingerprint_DiffersForDifferentInput()
    {
        var fp1 = LoopGuard.ComputeFingerprint("1.0.0|old_script|hash123");
        var fp2 = LoopGuard.ComputeFingerprint("1.0.0|new_script|hash123");
        fp1.Should().NotBe(fp2);
    }

    [Fact]
    public void ComputeFingerprint_Returns16CharHex()
    {
        var fp = LoopGuard.ComputeFingerprint("test input");
        fp.Should().HaveLength(16);
        fp.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    #endregion

    #region Clears Survive History Rebuild

    [Fact]
    public void ClearAll_SurvivesHistoryRebuild()
    {
        // 3 sessions of installs → suppression trips
        CreateEventsFile("ca_s1", "StuckPkg", "1.0.0", "completed", DateTime.UtcNow.AddHours(-3));
        CreateEventsFile("ca_s2", "StuckPkg", "1.0.0", "completed", DateTime.UtcNow.AddHours(-2));
        CreateEventsFile("ca_s3", "StuckPkg", "1.0.0", "completed", DateTime.UtcNow.AddMinutes(-90));

        var guard = CreateGuard();
        guard.ShouldSuppress("StuckPkg", "1.0.0").Suppress.Should().BeTrue();

        guard.ClearAll().Should().BeGreaterThan(0);

        // A fresh instance rebuilds history from the same events.jsonl files.
        // Before the ClearedAt watermark existed, this rebuild re-counted the
        // cleared installs and re-tripped suppression immediately — making
        // --clear-loop all (and any MDM-driven clear) a self-undoing no-op.
        var rebuilt = CreateGuard();
        rebuilt.ShouldSuppress("StuckPkg", "1.0.0").Suppress.Should().BeFalse();
    }

    [Fact]
    public void ClearLoop_SurvivesHistoryRebuild()
    {
        CreateEventsFile("cl_s1", "OnePkg", "1.0.0", "completed", DateTime.UtcNow.AddHours(-3));
        CreateEventsFile("cl_s2", "OnePkg", "1.0.0", "completed", DateTime.UtcNow.AddHours(-2));
        CreateEventsFile("cl_s3", "OnePkg", "1.0.0", "completed", DateTime.UtcNow.AddMinutes(-90));

        var guard = CreateGuard();
        guard.ShouldSuppress("OnePkg", "1.0.0").Suppress.Should().BeTrue();
        guard.ClearLoop("OnePkg").Should().BeTrue();

        var rebuilt = CreateGuard();
        rebuilt.ShouldSuppress("OnePkg", "1.0.0").Suppress.Should().BeFalse();
    }

    [Fact]
    public void ClearAll_FreshInstallsAfterClear_StillTripSuppression()
    {
        CreateEventsFile("cf_s1", "StillLooping", "1.0.0", "completed", DateTime.UtcNow.AddHours(-3));
        CreateEventsFile("cf_s2", "StillLooping", "1.0.0", "completed", DateTime.UtcNow.AddHours(-2));
        CreateEventsFile("cf_s3", "StillLooping", "1.0.0", "completed", DateTime.UtcNow.AddMinutes(-90));

        var guard = CreateGuard();
        guard.ShouldSuppress("StillLooping", "1.0.0").Suppress.Should().BeTrue();
        guard.ClearAll();

        // The loop is still live: 3 more installs land AFTER the clear.
        // Clears must not whitelist a still-broken package.
        CreateEventsFile("cf_s4", "StillLooping", "1.0.0", "completed", DateTime.UtcNow.AddMinutes(1));
        CreateEventsFile("cf_s5", "StillLooping", "1.0.0", "completed", DateTime.UtcNow.AddMinutes(2));
        CreateEventsFile("cf_s6", "StillLooping", "1.0.0", "completed", DateTime.UtcNow.AddMinutes(3));

        var rebuilt = CreateGuard();
        rebuilt.ShouldSuppress("StillLooping", "1.0.0").Suppress.Should().BeTrue();
    }

    #endregion

    #region Convergence (MarkNonConverged)

    [Fact]
    public void MarkNonConverged_SuppressesWithPreciseReason()
    {
        var guard = CreateGuard();
        var reason = guard.MarkNonConverged("BadCheck", "1.0.0", "fp1", reprobeHours: 24);
        reason.Should().Contain("did not converge");

        var (suppress, msg) = guard.ShouldSuppress("BadCheck", "1.0.0", "fp1");
        suppress.Should().BeTrue();
        msg.Should().Contain("did not converge");
    }

    [Fact]
    public void MarkNonConverged_AutoClearsOnFingerprintChange()
    {
        var guard = CreateGuard();
        guard.MarkNonConverged("BadCheck", "1.0.0", "fp1", reprobeHours: 24);
        guard.ShouldSuppress("BadCheck", "1.0.0", "fp1").Suppress.Should().BeTrue();

        // Admin fixes the pkgsinfo (any install-behavior field) — clears immediately
        var (suppress, reason) = guard.ShouldSuppress("BadCheck", "1.0.0", "fp2-fixed");
        suppress.Should().BeFalse();
        reason.Should().Contain("Auto-cleared");
    }

    [Fact]
    public void MarkNonConverged_SurvivesReload()
    {
        var guard = CreateGuard();
        guard.MarkNonConverged("BadCheck", "1.0.0", "fp1", reprobeHours: 24);

        var reloaded = CreateGuard();
        reloaded.ShouldSuppress("BadCheck", "1.0.0", "fp1").Suppress.Should().BeTrue();
    }

    #endregion

    #region Pending Restart Deferral

    [Fact]
    public void PendingRestart_DefersWhileNoRebootHasHappened()
    {
        var guard = CreateGuard();
        // Machine booted an hour ago; install just happened
        guard.BootTimeUtcProvider = () => DateTime.UtcNow.AddHours(-1);
        guard.RecordPendingRestart("FirmwarePkg", "1.0.0", "fp1");

        var (defer, reason) = guard.ShouldDeferForRestart("FirmwarePkg", "fp1");
        defer.Should().BeTrue();
        reason.Should().Contain("PENDING RESTART");
    }

    [Fact]
    public void PendingRestart_ClearsAfterReboot()
    {
        var guard = CreateGuard();
        guard.BootTimeUtcProvider = () => DateTime.UtcNow.AddHours(-1);
        guard.RecordPendingRestart("FirmwarePkg", "1.0.0", "fp1");
        guard.ShouldDeferForRestart("FirmwarePkg", "fp1").Defer.Should().BeTrue();

        // Reboot: boot time is now AFTER the memo was stamped
        guard.BootTimeUtcProvider = () => DateTime.UtcNow.AddMinutes(5);
        guard.ShouldDeferForRestart("FirmwarePkg", "fp1").Defer.Should().BeFalse();

        // Memo is gone — stays cleared even if boot time regresses (state, not luck)
        guard.BootTimeUtcProvider = () => DateTime.UtcNow.AddHours(-1);
        guard.ShouldDeferForRestart("FirmwarePkg", "fp1").Defer.Should().BeFalse();
    }

    [Fact]
    public void PendingRestart_ClearsOnFingerprintChange()
    {
        var guard = CreateGuard();
        guard.BootTimeUtcProvider = () => DateTime.UtcNow.AddHours(-1);
        guard.RecordPendingRestart("FirmwarePkg", "1.0.0", "fp1");

        guard.ShouldDeferForRestart("FirmwarePkg", "fp2-changed").Defer.Should().BeFalse();
    }

    [Fact]
    public void PendingRestart_UnknownPackage_DoesNotDefer()
    {
        var guard = CreateGuard();
        guard.ShouldDeferForRestart("NeverSeen", "fp1").Defer.Should().BeFalse();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates a mock events.jsonl file in the day-nested log directory structure
    /// </summary>
    private void CreateEventsFile(string sessionName, string packageName, string version, string status, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.UtcNow;
        var dayDir = Path.Combine(_logsDir, ts.ToString("yyyy-MM-dd"));
        var sessionDir = Path.Combine(dayDir, sessionName);
        Directory.CreateDirectory(sessionDir);

        var evt = new
        {
            package = packageName,
            action = "install",
            status = status,
            version = version,
            timestamp = ts.ToString("o")
        };

        var eventsPath = Path.Combine(sessionDir, "events.jsonl");
        // Append to allow multiple events in same session
        File.AppendAllText(eventsPath, JsonSerializer.Serialize(evt) + "\n");
    }

    #endregion

    #region Trigger reporting — why the package keeps installing

    private static InstallTrigger FileMissingTrigger(string path = "C:/Program Files/Thing/thing.exe") =>
        InstallTrigger.From(StatusReasonCode.FileMissing, DetectionMethod.InstallsArray,
            $"installs[0] file {path}: file not found")!;

    [Fact]
    public void SuppressionMessage_NamesTheCheckThatKeepsTriggeringTheInstall()
    {
        var guard = CreateGuard();
        var trigger = FileMissingTrigger();

        guard.RecordAttempt("LoopPkg", "1.0.0", true, trigger: trigger);
        guard.RecordAttempt("LoopPkg", "1.0.0", true, trigger: trigger);
        guard.RecordAttempt("LoopPkg", "1.0.0", true, trigger: trigger);

        var (suppress, reason) = guard.ShouldSuppress("LoopPkg", "1.0.0", null, trigger);

        suppress.Should().BeTrue();
        reason.Should().Contain("Needs install because");
        reason.Should().Contain(StatusReasonCode.FileMissing);
        reason.Should().Contain("installs[0] file");
        reason.Should().Contain("thing.exe");
        reason.Should().Contain("same on all 3 attempts");
        reason.Should().Contain("--clear-loop LoopPkg");
    }

    [Fact]
    public void SuppressionMessage_StillReportsTheLoopRuleAndTheVersion()
    {
        var guard = CreateGuard();
        var trigger = FileMissingTrigger();
        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("LoopPkg", "2.5.0", true, trigger: trigger);

        var (_, reason) = guard.ShouldSuppress("LoopPkg", "2.5.0", null, trigger);

        reason.Should().Contain("LoopPkg v2.5.0");
        reason.Should().Contain("Loop rule:");
        reason.Should().MatchRegex("Rapid-fire|Install loop|Escalated|Persistent");
    }

    [Fact]
    public void SuppressionMessage_SummarisesMultipleDistinctTriggers()
    {
        var guard = CreateGuard();
        var missing = FileMissingTrigger();
        var outdated = InstallTrigger.From(StatusReasonCode.VersionOutdated, DetectionMethod.Msi,
            "installs[1] msi product_code={GUID}: registered version 1.0 is older than the catalog's 2.0")!;

        guard.RecordAttempt("MixedPkg", "1.0.0", true, trigger: missing);
        guard.RecordAttempt("MixedPkg", "1.0.0", true, trigger: outdated);
        guard.RecordAttempt("MixedPkg", "1.0.0", true, trigger: outdated);

        var (suppress, reason) = guard.ShouldSuppress("MixedPkg", "1.0.0", null, outdated);

        suppress.Should().BeTrue();
        reason.Should().Contain("most recent of 3 attempts");
        reason.Should().Contain($"{StatusReasonCode.VersionOutdated} x2");
        reason.Should().Contain($"{StatusReasonCode.FileMissing} x1");
    }

    [Fact]
    public void SuppressionMessage_SaysSoWhenNoTriggerWasEverRecorded()
    {
        // State written by a client that predates trigger capture: the warning must
        // admit it has no cause rather than silently reporting only the counting rule.
        var guard = CreateGuard();
        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("OldPkg", "1.0.0", true);

        var (suppress, reason) = guard.ShouldSuppress("OldPkg", "1.0.0");

        suppress.Should().BeTrue();
        reason.Should().Contain("not recorded");
    }

    [Fact]
    public void SuppressedEvaluation_RefreshesTheReportedCauseWithoutCountingAnAttempt()
    {
        var guard = CreateGuard();
        var missing = FileMissingTrigger();
        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("LoopPkg", "1.0.0", true, trigger: missing);

        var nowOutdated = InstallTrigger.From(StatusReasonCode.VersionOutdated, DetectionMethod.File,
            "installs[0] file C:/Program Files/Thing/thing.exe: file version 1.0 is older than the catalog's 2.0")!;
        var (suppress, reason) = guard.ShouldSuppress("LoopPkg", "1.0.0", null, nowOutdated);

        suppress.Should().BeTrue();
        reason.Should().Contain(StatusReasonCode.VersionOutdated);

        // The refresh must not inflate the attempt evidence — it was never installed.
        var state = guard.GetPackageState("LoopPkg")!;
        state.AttemptCount.Should().Be(3);
        state.TriggerCounts.Values.Sum().Should().Be(3);
    }

    [Fact]
    public void CatalogChange_ClearsTheRecordedTriggerEvidence()
    {
        var guard = CreateGuard();
        var trigger = FileMissingTrigger();
        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("LoopPkg", "1.0.0", true, "fingerprint-a", trigger: trigger);

        guard.ShouldSuppress("LoopPkg", "1.0.0", "fingerprint-b").Suppress.Should().BeFalse();

        var state = guard.GetPackageState("LoopPkg")!;
        state.TriggerCounts.Should().BeEmpty();
        state.Trigger.Should().BeNull();
    }

    [Fact]
    public void MarkNonConverged_NamesTheCheckThatDidNotConverge()
    {
        var guard = CreateGuard();
        var trigger = InstallTrigger.From(StatusReasonCode.InstallcheckNeeded, DetectionMethod.Script,
            "installcheck_script exited 0, which means install needed (script output: legacy key still present)")!;

        var reason = guard.MarkNonConverged("StuckPkg", "1.0.0", "fp", 24, trigger);

        reason.Should().Contain("installcheck_script exited 0");
        reason.Should().Contain("legacy key still present");

        var (suppress, message) = guard.ShouldSuppress("StuckPkg", "1.0.0", "fp");
        suppress.Should().BeTrue();
        message.Should().Contain("legacy key still present");
    }

    [Fact]
    public void Trigger_DetailIsFlattenedAndTruncated()
    {
        var noisy = "line one" + Environment.NewLine + new string('x', 500);
        var trigger = InstallTrigger.From(StatusReasonCode.ScriptError, DetectionMethod.Script, noisy)!;

        trigger.Detail.Should().NotContainAny(Environment.NewLine, "\r", "\n");
        trigger.Detail.Length.Should().BeLessThanOrEqualTo(InstallTrigger.MaxDetailLength + 1);
    }

    [Fact]
    public void Trigger_SurvivesAStateReload()
    {
        var trigger = FileMissingTrigger();
        var guard = CreateGuard();
        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("LoopPkg", "1.0.0", true, trigger: trigger);

        var reloaded = CreateGuard();
        var (suppress, reason) = reloaded.ShouldSuppress("LoopPkg", "1.0.0");

        suppress.Should().BeTrue();
        reason.Should().Contain("thing.exe");
    }

    [Fact]
    public void SuppressedReport_CarriesTheTrigger()
    {
        var guard = CreateGuard();
        var trigger = FileMissingTrigger();
        for (var i = 0; i < 3; i++)
            guard.RecordAttempt("LoopPkg", "1.0.0", true, trigger: trigger);
        guard.ShouldSuppress("LoopPkg", "1.0.0");

        var report = guard.GetSuppressedReport().Single(r => r.Name == "LoopPkg");
        report.Trigger.Should().NotBeNull();
        report.Trigger!.ReasonCode.Should().Be(StatusReasonCode.FileMissing);
        report.TriggerSummary.Should().Contain("thing.exe");
    }

    #endregion
}
