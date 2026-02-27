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
/// Auto-clear: when ANY install-behavior field in the pkgsinfo changes (version,
/// installcheck_script, installs array, hash, scripts, etc.), suppression is automatically
/// cleared — the root cause may have been fixed. Change detection uses a SHA256 fingerprint
/// of all install-behavior fields, computed by the caller and passed as catalogFingerprint.
///
/// Backoff strategy:
///   3+ installs of same version across 3+ sessions → suppress 6 hours
///   5+ installs across 5+ sessions → suppress 24 hours
///   8+ installs → suppress indefinitely until manual clear
///   3 installs within 2 hours (rapid-fire) → suppress 12 hours
///
/// State persisted to: C:\ProgramData\ManagedInstalls\reports\state.json
/// Clear with: managedsoftwareupdate --clear-loop (name or all)
/// </summary>
public class LoopGuard
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls");

    private static readonly string ReportsDir = Path.Combine(StateDir, "reports");

    private static readonly string StatePath = Path.Combine(ReportsDir, "state.json");

    // Legacy path for migration from older versions
    private static readonly string LegacyStatePath = Path.Combine(ReportsDir, "loop_state.json");

    private static readonly string LogsDir = Path.Combine(StateDir, "logs");

    private static readonly string CacheDir = Path.Combine(StateDir, "Cache");

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

    /// <summary>
    /// Creates a new LoopGuard. If isBootstrap is true, suppression is disabled
    /// to avoid blocking first-run provisioning.
    /// </summary>
    public LoopGuard(bool isBootstrap = false)
    {
        _isBootstrap = isBootstrap;
        _state = LoadState();
        BuildHistoryFromEvents();
    }

    /// <summary>
    /// For unit testing — constructor that takes custom paths.
    /// </summary>
    internal LoopGuard(string statePath, string logsDir, bool isBootstrap = false, string? cacheDir = null)
    {
        _isBootstrap = isBootstrap;
        StatePath_Override = statePath;
        LogsDir_Override = logsDir;
        CacheDir_Override = cacheDir;
        _state = LoadState();
        BuildHistoryFromEvents();
    }

    /// <summary>
    /// Computes a SHA256 fingerprint from the install-behavior fields of a catalog item.
    /// The caller builds the input string by concatenating all fields that affect whether
    /// an install succeeds or loops. If ANY field changes, the fingerprint changes and
    /// LoopGuard auto-clears suppression.
    ///
    /// Recommended fields to include (pipe-delimited):
    ///   version | installcheck_script | installs (JSON) | check (JSON) |
    ///   installer hash | installer url | installer type |
    ///   install_script | postinstall_script | preinstall_script
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
    /// </summary>
    public (bool Suppress, string Reason) ShouldSuppress(string packageName, string version, string? catalogFingerprint = null)
    {
        // Never suppress during bootstrap — first-run provisioning must complete
        if (_isBootstrap)
            return (false, "");

        if (string.IsNullOrEmpty(packageName))
            return (false, "");

        var key = packageName.ToLowerInvariant();

        // Check explicit suppression state first (from previous runs)
        if (_state.Packages.TryGetValue(key, out var pkgState))
        {
            if (pkgState.SuppressedUntil.HasValue)
            {
                // Auto-clear: if the catalog fingerprint changed, ANY install-behavior
                // field in the pkgsinfo was updated — root cause may be fixed.
                // Falls back to version-only comparison if no fingerprint is available.
                bool catalogChanged = false;
                string changeDetail = "";

                if (!string.IsNullOrEmpty(catalogFingerprint) && !string.IsNullOrEmpty(pkgState.CatalogFingerprint))
                {
                    // Fingerprint comparison — covers version, scripts, hash, installs, etc.
                    if (!string.Equals(catalogFingerprint, pkgState.CatalogFingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        catalogChanged = true;
                        changeDetail = !string.Equals(version, pkgState.LastVersion, StringComparison.OrdinalIgnoreCase)
                            ? $"catalog changed (version {pkgState.LastVersion} → {version})"
                            : $"catalog changed (pkgsinfo fields updated, same version {version})";
                    }
                }
                else if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(pkgState.LastVersion) &&
                         !string.Equals(version, pkgState.LastVersion, StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback: version-only comparison when fingerprint not available
                    catalogChanged = true;
                    changeDetail = $"catalog version changed from {pkgState.LastVersion} to {version}";
                }

                if (catalogChanged)
                {
                    pkgState.SuppressedUntil = null;
                    pkgState.SuppressionReason = null;
                    pkgState.AttemptCount = 0;
                    pkgState.SessionCount = 0;
                    pkgState.VersionAttempts.Clear();
                    pkgState.RecentTimestamps.Clear();
                    pkgState.LastVersion = version;
                    pkgState.CatalogFingerprint = catalogFingerprint;
                    SaveState();
                    return (false, $"Auto-cleared: {changeDetail}");
                }

                if (pkgState.SuppressedUntil.Value == DateTime.MaxValue)
                {
                    // Indefinite suppression
                    return (true, $"LOOP SUPPRESSED: {packageName} — indefinitely suppressed after {pkgState.AttemptCount} install attempts. Clear with: managedsoftwareupdate --clear-loop {packageName}");
                }

                if (DateTime.UtcNow < pkgState.SuppressedUntil.Value)
                {
                    var remaining = pkgState.SuppressedUntil.Value - DateTime.UtcNow;
                    return (true, $"LOOP SUPPRESSED: {packageName} — suppressed for {FormatDuration(remaining)} ({pkgState.SuppressionReason}). Clear with: managedsoftwareupdate --clear-loop {packageName}");
                }

                // Suppression expired — clear it but keep history
                pkgState.SuppressedUntil = null;
                pkgState.SuppressionReason = null;
                SaveState();
            }
        }

        // Analyze current history for new loop conditions
        return AnalyzeForLoop(key, packageName, version);
    }

    /// <summary>
    /// Records an install attempt (call after InstallerService.InstallAsync completes).
    /// catalogFingerprint should match what was passed to ShouldSuppress for consistency.
    /// </summary>
    public void RecordAttempt(string packageName, string version, bool success, string? catalogFingerprint = null)
    {
        if (string.IsNullOrEmpty(packageName))
            return;

        var key = packageName.ToLowerInvariant();

        if (!_state.Packages.TryGetValue(key, out var pkgState))
        {
            pkgState = new PackageLoopState { PackageName = packageName };
            _state.Packages[key] = pkgState;
        }

        pkgState.AttemptCount++;
        pkgState.LastAttempt = DateTime.UtcNow;
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

        // Check if this attempt triggers suppression
        var (suppress, reason) = EvaluateSuppressionThresholds(key, pkgState, version);
        if (suppress)
        {
            // Suppression will take effect on the NEXT run, not this one
            // (current run already committed to installing)
            pkgState.SuppressionReason = reason;
        }

        SaveState();
    }

    /// <summary>
    /// Clears loop suppression for a specific package.
    /// </summary>
    public bool ClearLoop(string packageName)
    {
        var key = packageName.ToLowerInvariant();
        if (_state.Packages.TryGetValue(key, out var pkgState))
        {
            pkgState.SuppressedUntil = null;
            pkgState.SuppressionReason = null;
            pkgState.AttemptCount = 0;
            pkgState.SessionCount = 0;
            pkgState.VersionAttempts.Clear();
            pkgState.RecentTimestamps.Clear();
            SaveState();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears loop suppression for all packages.
    /// </summary>
    public int ClearAll()
    {
        var count = _state.Packages.Count(p => p.Value.SuppressedUntil.HasValue);
        _state = new LoopGuardState();
        SaveState();
        return count;
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
            var suppressUntil = DateTime.UtcNow.AddHours(12);
            pkgState.SuppressedUntil = suppressUntil;
            var reason = $"Rapid-fire loop: {recentCount} installs within 2 hours";
            pkgState.SuppressionReason = reason;
            SaveState();
            return (true, reason);
        }

        // Threshold 2: Same version reinstalled 3+ times across 3+ sessions
        if (!string.IsNullOrEmpty(version) &&
            pkgState.VersionAttempts.TryGetValue(version, out var versionCount) &&
            versionCount >= 3 && pkgState.SessionCount >= 3)
        {
            // Escalating backoff
            DateTime suppressUntil;
            string reason;

            if (versionCount >= 8)
            {
                // Indefinite — requires manual clear
                suppressUntil = DateTime.MaxValue;
                reason = $"Persistent loop: version {version} installed {versionCount} times across {pkgState.SessionCount} sessions (indefinite)";
            }
            else if (versionCount >= 5)
            {
                suppressUntil = DateTime.UtcNow.AddHours(24);
                reason = $"Escalated loop: version {version} installed {versionCount} times across {pkgState.SessionCount} sessions (24h suppression)";
            }
            else
            {
                suppressUntil = DateTime.UtcNow.AddHours(6);
                reason = $"Install loop: version {version} installed {versionCount} times across {pkgState.SessionCount} sessions (6h suppression)";
            }

            pkgState.SuppressedUntil = suppressUntil;
            pkgState.SuppressionReason = reason;
            SaveState();
            return (true, reason);
        }

        // Threshold 3: High total attempt count across sessions (any version)
        if (pkgState.AttemptCount >= 8 && pkgState.SessionCount >= 5)
        {
            var suppressUntil = DateTime.MaxValue;
            var reason = $"Persistent loop: {pkgState.AttemptCount} total installs across {pkgState.SessionCount} sessions (indefinite)";
            pkgState.SuppressedUntil = suppressUntil;
            pkgState.SuppressionReason = reason;
            SaveState();
            return (true, reason);
        }

        if (pkgState.AttemptCount >= 5 && pkgState.SessionCount >= 4)
        {
            var suppressUntil = DateTime.UtcNow.AddHours(24);
            var reason = $"Escalated loop: {pkgState.AttemptCount} total installs across {pkgState.SessionCount} sessions (24h suppression)";
            pkgState.SuppressedUntil = suppressUntil;
            pkgState.SuppressionReason = reason;
            SaveState();
            return (true, reason);
        }

        return (false, "");
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

                    var packageName = eventData.TryGetValue("package", out var p) ? p.GetString() : null;
                    if (string.IsNullOrEmpty(packageName))
                        continue;

                    var status = eventData.TryGetValue("status", out var s) ? s.GetString() : "";
                    var version = eventData.TryGetValue("version", out var v) ? v.GetString() : "";
                    var timestamp = eventData.TryGetValue("timestamp", out var ts) ? ts.GetString() : null;

                    var key = packageName.ToLowerInvariant();

                    if (!_state.Packages.TryGetValue(key, out var pkgState))
                    {
                        pkgState = new PackageLoopState { PackageName = packageName };
                        _state.Packages[key] = pkgState;
                    }

                    // Only count if not already tracked in state (avoid double-counting
                    // from both state file and events)
                    if (!pkgState.ProcessedSessions.Contains(sessionId))
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

                        if (DateTime.TryParse(timestamp, out var ts2))
                        {
                            pkgState.RecentTimestamps.Add(ts2.ToUniversalTime());
                            if (pkgState.LastAttempt == null || ts2.ToUniversalTime() > pkgState.LastAttempt)
                                pkgState.LastAttempt = ts2.ToUniversalTime();
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
                    return wrapper.LoopGuard;

                // Fall back to reading as bare LoopGuardState (legacy state.json or test)
                var state = JsonSerializer.Deserialize<LoopGuardState>(json, JsonOptions);
                if (state != null && state.Packages.Count > 0)
                    return state;
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
            $"  Last attempt: {pkgState.LastAttempt?.ToString("g") ?? "never"}",
            $"  Last success: {pkgState.LastSuccess}"
        };

        if (pkgState.VersionAttempts.Count > 0)
        {
            lines.Add($"  Versions attempted: {string.Join(", ", pkgState.VersionAttempts.Select(v => $"{v.Key} ({v.Value}x)"))}");
        }

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
}

#endregion
