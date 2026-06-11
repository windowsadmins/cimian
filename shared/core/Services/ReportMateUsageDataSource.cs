// ReportMateUsageDataSource.cs - IUsageDataSource backed by ReportMate usagetracker
// Reads the per-user JSON files written by ReportMate's usagetracker.exe
// companion (reportmate-client-win). Each logged-in user gets one file at
// %ProgramData%\ManagedReports\usagetracker\{username}.json holding
// per-(exe path, local date) cumulative foreground/active counters.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cimian.Core.Services;

/// <summary>
/// Usage data source backed by ReportMate usagetracker per-user JSON files.
/// Answers are device-wide: the last-used timestamp for an executable is the
/// MAX across every user's file, so a package one user runs daily is never
/// considered untouched because another user ignores it.
///
/// Granularity is one day — usagetracker keys counters by local date, so
/// "last used" resolves to local midnight of the most recent day with
/// non-zero foreground or active seconds. That is exact enough for
/// thresholds measured in tens of days.
///
/// All parsing is defensive: a malformed or mid-rotation file degrades to
/// "no data from that user" (logged), never an exception to the caller.
/// </summary>
public sealed class ReportMateUsageDataSource : IUsageDataSource
{
    /// <summary>Default usagetracker state directory on a managed device.</summary>
    public static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedReports", "usagetracker");

    private readonly string _directory;
    private readonly ILogger? _logger;
    private readonly Lazy<Snapshot> _snapshot;

    public ReportMateUsageDataSource(string? directory = null, ILogger? logger = null)
    {
        _directory = string.IsNullOrEmpty(directory) ? DefaultDirectory : directory;
        _logger = logger;
        // One scan per agent run: the files are tiny (KBs) and the engine
        // asks about many packages, so parse everything once and serve
        // lookups from memory.
        _snapshot = new Lazy<Snapshot>(LoadSnapshot);
    }

    public bool IsAvailable => _snapshot.Value.FilesParsed > 0;

    public string SourceName => "reportmate-usagetracker";

    public bool TryGetLastUsed(string executablePath, out DateTime lastUsedUtc)
    {
        lastUsedUtc = default;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var snap = _snapshot.Value;

        // Exact path match first (usagetracker keys by absolute exe path).
        if (snap.LastUsedByExePath.TryGetValue(executablePath, out lastUsedUtc))
        {
            return true;
        }

        // Fall back to basename matching: pkginfo authors may record a path
        // under Program Files while the tracker observed Program Files (x86),
        // or per-user installs under AppData. The basename is stable across
        // those layouts. MAX across all matches keeps the device-wide rule.
        var baseName = Path.GetFileName(executablePath);
        if (!string.IsNullOrEmpty(baseName) &&
            snap.LastUsedByExeName.TryGetValue(baseName, out lastUsedUtc))
        {
            return true;
        }

        lastUsedUtc = default;
        return false;
    }

    public int GetHistoryDays()
    {
        var earliest = _snapshot.Value.EarliestRecordUtc;
        if (earliest is null)
        {
            return 0;
        }

        var days = (int)(DateTime.UtcNow - earliest.Value).TotalDays;
        return Math.Max(0, days);
    }

    public int GetDataFreshnessDays()
    {
        var latest = _snapshot.Value.LatestWriteUtc;
        if (latest is null)
        {
            return int.MaxValue;
        }

        var age = DateTime.UtcNow - latest.Value;

        // A write stamp from the future means clock skew or tampering. Up to
        // a day of skew reads as "fresh now"; beyond that the telemetry is
        // not trustworthy, and untrustworthy must mean "too stale to act on",
        // never "perfectly fresh".
        if (age < TimeSpan.FromDays(-1))
        {
            _logger?.LogWarning(
                "Usage data write stamp is {Hours:F0}h in the future - treating as invalid",
                -age.TotalHours);
            return int.MaxValue;
        }

        return Math.Max(0, (int)age.TotalDays);
    }

    // ─────────────────────────── parsing ───────────────────────────

