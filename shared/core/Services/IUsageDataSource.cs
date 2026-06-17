// IUsageDataSource.cs - Abstraction over per-device application usage data
// Backs the unused-software removal feature (unused_software_removal_info):
// the engine asks "when was this executable last used on this device?" and
// refuses to act when the source cannot answer confidently.

namespace Cimian.Core.Services;

/// <summary>
/// Provides device-wide application usage answers for stale-usage removal.
/// Implementations aggregate across all users on the device — a package is
/// only "untouched" when NO user has used it. All methods must fail safe:
/// absence of data is never evidence of disuse.
/// </summary>
public interface IUsageDataSource
{
    /// <summary>
    /// True when the source is present and responsive on this device.
    /// False means the entire stale-usage pass should be skipped.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable source name for logging and reports.</summary>
    string SourceName { get; }

    /// <summary>
    /// Returns the most recent observed usage timestamp (UTC) for the
    /// executable across all users on this device. Path comparison is
    /// case-insensitive. Returns false when the executable has no record —
    /// callers must treat that as "no signal", not "unused".
    /// </summary>
    bool TryGetLastUsed(string executablePath, out DateTime lastUsedUtc);

    /// <summary>
    /// Calendar days of usage history this source can attest to on this
    /// device (earliest record to today). Gates against acting on freshly
    /// imaged machines that simply have no history yet.
    /// </summary>
    int GetHistoryDays();

    /// <summary>
    /// Days since the source last recorded anything (any user, any app).
    /// Stale telemetry — e.g. no logons for weeks — must not drive
    /// uninstalls; callers skip the pass when this exceeds their threshold.
    /// </summary>
    int GetDataFreshnessDays();
}

/// <summary>
/// Null implementation used when stale-usage removal is disabled or no
/// usage source is configured. Reports unavailable and returns no data,
/// so every caller takes its fail-safe path.
/// </summary>
public sealed class NoOpUsageDataSource : IUsageDataSource
{
    public bool IsAvailable => false;

    public string SourceName => "none";

    public bool TryGetLastUsed(string executablePath, out DateTime lastUsedUtc)
    {
        lastUsedUtc = default;
        return false;
    }

    public int GetHistoryDays() => 0;

    public int GetDataFreshnessDays() => int.MaxValue;
}
