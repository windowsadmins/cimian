using Xunit;
using FluentAssertions;
using Cimian.Core.Models;
using Cimian.Core.Version;
using Cimian.CLI.managedsoftwareupdate.Services;
using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Coverage for the agent-version eligibility gate exercised by UpdateEngine.
/// Calls UpdateEngine.IsEligibleForAgentVersion directly (exposed via
/// InternalsVisibleTo) so the real decision branches and reason codes are
/// asserted, not just the underlying string/version-comparison primitives.
/// </summary>
public class AgentVersionGateTests
{
    private static readonly string RunningVersion = VersionService.GetRunningAgentVersion();

    private static CatalogItem ItemWithMinimum(string? minimum)
        => new() { Name = "test-pkg", Version = "1.0", MinimumCimianVersion = minimum };

    /// <summary>
    /// Bumps the leading numeric segment by +1 so the resulting version is
    /// strictly greater than the running agent version under
    /// VersionService.CompareVersions, regardless of build-time format.
    /// </summary>
    private static string OneAboveRunning()
    {
        var parts = RunningVersion.Split('.');
        if (parts.Length == 0 || !long.TryParse(parts[0], out var major))
        {
            return "9999.0.0.0";
        }
        parts[0] = (major + 1).ToString();
        return string.Join('.', parts);
    }

    [Fact]
    public void GetRunningAgentVersion_ReturnsNonEmpty()
    {
        RunningVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void IsEligible_MinimumNull_AllowsInstall()
    {
        var item = ItemWithMinimum(null);

        var eligible = UpdateEngine.IsEligibleForAgentVersion(item, out var reason, out var code);

        eligible.Should().BeTrue();
        reason.Should().BeEmpty();
        code.Should().BeEmpty();
    }

    [Fact]
    public void IsEligible_MinimumEmpty_AllowsInstall()
    {
        var item = ItemWithMinimum(string.Empty);

        var eligible = UpdateEngine.IsEligibleForAgentVersion(item, out var reason, out var code);

        eligible.Should().BeTrue();
        reason.Should().BeEmpty();
        code.Should().BeEmpty();
    }

    [Fact]
    public void IsEligible_MinimumWhitespace_AllowsInstall()
    {
        var item = ItemWithMinimum("   ");

        var eligible = UpdateEngine.IsEligibleForAgentVersion(item, out _, out _);

        eligible.Should().BeTrue();
    }

    [Fact]
    public void IsEligible_RunningEqualsMinimum_AllowsInstall_AtBoundary()
    {
        var item = ItemWithMinimum(RunningVersion);

        var eligible = UpdateEngine.IsEligibleForAgentVersion(item, out var reason, out var code);

        eligible.Should().BeTrue();
        reason.Should().BeEmpty();
        code.Should().BeEmpty();
    }

    [Fact]
    public void IsEligible_RunningAboveMinimum_AllowsInstall()
    {
        // Use "0.0.0.1" as a minimum that any real running version will exceed.
        var item = ItemWithMinimum("0.0.0.1");

        var eligible = UpdateEngine.IsEligibleForAgentVersion(item, out _, out _);

        eligible.Should().BeTrue();
    }

    [Fact]
    public void IsEligible_RunningBelowMinimum_BlocksInstall_WithReasonCode()
    {
        var futureMinimum = OneAboveRunning();
        var item = ItemWithMinimum(futureMinimum);

        var eligible = UpdateEngine.IsEligibleForAgentVersion(item, out var reason, out var code);

        eligible.Should().BeFalse();
        code.Should().Be(StatusReasonCode.AgentVersionTooOld);
        reason.Should().Contain(futureMinimum);
        reason.Should().Contain(RunningVersion);
    }

    [Fact]
    public void IsEligible_PreReleaseRunning_TreatedAsOlderThanRelease()
    {
        // Sanity check on the underlying comparator that the gate relies on:
        // pre-release < same numeric release.
        VersionService.CompareVersions("2026.05.01.0000-beta1", "2026.05.01.0000")
            .Should().BeLessThan(0);
    }
}
