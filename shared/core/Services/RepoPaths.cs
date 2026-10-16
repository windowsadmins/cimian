namespace Cimian.Core.Services;

/// <summary>
/// File names of repository files in each format. A YAML catalog or manifest is
/// name.yaml, a plist one has no extension (Munki's layout). The client builds
/// catalog and manifest addresses here instead of writing an extension. The client's
/// local files (cache, Config.yaml, InstallInfo.yaml) are not repository files and
/// are always YAML.
/// </summary>
public static class RepoPaths
{
    private const string YamlExtension = ".yaml";
    private const string PlistExtension = ".plist";

    /// <summary>name.yaml, or name without an extension for a plist.</summary>
    public static string CatalogFileName(string name, RepoFormat format)
        => format == RepoFormat.Plist ? name : name + YamlExtension;

    /// <summary>name.yaml, or name without an extension for a plist.</summary>
    public static string ManifestFileName(string name, RepoFormat format)
        => format == RepoFormat.Plist ? name : name + YamlExtension;

    /// <summary>
    /// The name without one .yaml or .plist suffix, in any letter case. A name
    /// with neither is returned as it is.
    /// </summary>
    public static string StripSuffix(string name)
    {
        if (name.EndsWith(YamlExtension, StringComparison.OrdinalIgnoreCase))
            return name[..^YamlExtension.Length];
        if (name.EndsWith(PlistExtension, StringComparison.OrdinalIgnoreCase))
            return name[..^PlistExtension.Length];
        return name;
    }
}
