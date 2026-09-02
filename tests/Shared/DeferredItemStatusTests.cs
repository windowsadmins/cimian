using Cimian.Core.Models;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// A deferred item is pending, never installed.
/// </summary>
/// <remarks>
/// The regression this guards: deferring an item - outside its install_window, a
/// blocking application running, an active user in auto mode - removes it from the
/// action lists. It then reached the resolver with no outcome and no pending flag
/// and fell through to the "Installed" default, so a package the status check had
/// just reported missing was published to the fleet as installed. Every
/// install_window package on a machine checking in outside its window was affected,
/// which made the fleet view assert the opposite of what was on disk.
/// </remarks>
public class DeferredItemStatusTests
{
    [Theory]
    [InlineData("install", "Pending Install")]
    [InlineData("update", "Pending Update")]
    [InlineData("uninstall", "Pending Removal")]
    public void DeferredItem_ReportsPending(string kind, string expected)
    {
        Assert.Equal(expected, SessionItemStatusResolver.ResolveDeferred(kind));
    }

    [Fact]
    public void DeferredItem_NeverReportsInstalled()
    {
        foreach (var kind in new[] { "install", "update", "uninstall", "", "something-else" })
        {
            Assert.NotEqual("Installed", SessionItemStatusResolver.ResolveDeferred(kind));
        }
    }

    [Fact]
    public void UnknownDeferralKind_FallsBackToPendingInstall()
    {
        // A new deferral path that forgets to name its kind must still be honest.
        Assert.Equal("Pending Install", SessionItemStatusResolver.ResolveDeferred("whatever"));
    }

    [Fact]
    public void ResolverStillDefaultsToInstalled_WhenNothingIsPending()
    {
        // The default itself is correct for an item the status check found present;
        // the bug was reaching it with a deferred item. Pin the contract so the two
        // paths cannot be conflated again.
        Assert.Equal("Installed", SessionItemStatusResolver.Resolve(
            outcome: null, isPendingInstall: false, isPendingUpdate: false,
            isPendingUninstall: false, manifestAction: "install"));

        Assert.Equal("Pending Install", SessionItemStatusResolver.Resolve(
            outcome: null, isPendingInstall: true, isPendingUpdate: false,
            isPendingUninstall: false, manifestAction: "install"));
    }
}
