// StaleUsageEvaluator.cs - per-package stale-usage removal decision
// Pure decision logic for "should this installed package be uninstalled
// because nobody has used it" — separated from UpdateEngine so the guard
// ladder is unit-testable without registry or manifest plumbing.

using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

public enum StaleUsageOutcome
{
    /// <summary>days_untouched_before_uninstall absent or &lt;= 0.</summary>
    NotOptedIn,
    /// <summary>Package has not approved silent removal (unattended_uninstall false).</summary>
    NotUnattended,
    /// <summary>No uninstall method defined — nothing the engine could run.</summary>
    NotUninstallable,
    /// <summary>No usage_tracked_paths and no .exe in the installs array.</summary>
    NoTrackedExecutables,
    /// <summary>Device has less usage history than the package (or global) minimum.</summary>
    InsufficientHistory,
    /// <summary>No tracked executable has any usage record — absence is not disuse.</summary>
    NoUsageData,
    /// <summary>Used within the threshold; keep.</summary>
    RecentlyUsed,
    /// <summary>All tracked executables untouched past the threshold; uninstall.</summary>
    Stale,
}

/// <param name="Outcome">The decision.</param>
/// <param name="DaysSinceLastUsed">Days since the newest usage across tracked exes (-1 when no data).</param>
/// <param name="ThresholdDays">The package's days_untouched_before_uninstall.</param>
public sealed record StaleUsageDecision(
    StaleUsageOutcome Outcome,
    double DaysSinceLastUsed = -1,
    int ThresholdDays = 0);

/// <summary>
/// Evaluates one catalog item against a usage data source. Every ambiguous
/// state resolves to a non-removal outcome: only a package that opted in,
/// can be silently uninstalled, has enough device history, and has a real
/// usage record older than its threshold comes back <see cref="StaleUsageOutcome.Stale"/>.
/// </summary>
public static class StaleUsageEvaluator
{
    public static StaleUsageDecision Evaluate(
        CatalogItem item,
        IUsageDataSource usage,
        int globalMinimumHistoryDays)
    {
        var threshold = item.DaysUntouchedBeforeUninstall ?? 0;
        if (threshold <= 0)
        {
            return new StaleUsageDecision(StaleUsageOutcome.NotOptedIn);
        }

        if (!item.UnattendedUninstall)
        {
            return new StaleUsageDecision(StaleUsageOutcome.NotUnattended, ThresholdDays: threshold);
        }

        if (!item.IsUninstallable())
        {
            return new StaleUsageDecision(StaleUsageOutcome.NotUninstallable, ThresholdDays: threshold);
        }

        var trackedPaths = ResolveTrackedExecutables(item);
        if (trackedPaths.Count == 0)
        {
            return new StaleUsageDecision(StaleUsageOutcome.NoTrackedExecutables, ThresholdDays: threshold);
        }

        var minHistory = item.MinimumUsageHistoryDays ?? globalMinimumHistoryDays;
        if (usage.GetHistoryDays() < minHistory)
        {
            return new StaleUsageDecision(StaleUsageOutcome.InsufficientHistory, ThresholdDays: threshold);
        }

        DateTime? newest = null;
        foreach (var exe in trackedPaths)
        {
            if (usage.TryGetLastUsed(exe, out var lastUsed) &&
                (newest is null || lastUsed > newest))
            {
                newest = lastUsed;
            }
        }

        if (newest is null)
        {
            // None of the tracked exes has ever been observed. The tracker may
            // predate the app, the paths may not match, or the app may truly
            // never have launched — indistinguishable, so never uninstall.
            return new StaleUsageDecision(StaleUsageOutcome.NoUsageData, ThresholdDays: threshold);
        }

        var daysSince = (DateTime.UtcNow - newest.Value).TotalDays;
        return daysSince > threshold
            ? new StaleUsageDecision(StaleUsageOutcome.Stale, daysSince, threshold)
            : new StaleUsageDecision(StaleUsageOutcome.RecentlyUsed, daysSince, threshold);
    }

    /// <summary>
    /// usage_tracked_paths when present; otherwise every .exe mentioned in the
    /// installs array (path or key_path — key_path is the MSI's primary
    /// executable, which is exactly what a user launches).
    /// </summary>
    internal static List<string> ResolveTrackedExecutables(CatalogItem item)
    {
        if (item.UsageTrackedPaths is { Count: > 0 })
        {
            return item.UsageTrackedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }

        var fromInstalls = new List<string>();
        foreach (var inst in item.Installs)
        {
            if (inst.Path?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            {
                fromInstalls.Add(inst.Path);
            }
            if (inst.KeyPath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            {
                fromInstalls.Add(inst.KeyPath);
            }
        }
        return fromInstalls;
    }
}
