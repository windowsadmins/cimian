// InstallInfoFile.cs - Shared model for InstallInfo.yaml
// Written by managedsoftwareupdate at end of check phase, read by MSC GUI.
// This is the single source of truth for what the GUI displays.

using YamlDotNet.Serialization;

namespace Cimian.Core.Models;

/// <summary>
/// Root structure for InstallInfo.yaml — written by managedsoftwareupdate, read by MSC GUI.
/// the GUI just deserializes and renders.
/// </summary>
public class InstallInfoFile
{
    [YamlMember(Alias = "managed_installs")]
    public List<InstallInfoItem> ManagedInstalls { get; set; } = [];

    [YamlMember(Alias = "managed_updates")]
    public List<InstallInfoItem> ManagedUpdates { get; set; } = [];

    [YamlMember(Alias = "removals")]
    public List<InstallInfoItem> Removals { get; set; } = [];

    [YamlMember(Alias = "optional_installs")]
    public List<InstallInfoItem> OptionalInstalls { get; set; } = [];

    [YamlMember(Alias = "problem_items")]
    public List<InstallInfoProblem> ProblemItems { get; set; } = [];

    [YamlMember(Alias = "processed_installs")]
    public List<InstallInfoItem> ProcessedInstalls { get; set; } = [];

    [YamlMember(Alias = "featured_items")]
    public List<string> FeaturedItems { get; set; } = [];

    [YamlMember(Alias = "last_check")]
    public DateTime LastCheck { get; set; }
}

/// <summary>
/// A single software item with full catalog metadata + computed status.
/// </summary>
public class InstallInfoItem
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "display_name")]
    public string? DisplayName { get; set; }

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = string.Empty;

    [YamlMember(Alias = "installed_version")]
    public string? InstalledVersion { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "category")]
    public string? Category { get; set; }

    [YamlMember(Alias = "developer")]
    public string? Developer { get; set; }

    [YamlMember(Alias = "icon")]
    public string? Icon { get; set; }

    [YamlMember(Alias = "installer_item_size")]
    public long InstallerItemSize { get; set; }

    [YamlMember(Alias = "installed")]
    public bool Installed { get; set; }

    [YamlMember(Alias = "needs_update")]
    public bool NeedsUpdate { get; set; }

    [YamlMember(Alias = "uninstallable")]
    public bool Uninstallable { get; set; }

    [YamlMember(Alias = "status")]
    public string Status { get; set; } = string.Empty;

    [YamlMember(Alias = "restart_action")]
    public string? RestartAction { get; set; }

    [YamlMember(Alias = "will_be_installed")]
    public bool WillBeInstalled { get; set; }

    [YamlMember(Alias = "will_be_removed")]
    public bool WillBeRemoved { get; set; }

    [YamlMember(Alias = "notes")]
    public string? Notes { get; set; }

    [YamlMember(Alias = "release_notes")]
    public string? ReleaseNotes { get; set; }

    [YamlMember(Alias = "screenshots")]
    public List<string>? Screenshots { get; set; }

    [YamlMember(Alias = "force_install_after_date")]
    public DateTime? ForceInstallAfterDate { get; set; }

    [YamlMember(Alias = "precached")]
    public bool Precached { get; set; }
}

/// <summary>
/// An item that encountered installation problems.
/// </summary>
public class InstallInfoProblem
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "display_name")]
    public string? DisplayName { get; set; }

    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    [YamlMember(Alias = "error_message")]
    public string? ErrorMessage { get; set; }

    [YamlMember(Alias = "note")]
    public string? Note { get; set; }
}
