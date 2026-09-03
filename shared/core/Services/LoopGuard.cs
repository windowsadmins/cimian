// LoopGuard.cs - Active install loop prevention service
// Prevents packages from being reinstalled in a loop by tracking install history
// and applying exponential backoff suppression.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cimian.Core.Models;

namespace Cimian.Core.Services;

/// <summary>
/// LoopGuard provides active install loop prevention by tracking per-package install
/// history from events.jsonl files and applying exponential backoff suppression.
///
/// DetectInstallLoopEnhanced in DataExporter is the PASSIVE counterpart — it enriches
/// items.json with loop warnings for dashboards/monitoring. LoopGuard is the ACTIVE layer
/// that integrates into UpdateEngine.IdentifyActions() to actually suppress looping packages.
///
/// Auto-clear: when the pkgsinfo behind a package changes, its loop history is cleared —
/// the root cause may have been fixed. This is the CENTRAL lever: makecatalogs stamps a
/// loop_fingerprint over each catalog item's whole content, the caller passes it as
/// catalogFingerprint, and publishing a fix therefore gets a suppressed package installing
/// again across the whole fleet without touching a single machine. It is evaluated whether
/// or not suppression is currently active, and performs exactly the same reset as an
/// explicit --clear-loop, so a fix is never judged on pre-fix history.
///
/// Backoff strategy (finite — never a permanent blacklist, so a transient failure
/// like a download outage self-heals on retry instead of needing a manual clear):
///   3+ installs of same version across 3+ sessions → suppress 6 hours
///   5+ installs across 5+ sessions → suppress 24 hours
///   8+ installs → suppress 7 days (the cap), then retry automatically
///   3 installs within 2 hours (rapid-fire) → suppress 12 hours
///
/// When a window expires the accumulated counters are retired with it, so the package is
/// genuinely retried instead of instantly re-tripping the same thresholds on the same
/// history; a persistent SuppressionCycles count floors the next window (1 prior cycle →
/// 24h, 2+ → the 7-day cap), so a genuinely-broken package converges on a few installs a
/// week and then goes quiet. One whose root cause was fixed (pkgsinfo change auto-clears
/// immediately and resets the cycles, or the underlying failure simply went away)
/// installs cleanly on its next window. Legacy permanently-suppressed entries
/// (DateTime.MaxValue, written before this cap) are migrated to a finite window
/// (LoopMaxTime) anchored on their last attempt when first seen.
///
/// State persisted to: %ProgramData%\ManagedInstalls\reports\state.json
/// Clear with: managedsoftwareupdate --clear-loop (name or all)
/// </summary>
public class LoopGuard
{
    private static readonly string ReportsDir = CimianPaths.ReportsDir;

    private static readonly string StatePath = Path.Combine(ReportsDir, "state.json");

    // Legacy path for migration from older versions
    private static readonly string LegacyStatePath = Path.Combine(ReportsDir, "loop_state.json");

    private static readonly string LogsDir = CimianPaths.LogsDir;

    private static readonly string CacheDir = CimianPaths.CacheDir;

    // Default upper bound (days) on any single suppression window, overridable per-fleet
    // via the LoopMaxTime config setting. The top escalation tier used to be
    // DateTime.MaxValue (permanent until a manual --clear-loop or a pkgsinfo change),
    // which turned a transient failure — e.g. a download outage that stranded a package
    // for 8+ sessions — into an indefinite blacklist. Capping at a long-but-finite window
    // keeps the system self-healing: the worst case is a once-per-LoopMaxTime retry.
    private const int DefaultMaxSuppressionDays = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions JsonLinesOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private LoopGuardState _state;
    private readonly bool _isBootstrap;
    private readonly bool _disabled;
    private readonly int _maxSuppressionDays;

    /// <summary>
    /// Creates a new LoopGuard.
    /// If isBootstrap is true, suppression is disabled to avoid blocking first-run provisioning.
    /// If disabled is true, suppression is turned off entirely — the admin-facing global
    /// kill-switch, driven by the LoopGuardEnabled config setting.
    /// maxSuppressionDays caps the longest suppression window (the global LoopMaxTime config,
    /// in days); a non-positive value falls back to the DefaultMaxSuppressionDays default.
    /// </summary>
    public LoopGuard(bool isBootstrap = false, bool disabled = false, int maxSuppressionDays = DefaultMaxSuppressionDays)
    {
        _isBootstrap = isBootstrap;
        _disabled = disabled;
        _maxSuppressionDays = maxSuppressionDays > 0 ? maxSuppressionDays : DefaultMaxSuppressionDays;
        _state = LoadState();
        BuildHistoryFromEvents();
    }

    /// <summary>
    /// For unit testing — constructor that takes custom paths.
    /// </summary>
    internal LoopGuard(string statePath, string logsDir, bool isBootstrap = false, string? cacheDir = null, bool disabled = false, int maxSuppressionDays = DefaultMaxSuppressionDays)
    {
        _isBootstrap = isBootstrap;
        _disabled = disabled;
        _maxSuppressionDays = maxSuppressionDays > 0 ? maxSuppressionDays : DefaultMaxSuppressionDays;
        StatePath_Override = statePath;
        LogsDir_Override = logsDir;
        CacheDir_Override = cacheDir;
        _state = LoadState();
        BuildHistoryFromEvents();
    }

