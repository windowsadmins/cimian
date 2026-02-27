using Xunit;
using FluentAssertions;
using Cimian.Core.Services;
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

    private LoopGuard CreateGuard(bool isBootstrap = false)
    {
        return new LoopGuard(_statePath, _logsDir, isBootstrap, _cacheDir);
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
    public void SameVersion_EightAttempts_IndefiniteSuppression()
    {
        for (int i = 1; i <= 8; i++)
            CreateEventsFile($"session{i}", "EscPkg8", "3.0.0", "completed", DateTime.UtcNow.AddDays(-8 + i));

        var guard = CreateGuard();

        var (suppress, reason) = guard.ShouldSuppress("EscPkg8", "3.0.0");
        suppress.Should().BeTrue();
        reason.Should().Contain("indefinite");
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
}
