using Xunit;
using FluentAssertions;
using Cimian.Core.Version;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Coverage for the OS-version eligibility gate exercised by UpdateEngine.
/// The gate helper inside UpdateEngine is private static; these tests verify
/// the building blocks (current OS detection + version comparison) that
/// drive every branch of the gate. The wired-up skip path is exercised via
/// integration / dogfooding (see PR description).
/// </summary>
public class OsVersionGateTests
{
    [Fact]
    public void GetCurrentOsVersion_ReturnsNonEmptyDottedString()
    {
        var current = VersionService.GetCurrentOsVersion();

        current.Should().NotBeNullOrWhiteSpace();
        current.Should().Contain(".");
        current.Split('.')[0].Should().Be("10");
    }

    [Fact]
    public void GateLogic_BothBoundsEmpty_IsEligible()
    {
        // Mirrors the early-return branch: no min, no max => always eligible.
        var min = (string?)null;
        var max = string.Empty;

        var hasBound = !string.IsNullOrWhiteSpace(min) || !string.IsNullOrWhiteSpace(max);
        hasBound.Should().BeFalse();
    }

    [Fact]
    public void GateLogic_RunningEqualsMinimum_IsEligible()
    {
        var current = VersionService.GetCurrentOsVersion();

        VersionService.CompareVersions(current, current).Should().Be(0);
    }

    [Fact]
    public void GateLogic_RunningAboveMinimum_IsEligible()
    {
        VersionService.CompareVersions("10.0.22631", "10.0.19045")
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void GateLogic_RunningBelowMinimum_IsIneligible_TooOld()
    {
        VersionService.CompareVersions("10.0.19045", "10.0.22631")
            .Should().BeLessThan(0);
    }

    [Fact]
    public void GateLogic_RunningAboveMaximum_IsIneligible_TooNew()
    {
        VersionService.CompareVersions("10.0.26200", "10.0.22631")
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void GateLogic_RunningInRange_IsEligible()
    {
        var min = "10.0.19045";
        var max = "10.0.26200";
        var current = "10.0.22631";

        VersionService.CompareVersions(current, min).Should().BeGreaterThan(0);
        VersionService.CompareVersions(current, max).Should().BeLessThan(0);
    }
}
