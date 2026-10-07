namespace Cimian.Core.Services;

/// <summary>
/// The encoding of a repository file (pkginfo, manifest, catalog). Yaml is the
/// default and Plist is an XML property list with the same keys. A client's local
/// files (Config.yaml, InstallInfo.yaml, the cache) are always YAML.
/// </summary>
public enum RepoFormat
{
    Yaml,
    Plist,
}

/// <summary>
/// The kind of repository file. A catalog is a mapping with the single key items
/// in YAML and a bare array in a plist (Munki's shape).
/// </summary>
public enum RepoFileKind
{
    PkgInfo,
    Manifest,
    Catalog,
}

public static class RepoFormatParser
{
    /// <summary>
    /// The format named by a setting value: yaml or plist, in any letter case,
    /// with surrounding white space ignored. Null or empty means YAML. Any
    /// other text throws, so that a typo never silently selects a format.
    /// </summary>
    public static RepoFormat Parse(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return RepoFormat.Yaml;
        if (string.Equals(text, "yaml", StringComparison.OrdinalIgnoreCase))
            return RepoFormat.Yaml;
        if (string.Equals(text, "plist", StringComparison.OrdinalIgnoreCase))
            return RepoFormat.Plist;
        throw new ArgumentException($"Unknown repository format '{value}': use yaml or plist.");
    }

    /// <summary>The setting's word for a format: yaml or plist.</summary>
    public static string Name(RepoFormat format) => format switch
    {
        RepoFormat.Plist => "plist",
        _ => "yaml",
    };
}
