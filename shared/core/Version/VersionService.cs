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
        
        // Normalize both versions
        var localNormalized = Normalize(local);
        var remoteNormalized = Normalize(remote);
        
        // Parse and compare
        var localParsed = ParseVersion(localNormalized);
        var remoteParsed = ParseVersion(remoteNormalized);
        
        if (localParsed == null || remoteParsed == null)
        {
            // Fallback to string comparison if parsing fails
            return string.Compare(localNormalized, remoteNormalized, StringComparison.OrdinalIgnoreCase) < 0;
        }
        
        return localParsed.CompareTo(remoteParsed) < 0;
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