    /// <summary>
    /// Computes a short SHA256 fingerprint of arbitrary catalog content. If the input
    /// changes, the fingerprint changes and LoopGuard clears the package's loop history.
    ///
    /// Two callers share this so both sides agree on the algorithm: makecatalogs hashes
    /// each serialized catalog item into its loop_fingerprint field (the authoritative
    /// value), and UpdateEngine folds that — or, for catalogs written before the field
    /// existed, a concatenation of the item's install-behavior fields — together with the
    /// running agent version.
    /// </summary>
    public static string ComputeFingerprint(string fieldsConcat)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fieldsConcat));
        return Convert.ToHexStringLower(bytes)[..16]; // 16-char hex = 64 bits, sufficient for change detection
    }

    // Allow override for testing
    private string? StatePath_Override { get; }
    private string? LogsDir_Override { get; }
    private string? CacheDir_Override { get; }
    private string EffectiveStatePath => StatePath_Override ?? StatePath;
    private string EffectiveLogsDir => LogsDir_Override ?? LogsDir;
    private string EffectiveCacheDir => CacheDir_Override ?? CacheDir;

    #region Public API

    /// <summary>
    /// Checks whether a package should be suppressed due to install loop detection.
    /// Returns (suppress, reason) — if suppress is true, the package should NOT be installed.
    ///
    /// catalogFingerprint: SHA256 hash of the catalog item's install-behavior fields.
    /// If provided and different from the stored fingerprint, suppression is auto-cleared
    /// because the pkgsinfo was changed (version, installcheck_script, hash, installs, etc.).
    /// Falls back to version-only comparison if no fingerprint is provided.
    ///
    /// trigger: the status check that decided the package needs to run right now.
    /// Recorded against the package and replayed in the suppression message, so the
    /// warning names the criterion that never converges instead of only the counting
    /// rule that tripped.
    /// </summary>
    public (bool Suppress, string Reason) ShouldSuppress(string packageName, string version, string? catalogFingerprint = null, InstallTrigger? trigger = null)
    {
        // Never suppress during bootstrap — first-run provisioning must complete
        if (_isBootstrap)
            return (false, "");

        // Globally disabled by config (LoopGuardEnabled: false) — admin opted out entirely
        if (_disabled)
            return (false, "");

        if (string.IsNullOrEmpty(packageName))
            return (false, "");

        var key = packageName.ToLowerInvariant();

        // Check explicit suppression state first (from previous runs)
        if (_state.Packages.TryGetValue(key, out var pkgState))
        {
            // Refresh the observed cause before anything else. A suppressed package is
            // never installed, so its trigger would otherwise freeze at whatever was
            // true when the window opened — and an operator reading the warning a day
            // later needs to know what the item still wants now.
            NoteTrigger(pkgState, trigger, countIt: false);

            // Auto-clear: if the catalog fingerprint changed, the pkgsinfo was updated
            // and the root cause may be fixed. This is deliberately evaluated BEFORE the
            // suppression check and regardless of whether suppression is currently
            // active: a package that has accumulated loop history but has not tripped
            // yet must also start clean, otherwise the first install after the fix is
            // judged on pre-fix evidence and trips immediately.
            if (DetectCatalogChange(pkgState, version, catalogFingerprint, out var changeDetail))
            {
                ResetLoopHistory(pkgState, version, catalogFingerprint);
                SaveState();
                return (false, $"Auto-cleared: {changeDetail}");
            }

            // First sighting of a fingerprint for a package whose state predates it:
            // record it (no clear — we have nothing to compare against) so the NEXT
            // catalog change is detected instead of falling back to version-only.
            if (!string.IsNullOrEmpty(catalogFingerprint) && string.IsNullOrEmpty(pkgState.CatalogFingerprint))
            {
                pkgState.CatalogFingerprint = catalogFingerprint;
                SaveState();
            }

            if (pkgState.SuppressedUntil.HasValue)
            {
                // Migrate any legacy permanent suppression (DateTime.MaxValue, written
                // before the finite cap existed) to a concrete 7-day window anchored on
                // the last real attempt. A package stranded permanently by a transient
                // failure then self-heals: entries stuck longer than the cap are already
                // past the window and retry now; recent ones wait out the remainder — no
                // manual --clear-loop required.
                if (pkgState.SuppressedUntil.Value == DateTime.MaxValue)
                {
                    pkgState.SuppressedUntil = pkgState.LastAttempt.GetValueOrDefault().AddDays(_maxSuppressionDays);
                    SaveState();
                }

                if (DateTime.UtcNow < pkgState.SuppressedUntil.Value)
                {
                    var remaining = pkgState.SuppressedUntil.Value - DateTime.UtcNow;
                    return (true, BuildSuppressionMessage(packageName, version, pkgState, remaining));
                }

                // Suppression expired. Retire the history that produced it as well as
                // the window itself: the counters never decay, so leaving them intact
                // meant the threshold pass below re-tripped on the same evidence and
                // re-suppressed without the package ever being retried once — a permanent
                // blacklist by the back door, and the opposite of the finite backoff
                // documented above.
                // SuppressionCycles survives the reset and floors the next window, so a
                // genuinely-broken package still escalates to the 7-day cap.
                pkgState.SuppressionCycles++;
                var cycles = pkgState.SuppressionCycles;
                ResetLoopHistory(pkgState, version, catalogFingerprint ?? pkgState.CatalogFingerprint);
                pkgState.SuppressionCycles = cycles;
                SaveState();
                return (false, "");
            }
        }

        // Analyze current history for new loop conditions
        return AnalyzeForLoop(key, packageName, version);
    }

    /// <summary>
    /// Records that a package's own checks now report nothing to do. Convergence is the
    /// definition of a fixed package, so any loop history and any open suppression window
    /// are retired here.
    /// <para>
    /// Without this, LoopGuard only ever hears about a package while it still needs
    /// action: <see cref="ShouldSuppress"/> is reached from the engine only when the
    /// status check says NeedsAction. The moment a looping package starts converging —
    /// the pkgsinfo was fixed, the installer finally took, the machine changed — the
    /// guard is never consulted for it again, so its window, its (possibly absent)
    /// trigger and its counters freeze exactly as they were and keep being reported as a
    /// live loop for as long as the window runs. Measured on a lab fleet: healthy
    /// packages whose installcheck reported "no action needed" on the same run were still
    /// listed in loop_suppressed.json, and legacy indefinite entries for packages that
    /// had converged months earlier never reached the MaxValue migration below at all.
    /// </para>
    /// <para>
    /// This is deliberately stronger than letting the window expire: an expiry costs the
    /// package a full LoopMaxTime of being reported broken and, via
    /// <see cref="PackageLoopState.SuppressionCycles"/>, floors its next window. A package
    /// that converged is not owed either.
    /// </para>
    /// </summary>
    public void NoteConverged(string packageName, string? catalogFingerprint = null)
    {
        if (_disabled || string.IsNullOrEmpty(packageName))
            return;

        var key = packageName.ToLowerInvariant();
        if (!_state.Packages.TryGetValue(key, out var pkgState))
            return;

        // Nothing accumulated: leave the entry untouched rather than rewriting state
        // (and the state file) on every converged item of every run.
        if (pkgState.SuppressedUntil == null
            && pkgState.AttemptCount == 0
            && pkgState.SuppressionCycles == 0
            && pkgState.PendingRestartSince == null)
        {
            return;
        }

        ResetLoopHistory(pkgState, version: "", catalogFingerprint: catalogFingerprint ?? pkgState.CatalogFingerprint);
        SaveState();
    }

    /// <summary>
    /// Records the check that decided this package needs to run. Called on every
    /// evaluation (so the reported cause stays current while suppressed) and on every
    /// real attempt with <paramref name="countIt"/> set, which is what builds the
    /// "same reason every time" evidence the warning reports.
    /// </summary>
    private static void NoteTrigger(PackageLoopState pkgState, InstallTrigger? trigger, bool countIt)
    {
        if (trigger == null)
            return;

        pkgState.Trigger = trigger;
        pkgState.TriggerLastSeen = DateTime.UtcNow;

        if (!countIt)
            return;

        var key = trigger.Key;
        pkgState.TriggerCounts.TryGetValue(key, out var count);
        if (count == 0 && pkgState.TriggerCounts.Count >= MaxTrackedTriggers)
            return;
        pkgState.TriggerCounts[key] = count + 1;
    }

    /// <summary>
    /// Distinct triggers retained per package. Past a handful the item is not "stuck on
    /// one criterion", which is the distinction the summary needs to draw.
    /// </summary>
    private const int MaxTrackedTriggers = 5;

    /// <summary>
    /// The operator-facing suppression warning. Reports three things in order: how long
    /// the package is gagged, the counting rule that gagged it, and — the part that was
    /// missing — the detection result that keeps deciding the package must run. Without
    /// the last one every diagnosis started by finding the machine and re-reading the
    /// pkgsinfo by hand; the warning now carries the path, product code or script output
    /// that never converges.
    /// </summary>
    private static string BuildSuppressionMessage(string packageName, string version, PackageLoopState pkgState, TimeSpan remaining)
    {
        var shown = string.IsNullOrEmpty(version) ? pkgState.LastVersion : version;
        var name = string.IsNullOrEmpty(shown) ? packageName : $"{packageName} v{shown}";
        var rule = pkgState.SuppressionReason ?? "repeated installs";
        return $"Looping install detected: {name} — {rule}; paused for {FormatDuration(remaining)}";
    }

    /// <summary>
    /// The second half of a loop report: what the package's own checks keep finding, which
    /// is the part an admin can act on. Kept separate from the suppression message so the
    /// two can be surfaced as two warnings on the same item rather than one paragraph.
    /// Null when the package has no loop history at all.
    /// </summary>
    public string? GetSuppressionCause(string packageName)
    {
        if (string.IsNullOrEmpty(packageName))
            return null;

        return _state.Packages.TryGetValue(packageName.ToLowerInvariant(), out var pkgState)
            ? $"Needs install because {DescribeTrigger(pkgState)}"
            : null;
    }

    /// <summary>
    /// One-line account of why the package keeps asking to install, with how consistent
    /// that answer has been across attempts.
    /// </summary>
    private static string DescribeTrigger(PackageLoopState pkgState)
    {
        if (pkgState.Trigger == null)
            return "the cause was not recorded yet — it is captured on the next check";

        var described = pkgState.Trigger.Describe();
        var code = pkgState.Trigger.ReasonCode;
        var total = pkgState.TriggerCounts.Values.Sum();

        // Machine-facing tags go at the end, in brackets, so the sentence itself stays
        // readable: what was checked, what it found, then how consistent it has been.
        if (pkgState.TriggerCounts.Count == 1 && total >= 2)
            return $"{described} [{code}, unchanged over all {total} attempts]";

        if (pkgState.TriggerCounts.Count > 1)
        {
            var codes = pkgState.TriggerCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key.Split('|')[0]} x{kv.Value}");
            return $"{described} [most recent of {total} attempts: {string.Join(", ", codes)}]";
        }

        return string.IsNullOrEmpty(code) ? described : $"{described} [{code}]";
    }

    /// <summary>
    /// True when the catalog item backing this package has changed since the state was
    /// written — the admin edited the pkgsinfo, so the root cause may be fixed.
    /// Prefers the fingerprint (any install-behavior field) and falls back to a
    /// version-only comparison when no fingerprint is available on either side.
    /// </summary>
    private static bool DetectCatalogChange(PackageLoopState pkgState, string version,
                                            string? catalogFingerprint, out string changeDetail)
    {
        changeDetail = "";

        if (!string.IsNullOrEmpty(catalogFingerprint) && !string.IsNullOrEmpty(pkgState.CatalogFingerprint))
        {
            if (string.Equals(catalogFingerprint, pkgState.CatalogFingerprint, StringComparison.OrdinalIgnoreCase))
                return false;

            changeDetail = !string.Equals(version, pkgState.LastVersion, StringComparison.OrdinalIgnoreCase)
                ? $"catalog changed (version {pkgState.LastVersion} → {version})"
                : $"catalog changed (pkgsinfo fields updated, same version {version})";
            return true;
        }

        // Fallback: version-only comparison when no fingerprint is available
        if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(pkgState.LastVersion) &&
            !string.Equals(version, pkgState.LastVersion, StringComparison.OrdinalIgnoreCase))
        {
            changeDetail = $"catalog version changed from {pkgState.LastVersion} to {version}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Retires a package's accumulated loop history: drops the suppression window, zeroes
    /// every counter the thresholds read, and stamps the ClearedAt watermark so the next
    /// run's events.jsonl rebuild does not put it all back.
    /// <para>
    /// ProcessedSessions is cleared with the rest — without it SessionCount is recomputed
    /// from "sessions ever seen" by the rebuild and snaps straight back to 3+, so the
    /// session gate on every threshold was already satisfied and the package re-suppressed
    /// on ~3 fresh installs rather than 3 fresh sessions. This is the same reset
    /// <see cref="ClearLoop"/> performs: a catalog-driven clear must be exactly as strong
    /// as the local one, since it is the central lever for getting a fixed package moving
    /// again fleet-wide.
    /// </para>
    /// </summary>
    private static void ResetLoopHistory(PackageLoopState pkgState, string version, string? catalogFingerprint)
    {
        pkgState.SuppressedUntil = null;
        pkgState.SuppressionReason = null;
        pkgState.AttemptCount = 0;
        pkgState.SessionCount = 0;
        pkgState.SuppressionCycles = 0;
        pkgState.VersionAttempts.Clear();
        pkgState.RecentTimestamps.Clear();
        pkgState.ProcessedSessions.Clear();
        pkgState.TriggerCounts.Clear();
        pkgState.Trigger = null;
        pkgState.TriggerLastSeen = null;
        pkgState.PendingRestartSince = null;
        pkgState.ClearedAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(version))
            pkgState.LastVersion = version;
        if (!string.IsNullOrEmpty(catalogFingerprint))
            pkgState.CatalogFingerprint = catalogFingerprint;
    }

    /// <summary>
    /// Session id of the run that owns this guard, in the same "yyyy-MM-dd/HHMM" form
    /// the events.jsonl rebuild uses. RecordAttempt stamps it into the package's
    /// processed-session set so the next run's rebuild does not count the same
    /// install a second time. Without it every real attempt was counted twice
    /// (once live, once from history), so four successful upgrades of a
    /// frequently-released package read as "8 installs across 4 sessions" and
    /// tripped the total-attempt threshold on the fifth version.
    /// </summary>
    private string? _currentSessionId;

    public void SetCurrentSession(string? sessionDir)
    {
        if (string.IsNullOrEmpty(sessionDir))
            return;
        var timeDir = Path.GetFileName(sessionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dayDir = Path.GetFileName(Path.GetDirectoryName(sessionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "");
        _currentSessionId = string.IsNullOrEmpty(dayDir) ? timeDir : $"{dayDir}/{timeDir}";
    }

    /// <summary>
    /// Records an install attempt (call after InstallerService.InstallAsync completes).
    /// catalogFingerprint should match what was passed to ShouldSuppress for consistency.
    /// <para>
    /// Set <paramref name="selfReportedWarning"/> to true when the postinstall script
    /// signalled a Warning outcome via the <c>CIMIAN-WARNING:</c> marker convention
    /// (see <see cref="Cimian.Core.Models.ItemOutcome.WarningMessage"/>). Those runs
    /// are intentional soft-fails — the script ran successfully but the system is in
    /// a known-bad state awaiting external remediation (e.g. SecureBoot enabled by
    /// user, BIOS password set). They are NOT install loops, and counting them would
    /// suppress packages whose only job is to keep flagging the unremediated state.
    /// Real install loops (repeated normal installs of the same version with no
    /// marker) still accumulate and trip suppression as before.
    /// </para>
    /// </summary>
    public void RecordAttempt(string packageName, string version, bool success, string? catalogFingerprint = null, bool selfReportedWarning = false, InstallTrigger? trigger = null, bool loopExempt = false)
    {
        if (string.IsNullOrEmpty(packageName))
            return;

        // OnDemand and recurring items re-install every run by design, so the engine
        // never asks ShouldSuppress about them. Counting their attempts anyway wrote a
        // suppression window that nothing would ever enforce — invisible in behaviour,
        // but reported in loop_suppressed.json as a live loop. On a lab fleet this was
        // 11 of 76 active "loops", every one of them a provisioning helper doing exactly
        // what OnDemand means.
        if (loopExempt)
            return;

        // Globally disabled by config (LoopGuardEnabled: false): don't accumulate any
        // suppression state. ShouldSuppress already short-circuits, but we also skip
        // recording so the kill-switch is honest — re-enabling LoopGuard later behaves
        // as if no loop history exists rather than instantly suppressing packages based
        // on attempts logged during the disabled window. Passive loop reporting is
        // unaffected: install_loop_detected in items.json is derived from the structured
        // event history (DataExporter), not from this guard's internal state.
        if (_disabled)
            return;

        // Self-reported warnings are not install attempts for loop-detection purposes.
        // Skip counter updates entirely so the per-version count and rapid-fire window
        // are not polluted by intentional soft-fails. See remarks above.
        if (selfReportedWarning)
            return;

        var key = packageName.ToLowerInvariant();

        if (!_state.Packages.TryGetValue(key, out var pkgState))
        {
            pkgState = new PackageLoopState { PackageName = packageName };
            _state.Packages[key] = pkgState;
        }

        pkgState.AttemptCount++;
        pkgState.LastAttempt = DateTime.UtcNow;
        NoteTrigger(pkgState, trigger, countIt: true);
        if (!string.IsNullOrEmpty(_currentSessionId) && !pkgState.ProcessedSessions.Contains(_currentSessionId))
        {
            pkgState.ProcessedSessions.Add(_currentSessionId);
            pkgState.SessionCount = pkgState.ProcessedSessions.Count;
        }
        pkgState.LastVersion = version;
        pkgState.LastSuccess = success;
        if (!string.IsNullOrEmpty(catalogFingerprint))
            pkgState.CatalogFingerprint = catalogFingerprint;

        // Track per-version counts
        if (!string.IsNullOrEmpty(version))
        {
            pkgState.VersionAttempts.TryGetValue(version, out var count);
            pkgState.VersionAttempts[version] = count + 1;
        }

        // Track timestamps for rapid-fire detection
        pkgState.RecentTimestamps.Add(DateTime.UtcNow);

        // Keep only last 20 timestamps
        while (pkgState.RecentTimestamps.Count > 20)
            pkgState.RecentTimestamps.RemoveAt(0);

        // Check if this attempt triggers suppression. Suppress() records the window and
        // the rule that opened it; the operator-facing message is composed at report time
        // from that rule plus the observed cause, so nothing is stored pre-formatted.
        EvaluateSuppressionThresholds(key, pkgState, version);

        SaveState();
    }

    /// <summary>
    /// Clears loop suppression for a specific package.
    /// Stamps a ClearedAt watermark so the cleared history is not re-counted by the
    /// next run's rebuild from events.jsonl — only installs that happen AFTER the
    /// clear accumulate toward suppression again. A still-looping package therefore
    /// re-suppresses after 3 fresh installs (as it should), but a clear is never
    /// silently undone by history.
    /// </summary>
    public bool ClearLoop(string packageName)
    {
        var key = packageName.ToLowerInvariant();
        if (_state.Packages.TryGetValue(key, out var pkgState))
        {
            ResetLoopHistory(pkgState, version: "", catalogFingerprint: null);
            SaveState();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears loop suppression for all packages.
    /// Stamps the root ClearedAt watermark for the same reason as ClearLoop: the
    /// old implementation reset state wholesale, which the next run promptly undid
    /// by re-counting 7 days of events.jsonl and re-tripping "3 installs across 3
    /// sessions" — making --clear-loop all (and any MDM-driven clear) a no-op.
    /// </summary>
    public int ClearAll()
    {
        var count = _state.Packages.Count(p => p.Value.SuppressedUntil.HasValue);
        _state = new LoopGuardState { ClearedAt = DateTime.UtcNow };
        SaveState();
        return count;
    }

    /// <summary>
    /// System boot time (UTC), injectable for tests. Default derives it from uptime.
    /// </summary>
    internal Func<DateTime> BootTimeUtcProvider { get; set; } =
        () => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    // Clock-skew fudge when comparing boot time against the pending-restart memo.
    private static readonly TimeSpan BootTimeSkewTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Records that a successful install has not yet converged because the item's
    /// restart_action finalizes it at the next reboot/logout. While the memo stands,
    /// ShouldDeferForRestart tells the engine to skip the reinstall instead of
    /// churning the installer every session until the machine restarts.
    /// </summary>
    public void RecordPendingRestart(string packageName, string version, string? catalogFingerprint = null)
    {
        if (_disabled || string.IsNullOrEmpty(packageName))
            return;

        var key = packageName.ToLowerInvariant();
        if (!_state.Packages.TryGetValue(key, out var pkgState))
        {
            pkgState = new PackageLoopState { PackageName = packageName };
            _state.Packages[key] = pkgState;
        }

        pkgState.PendingRestartSince = DateTime.UtcNow;
        pkgState.LastVersion = version;
        if (!string.IsNullOrEmpty(catalogFingerprint))
            pkgState.CatalogFingerprint = catalogFingerprint;
        SaveState();
    }

    /// <summary>
    /// Whether an install for this package should be deferred because a prior
    /// successful install is awaiting a reboot/logout to finalize. The memo clears
    /// itself when: the system boot time advances past it (the reboot happened —
    /// re-evaluate normally), the pkgsinfo fingerprint changes (admin shipped a
    /// change), or it ages past the LoopMaxTime cap (defensive: a logout-finalized
    /// item on a machine that never reboots, or drift such as a user uninstalling
    /// the app, must not be deferred forever).
    /// </summary>
    public (bool Defer, string Reason) ShouldDeferForRestart(string packageName, string? catalogFingerprint = null)
    {
        if (_isBootstrap || _disabled || string.IsNullOrEmpty(packageName))
            return (false, "");

        var key = packageName.ToLowerInvariant();
        if (!_state.Packages.TryGetValue(key, out var pkgState) || !pkgState.PendingRestartSince.HasValue)
            return (false, "");

        var since = pkgState.PendingRestartSince.Value;

        // Pkgsinfo changed since the memo was stamped — defer no longer applies.
        if (!string.IsNullOrEmpty(catalogFingerprint) && !string.IsNullOrEmpty(pkgState.CatalogFingerprint) &&
            !string.Equals(catalogFingerprint, pkgState.CatalogFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            pkgState.PendingRestartSince = null;
            SaveState();
            return (false, "");
        }

        // Reboot happened — clear the memo and let the normal installcheck decide.
        DateTime bootTime;
        try { bootTime = BootTimeUtcProvider(); }
        catch { bootTime = DateTime.MinValue; }
        if (bootTime - BootTimeSkewTolerance > since)
        {
            pkgState.PendingRestartSince = null;
            SaveState();
            return (false, "");
        }

        // Defensive age-out.
        if (DateTime.UtcNow - since > TimeSpan.FromDays(_maxSuppressionDays))
        {
            pkgState.PendingRestartSince = null;
            SaveState();
            return (false, "");
        }

        return (true, $"Pending restart: {packageName} v{pkgState.LastVersion} installed successfully and is finalized by a reboot — reinstall deferred until the machine restarts");
    }

    /// <summary>
    /// Marks a package whose successful install provably did not converge: the
    /// installcheck that scheduled the install still reports "action needed"
    /// immediately afterwards, so the pkgsinfo's detection criteria never match
    /// what the installer lays down. This is the root cause of the install-loop
    /// class — recording it here on the FIRST install replaces churning through
    /// 3+ sessions before the counting thresholds notice. Suppression re-probes
    /// after <paramref name="reprobeHours"/> (capped by LoopMaxTime) and, as with
    /// any suppression, auto-clears the moment the pkgsinfo fingerprint changes.
    /// Returns the reason recorded, for surfacing on the session's outcome.
    /// </summary>
    public string MarkNonConverged(string packageName, string version, string? catalogFingerprint, int reprobeHours = 24, InstallTrigger? trigger = null)
    {
        var hours = reprobeHours > 0 ? reprobeHours : 24;
        hours = Math.Min(hours, _maxSuppressionDays * 24);
        var reason = "installcheck still reported action needed immediately after a successful install";

        if (_disabled || string.IsNullOrEmpty(packageName))
            return reason;

        var key = packageName.ToLowerInvariant();
        if (!_state.Packages.TryGetValue(key, out var pkgState))
        {
            pkgState = new PackageLoopState { PackageName = packageName };
            _state.Packages[key] = pkgState;
        }

        pkgState.SuppressedUntil = DateTime.UtcNow.AddHours(hours);
        pkgState.SuppressionReason = reason;
        pkgState.LastVersion = version;
        NoteTrigger(pkgState, trigger, countIt: false);
        if (!string.IsNullOrEmpty(catalogFingerprint))
            pkgState.CatalogFingerprint = catalogFingerprint;
        SaveState();
        return reason;
    }

    private static DateTime? MaxNullable(DateTime? a, DateTime? b)
    {
        if (!a.HasValue) return b;
        if (!b.HasValue) return a;
        return a.Value > b.Value ? a : b;
    }

    /// <summary>
    /// Gets a summary of all currently suppressed packages.
    /// </summary>
    public List<(string Name, string Reason, DateTime? SuppressedUntil)> GetSuppressedPackages()
    {
        var result = new List<(string, string, DateTime?)>();
        foreach (var (key, pkgState) in _state.Packages)
        {
            if (pkgState.SuppressedUntil.HasValue &&
                (pkgState.SuppressedUntil.Value == DateTime.MaxValue || DateTime.UtcNow < pkgState.SuppressedUntil.Value))
            {
                result.Add((pkgState.PackageName, pkgState.SuppressionReason ?? "Unknown", pkgState.SuppressedUntil));
            }
        }
        return result;
    }

    /// <summary>
    /// Returns a report-ready list of currently suppressed packages, suitable for
    /// serialization into reports/loop_suppressed.json. Includes the last-attempted
    /// version and the operator-facing clear command for each entry.
    /// </summary>
    public List<LoopSuppressedReportItem> GetSuppressedReport()
    {
        var result = new List<LoopSuppressedReportItem>();
        foreach (var (_, pkgState) in _state.Packages)
        {
            if (!pkgState.SuppressedUntil.HasValue) continue;
            var until = pkgState.SuppressedUntil.Value;
            // Indefinite (DateTime.MaxValue) and not-yet-expired entries both qualify.
            if (until != DateTime.MaxValue && DateTime.UtcNow >= until) continue;

            result.Add(new LoopSuppressedReportItem
            {
                Name            = pkgState.PackageName,
                Version         = pkgState.LastVersion ?? "",
                Reason          = pkgState.SuppressionReason ?? "Unknown",
                SuppressedUntil = until == DateTime.MaxValue ? null : until,
                Trigger         = pkgState.Trigger,
                TriggerSummary  = DescribeTrigger(pkgState),
                ClearCommand    = $"managedsoftwareupdate --clear-loop {pkgState.PackageName}"
            });
        }
        return result;
    }

    /// <summary>
    /// Gets loop state for a specific package (for reporting/diagnostics).
    /// </summary>
    public PackageLoopState? GetPackageState(string packageName)
    {
        var key = packageName.ToLowerInvariant();
        return _state.Packages.TryGetValue(key, out var state) ? state : null;
    }

    #endregion

    #region Loop Analysis

    private (bool Suppress, string Reason) AnalyzeForLoop(string key, string packageName, string version)
    {
        if (!_state.Packages.TryGetValue(key, out var pkgState))
            return (false, "");

        return EvaluateSuppressionThresholds(key, pkgState, version);
    }

    /// <summary>
    /// Evaluates whether a package has hit suppression thresholds.
    /// Returns (true, reason) if the package should be suppressed.
    /// </summary>
    private (bool Suppress, string Reason) EvaluateSuppressionThresholds(string key, PackageLoopState pkgState, string version)
    {
        // Threshold 1: Rapid-fire — 3 installs within 2 hours
        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
        var recentCount = pkgState.RecentTimestamps.Count(t => t >= twoHoursAgo);
        if (recentCount >= 3)
        {
            return Suppress(pkgState, TimeSpan.FromHours(12),
                $"{recentCount} installs within 2 hours");
        }

        // Threshold 2: Same version reinstalled 3+ times across 3+ sessions
        if (!string.IsNullOrEmpty(version) &&
            pkgState.VersionAttempts.TryGetValue(version, out var versionCount) &&
            versionCount >= 3 && pkgState.SessionCount >= 3)
        {
            // Escalating backoff
            TimeSpan window;
            string reason;

            if (versionCount >= 8)
            {
                // Top tier — capped at 7 days (finite), then retries automatically
                window = TimeSpan.FromDays(_maxSuppressionDays);
                reason = $"installed {versionCount} times across {pkgState.SessionCount} sessions";
            }
            else if (versionCount >= 5)
            {
                window = TimeSpan.FromHours(24);
                reason = $"installed {versionCount} times across {pkgState.SessionCount} sessions";
            }
            else
            {
                window = TimeSpan.FromHours(6);
                reason = $"installed {versionCount} times across {pkgState.SessionCount} sessions";
            }

            return Suppress(pkgState, window, reason);
        }

        // Threshold 3: High total attempt count across sessions (any version).
        // The first attempt at each distinct version is a legitimate upgrade, not
        // evidence of a loop: a package that ships five builds in a week must not
        // be gagged for installing each of them once. Only attempts beyond the
        // first per version count here; a genuine loop (many attempts, one
        // version) is unaffected.
        var distinctVersions = pkgState.VersionAttempts.Count;
        var loopAttempts = distinctVersions > 1
            ? pkgState.AttemptCount - (distinctVersions - 1)
            : pkgState.AttemptCount;
        if (loopAttempts >= 8 && pkgState.SessionCount >= 5)
        {
            // Top tier — capped at 7 days (finite), then retries automatically
            return Suppress(pkgState, TimeSpan.FromDays(_maxSuppressionDays),
                $"{pkgState.AttemptCount} installs across {pkgState.SessionCount} sessions");
        }

        if (loopAttempts >= 5 && pkgState.SessionCount >= 4)
        {
            return Suppress(pkgState, TimeSpan.FromHours(24),
                $"{pkgState.AttemptCount} installs across {pkgState.SessionCount} sessions");
        }

        return (false, "");
    }

    /// <summary>
    /// Opens a suppression window, applying the escalation floor from
    /// <see cref="PackageLoopState.SuppressionCycles"/>.
    /// <para>
    /// The raw counters are retired when a window expires so the package actually gets
    /// retried; the cycle count is what remembers that it has been here before. One
    /// served window floors the next at 24h, two or more at the 7-day cap — so a
    /// genuinely-broken package converges on "3 installs a week, then quiet" instead of
    /// looping every 6 hours forever, while a fixed one (catalog change or explicit
    /// clear, both of which zero the cycles) starts from the bottom tier again.
    /// </para>
    /// </summary>
    private (bool Suppress, string Reason) Suppress(PackageLoopState pkgState, TimeSpan window, string reason)
    {
        var floor = pkgState.SuppressionCycles switch
        {
            <= 0 => TimeSpan.Zero,
            1 => TimeSpan.FromHours(24),
            _ => TimeSpan.FromDays(_maxSuppressionDays)
        };

        if (floor > window)
        {
            window = floor;
            reason += $" — escalated to {FormatDuration(window)} after {pkgState.SuppressionCycles} prior suppression window(s)";
        }

        pkgState.SuppressedUntil = DateTime.UtcNow + window;
        pkgState.SuppressionReason = reason;
        SaveState();

        // The run that trips a loop reports it the same way as every run after it —
        // otherwise the first (and most useful) warning is the only one that arrives
        // without the package name or how long it is paused for.
        return (true, BuildSuppressionMessage(pkgState.PackageName, pkgState.LastVersion ?? "", pkgState, window));
    }

    #endregion

    #region History Building

    /// <summary>
    /// Builds package history from events.jsonl files in the logs directory.
    /// Uses the same day-nested directory structure as SessionLogger:
    ///   logs/YYYY-MM-DD/HHMM/events.jsonl
    /// </summary>
    private void BuildHistoryFromEvents()
    {
        var logsDir = EffectiveLogsDir;
        if (!Directory.Exists(logsDir))
            return;

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var sessionsProcessed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Enumerate day directories (YYYY-MM-DD format)
            foreach (var dayDir in Directory.GetDirectories(logsDir).OrderByDescending(d => Path.GetFileName(d)))
            {
                var dayName = Path.GetFileName(dayDir);
                if (!DateTime.TryParseExact(dayName, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var dayDate))
                    continue;

                if (dayDate < cutoff.Date)
                    break; // Days are ordered descending, so remaining are older

                // Enumerate time directories within the day
                foreach (var sessionDir in Directory.GetDirectories(dayDir))
                {
                    var eventsPath = Path.Combine(sessionDir, "events.jsonl");
                    if (!File.Exists(eventsPath))
                        continue;

                    var sessionId = $"{dayName}/{Path.GetFileName(sessionDir)}";
                    if (!sessionsProcessed.Add(sessionId))
                        continue;

                    ProcessEventsFile(eventsPath, sessionId);
                }
            }

            // Also check legacy flat directory format
            foreach (var sessionDir in Directory.GetDirectories(logsDir))
            {
                var dirName = Path.GetFileName(sessionDir);
                // Skip day directories (already processed above)
                if (DateTime.TryParseExact(dirName, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out _))
                    continue;

                var eventsPath = Path.Combine(sessionDir, "events.jsonl");
                if (!File.Exists(eventsPath))
                    continue;

                if (!sessionsProcessed.Add(dirName))
                    continue;

                ProcessEventsFile(eventsPath, dirName);
            }
        }
        catch (Exception)
        {
            // If history building fails, continue with whatever state we loaded
        }
    }

    private void ProcessEventsFile(string eventsPath, string sessionId)
    {
        try
        {
            foreach (var line in File.ReadLines(eventsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var eventData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line, JsonLinesOptions);
                    if (eventData == null)
                        continue;

                    var action = eventData.TryGetValue("action", out var a) ? a.GetString() : "";
                    if (!string.Equals(action, "install", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var packageName =
                        (eventData.TryGetValue("package_name", out var pn) ? pn.GetString() : null) ??
                        (eventData.TryGetValue("package",      out var p)  ? p.GetString()  : null);
                    if (string.IsNullOrEmpty(packageName))
                        continue;

                    var status = eventData.TryGetValue("status", out var s) ? s.GetString() : "";
                    var version =
                        (eventData.TryGetValue("package_version", out var pv) ? pv.GetString() : null) ??
                        (eventData.TryGetValue("version",         out var v)  ? v.GetString()  : "");
                    var timestamp = eventData.TryGetValue("timestamp", out var ts) ? ts.GetString() : null;

                    var key = packageName.ToLowerInvariant();

                    if (!_state.Packages.TryGetValue(key, out var pkgState))
                    {
                        pkgState = new PackageLoopState { PackageName = packageName };
                        _state.Packages[key] = pkgState;
                    }

                    // Clear watermark: events older than the most recent clear
                    // (--clear-loop, per-package or all) are history that was
                    // deliberately discarded. Skip counting them — but still mark
                    // the session processed so they are never revisited. Without
                    // this gate, every clear was undone on the next run by this
                    // very rebuild re-counting the last 7 days of events.
                    var clearWatermark = MaxNullable(_state.ClearedAt, pkgState.ClearedAt);
                    DateTime? eventTime = DateTime.TryParse(timestamp, out var ts2)
                        ? ts2.ToUniversalTime()
                        : null;
                    var preClearHistory = clearWatermark.HasValue &&
                        (!eventTime.HasValue || eventTime.Value < clearWatermark.Value);

                    // Only count if not already tracked in state (avoid double-counting
                    // from both state file and events)
                    if (!pkgState.ProcessedSessions.Contains(sessionId) && !preClearHistory)
                    {
                        pkgState.AttemptCount++;
                        pkgState.LastSuccess = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);

                        if (!string.IsNullOrEmpty(version))
                        {
                            pkgState.LastVersion = version;
                            pkgState.VersionAttempts.TryGetValue(version, out var vc);
                            pkgState.VersionAttempts[version] = vc + 1;
                        }

                        if (eventTime.HasValue)
                        {
                            pkgState.RecentTimestamps.Add(eventTime.Value);
                            if (pkgState.LastAttempt == null || eventTime.Value > pkgState.LastAttempt)
                                pkgState.LastAttempt = eventTime.Value;
                        }
                    }

                    pkgState.ProcessedSessions.Add(sessionId);
                }
                catch
                {
                    // Skip malformed event lines
                }
            }

            // Update session counts
            foreach (var pkgState in _state.Packages.Values)
            {
                pkgState.SessionCount = pkgState.ProcessedSessions.Count;
            }
        }
        catch
        {
            // Skip unreadable event files
        }
    }

    #endregion

    #region State Persistence

    /// <summary>
    /// Replaces any legacy permanent suppression (DateTime.MaxValue, written before the
    /// finite cap existed) with a concrete window anchored on the package's last real
    /// attempt.
    /// <para>
    /// <see cref="ShouldSuppress"/> performs the same migration, but only for packages it
    /// is actually asked about — and a package is only asked about while its status check
    /// still reports NeedsAction. An item that converged, or that was dropped from the
    /// manifest, is never evaluated again, so its MaxValue entry was unreachable by that
    /// path and stayed permanent forever. Doing it at load covers every entry in the file
    /// exactly once per run, whatever the manifest happens to contain today.
    /// </para>
    /// </summary>
    private LoopGuardState CapIndefiniteWindows(LoopGuardState state)
    {
        foreach (var (_, pkgState) in state.Packages)
        {
            if (pkgState.SuppressedUntil == DateTime.MaxValue)
            {
                pkgState.SuppressedUntil = pkgState.LastAttempt.GetValueOrDefault().AddDays(_maxSuppressionDays);
            }
        }
        return state;
    }

    private LoopGuardState LoadState()
    {
        var path = EffectiveStatePath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);

                // Try reading as the new CimianState wrapper first
                var wrapper = JsonSerializer.Deserialize<CimianState>(json, JsonOptions);
                if (wrapper?.LoopGuard != null)
                    return CapIndefiniteWindows(wrapper.LoopGuard);

                // Fall back to reading as bare LoopGuardState (legacy state.json or test)
                var state = JsonSerializer.Deserialize<LoopGuardState>(json, JsonOptions);
                if (state != null && state.Packages.Count > 0)
                    return CapIndefiniteWindows(state);
            }

            // Migrate from legacy loop_state.json if it exists
            var legacyPath = EffectiveLegacyStatePath;
            if (legacyPath != null && File.Exists(legacyPath))
            {
                var json = File.ReadAllText(legacyPath);
                var state = JsonSerializer.Deserialize<LoopGuardState>(json, JsonOptions);
                if (state != null)
                {
                    // Save to new location and remove legacy file
                    _state = state;
                    SaveState();
                    try { File.Delete(legacyPath); } catch { }
                    return state;
                }
            }
        }
        catch
        {
            // If state file is corrupt, start fresh
        }

        return new LoopGuardState();
    }

    private void SaveState()
    {
        var path = EffectiveStatePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var wrapper = new CimianState { LoopGuard = _state };
            _state.LastUpdated = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(wrapper, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort persistence — if it fails, we still have in-memory state
        }
    }

    private string? EffectiveLegacyStatePath
    {
        get
        {
            // In test mode, no legacy path
            if (StatePath_Override != null) return null;
            return LegacyStatePath;
        }
    }

    #endregion

    #region Cache Analysis

    /// <summary>
    /// Checks if a package has a cached installer, indicating repeated downloads.
    /// Returns a cache signal that supplements the events-based loop detection.
    /// If a cached file exists for a looping package, the loop is an install/status-check
    /// issue, not a download issue (no bandwidth waste on re-download).
    /// </summary>
    public (bool HasCache, string? CachePath) CheckCacheForPackage(string packageName)
    {
        var cacheDir = EffectiveCacheDir;
        if (!Directory.Exists(cacheDir))
            return (false, null);

        try
        {
            // Cache uses package name as subdirectory
            var packageCacheDir = Path.Combine(cacheDir, packageName);
            if (Directory.Exists(packageCacheDir))
            {
                var files = Directory.GetFiles(packageCacheDir);
                if (files.Length > 0)
                    return (true, files[0]);
            }

            // Also check flat cache (file named after package)
            foreach (var file in Directory.GetFiles(cacheDir))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (string.Equals(fileName, packageName, StringComparison.OrdinalIgnoreCase))
                    return (true, file);
            }
        }
        catch
        {
            // Cache check is supplementary — don't fail on errors
        }

        return (false, null);
    }

    /// <summary>
    /// Enriches the suppression reason with cache information for diagnostics.
    /// </summary>
    public string GetDiagnosticInfo(string packageName)
    {
        var key = packageName.ToLowerInvariant();
        if (!_state.Packages.TryGetValue(key, out var pkgState))
            return $"{packageName}: no loop history";

        var lines = new List<string>
        {
            $"{packageName}:",
            $"  Attempts: {pkgState.AttemptCount} across {pkgState.SessionCount} sessions",
            $"  Last version: {pkgState.LastVersion ?? "(unknown)"}",
            $"  Catalog fingerprint: {pkgState.CatalogFingerprint ?? "(none)"}",
            $"  Suppression cycles served: {pkgState.SuppressionCycles}",
            $"  Last attempt: {pkgState.LastAttempt?.ToString("g") ?? "never"}",
            $"  Last success: {pkgState.LastSuccess}"
        };

        if (pkgState.VersionAttempts.Count > 0)
        {
            lines.Add($"  Versions attempted: {string.Join(", ", pkgState.VersionAttempts.Select(v => $"{v.Key} ({v.Value}x)"))}");
        }

        lines.Add($"  Needs install because {DescribeTrigger(pkgState)}");
        if (pkgState.TriggerLastSeen.HasValue)
            lines.Add($"  Trigger last seen: {pkgState.TriggerLastSeen.Value.ToString("g")}");

        var (hasCache, cachePath) = CheckCacheForPackage(packageName);
        if (hasCache)
        {
            lines.Add($"  Cache: HIT — {cachePath}");
            lines.Add($"  Diagnosis: Loop is install/status-check issue, not download (cached installer exists)");
        }
        else
        {
            lines.Add($"  Cache: MISS — package not cached");
        }

        if (pkgState.SuppressedUntil.HasValue)
        {
            var until = pkgState.SuppressedUntil.Value == DateTime.MaxValue
                ? "indefinite"
                : pkgState.SuppressedUntil.Value.ToString("g");
            lines.Add($"  Suppressed until: {until}");
            lines.Add($"  Reason: {pkgState.SuppressionReason}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    #endregion

    #region Helpers

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{duration.TotalDays:F0}d {duration.Hours}h";
        if (duration.TotalHours >= 1)
            return $"{duration.TotalHours:F0}h {duration.Minutes}m";
        return $"{duration.TotalMinutes:F0}m";
    }

    #endregion
}

#region State Models

/// <summary>
/// Top-level state file structure for reports/state.json.
/// Contains LoopGuard data and is extensible for future state sections.
/// </summary>
public class CimianState
{
    [JsonPropertyName("loop_guard")]
    public LoopGuardState LoopGuard { get; set; } = new();
}

/// <summary>
/// Root state for LoopGuard persistence
/// </summary>
public class LoopGuardState
{
    [JsonPropertyName("last_updated")]
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Watermark stamped by ClearAll(). History rebuilds ignore install events older
    /// than this, so a fleet-wide clear is not silently undone by the next run
    /// re-counting the last 7 days of events.jsonl.
    /// </summary>
    [JsonPropertyName("cleared_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ClearedAt { get; set; }

    [JsonPropertyName("packages")]
    public Dictionary<string, PackageLoopState> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Per-package loop tracking state
/// </summary>
public class PackageLoopState
{
    [JsonPropertyName("package_name")]
    public string PackageName { get; set; } = string.Empty;

    [JsonPropertyName("attempt_count")]
    public int AttemptCount { get; set; }

    [JsonPropertyName("session_count")]
    public int SessionCount { get; set; }

    [JsonPropertyName("last_attempt")]
    public DateTime? LastAttempt { get; set; }

    [JsonPropertyName("last_version")]
    public string? LastVersion { get; set; }

    /// <summary>
    /// SHA256 fingerprint of the catalog item's install-behavior fields.
    /// Used for auto-clear: if fingerprint changes, the pkgsinfo was modified
    /// (version, installcheck_script, hash, installs array, scripts, etc.).
    /// </summary>
    [JsonPropertyName("catalog_fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogFingerprint { get; set; }

    [JsonPropertyName("last_success")]
    public bool LastSuccess { get; set; }

    [JsonPropertyName("suppressed_until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? SuppressedUntil { get; set; }

    [JsonPropertyName("suppression_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SuppressionReason { get; set; }

    [JsonPropertyName("version_attempts")]
    public Dictionary<string, int> VersionAttempts { get; set; } = new();

    [JsonPropertyName("recent_timestamps")]
    public List<DateTime> RecentTimestamps { get; set; } = new();

    /// <summary>
    /// Tracks which session IDs have been processed to avoid double-counting
    /// when rebuilding from events.jsonl
    /// </summary>
    [JsonPropertyName("processed_sessions")]
    public HashSet<string> ProcessedSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Number of suppression windows this package has already served and exhausted.
    /// Expiring a window resets the raw counters (so the package genuinely retries
    /// instead of re-tripping instantly on the same history), and this survives that
    /// reset to keep the backoff escalating: 1 prior cycle floors the next window at
    /// 24h, 2+ floors it at the 7-day cap. A catalog change or an explicit clear
    /// resets it to zero — that is a fix, and a fixed package starts clean.
    /// </summary>
    [JsonPropertyName("suppression_cycles")]
    public int SuppressionCycles { get; set; }

    /// <summary>
    /// Watermark stamped by ClearLoop(). Same contract as LoopGuardState.ClearedAt
    /// but scoped to this package: install events older than this are never
    /// re-counted, so a per-package clear survives history rebuilds.
    /// </summary>
    [JsonPropertyName("cleared_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ClearedAt { get; set; }

    /// <summary>
    /// Set when a successful install did not converge (installcheck still reports
    /// action needed) and the item's restart_action explains why — the install is
    /// finalized by a reboot/logout. While set, reinstalls are deferred instead of
    /// churning every session until the machine restarts. Cleared when the system
    /// boot time advances past this timestamp, when the pkgsinfo changes, or when
    /// the memo ages out (defensive cap — see ShouldDeferForRestart).
    /// </summary>
    [JsonPropertyName("pending_restart_since")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PendingRestartSince { get; set; }

    /// <summary>
    /// The check that decided this package needed to run, as of the most recent
    /// evaluation. This is the diagnosis: the counting rule says a loop exists, the
    /// trigger says which installs entry, installcheck_script or product code never
    /// converges. Refreshed on every attempt AND on every suppressed evaluation, so
    /// the warning reports what the package still wants today rather than what it
    /// wanted when the window opened.
    /// </summary>
    [JsonPropertyName("trigger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstallTrigger? Trigger { get; set; }

    /// <summary>When <see cref="Trigger"/> was last observed.</summary>
    [JsonPropertyName("trigger_last_seen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? TriggerLastSeen { get; set; }

    /// <summary>
    /// Distinct triggers seen across the attempts that built the current history,
    /// keyed by <see cref="InstallTrigger.Key"/> and counted. One entry with a count
    /// equal to the attempt count is a stuck detection criterion (the pkgsinfo is
    /// wrong); several entries mean the machine keeps changing underneath the item.
    /// Bounded — a package with more distinct triggers than this is already diagnosed.
    /// </summary>
    [JsonPropertyName("trigger_counts")]
    public Dictionary<string, int> TriggerCounts { get; set; } = new(StringComparer.Ordinal);
}

#endregion
