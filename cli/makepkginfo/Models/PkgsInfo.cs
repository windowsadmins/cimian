using YamlDotNet.Serialization;

namespace Cimian.CLI.Makepkginfo.Models;

/// <summary>
/// Installer details for a package
/// Migrated from Go: Installer struct in cmd/makepkginfo/main.go
/// </summary>
public class Installer
{
    [YamlMember(Alias = "type", Order = 1)]
    public string? Type { get; set; }

    [YamlMember(Alias = "size", Order = 2)]
    public long? Size { get; set; }

    [YamlMember(Alias = "location", Order = 3)]
    public string? Location { get; set; }

    [YamlMember(Alias = "hash", Order = 4)]
    public string? Hash { get; set; }

    // MSI ProductCode/UpgradeCode live on the installs[] type=msi entry, not here.
    [YamlMember(Alias = "arguments", Order = 7)]
    public List<string>? Arguments { get; set; }
}

/// <summary>
/// Installation check item
/// Migrated from Go: InstallItem struct in cmd/makepkginfo/main.go
/// </summary>
public class InstallItem
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "file";

    // Nullable so OmitNull suppresses the key on type=msi entries (which have
    // no path — install identity comes from product_code/upgrade_code).
    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    [YamlMember(Alias = "md5checksum")]
    public string? Md5Checksum { get; set; }

    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    [YamlMember(Alias = "product_code")]
    public string? ProductCode { get; set; }

    [YamlMember(Alias = "upgrade_code")]
    public string? UpgradeCode { get; set; }
}

/// <summary>
/// Package information structure
/// Migrated from Go: PkgsInfo struct in cmd/makepkginfo/main.go
/// </summary>
public class PkgsInfo
{
    [YamlMember(Alias = "name", Order = 1)]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "display_name", Order = 2, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? DisplayName { get; set; }

    [YamlMember(Alias = "identifier", Order = 3, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? Identifier { get; set; }

    [YamlMember(Alias = "version", Order = 4)]
    public string Version { get; set; } = string.Empty;

    [YamlMember(Alias = "catalogs", Order = 5, DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Catalogs { get; set; } = new();

    [YamlMember(Alias = "category", Order = 6, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? Category { get; set; }

    [YamlMember(Alias = "description", Order = 7, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? Description { get; set; }

    [YamlMember(Alias = "developer", Order = 8, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? Developer { get; set; }

    [YamlMember(Alias = "installer_type", Order = 9, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? InstallerType { get; set; }

    [YamlMember(Alias = "unattended_install", Order = 10, DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool UnattendedInstall { get; set; }

    [YamlMember(Alias = "minimum_os_version", Order = 11, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? MinOSVersion { get; set; }

    [YamlMember(Alias = "maximum_os_version", Order = 12, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? MaxOSVersion { get; set; }

    [YamlMember(Alias = "minimum_cimian_version", Order = 13, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? MinCimianVersion { get; set; }

    [YamlMember(Alias = "installs", Order = 14, DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<InstallItem>? Installs { get; set; }

    [YamlMember(Alias = "installcheck_script", Order = 15, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? InstallCheckScript { get; set; }

    [YamlMember(Alias = "uninstallcheck_script", Order = 16, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? UninstallCheckScript { get; set; }

    [YamlMember(Alias = "preinstall_script", Order = 17, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? PreinstallScript { get; set; }

    [YamlMember(Alias = "postinstall_script", Order = 18, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? PostinstallScript { get; set; }

    [YamlMember(Alias = "preuninstall_script", Order = 19, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? PreuninstallScript { get; set; }

    [YamlMember(Alias = "postuninstall_script", Order = 20, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? PostuninstallScript { get; set; }

    [YamlMember(Alias = "uninstaller_path", Order = 21, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? UninstallerPath { get; set; }

    [YamlMember(Alias = "OnDemand", Order = 22, DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool OnDemand { get; set; }

    [YamlMember(Alias = "managed_profiles", Order = 23, DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string>? ManagedProfiles { get; set; }

    [YamlMember(Alias = "managed_apps", Order = 24, DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string>? ManagedApps { get; set; }

    [YamlMember(Alias = "installer", Order = 25, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public Installer? Installer { get; set; }

    [YamlMember(Alias = "unattended_uninstall", Order = 26, DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool UnattendedUninstall { get; set; }

    /// <summary>
    /// Opt-in stale-usage removal: uninstall when none of the tracked
    /// executables have been used for this many days.
    /// </summary>
    [YamlMember(Alias = "days_untouched_before_uninstall", Order = 27, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public int? DaysUntouchedBeforeUninstall { get; set; }

    /// <summary>
    /// Executable paths whose usage gates stale-usage removal. When empty,
    /// the client falls back to .exe entries in the installs array.
    /// </summary>
    [YamlMember(Alias = "usage_tracked_paths", Order = 28, DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string>? UsageTrackedPaths { get; set; }

    /// <summary>
    /// Minimum days of usage history required on the device before
    /// stale-usage removal may act.
    /// </summary>
    [YamlMember(Alias = "minimum_usage_history_days", Order = 29, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public int? MinimumUsageHistoryDays { get; set; }
}

/// <summary>
/// Cimian configuration file
/// </summary>
public class CimianConfig
{
    [YamlMember(Alias = "repo_path")]
    public string? RepoPath { get; set; }
}
