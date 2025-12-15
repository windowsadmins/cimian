using YamlDotNet.Serialization;

namespace Cimian.CLI.Manifestutil.Models;

/// <summary>
/// Represents a package deployment manifest that defines which packages to install/uninstall
/// Migrated from Go: Manifest struct in cmd/manifestutil/main.go
/// </summary>
public class PackageManifest
{
    /// <summary>
    /// Human-readable name for the manifest
    /// </summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Packages that should be installed and kept up to date
    /// </summary>
    [YamlMember(Alias = "managed_installs")]
    public List<string>? ManagedInstalls { get; set; }

    /// <summary>
    /// Packages that should be uninstalled if present
    /// </summary>
    [YamlMember(Alias = "managed_uninstalls")]
    public List<string>? ManagedUninstalls { get; set; }

    /// <summary>
    /// Packages that should be updated if already installed (but not force-installed)
    /// </summary>
    [YamlMember(Alias = "managed_updates")]
    public List<string>? ManagedUpdates { get; set; }

    /// <summary>
    /// Packages available for optional installation by user choice
    /// </summary>
    [YamlMember(Alias = "optional_installs")]
    public List<string>? OptionalInstalls { get; set; }

    /// <summary>
    /// Other manifests to include (inheritance)
    /// </summary>
    [YamlMember(Alias = "included_manifests")]
    public List<string>? IncludedManifests { get; set; }

    /// <summary>
    /// Catalogs to use for package resolution
    /// </summary>
    [YamlMember(Alias = "catalogs")]
    public List<string>? Catalogs { get; set; }
}

/// <summary>
/// Cimian configuration file structure
/// </summary>
public class CimianConfig
{
    /// <summary>
    /// Path to the package repository
    /// </summary>
    [YamlMember(Alias = "repo_path")]
    public string? RepoPath { get; set; }

    /// <summary>
    /// Path to managed installs directory
    /// </summary>
    [YamlMember(Alias = "managed_installs_dir")]
    public string? ManagedInstallsDir { get; set; }
}
