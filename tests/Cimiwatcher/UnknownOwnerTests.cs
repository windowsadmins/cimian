using Xunit;
using Cimian.CLI.Cimiwatcher.Services;

namespace Cimian.Tests.Cimiwatcher;

public class UnknownOwnerTests
{
    [Fact]
    public void ARequestWhoseOwnerCannotBeRead_IsNotQueued()
    {
        // Each unknown owner used to get a queue place of its own, which anyone able to make
        // the owner unreadable could use to jump the one-place-per-user rule.
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var pending = FileWatcherService.BuildPending(
            new[] { "a", "b" },
            path => path == "a" ? "S-1-5-21-1" : null,
            path => now);

        var only = Assert.Single(pending);
        Assert.Equal("a", only.Path);
    }
}
