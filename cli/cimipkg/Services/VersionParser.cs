using System;
using System.Text.RegularExpressions;

namespace Cimian.CLI.Cimipkg.Services;

/// <summary>
/// Result of version parsing containing both original and normalized versions.
/// </summary>
public record VersionParseResult(
    string OriginalVersion,
    string NormalizedVersion,
    bool IsDateBased
);

/// <summary>
/// Parses and normalizes version strings for package building.
/// Supports date-based versions (YYYY.MM.DD) and semantic versions (x.y.z).
/// </summary>
public static class VersionParser
{
    // Regex patterns for version formats
    private static readonly Regex DateVersionPattern = new(
        @"^(\d{4})\.(\d{1,2})\.(\d{1,2})(?:\.(\d+))?$",
        RegexOptions.Compiled);
    
    private static readonly Regex SemVerPattern = new(
        @"^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([a-zA-Z0-9.-]+))?(?:\+([a-zA-Z0-9.-]+))?$",
        RegexOptions.Compiled);
    
    private static readonly Regex SimpleVersionPattern = new(
        @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a version string and returns both original and normalized versions.
    /// </summary>
    /// <param name="version">The version string to parse.</param>
    /// <param name="packageFormat">Target package format: "pkg" or "nupkg".</param>
    /// <returns>Parsed version result with original and normalized versions.</returns>
    /// <exception cref="ArgumentException">Thrown when version format is invalid.</exception>
    public static VersionParseResult Parse(string version, string packageFormat = "pkg")
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Version cannot be null or empty.", nameof(version));
        }

        version = version.Trim();

        // Check for date-based version (YYYY.MM.DD or YYYY.MM.DD.revision)
        var dateMatch = DateVersionPattern.Match(version);
        if (dateMatch.Success)
        {
            return ParseDateVersion(dateMatch, packageFormat);
        }

        // Check for semantic version
        var semVerMatch = SemVerPattern.Match(version);
        if (semVerMatch.Success)
        {
            return ParseSemVer(semVerMatch, packageFormat);
        }

        // Check for simple version (1.0, 1.0.0, 1.0.0.0)
        var simpleMatch = SimpleVersionPattern.Match(version);
        if (simpleMatch.Success)
        {
            return ParseSimpleVersion(simpleMatch, packageFormat);
        }

        throw new ArgumentException(
            $"Invalid version format: '{version}'. Expected YYYY.MM.DD, x.y.z, or x.y.z.w format.",
            nameof(version));
    }

    /// <summary>
    /// Parses a date-based version (YYYY.MM.DD or YYYY.MM.DD.revision).
    /// For .nupkg format, converts to NuGet-compatible version.
    /// </summary>
    private static VersionParseResult ParseDateVersion(Match match, string packageFormat)
    {
        var year = int.Parse(match.Groups[1].Value);
        var month = int.Parse(match.Groups[2].Value);
        var day = int.Parse(match.Groups[3].Value);
        var revision = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;

        // Validate date components
        if (year < 2000 || year > 2100)
        {
            throw new ArgumentException($"Year {year} is out of valid range (2000-2100).");
        }
        if (month < 1 || month > 12)
        {
            throw new ArgumentException($"Month {month} is out of valid range (1-12).");
        }
        if (day < 1 || day > 31)
        {
            throw new ArgumentException($"Day {day} is out of valid range (1-31).");
        }

        // Original version preserves the exact format for filenames
        var originalVersion = revision > 0
            ? $"{year}.{month:D2}.{day:D2}.{revision}"
            : $"{year}.{month:D2}.{day:D2}";

        // Normalized version for NuGet compatibility
        // NuGet requires x.y.z or x.y.z.w format with each component < 65535
        string normalizedVersion;
        if (packageFormat == "nupkg")
        {
            // For NuGet, we need to use a compatible format
            // YYYY.MM.DD becomes YYYY.M.D (strip leading zeros) for major.minor.patch
            // with optional revision as fourth component
            normalizedVersion = revision > 0
                ? $"{year}.{month}.{day}.{revision}"
                : $"{year}.{month}.{day}";
        }
        else
        {
            // For .pkg format, use the original format
            normalizedVersion = originalVersion;
        }

        return new VersionParseResult(originalVersion, normalizedVersion, IsDateBased: true);
    }

    /// <summary>
    /// Parses a semantic version (x.y.z or x.y.z-prerelease+build).
    /// </summary>
    private static VersionParseResult ParseSemVer(Match match, string packageFormat)
    {
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = int.Parse(match.Groups[3].Value);
        var revision = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
        var prerelease = match.Groups[5].Success ? match.Groups[5].Value : null;
        var buildMetadata = match.Groups[6].Success ? match.Groups[6].Value : null;

        // Construct original version (used for filenames)
        var originalVersion = revision > 0
            ? $"{major}.{minor}.{patch}.{revision}"
            : $"{major}.{minor}.{patch}";

        if (!string.IsNullOrEmpty(prerelease))
        {
            originalVersion += $"-{prerelease}";
        }
        if (!string.IsNullOrEmpty(buildMetadata))
        {
            originalVersion += $"+{buildMetadata}";
        }

        // Normalized version for NuGet (no build metadata, prerelease supported)
        string normalizedVersion;
        if (packageFormat == "nupkg")
        {
            normalizedVersion = revision > 0
                ? $"{major}.{minor}.{patch}.{revision}"
                : $"{major}.{minor}.{patch}";

            if (!string.IsNullOrEmpty(prerelease))
            {
                normalizedVersion += $"-{prerelease}";
            }
            // NuGet ignores build metadata
        }
        else
        {
            // For .pkg format, use original without build metadata
            normalizedVersion = revision > 0
                ? $"{major}.{minor}.{patch}.{revision}"
                : $"{major}.{minor}.{patch}";

            if (!string.IsNullOrEmpty(prerelease))
            {
                normalizedVersion += $"-{prerelease}";
            }
        }

        return new VersionParseResult(originalVersion, normalizedVersion, IsDateBased: false);
    }

    /// <summary>
    /// Parses a simple version (1, 1.0, 1.0.0, 1.0.0.0).
    /// </summary>
    private static VersionParseResult ParseSimpleVersion(Match match, string packageFormat)
    {
        var major = int.Parse(match.Groups[1].Value);
        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        var revision = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;

        // Build version string with appropriate components
        var originalVersion = revision > 0
            ? $"{major}.{minor}.{patch}.{revision}"
            : $"{major}.{minor}.{patch}";

        // For NuGet, ensure at least 3 components
        var normalizedVersion = revision > 0
            ? $"{major}.{minor}.{patch}.{revision}"
            : $"{major}.{minor}.{patch}";

        return new VersionParseResult(originalVersion, normalizedVersion, IsDateBased: false);
    }

    /// <summary>
    /// Checks if a version string looks like a date-based version.
    /// </summary>
    public static bool IsDateBasedVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        return DateVersionPattern.IsMatch(version.Trim());
    }

    /// <summary>
    /// Validates that a version string is in a supported format.
    /// </summary>
    public static bool IsValidVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        version = version.Trim();

        return DateVersionPattern.IsMatch(version)
            || SemVerPattern.IsMatch(version)
            || SimpleVersionPattern.IsMatch(version);
    }
}
