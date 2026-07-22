using System.Reflection;
using System.Text.RegularExpressions;

namespace Cimian.Core.Version;

/// <summary>
/// Version comparison and normalization service
/// Migrated from Go: pkg/version/version.go and pkg/status/status.go (IsOlderVersion)
/// 
/// This must produce identical results to the Go implementation for all version formats:
/// - Semantic versions (1.0.0, 1.2.3-beta)
/// - Windows build numbers (10.0.19045, 10.0.22621)
/// - Chrome-style versions (139.0.7258.139)
/// - Date-based versions (2024.1.2.3)
/// - v-prefixed versions (v1.0.0)
/// </summary>
public static class VersionService
{
    private static readonly Regex VersionCleanupRegex = new(@"^v", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PreReleaseRegex = new(@"-(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the running Cimian agent version from assembly metadata.
    /// Reads the entry assembly's AssemblyInformationalVersion (CI builds embed
    /// yyyy.MM.dd.HHmm here) with any "+commit" suffix stripped, falling back
    /// to AssemblyFileVersion and then AssemblyVersion (e.g. "1.0.0.0") for
    /// dev builds without an informational version stamp. Format is therefore
    /// build-dependent; do not assume yyyy.MM.dd.HHmm.
    /// </summary>
    public static string GetRunningAgentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+');
            return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrEmpty(fileVersion))
        {
            return fileVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "UNKNOWN";
    }
    
    /// <summary>
    /// Compares two version strings and returns true if local is older than remote.
    /// Migrated from Go: pkg/status/status.go IsOlderVersion()
    /// </summary>
    /// <param name="local">The local/installed version</param>
    /// <param name="remote">The remote/catalog version</param>
    /// <returns>True if local is strictly older than remote</returns>
    public static bool IsOlderVersion(string? local, string? remote)
    {
        // Handle null/empty cases
        if (string.IsNullOrWhiteSpace(local) && string.IsNullOrWhiteSpace(remote))
            return false;
        
        if (string.IsNullOrWhiteSpace(local))
            return true; // Empty local is always "older" than any remote
            
        if (string.IsNullOrWhiteSpace(remote))
            return false; // Any local is not older than empty remote
        
        return CompareVersions(local, remote) < 0;
    }
    
    /// <summary>
    /// Compares two version strings.
    /// Returns: -1 if v1 &lt; v2, 0 if v1 == v2, 1 if v1 &gt; v2
    /// </summary>
    public static int CompareVersions(string? v1, string? v2)
    {
        if (string.IsNullOrWhiteSpace(v1) && string.IsNullOrWhiteSpace(v2))
            return 0;
            
        if (string.IsNullOrWhiteSpace(v1))
            return -1;
            
        if (string.IsNullOrWhiteSpace(v2))
            return 1;

        // When both are Cimian calendar build stamps, compare by decoded build time.
        // Element-wise numeric comparison mis-orders the legacy 3-component form:
        // "2026.7.2006" -> [2026,7,2006] sorts newer than "2026.07.20.0632" ->
        // [2026,7,20,632] because 2006 > 20, which would let a stale agent consider
        // itself current and suppress its own self-update. See TryParseCalendarBuildStamp.
        if (TryParseCalendarBuildStamp(v1, out var stamp1) &&
            TryParseCalendarBuildStamp(v2, out var stamp2))
        {
            return stamp1.CompareTo(stamp2);
        }

        var v1Normalized = Normalize(v1);
        var v2Normalized = Normalize(v2);
        
        var v1Parsed = ParseVersion(v1Normalized);
        var v2Parsed = ParseVersion(v2Normalized);
        
        if (v1Parsed == null || v2Parsed == null)
        {
            return string.Compare(v1Normalized, v2Normalized, StringComparison.OrdinalIgnoreCase);
        }
        
        return v1Parsed.CompareTo(v2Parsed);
    }
    
    /// <summary>
    /// Returns the running Windows OS version as a string in the form "10.0.x.y".
    /// </summary>
    public static string GetCurrentOsVersion()
    {
        return Environment.OSVersion.Version.ToString();
    }

    /// <summary>
    /// Build number at which the Windows kernel began shipping as Windows 11.
    /// Windows 11 kept the 10.0 major.minor of Windows 10 and is distinguished
    /// only by build number, so Environment.OSVersion reports it as "10.0.22000+".
    /// </summary>
    private const long Windows11MinimumBuild = 22000;

    /// <summary>
    /// Compares a running Windows OS version against a minimum/maximum requirement
    /// with awareness that Windows 11 reports a "10.0.&lt;build&gt;" kernel version.
    /// Package authors write the marketing version ("11" / "11.0") in
    /// minimum_os_version / maximum_os_version, so a literal numeric compare against
    /// the running "10.0.26200" would wrongly reject Windows 11.
    ///
    /// When the requirement is a bare marketing major ("10" or "11"), the comparison
    /// is by Windows generation only, so any Windows 11 build satisfies an "11" floor
    /// or ceiling (and any Windows 10 build satisfies a "10" one). When the requirement
    /// pins a build (e.g. "10.0.22631"), versions are compared numerically.
    ///
    /// Returns: -1 if current is older than required, 0 if equivalent, 1 if newer.
    /// </summary>
    public static int CompareOsVersion(string? current, string? requirement)
    {
        if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(requirement))
            return 0;
        if (string.IsNullOrWhiteSpace(current))
            return -1;
        if (string.IsNullOrWhiteSpace(requirement))
            return 1;

        // Bare marketing major ("10" / "11"): compare by Windows generation only.
        if (TryGetMarketingMajor(Normalize(requirement), out var requiredGen))
        {
            return WindowsGenerationOf(current).CompareTo(requiredGen);
        }

        // Build-pinned requirement: compare numerically, but first fold any
        // marketing "11.x.<build>" form (the example cimiimport prints for
        // --maximum_os_version, e.g. "11.0.22000") into the "10.0.<build>"
        // kernel version Windows actually reports — otherwise a build-pinned
        // Win 11 value would compare as newer than the running "10.0.<build>".
        return CompareVersions(ToKernelWindowsVersion(current), ToKernelWindowsVersion(requirement));
    }

