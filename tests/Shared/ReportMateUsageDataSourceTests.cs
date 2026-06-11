using System.Globalization;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Fixture tests for ReportMateUsageDataSource. Each test writes usagetracker
/// JSON files (the schema reportmate-client-win's usagetracker.exe produces)
/// into a temp directory and points the source at it. The contract under
/// test is the fail-safe behavior: parse errors and missing data must always
/// degrade toward "no signal", never toward "unused".
/// </summary>
public sealed class ReportMateUsageDataSourceTests : IDisposable
{
    private readonly string _dir;

    public ReportMateUsageDataSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cimian-usage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static string DateKey(int daysAgo) =>
        DateTime.Now.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void WriteUserFile(string username, string json) =>
        File.WriteAllText(Path.Combine(_dir, username + ".json"), json);

    private static string TrackerJson(
        string exePath, int lastUsedDaysAgo,
        double activeSeconds = 120, int startedDaysAgo = 60, int lastUpdatedDaysAgo = 0)
    {
        var exe = exePath.Replace(@"\", @"\\");
        return $$"""
            {
              "SchemaVersion": 1,
              "Username": "user",
              "StartedAt": "{{DateTime.UtcNow.AddDays(-startedDaysAgo):O}}",
              "LastUpdatedAt": "{{DateTime.UtcNow.AddDays(-lastUpdatedDaysAgo):O}}",
              "ByAppByDate": {
                "{{exe}}": {
                  "{{DateKey(lastUsedDaysAgo)}}": { "ForegroundSeconds": 300, "ActiveSeconds": {{activeSeconds}} }
                }
              }
            }
            """;
    }

    // ── availability ────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_False_WhenDirectoryMissing()
    {
        var source = new ReportMateUsageDataSource(Path.Combine(_dir, "does-not-exist"));
        Assert.False(source.IsAvailable);
    }

    [Fact]
    public void IsAvailable_False_WhenDirectoryEmpty()
    {
        var source = new ReportMateUsageDataSource(_dir);
        Assert.False(source.IsAvailable);
    }

    [Fact]
    public void IsAvailable_True_WithOneParseableFile()
    {
        WriteUserFile("alice", TrackerJson(@"C:\Apps\tool.exe", lastUsedDaysAgo: 3));
        var source = new ReportMateUsageDataSource(_dir);
        Assert.True(source.IsAvailable);
    }

    // ── lookups ─────────────────────────────────────────────────────────

    [Fact]
    public void TryGetLastUsed_ExactPath_CaseInsensitive()
    {
        WriteUserFile("alice", TrackerJson(@"C:\Program Files\StaleApp\staleapp.exe", lastUsedDaysAgo: 5));
        var source = new ReportMateUsageDataSource(_dir);

        Assert.True(source.TryGetLastUsed(@"C:\PROGRAM FILES\STALEAPP\STALEAPP.EXE", out var lastUsed));
        var daysSince = (DateTime.UtcNow - lastUsed).TotalDays;
        Assert.InRange(daysSince, 3.5, 6.5); // day granularity + TZ slop
    }

    [Fact]
    public void TryGetLastUsed_FallsBackToBasename_WhenPathDiffers()
    {
        // Tracker observed the (x86) layout; pkginfo recorded the 64-bit path.
        WriteUserFile("alice", TrackerJson(@"C:\Program Files (x86)\StaleApp\staleapp.exe", lastUsedDaysAgo: 5));
        var source = new ReportMateUsageDataSource(_dir);

        Assert.True(source.TryGetLastUsed(@"C:\Program Files\StaleApp\staleapp.exe", out _));
    }

    [Fact]
    public void TryGetLastUsed_False_ForUnknownExecutable()
    {
        WriteUserFile("alice", TrackerJson(@"C:\Apps\tool.exe", lastUsedDaysAgo: 3));
        var source = new ReportMateUsageDataSource(_dir);

        Assert.False(source.TryGetLastUsed(@"C:\Apps\never-seen.exe", out var lastUsed));
        Assert.Equal(default, lastUsed);
    }

    [Fact]
    public void TryGetLastUsed_TakesMax_AcrossUsers()
    {
        // Bob used the app yesterday; Alice last touched it 40 days ago.
        // Device-wide answer must be Bob's — the app is NOT stale.
        WriteUserFile("alice", TrackerJson(@"C:\Apps\shared.exe", lastUsedDaysAgo: 40));
        WriteUserFile("bob", TrackerJson(@"C:\Apps\shared.exe", lastUsedDaysAgo: 1));
        var source = new ReportMateUsageDataSource(_dir);

        Assert.True(source.TryGetLastUsed(@"C:\Apps\shared.exe", out var lastUsed));
        Assert.InRange((DateTime.UtcNow - lastUsed).TotalDays, -0.5, 2.5);
    }

    [Fact]
    public void TryGetLastUsed_IgnoresZeroCounterDays()
    {
        // A recent date row with all-zero counters is bookkeeping, not usage:
        // last-used must resolve to the older day with real activity.
        var json = $$"""
            {
              "SchemaVersion": 1,
              "Username": "alice",
              "StartedAt": "{{DateTime.UtcNow.AddDays(-60):O}}",
              "LastUpdatedAt": "{{DateTime.UtcNow:O}}",
              "ByAppByDate": {
                "C:\\Apps\\tool.exe": {
                  "{{DateKey(30)}}": { "ForegroundSeconds": 500, "ActiveSeconds": 200 },
                  "{{DateKey(1)}}": { "ForegroundSeconds": 0, "ActiveSeconds": 0 }
                }
              }
            }
            """;
        WriteUserFile("alice", json);
        var source = new ReportMateUsageDataSource(_dir);

        Assert.True(source.TryGetLastUsed(@"C:\Apps\tool.exe", out var lastUsed));
        Assert.InRange((DateTime.UtcNow - lastUsed).TotalDays, 28.5, 31.5);
    }

    // ── robustness ──────────────────────────────────────────────────────

    [Fact]
    public void MalformedFile_IsSkipped_OthersStillParsed()
    {
        WriteUserFile("corrupt", "{ this is not json ");
        WriteUserFile("alice", TrackerJson(@"C:\Apps\tool.exe", lastUsedDaysAgo: 3));
        var source = new ReportMateUsageDataSource(_dir);

        Assert.True(source.IsAvailable);
        Assert.True(source.TryGetLastUsed(@"C:\Apps\tool.exe", out _));
    }

    [Fact]
    public void EmptyByAppByDate_StillCountsTowardAvailabilityAndHistory()
    {
        // Fresh user, tracker running but nothing used yet: the file proves
        // tracking has been active since StartedAt, so history accrues even
        // though no app rows exist.
        var json = $$"""
            {
              "SchemaVersion": 1,
              "Username": "fresh",
              "StartedAt": "{{DateTime.UtcNow.AddDays(-20):O}}",
              "LastUpdatedAt": "{{DateTime.UtcNow:O}}",
              "ByAppByDate": {}
            }
            """;
        WriteUserFile("fresh", json);
        var source = new ReportMateUsageDataSource(_dir);

        Assert.True(source.IsAvailable);
        Assert.InRange(source.GetHistoryDays(), 19, 21);
    }

    // ── guards ──────────────────────────────────────────────────────────

    [Fact]
    public void GetHistoryDays_SpansEarliestRecord()
    {
        WriteUserFile("alice", TrackerJson(@"C:\Apps\tool.exe", lastUsedDaysAgo: 10, startedDaysAgo: 45));
        var source = new ReportMateUsageDataSource(_dir);

        Assert.InRange(source.GetHistoryDays(), 44, 46);
    }

    [Fact]
    public void GetDataFreshnessDays_ReflectsLatestWrite()
    {
        // Telemetry last written 9 days ago (e.g. device idle, no logons):
        // callers with a 7-day staleness threshold must refuse to act.
        WriteUserFile("alice", TrackerJson(@"C:\Apps\tool.exe", lastUsedDaysAgo: 12, lastUpdatedDaysAgo: 9));
        var source = new ReportMateUsageDataSource(_dir);

        Assert.InRange(source.GetDataFreshnessDays(), 8, 10);
    }

    [Fact]
    public void Guards_AreFailSafe_WhenNoFiles()
    {
        var source = new ReportMateUsageDataSource(_dir);

        Assert.Equal(0, source.GetHistoryDays());
        Assert.Equal(int.MaxValue, source.GetDataFreshnessDays());
    }

    [Fact]
    public void GetDataFreshnessDays_FutureWriteStamp_IsInvalid_NotFresh()
    {
        // LastUpdatedAt 3 days in the future = clock skew or tampering.
        // Treating it as "fresh" would let untrustworthy telemetry drive
        // uninstalls; it must read as too-stale-to-act instead.
        WriteUserFile("skewed", TrackerJson(@"C:\Apps\tool.exe", lastUsedDaysAgo: 12, lastUpdatedDaysAgo: -3));
        var source = new ReportMateUsageDataSource(_dir);

        Assert.Equal(int.MaxValue, source.GetDataFreshnessDays());
    }

    [Fact]
    public void GetDataFreshnessDays_SmallFutureSkew_ReadsAsFresh()
    {
        // A few hours of clock skew is normal fleet reality - don't let it
        // disable the feature device-wide. -0.5 days is within the 1-day
        // tolerance window.
        var json = $$"""
            {
              "SchemaVersion": 1,
              "Username": "slight",
              "StartedAt": "{{DateTime.UtcNow.AddDays(-30):O}}",
              "LastUpdatedAt": "{{DateTime.UtcNow.AddHours(12):O}}",
              "ByAppByDate": {}
            }
            """;
        WriteUserFile("slight", json);
        var source = new ReportMateUsageDataSource(_dir);

        Assert.Equal(0, source.GetDataFreshnessDays());
    }
}
