using Xunit;
using Cimian.CLI.Cimiwatcher.Services;

namespace Cimian.Tests.Cimiwatcher;

/// <summary>
/// How CimianWatcher picks the next user request from triggers\. Every user can write
/// there, so no user may jump the queue, flood it, or block it for others.
/// </summary>
public class RequestQueueTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static FileWatcherService.PendingRequest Req(string path, string owner, int secondsAfterT0) =>
        new(path, owner, T0.AddSeconds(secondsAfterT0));

    [Fact]
    public void OrderIsWhenTheWatcherFirstSawEachRequest()
    {
        // File times belong to the file's owner and can be backdated; first-seen cannot.
        var (next, _) = FileWatcherService.SelectNext(new[]
        {
            Req("b", "S-1-5-21-2", 5),
            Req("a", "S-1-5-21-1", 0),
        });

        Assert.Equal("a", next!.Value.Path);
    }

    [Fact]
    public void OneUser_GetsOnePendingRequest_TheirLatest_InTheirOriginalPlace()
    {
        var (next, superseded) = FileWatcherService.SelectNext(new[]
        {
            Req("a-old", "S-1-5-21-1", 0),
            Req("b", "S-1-5-21-2", 1),
            Req("a-new", "S-1-5-21-1", 2),
            Req("a-newer", "S-1-5-21-1", 3),
        });

        Assert.Equal("a-newer", next!.Value.Path);
        Assert.Equal(new[] { "a-new", "a-old" }, superseded.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void AfterOneUsersRequest_TheNextUserIsServed()
    {
        var (next, _) = FileWatcherService.SelectNext(new[]
        {
            Req("b", "S-1-5-21-2", 1),
            Req("a-again", "S-1-5-21-1", 9),
        });

        Assert.Equal("b", next!.Value.Path);
    }

    [Fact]
    public void NothingPending_SelectsNothing()
    {
        var (next, superseded) = FileWatcherService.SelectNext(Array.Empty<FileWatcherService.PendingRequest>());

        Assert.Null(next);
        Assert.Empty(superseded);
    }

    [Fact]
    public void UserRuns_AreSpacedByAMinimumInterval()
    {
        Assert.True(FileWatcherService.IsUserRunDue(null, T0));
        Assert.False(FileWatcherService.IsUserRunDue(T0, T0.AddSeconds(5)));
        Assert.True(FileWatcherService.IsUserRunDue(T0, T0 + FileWatcherService.MinUserRunInterval));
    }

    [Fact]
    public void Tracking_ForgetsFilesThatAreGoneAndExpiredSkips()
    {
        var firstSeen = new Dictionary<string, DateTime> { ["gone"] = T0, ["here"] = T0 };
        var skipUntil = new Dictionary<string, DateTime> { ["gone"] = T0.AddHours(1), ["here"] = T0.AddSeconds(-1) };

        FileWatcherService.PruneTracking(firstSeen, skipUntil, new HashSet<string> { "here" }, T0);

        Assert.Equal(new[] { "here" }, firstSeen.Keys);
        Assert.Empty(skipUntil);
    }
}
