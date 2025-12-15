using Cimian.CLI.Cimipkg.Services;
using Xunit;

namespace Cimian.Tests.Cimipkg;

/// <summary>
/// Tests for the VersionParser service.
/// </summary>
public class VersionParserTests
{
    #region Date-based Version Tests

    [Theory]
    [InlineData("2024.01.15", "2024.01.15", "2024.01.15")]
    [InlineData("2024.1.5", "2024.01.05", "2024.01.05")]
    [InlineData("2024.12.31", "2024.12.31", "2024.12.31")]
    [InlineData("2024.01.15.1", "2024.01.15.1", "2024.01.15.1")]
    public void Parse_DateVersion_Pkg_ReturnsExpectedVersions(string input, string expectedOriginal, string expectedNormalized)
    {
        var result = VersionParser.Parse(input, "pkg");

        Assert.Equal(expectedOriginal, result.OriginalVersion);
        Assert.Equal(expectedNormalized, result.NormalizedVersion);
        Assert.True(result.IsDateBased);
    }

    [Theory]
    [InlineData("2024.01.15", "2024.01.15", "2024.1.15")]
    [InlineData("2024.1.5", "2024.01.05", "2024.1.5")]
    [InlineData("2024.12.31", "2024.12.31", "2024.12.31")]
    [InlineData("2024.01.15.1", "2024.01.15.1", "2024.1.15.1")]
    public void Parse_DateVersion_Nupkg_ReturnsNuGetCompatibleVersion(string input, string expectedOriginal, string expectedNormalized)
    {
        var result = VersionParser.Parse(input, "nupkg");

        Assert.Equal(expectedOriginal, result.OriginalVersion);
        Assert.Equal(expectedNormalized, result.NormalizedVersion);
        Assert.True(result.IsDateBased);
    }

    [Fact]
    public void Parse_DateVersion_WithRevision_IncludesRevision()
    {
        var result = VersionParser.Parse("2024.03.15.5", "pkg");

        Assert.Equal("2024.03.15.5", result.OriginalVersion);
        Assert.Equal("2024.03.15.5", result.NormalizedVersion);
        Assert.True(result.IsDateBased);
    }

    #endregion

    #region Semantic Version Tests

    [Theory]
    [InlineData("1.0.0", "1.0.0", "1.0.0")]
    [InlineData("2.5.10", "2.5.10", "2.5.10")]
    [InlineData("1.0.0.1", "1.0.0.1", "1.0.0.1")]
    [InlineData("10.20.30", "10.20.30", "10.20.30")]
    public void Parse_SemVer_ReturnsExpectedVersions(string input, string expectedOriginal, string expectedNormalized)
    {
        var result = VersionParser.Parse(input, "pkg");

        Assert.Equal(expectedOriginal, result.OriginalVersion);
        Assert.Equal(expectedNormalized, result.NormalizedVersion);
        Assert.False(result.IsDateBased);
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha", "1.0.0-alpha")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta.1", "1.0.0-beta.1")]
    [InlineData("2.0.0-rc.1", "2.0.0-rc.1", "2.0.0-rc.1")]
    public void Parse_SemVer_WithPrerelease_IncludesPrerelease(string input, string expectedOriginal, string expectedNormalized)
    {
        var result = VersionParser.Parse(input, "pkg");

        Assert.Equal(expectedOriginal, result.OriginalVersion);
        Assert.Equal(expectedNormalized, result.NormalizedVersion);
        Assert.False(result.IsDateBased);
    }

    [Fact]
    public void Parse_SemVer_WithBuildMetadata_PreservesMetadataInOriginal()
    {
        var result = VersionParser.Parse("1.0.0+build.123", "pkg");

        Assert.Contains("+build.123", result.OriginalVersion);
        Assert.DoesNotContain("+build.123", result.NormalizedVersion);
        Assert.False(result.IsDateBased);
    }

    #endregion

    #region Simple Version Tests

    [Theory]
    [InlineData("1", "1.0.0", "1.0.0")]
    [InlineData("1.2", "1.2.0", "1.2.0")]
    [InlineData("1.2.3", "1.2.3", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3.4", "1.2.3.4")]
    public void Parse_SimpleVersion_NormalizesToThreeComponents(string input, string expectedOriginal, string expectedNormalized)
    {
        var result = VersionParser.Parse(input, "pkg");

        Assert.Equal(expectedOriginal, result.OriginalVersion);
        Assert.Equal(expectedNormalized, result.NormalizedVersion);
        Assert.False(result.IsDateBased);
    }

    #endregion

    #region Validation Tests

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Parse_EmptyOrNull_ThrowsArgumentException(string? input)
    {
        Assert.Throws<ArgumentException>(() => VersionParser.Parse(input!, "pkg"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.2.3.4.5")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0.0.0")]
    [InlineData("2024.13.01")] // Invalid month
    [InlineData("2024.01.32")] // Invalid day
    public void Parse_InvalidFormat_ThrowsArgumentException(string input)
    {
        Assert.Throws<ArgumentException>(() => VersionParser.Parse(input, "pkg"));
    }

    [Theory]
    [InlineData("2024.01.15")]
    [InlineData("2024.1.5")]
    public void IsDateBasedVersion_DateVersion_ReturnsTrue(string input)
    {
        Assert.True(VersionParser.IsDateBasedVersion(input));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.2.3.4")]
    [InlineData("1.0.0-alpha")]
    public void IsDateBasedVersion_NonDateVersion_ReturnsFalse(string input)
    {
        Assert.False(VersionParser.IsDateBasedVersion(input));
    }

    [Theory]
    [InlineData("2024.01.15", true)]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.0-alpha", true)]
    [InlineData("invalid", false)]
    [InlineData("", false)]
    public void IsValidVersion_ReturnsExpectedResult(string input, bool expected)
    {
        Assert.Equal(expected, VersionParser.IsValidVersion(input));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Parse_WhitespaceAroundVersion_TrimsAndParses()
    {
        var result = VersionParser.Parse("  1.0.0  ", "pkg");

        Assert.Equal("1.0.0", result.OriginalVersion);
        Assert.Equal("1.0.0", result.NormalizedVersion);
    }

    [Fact]
    public void Parse_LargeVersionNumbers_HandlesCorrectly()
    {
        var result = VersionParser.Parse("999.999.999", "pkg");

        Assert.Equal("999.999.999", result.OriginalVersion);
        Assert.Equal("999.999.999", result.NormalizedVersion);
    }

    [Fact]
    public void Parse_YearAtBoundary_HandlesCorrectly()
    {
        var result = VersionParser.Parse("2000.01.01", "pkg");
        Assert.True(result.IsDateBased);

        result = VersionParser.Parse("2100.12.31", "pkg");
        Assert.True(result.IsDateBased);
    }

    #endregion
}