    /// <summary>
    /// Folds a marketing Windows 11 version ("11.x.&lt;build&gt;") into the kernel
    /// version Windows reports ("10.0.&lt;build&gt;"). Windows 11 kept the 10.0
    /// major.minor and differs only by build, so the build tail is preserved.
    /// Versions that don't lead with a "11" major pass through unchanged.
    /// </summary>
    private static string ToKernelWindowsVersion(string version)
    {
        var parts = version.Trim().Split('.');
        if (parts.Length >= 2 && parts[0] == "11")
        {
            // Keep everything after major.minor (the build[.revision] tail).
            var tail = parts.Skip(2).ToArray();
            return tail.Length > 0 ? $"10.0.{string.Join('.', tail)}" : "10.0";
        }
        return version;
    }

    /// <summary>
    /// True when a normalized version is a bare Windows marketing major ("10" or "11"),
    /// i.e. has no build segment that would pin it to a specific release.
    /// </summary>
    private static bool TryGetMarketingMajor(string normalized, out int major)
    {
        major = 0;
        var parts = normalized.Split('.');
        if (parts.Length == 1 && int.TryParse(parts[0], out var m) && (m == 10 || m == 11))
        {
            major = m;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Maps a running Windows version (e.g. "10.0.26200.0") to its marketing
    /// generation: build >= 22000 is Windows 11, otherwise Windows 10.
    /// </summary>
    private static int WindowsGenerationOf(string version)
    {
        var parts = Normalize(version).Split('.');
        // Build number lives in the third segment of major.minor.build[.revision].
        if (parts.Length >= 3 && long.TryParse(parts[2], out var build))
        {
            return build >= Windows11MinimumBuild ? 11 : 10;
        }
        // No build present: fall back to the literal major.
        return long.TryParse(parts[0], out var major) ? (int)major : 0;
    }

    /// <summary>
    /// Normalizes a version string by trimming trailing ".0" segments.
    /// Migrated from Go: pkg/version/version.go Normalize()
    /// </summary>
    /// <param name="version">The version string to normalize</param>
    /// <returns>Normalized version string</returns>
    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;
        
        // Remove v prefix
        var normalized = VersionCleanupRegex.Replace(version.Trim(), "");
        
        // Split by dot
        var parts = normalized.Split('.');
        
        // Trim trailing "0" segments (but keep at least one segment)
        var trimmedParts = new List<string>(parts);
        while (trimmedParts.Count > 1 && trimmedParts[^1] == "0")
        {
            trimmedParts.RemoveAt(trimmedParts.Count - 1);
        }
        
        return string.Join(".", trimmedParts);
    }
    
    /// <summary>
    /// Bounds for treating a leading 4-digit component as a calendar year rather
    /// than a large version major (e.g. Unity's 6000.x). Mirrors cimipkg's
    /// VersionParser year gate.
    /// </summary>
    private const int CalendarYearMin = 2000;
    private const int CalendarYearMax = 2100;

    /// <summary>
    /// Decodes a Cimian calendar build stamp into a sortable YYYYMMDDHHMM key.
    ///
    /// The client stamps builds as <c>yyyy.MM.dd.HHmm</c> (canonical, e.g.
    /// <c>2026.07.20.0632</c>). Older builds emitted a 3-component <c>yyyy.M.DDHH</c>
    /// form with day and hour merged and no minutes (<c>2026.7.2006</c> is
    /// 2026-07-20 06:00), or a plain <c>yyyy.M.d</c>. All encode a build datetime;
    /// this returns them on one comparable scale so agent-version currency is reliable.
    ///
    /// Requires an explicit 4-digit year in [<see cref="CalendarYearMin"/>,
    /// <see cref="CalendarYearMax"/>]. Semantic versions, Windows build numbers, and
    /// Chrome-style versions therefore never match and fall through to the general
    /// comparison unchanged. The 2-digit MSI ProductVersion form (<c>26.7.2118</c>) is
    /// deliberately not matched — it never enters the agent version path and matching
    /// it would collide with ordinary two-digit-major semver.
    /// </summary>
    /// <returns>True and sets <paramref name="sortKey"/> if <paramref name="version"/>
    /// is a recognizable calendar build stamp; otherwise false.</returns>
    internal static bool TryParseCalendarBuildStamp(string? version, out long sortKey)
    {
        sortKey = 0;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var parts = version.Trim().Split('.');
        if (parts.Length < 3)
            return false;
        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsDigit))
                return false;
        }

        // 4-digit calendar-year gate.
        if (parts[0].Length != 4 || !int.TryParse(parts[0], out var year))
            return false;
        if (year < CalendarYearMin || year > CalendarYearMax)
            return false;

        var month = int.Parse(parts[1]);
        if (month < 1 || month > 12)
            return false;

        int day, hour, minute;
        if (parts.Length >= 4)
        {
            // yyyy.MM.dd.HHmm
            day = int.Parse(parts[2]);
            var hhmm = int.Parse(parts[3]);
            hour = hhmm / 100;
            minute = hhmm % 100;
        }
        else
        {
            // Exactly 3 components.
            var third = int.Parse(parts[2]);
            if (third <= 31)
            {
                // yyyy.M.d — plain calendar day, no time component.
                day = third;
                hour = 0;
                minute = 0;
            }
            else if (third >= 100)
            {
                // yyyy.M.DDHH — day and hour merged, minutes dropped.
                day = third / 100;
                hour = third % 100;
                minute = 0;
            }
            else
            {
                // 32..99: neither a valid day nor a DDHH pair.
                return false;
            }
        }

        if (day < 1 || day > 31 || hour < 0 || hour > 23 || minute < 0 || minute > 59)
            return false;

        sortKey = ((((long)year * 100 + month) * 100 + day) * 100 + hour) * 100 + minute;
        return true;
    }

    /// <summary>
    /// Parses a version string into a comparable Version object.
    /// Handles various formats: semantic, Windows build, Chrome-style, date-based.
    /// </summary>
    private static ParsedVersion? ParseVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        
        try
        {
            // Remove v prefix if present
            var cleanVersion = VersionCleanupRegex.Replace(version.Trim(), "");
            
            // Extract pre-release suffix
            string? preRelease = null;
            var preReleaseMatch = PreReleaseRegex.Match(cleanVersion);
            if (preReleaseMatch.Success)
            {
                preRelease = preReleaseMatch.Groups[1].Value;
                cleanVersion = cleanVersion[..preReleaseMatch.Index];
            }
            
            // Split into numeric parts
            var parts = cleanVersion.Split('.');
            var numericParts = new List<long>();
            
            foreach (var part in parts)
            {
                // Try to parse each part as a number
                if (long.TryParse(part, out var num))
                {
                    numericParts.Add(num);
                }
                else
                {
                    // Handle non-numeric parts (like "beta1")
                    // Try to extract leading digits
                    var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                    if (!string.IsNullOrEmpty(digits) && long.TryParse(digits, out var leadingNum))
                    {
                        numericParts.Add(leadingNum);
                    }
                    else
                    {
                        numericParts.Add(0);
                    }
                }
            }
            
            // Ensure at least one part
            if (numericParts.Count == 0)
            {
                numericParts.Add(0);
            }
            
            return new ParsedVersion(numericParts.ToArray(), preRelease);
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Internal representation of a parsed version for comparison
    /// </summary>
    private class ParsedVersion : IComparable<ParsedVersion>
    {
        public long[] Parts { get; }
        public string? PreRelease { get; }
        
        public ParsedVersion(long[] parts, string? preRelease)
        {
            Parts = parts;
            PreRelease = preRelease;
        }
        
        public int CompareTo(ParsedVersion? other)
        {
            if (other == null) return 1;
            
            // Compare numeric parts
            var maxLength = Math.Max(Parts.Length, other.Parts.Length);
            for (int i = 0; i < maxLength; i++)
            {
                var thisPart = i < Parts.Length ? Parts[i] : 0;
                var otherPart = i < other.Parts.Length ? other.Parts[i] : 0;
                
                if (thisPart < otherPart) return -1;
                if (thisPart > otherPart) return 1;
            }
            
            // If numeric parts are equal, compare pre-release
            // No pre-release > any pre-release (1.0.0 > 1.0.0-beta)
            if (PreRelease == null && other.PreRelease != null) return 1;
            if (PreRelease != null && other.PreRelease == null) return -1;
            if (PreRelease != null && other.PreRelease != null)
            {
                return ComparePreRelease(PreRelease, other.PreRelease);
            }
            
            return 0;
        }
        
        private static int ComparePreRelease(string a, string b)
        {
            // Common pre-release order: alpha < beta < rc < (none)
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "alpha", 1 },
                { "a", 1 },
                { "beta", 2 },
                { "b", 2 },
                { "rc", 3 },
                { "preview", 2 },
                { "dev", 0 }
            };
            
            // Extract base name and number (e.g., "beta1" -> "beta", 1)
            var aMatch = Regex.Match(a, @"^([a-zA-Z]+)(\d*)$");
            var bMatch = Regex.Match(b, @"^([a-zA-Z]+)(\d*)$");
            
            if (aMatch.Success && bMatch.Success)
            {
                var aBase = aMatch.Groups[1].Value;
                var bBase = bMatch.Groups[1].Value;
                
                var aOrder = order.GetValueOrDefault(aBase, 99);
                var bOrder = order.GetValueOrDefault(bBase, 99);
                
                if (aOrder != bOrder) return aOrder.CompareTo(bOrder);
                
                // Same base, compare numbers
                var aNum = string.IsNullOrEmpty(aMatch.Groups[2].Value) ? 0 : int.Parse(aMatch.Groups[2].Value);
                var bNum = string.IsNullOrEmpty(bMatch.Groups[2].Value) ? 0 : int.Parse(bMatch.Groups[2].Value);
                
                return aNum.CompareTo(bNum);
            }
            
            // Fallback to string comparison
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
