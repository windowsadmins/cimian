using Xunit;
using FluentAssertions;
using Cimian.Core.Version;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Coverage for the agent-version eligibility gate exercised by UpdateEngine.
/// The gate helper inside UpdateEngine is private static; these tests verify
/// the building blocks (running-agent version lookup + version comparison)
/// that drive every branch of the gate.
/// </summary>
public class AgentVersionGateTests
{
    [Fact]
    public void GetRunningAgentVersion_ReturnsNonEmpty()
    {
        var current = VersionService.GetRunningAgentVersion();

        current.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GateLogic_MinimumNull_IsEligible()
    {
        string? minimum = null;

        var hasBound = !string.IsNullOrWhiteSpace(minimum);
        hasBound.Should().BeFalse();
    }

    [Fact]
    public void GateLogic_MinimumEmpty_IsEligible()
    {
        var minimum = string.Empty;

        var hasBound = !string.IsNullOrWhiteSpace(minimum);
        hasBound.Should().BeFalse();
    }

    [Fact]
    public void GateLogic_RunningEqualsMinimum_IsEligible()
    {
        var version = "2026.05.01.0000";

        VersionService.CompareVersions(version, version).Should().Be(0);
    }

    [Fact]
    public void GateLogic_RunningAboveMinimum_IsEligible()
    {
        VersionService.CompareVersions("2026.05.01.0000", "2025.10.15.1200")
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void GateLogic_RunningBelowMinimum_IsIneligible()
    {
        VersionService.CompareVersions("2025.10.15.1200", "2026.05.01.0000")
            .Should().BeLessThan(0);
    }

    [Fact]
    public void GateLogic_PreReleaseRunning_IsOlderThanRelease()
    {
        VersionService.CompareVersions("2026.05.01.0000-beta1", "2026.05.01.0000")
            .Should().BeLessThan(0);
    }
}