    private sealed class Snapshot
    {
        public Dictionary<string, DateTime> LastUsedByExePath { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTime> LastUsedByExeName { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public DateTime? EarliestRecordUtc { get; set; }
        public DateTime? LatestWriteUtc { get; set; }
        public int FilesParsed { get; set; }
    }

    // Mirrors usagetracker's TrackerState. Field semantics:
    //   ByAppByDate[absolute exe path][yyyy-MM-dd local date] = counters
    private sealed class TrackerState
    {
        public string? Username { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? LastUpdatedAt { get; set; }
        public Dictionary<string, Dictionary<string, AppDayCounters>>? ByAppByDate { get; set; }
    }

    private sealed class AppDayCounters
    {
        public double ForegroundSeconds { get; set; }
        public double ActiveSeconds { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private Snapshot LoadSnapshot()
    {
        var snap = new Snapshot();

        string[] files;
        try
        {
            if (!Directory.Exists(_directory))
            {
                _logger?.LogDebug("Usage source directory not found: {Directory}", _directory);
                return snap;
            }
            files = Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cannot enumerate usage source directory {Directory}", _directory);
            return snap;
        }

        foreach (var file in files)
        {
            try
            {
                ParseUserFile(file, snap);
                snap.FilesParsed++;
            }
            catch (Exception ex)
            {
                // One unreadable user file (torn write, schema drift) must not
                // poison the others — and absolutely must not become "the app
                // is unused".
                _logger?.LogWarning(ex, "Skipping unreadable usage file {File}", Path.GetFileName(file));
            }
        }

        return snap;
    }

    private void ParseUserFile(string file, Snapshot snap)
    {
        var state = JsonSerializer.Deserialize<TrackerState>(File.ReadAllText(file), JsonOptions);
        if (state is null)
        {
            return;
        }

        // Freshness: prefer the writer's own stamp; fall back to file mtime
        // so a schema without LastUpdatedAt still yields a usable signal.
        var write = state.LastUpdatedAt?.ToUniversalTime()
                    ?? File.GetLastWriteTimeUtc(file);
        if (snap.LatestWriteUtc is null || write > snap.LatestWriteUtc)
        {
            snap.LatestWriteUtc = write;
        }

        // History floor: a fresh file with no usage rows still attests that
        // tracking has been running since StartedAt.
        var started = state.StartedAt?.ToUniversalTime();
        if (started is not null &&
            (snap.EarliestRecordUtc is null || started < snap.EarliestRecordUtc))
        {
            snap.EarliestRecordUtc = started;
        }

        if (state.ByAppByDate is null)
        {
            return;
        }

        foreach (var (exePath, byDate) in state.ByAppByDate)
        {
            if (string.IsNullOrWhiteSpace(exePath) || byDate is null)
            {
                continue;
            }

            foreach (var (dateKey, counters) in byDate)
            {
                if (!DateTime.TryParseExact(dateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var localDate))
                {
                    continue;
                }

                var dayUtc = DateTime.SpecifyKind(localDate, DateTimeKind.Local).ToUniversalTime();

                if (snap.EarliestRecordUtc is null || dayUtc < snap.EarliestRecordUtc)
                {
                    snap.EarliestRecordUtc = dayUtc;
                }

                // A date row with all-zero counters is bookkeeping, not usage.
                if (counters is null ||
                    (counters.ForegroundSeconds <= 0 && counters.ActiveSeconds <= 0))
                {
                    continue;
                }

                Bump(snap.LastUsedByExePath, exePath, dayUtc);
                var baseName = Path.GetFileName(exePath);
                if (!string.IsNullOrEmpty(baseName))
                {
                    Bump(snap.LastUsedByExeName, baseName, dayUtc);
                }
            }
        }
    }

    private static void Bump(Dictionary<string, DateTime> map, string key, DateTime candidateUtc)
    {
        if (!map.TryGetValue(key, out var existing) || candidateUtc > existing)
        {
            map[key] = candidateUtc;
        }
    }
}
