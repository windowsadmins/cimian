using Xunit;
using FluentAssertions;
using Cimian.Core.Models;
using Cimian.Core.Version;
using Cimian.CLI.managedsoftwareupdate.Services;
using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Coverage for the OS-version eligibility gate exercised by UpdateEngine.
/// Drives UpdateEngine.IsEligibleForOsVersion directly via InternalsVisibleTo
/// so all branches (no bounds, in-range, below-min, above-max, boundary) are
/// asserted against the real helper, not just the underlying VersionService.
/// </summary>
public class OsVersionGateTests
{
    [Fact]
    public void GetCurrentOsVersion_ReturnsNonEmptyDottedString()
    {
        var current = VersionService.GetCurrentOsVersion();

        current.Should().NotBeNullOrWhiteSpace();
        current.Should().MatchRegex(@"^\d+(\.\d+)+$");
    }

    [Fact]
    public void Gate_NoBounds_IsEligible()
    {
        var item = new CatalogItem { Name = "Test", Version = "1.0" };

        var eligible = UpdateEngine.IsEligibleForOsVersion(item, out var reason, out var code);

        eligible.Should().BeTrue();
        reason.Should().BeEmpty();
        code.Should().BeEmpty();
    }

    [Fact]
    public void Gate_RunningEqualsMinimum_IsEligible()
    {
        // Boundary equality at the minimum bound must be eligible.
        var current = VersionService.GetCurrentOsVersion();
        var item = new CatalogItem
        {
            Name = "Test",
            Version = "1.0",
            MinimumOsVersion = current
        };

        var eligible = UpdateEngine.IsEligibleForOsVersion(item, out _, out _);

        eligible.Should().BeTrue();
    }

    [Fact]
    public void Gate_RunningEqualsMaximum_IsEligible()
    {
        // Boundary equality at the maximum bound must be eligible.
        var current = VersionService.GetCurrentOsVersion();
        var item = new CatalogItem
        {
            Name = "Test",
            Version = "1.0",
            MaximumOsVersion = current
        };

        var eligible = UpdateEngine.IsEligibleForOsVersion(item, out _, out _);

        eligible.Should().BeTrue();
    }

    [Fact]
    public void Gate_RunningInRange_IsEligible()
    {
        var current = VersionService.GetCurrentOsVersion();
        var item = new CatalogItem
        {
            Name = "Test",
            Version = "1.0",
            MinimumOsVersion = "0.0.0.1",
            MaximumOsVersion = "999999.0"
        };

        var eligible = UpdateEngine.IsEligibleForOsVersion(item, out _, out _);

        eligible.Should().BeTrue();
        // Sanity: current sits strictly inside the constructed range.
        VersionService.CompareVersions(current, "0.0.0.1").Should().BeGreaterThan(0);
        VersionService.CompareVersions(current, "999999.0").Should().BeLessThan(0);
    }

    [Fact]
    public void Gate_RunningBelowMinimum_IsIneligible_TooOld()
    {
        // A min above any plausible current OS forces the too-old branch.
        var item = new CatalogItem
        {
            Name = "Test",
            Version = "1.0",
            MinimumOsVersion = "999999.0"
        };

        var eligible = UpdateEngine.IsEligibleForOsVersion(item, out var reason, out var code);

        eligible.Should().BeFalse();
        reason.Should().Contain("999999.0");
        reason.Should().Contain("newer");
        code.Should().Be(StatusReasonCode.OsVersionTooOld);
    }

    [Fact]
    public void Gate_RunningAboveMaximum_IsIneligible_TooNew()
    {
        // A max below any plausible current OS forces the too-new branch.
        var item = new CatalogItem
        {
            Name = "Test",
            Version = "1.0",
            MaximumOsVersion = "0.0.0.1"
        };

        var eligible = UpdateEngine.IsEligibleForOsVersion(item, out var reason, out var code);

        eligible.Should().BeFalse();
        reason.Should().Contain("0.0.0.1");
        reason.Should().Contain("older");
        code.Should().Be(StatusReasonCode.OsVersionTooNew);
    }

    [Fact]
    public void Gate_BothBoundsSetAndInRange_IsEligible()
    {
        var item = new CatalogItem
        {
            Name = "Test",
            Version = "1.0",
            MinimumOsVersion = "0.0.0.1",
            MaximumOsVersion = "999999.0"
        };

        var eligible = UpdateEngine.IsEligibleForOsVersion(item, out _, out _);

        eligible.Should().BeTrue();
    }
}
