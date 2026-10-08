namespace Cimian.Core.Services;

/// <summary>
/// Reads the repository path from Config.yaml for the admin tools.
/// <para>
/// Config.yaml uses PascalCase keys (<c>SoftwareRepoURL</c>, <c>ClientIdentifier</c>), and
/// <c>cimiimport --config</c> writes <c>RepoPath</c>. makecatalogs, makepkginfo and
/// manifestutil used to read only <c>repo_path</c>, so a repo configured through
/// cimiimport was invisible to them. <c>RepoPath</c> is the key to write; <c>repo_path</c>
/// is still read so existing configs keep working.
/// </para>
/// </summary>
public static class RepoPathConfig
{
    public const string Key = "RepoPath";
    public const string LegacyKey = "repo_path";

    /// <summary>The repository path in a Config.yaml document, or null if it sets none.</summary>
    public static string? Read(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return null;

        var config = YamlUtils.Deserializer.Deserialize<Dictionary<string, object?>>(yaml);
        if (config == null)
            return null;

        foreach (var key in new[] { Key, LegacyKey })
        {
            if (config.TryGetValue(key, out var value) && value is string path && !string.IsNullOrWhiteSpace(path))
                return path;
        }

        return null;
    }

    /// <summary>The repository path in a Config.yaml file, or null if the file is missing or sets none.</summary>
    public static string? ReadFile(string configPath) =>
        File.Exists(configPath) ? Read(File.ReadAllText(configPath)) : null;
}
