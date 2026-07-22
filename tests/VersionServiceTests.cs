using Xunit;
using FluentAssertions;
using Cimian.Core.Version;

namespace Cimian.Tests.Core.Version;

/// <summary>
/// Tests for VersionService - CRITICAL for migration parity
/// These tests must all pass before the Go version comparison logic can be retired.
/// 
/// Each test case corresponds to real-world version formats used in Cimian catalogs.
/// </summary>
public class VersionServiceTests
{
    #region IsOlderVersion Tests
    
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]    // Semantic: patch upgrade
    [InlineData("1.0.1", "1.0.0", false)]   // Semantic: patch downgrade
    [InlineData("1.0.0", "1.0.0", false)]   // Semantic: same version
    [InlineData("1.1.0", "1.0.9", false)]   // Semantic: minor is higher
    [InlineData("2.0.0", "1.9.9", false)]   // Semantic: major is higher
    [InlineData("1.0.0", "2.0.0", true)]    // Semantic: major upgrade
    public void IsOlderVersion_SemanticVersions_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("10.0.19045", "10.0.22621", true)]   // Windows: 19045 vs 22621
    [InlineData("10.0.22621", "10.0.22631", true)]   // Windows: 22621 vs 22631
    [InlineData("10.0.22631", "10.0.22621", false)]  // Windows: 22631 vs 22621
    [InlineData("10.0.22631", "10.0.22631", false)]  // Windows: same
    public void IsOlderVersion_WindowsBuildNumbers_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("139.0.7258.139", "140.0.0.1", true)]     // Chrome: major upgrade
    [InlineData("139.0.7258.100", "139.0.7258.139", true)] // Chrome: patch upgrade
    [InlineData("139.0.7258.139", "139.0.7258.100", false)] // Chrome: patch downgrade
    [InlineData("140.0.0.1", "139.0.7258.139", false)]     // Chrome: major downgrade
    public void IsOlderVersion_ChromeStyleVersions_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("2024.1.2.3", "2024.1.2.4", true)]   // Date-based: same month
    [InlineData("2024.1.2.3", "2024.2.1.1", true)]   // Date-based: different month
    [InlineData("2024.2.1.1", "2024.1.2.3", false)]  // Date-based: older month
    [InlineData("2023.12.1.1", "2024.1.1.1", true)]  // Date-based: year boundary
    public void IsOlderVersion_DateBasedVersions_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("1.2.3.4", "1.2.3.5", true)]   // 4-part: last digit
    [InlineData("1.2.3.4", "1.2.4.0", true)]   // 4-part: third digit
    [InlineData("1.2.4.0", "1.2.3.99", false)] // 4-part: third digit higher
    public void IsOlderVersion_FourPartVersions_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("v1.0.0", "1.0.1", true)]   // v-prefix: local has v
    [InlineData("1.0.0", "v1.0.1", true)]   // v-prefix: remote has v
    [InlineData("v1.0.0", "v1.0.1", true)]  // v-prefix: both have v
    [InlineData("v1.0.1", "v1.0.0", false)] // v-prefix: local is newer
    [InlineData("v1.0.0", "v1.0.0", false)] // v-prefix: same version
    public void IsOlderVersion_VPrefixedVersions_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0", true)]       // Pre-release: alpha vs release
    [InlineData("1.0.0-beta", "1.0.0", true)]        // Pre-release: beta vs release
    [InlineData("1.0.0-alpha", "1.0.0-beta", true)]  // Pre-release: alpha vs beta
    [InlineData("1.0.0-beta", "1.0.0-alpha", false)] // Pre-release: beta vs alpha
    [InlineData("1.0.0", "1.0.0-beta", false)]       // Pre-release: release vs beta
    [InlineData("1.0.0-rc", "1.0.0", true)]          // Pre-release: rc vs release
    [InlineData("1.0.0-beta", "1.0.0-rc", true)]     // Pre-release: beta vs rc
    public void IsOlderVersion_PreReleaseVersions_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("0.0.1", "0.0.2", true)]   // Edge: zero versions
    [InlineData("0.0.0", "0.0.1", true)]   // Edge: zero version
    [InlineData("", "1.0.0", true)]        // Edge: empty local
    [InlineData("1.0.0", "", false)]       // Edge: empty remote
    [InlineData("", "", false)]            // Edge: both empty
    [InlineData(null, "1.0.0", true)]      // Edge: null local
    [InlineData("1.0.0", null, false)]     // Edge: null remote
    [InlineData(null, null, false)]        // Edge: both null
    public void IsOlderVersion_EdgeCases_ReturnsCorrectResult(string? local, string? remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("  1.0.0  ", "1.0.1", true)]  // Whitespace handling
    [InlineData("1.0.0", "  1.0.1  ", true)]  // Whitespace on remote
    public void IsOlderVersion_WhitespaceHandling_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }
    
    #endregion
    
    #region Normalize Tests
    
    [Theory]
    [InlineData("1.0.0", "1")]           // Trim trailing .0.0
    [InlineData("1.0", "1")]             // Trim trailing .0
    [InlineData("1.2.0", "1.2")]         // Trim single trailing .0
    [InlineData("1.2.3", "1.2.3")]       // No trailing zeros
    [InlineData("1.2.3.0", "1.2.3")]     // Trim 4th part
    [InlineData("1.2.0.0", "1.2")]       // Trim multiple trailing zeros
    [InlineData("v1.0.0", "1")]          // Remove v prefix and normalize
    [InlineData("v1.2.3", "1.2.3")]      // Remove v prefix only
    [InlineData("0", "0")]               // Single zero preserved
    [InlineData("0.0.0", "0")]           // All zeros normalized
    public void Normalize_VariousVersions_ReturnsExpectedResult(string input, string expected)
    {
        VersionService.Normalize(input).Should().Be(expected);
    }
    
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Normalize_NullOrEmpty_ReturnsEmptyString(string? input)
    {
        VersionService.Normalize(input).Should().BeEmpty();
    }
    
    #endregion
    
    #region CompareVersions Tests
    
    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.9.9", "2.0.0", -1)]
    public void CompareVersions_ReturnsCorrectSign(string v1, string v2, int expectedSign)
    {
        var result = VersionService.CompareVersions(v1, v2);
        
        if (expectedSign < 0) result.Should().BeNegative();
        else if (expectedSign > 0) result.Should().BePositive();
        else result.Should().Be(0);
    }

    #endregion

    #region CompareOsVersion Tests

    [Theory]
    // Windows 11 reports a 10.0.<build> kernel version; a bare "11" requirement
    // must match by generation (build >= 22000), not by literal major.
    [InlineData("10.0.26200.0", "11.0", 0)]    // Win 11 25H2 satisfies "11"
    [InlineData("10.0.26200.0", "11", 0)]      // bare major, no minor
    [InlineData("10.0.22000.0", "11.0", 0)]    // first Win 11 build
    [InlineData("10.0.19045.0", "11.0", -1)]   // Win 10 is older than "11"
    [InlineData("10.0.26200.0", "10.0", 1)]    // Win 11 is newer than "10"
    [InlineData("10.0.26200.0", "10", 1)]      // bare "10"
    [InlineData("10.0.19045.0", "10.0", 0)]    // Win 10 satisfies "10"
    // Build-pinned requirements compare numerically (no marketing mapping).
    [InlineData("10.0.26200.0", "10.0.22631", 1)]
    [InlineData("10.0.19045.0", "10.0.22631", -1)]
    // Build-pinned marketing form ("11.0.<build>", as cimiimport's --maximum_os_version
    // help prints) folds to the kernel "10.0.<build>" before the numeric compare.
    [InlineData("10.0.26200.0", "11.0.22000", 1)]    // Win 11 25H2 newer than 11.0.22000 floor
    [InlineData("10.0.22000.0", "11.0.22000", 0)]    // exact build match
    [InlineData("10.0.19045.0", "11.0.22000", -1)]   // Win 10 older than 11.0.22000
    public void CompareOsVersion_ReturnsCorrectSign(string current, string requirement, int expectedSign)
    {
        var result = VersionService.CompareOsVersion(current, requirement);

        if (expectedSign < 0) result.Should().BeNegative();
        else if (expectedSign > 0) result.Should().BePositive();
        else result.Should().Be(0);
    }

    [Fact]
    public void CompareOsVersion_Win11_SatisfiesBothMinAndMax11()
    {
        // Regression: a "11.0" minimum AND a "11.0" maximum must both admit Win 11.
        const string win11 = "10.0.26200.0";

        VersionService.CompareOsVersion(win11, "11.0").Should().Be(0, "Win 11 is not older than min 11.0");
        VersionService.CompareOsVersion(win11, "11.0").Should().Be(0, "Win 11 is not newer than max 11.0");
    }

    #endregion

    #region Real-World Catalog Version Tests
    
    /// <summary>
    /// Tests with actual version formats from Cimian production catalogs
    /// </summary>
    [Theory]
    [InlineData("24.09", "24.10", true)]                    // 7-Zip style
    [InlineData("8.7.1", "8.7.2", true)]                    // Notepad++ style
    [InlineData("1.95.3", "1.96.0", true)]                  // VSCode style
    [InlineData("131.0.2", "131.0.10", true)]               // Firefox (string vs numeric)
    [InlineData("2024.11.1.0", "2024.11.2.0", true)]       // Office 365 style
    [InlineData("24.09", "24.9", false)]                    // 7-Zip (24.09 == 24.9 after normalize)
    public void IsOlderVersion_RealWorldCatalogVersions_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }

    #endregion

    #region Calendar Build Stamp Tests (AB#3754)

    [Theory]
    // The core bug: the legacy 3-component "yyyy.M.DDHH" stamp (day+hour merged, no
    // minutes) must not sort newer than the 4-component "yyyy.MM.dd.HHmm" stamp.
    // "2026.7.2006" is 2026-07-20 06:00; "2026.07.20.0632" is 2026-07-20 06:32.
    [InlineData("2026.7.2006", "2026.07.20.0632", true)]    // 06:00 older than 06:32
    [InlineData("2026.07.20.0632", "2026.7.2006", false)]   // and the reverse
    [InlineData("2026.6.2405", "2026.06.24.0519", true)]    // 2026-06-24 05:00 < 05:19
    [InlineData("2026.6.2418", "2026.06.24.0519", false)]   // 2026-06-24 18:00 > 05:19
    [InlineData("2026.6.511", "2026.06.05.0221", false)]    // 2026-06-05 11:00 > 02:21
    // Same build in both encodings compares equal enough for "not older".
    [InlineData("2026.07.20.0632", "2026.07.20.0632", false)]
    // Cross-encoding across months / year boundary.
    [InlineData("2025.12.17.2120", "2026.01.20.1609", true)]
    [InlineData("2026.7.2006", "2026.06.24.0519", false)]   // July build newer than June
    public void IsOlderVersion_CalendarBuildStamps_ReturnsCorrectResult(string local, string remote, bool expected)
    {
        VersionService.IsOlderVersion(local, remote).Should().Be(expected);
    }

    [Theory]
    [InlineData("2026.07.20.0632", 202607200632L)]  // canonical yyyy.MM.dd.HHmm
    [InlineData("2026.7.2006", 202607200600L)]       // 3-component yyyy.M.DDHH -> 06:00
    [InlineData("2026.6.511", 202606051100L)]        // DDHH with single-digit day
    [InlineData("2026.6.2418", 202606241800L)]       // DDHH, hour 18
    [InlineData("2026.7.5", 202607050000L)]          // plain yyyy.M.d -> midnight
    [InlineData("2025.09.13.2245", 202509132245L)]
    public void TryParseCalendarBuildStamp_ValidStamps_DecodeToSortKey(string version, long expected)
    {
        VersionService.TryParseCalendarBuildStamp(version, out var sortKey).Should().BeTrue();
        sortKey.Should().Be(expected);
    }

    [Theory]
    [InlineData("1.2.3")]              // semver
    [InlineData("10.0.22621")]         // Windows build number
    [InlineData("139.0.7258.139")]     // Chrome
    [InlineData("26.7.2118")]          // MSI 2-digit-year form (out of scope by design)
    [InlineData("2.55.0.windows.1")]   // NuGet-style suffix
    [InlineData("2026.07.20.0632-beta1")] // pre-release suffix is not a bare stamp
    [InlineData("2101.01.01.0000")]    // year above range
    [InlineData("2026.13.01.0000")]    // month out of range
    [InlineData("2026.7.99")]          // 32..99 third component: neither day nor DDHH
    [InlineData("2026")]               // too few components
    [InlineData("2026.7")]             // too few components
    [InlineData("")]
    public void TryParseCalendarBuildStamp_NonStamps_ReturnFalse(string version)
    {
        VersionService.TryParseCalendarBuildStamp(version, out _).Should().BeFalse();
    }

    #endregion
}
