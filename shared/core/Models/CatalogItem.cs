using System.ComponentModel.DataAnnotations;
using YamlDotNet.Serialization;

namespace Cimian.Core.Models;

/// <summary>
/// Represents a catalog item that defines an installable package
/// Migrated from Go struct: catalog.Item
/// </summary>
public class CatalogItem
{
    /// <summary>
    /// Unique identifier for the catalog item
    /// </summary>
    [Required]
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Version of the package
    /// </summary>
    [Required]
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the package
    /// </summary>
    [YamlMember(Alias = "display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Description of the package
    /// </summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Category for grouping packages
    /// </summary>
    [YamlMember(Alias = "category")]
    public string? Category { get; set; }

    /// <summary>
    /// Developer/publisher information
    /// </summary>
    [YamlMember(Alias = "developer")]
    public string? Developer { get; set; }

    /// <summary>
    /// Direct download URL for the package
    /// </summary>
    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    /// <summary>
    /// SHA256 hash for verification
    /// </summary>
    [YamlMember(Alias = "hash")]
    public string? Hash { get; set; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    [YamlMember(Alias = "size")]
    public long? Size { get; set; }

    /// <summary>
    /// Installer type (msi, exe, powershell, nupkg, msix)
    /// </summary>
    [YamlMember(Alias = "installer_type")]
    public string? InstallerType { get; set; }

    /// <summary>
    /// Installation arguments/flags
    /// </summary>
    [YamlMember(Alias = "installer_args")]
    public List<string>? InstallerArgs { get; set; }

    /// <summary>
    /// Uninstall arguments/flags
    /// </summary>
    [YamlMember(Alias = "uninstaller_args")]
    public List<string>? UninstallerArgs { get; set; }

    /// <summary>
    /// Pre-install PowerShell script
    /// </summary>
    [YamlMember(Alias = "preinstall_script")]
    public string? PreinstallScript { get; set; }

    /// <summary>
    /// Post-install PowerShell script
    /// </summary>
    [YamlMember(Alias = "postinstall_script")]
    public string? PostinstallScript { get; set; }

    /// <summary>
    /// Pre-uninstall PowerShell script
    /// </summary>
    [YamlMember(Alias = "preuninstall_script")]
    public string? PreuninstallScript { get; set; }

    /// <summary>
    /// Post-uninstall PowerShell script
    /// </summary>
    [YamlMember(Alias = "postuninstall_script")]
    public string? PostuninstallScript { get; set; }

    /// <summary>
    /// Package dependencies
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<string>? Requires { get; set; }

    /// <summary>
    /// Packages that must be updated with this package
    /// </summary>
    [YamlMember(Alias = "update_for")]
    public List<string>? UpdateFor { get; set; }

    /// <summary>
    /// Minimum OS version required
    /// </summary>
    [YamlMember(Alias = "minimum_os_version")]
    public string? MinimumOsVersion { get; set; }

    /// <summary>
    /// Maximum OS version supported
    /// </summary>
    [YamlMember(Alias = "maximum_os_version")]
    public string? MaximumOsVersion { get; set; }

    /// <summary>
    /// Supported architectures (x64, arm64, etc.)
    /// </summary>
    [YamlMember(Alias = "supported_architectures")]
    public List<string>? SupportedArchitectures { get; set; }

    /// <summary>
    /// Installation timeout in minutes
    /// </summary>
    [YamlMember(Alias = "installer_timeout")]
    public int? InstallerTimeout { get; set; }

    /// <summary>
    /// Whether installation requires elevation
    /// </summary>
    [YamlMember(Alias = "requires_elevation")]
    public bool RequiresElevation { get; set; } = true;

    /// <summary>
    /// Whether to restart after installation
    /// </summary>
    [YamlMember(Alias = "restart_required")]
    public bool RestartRequired { get; set; }

    /// <summary>
    /// Time window during which installation is allowed
    /// </summary>
    [YamlMember(Alias = "install_window")]
    public InstallWindowInfo? InstallWindow { get; set; }

    /// <summary>
    /// Additional metadata for cloud/enterprise features
    /// </summary>
    [YamlMember(Alias = "metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Returns the effective display name (DisplayName or Name)
    /// </summary>
    public string GetDisplayName() => DisplayName ?? Name;

    /// <summary>
    /// Returns the full package identifier (name-version)
    /// </summary>
    public string GetPackageId() => $"{Name}-{Version}";

    /// <summary>
    /// Checks if the package supports the specified architecture
    /// </summary>
    public bool SupportsArchitecture(string architecture)
    {
        return SupportedArchitectures?.Contains(architecture, StringComparer.OrdinalIgnoreCase) ?? true;
    }
}

/// <summary>
/// Defines a time window during which installation is allowed
/// </summary>
public class InstallWindowInfo
{
    [YamlMember(Alias = "start")]
    public string Start { get; set; } = string.Empty;

    [YamlMember(Alias = "end")]
    public string End { get; set; } = string.Empty;

    [YamlMember(Alias = "weekdays")]
    public List<string>? Weekdays { get; set; }
}
