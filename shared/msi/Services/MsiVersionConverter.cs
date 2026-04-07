using System.Text.RegularExpressions;

namespace Cimian.Msi.Services;

/// <summary>
/// Converts version strings to MSI-compatible format.
/// MSI versions are limited to major.minor.build where:
///   major: 0-255
///   minor: 0-255
///   build: 0-65535
///
/// Date-based versions (YYYY.MM.DD.HHMM) are converted to (YY.MDD.HHMM)
/// where YY = year - 2000, MDD = month * 100 + day.
/// The full original version is preserved in CIMIAN_FULL_VERSION property.
/// </summary>
public static partial class MsiVersionConverter
{
    /// <summary>
    /// Convert a version string to MSI-compatible format.
    /// Returns (msiVersion, fullVersion) where fullVersion is the original.
    /// </summary>
    public static (string MsiVersion, string FullVersion) Convert(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var fullVersion = version.Trim();

        // Try date-based: YYYY.MM.DD or YYYY.MM.DD.HHMM or YYYY.MM.DD.revision
        var dateMatch = DateVersionRegex().Match(fullVersion);
        if (dateMatch.Success)
        {
            var year = int.Parse(dateMatch.Groups[1].Value);
            var month = int.Parse(dateMatch.Groups[2].Value);
            var day = int.Parse(dateMatch.Groups[3].Value);
            var suffix = dateMatch.Groups[4].Success ? int.Parse(dateMatch.Groups[4].Value) : 0;

            if (year >= 2000 && month is >= 1 and <= 12 && day is >= 1 and <= 31)
            {
                var major = year - 2000; // 2026 → 26 (fits in 0-255)
                var minor = month * 100 + day; // 4*100+5 = 405 (fits in 0-65535 via build)
                var build = suffix; // HHMM or revision (fits in 0-65535)

                // If minor > 255, pack into: major.0.MMDDHHMM or use build for both
                if (minor <= 255 && build <= 65535)
                {
                    return ($"{major}.{minor}.{build}", fullVersion);
                }

                // Minor > 255: pack month+day into build field
                // major.0.(MMDD * 10 + revision_last_digit) — but this loses precision
                // Better: major.month.(day * 10000 + suffix_clamped)
                if (build <= 0)
                {
                    return ($"{major}.{month}.{day}", fullVersion);
                }

                // Full date+time: pack as major.month.(day*2400 + HH*100+MM) — but may exceed 65535
                // day*2400 max = 31*2400 = 74400 > 65535. So: major.month.(day*100 + HH)
                var hh = build / 100;
                var buildPacked = day * 100 + hh;
                if (buildPacked <= 65535)
                {
                    return ($"{major}.{month}.{buildPacked}", fullVersion);
                }

                // Fallback: just use day
                return ($"{major}.{month}.{day}", fullVersion);
            }
        }

        // Try semantic: X.Y.Z or X.Y.Z.W (may have prerelease suffix)
        var semMatch = SemanticVersionRegex().Match(fullVersion);
        if (semMatch.Success)
        {
            var major = int.Parse(semMatch.Groups[1].Value);
            var minor = int.Parse(semMatch.Groups[2].Value);
            var patch = semMatch.Groups[3].Success ? int.Parse(semMatch.Groups[3].Value) : 0;

            // Clamp to MSI limits
            major = Math.Min(major, 255);
            minor = Math.Min(minor, 255);
            patch = Math.Min(patch, 65535);

            return ($"{major}.{minor}.{patch}", fullVersion);
        }

        // Fallback: try to use as-is, or default to 1.0.0
        return IsValidMsiVersion(fullVersion) ? (fullVersion, fullVersion) : ("1.0.0", fullVersion);
    }

    /// <summary>
    /// Validate whether a version string is MSI-compatible.
    /// </summary>
    public static bool IsValidMsiVersion(string version)
    {
        var match = MsiVersionRegex().Match(version);
        if (!match.Success) return false;

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

        return major <= 255 && minor <= 255 && build <= 65535;
    }

    [GeneratedRegex(@"^(\d{4})\.(\d{1,2})\.(\d{1,2})(?:\.(\d+))?$")]
    private static partial Regex DateVersionRegex();

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?(?:[.-].*)?$")]
    private static partial Regex SemanticVersionRegex();

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?$")]
    private static partial Regex MsiVersionRegex();
}
