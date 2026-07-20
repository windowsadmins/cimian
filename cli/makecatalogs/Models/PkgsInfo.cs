using YamlDotNet.Serialization;

namespace Cimian.CLI.Makecatalogs.Models;

/// <summary>
/// Installer details for a package
/// Migrated from Go: Installer struct in cmd/makecatalogs/main.go
/// </summary>
public class Installer
{
    [YamlMember(Alias = "location")]
    public string? Location { get; set; }

    [YamlMember(Alias = "hash")]
    public string? Hash { get; set; }

    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    [YamlMember(Alias = "size")]
    public long? Size { get; set; }

    [YamlMember(Alias = "switches")]
    public List<string>? Switches { get; set; }

    [YamlMember(Alias = "flags")]
    public List<string>? Flags { get; set; }

    [YamlMember(Alias = "subcommand")]
    public string? Subcommand { get; set; }

    [YamlMember(Alias = "arguments")]
    public List<string>? Arguments { get; set; }

    [YamlMember(Alias = "args")]
    public List<string>? Args { get; set; }

    [YamlMember(Alias = "temp_dir")]
    public string? TempDir { get; set; }

    [YamlMember(Alias = "product_code")]
    public string? ProductCode { get; set; }

    [YamlMember(Alias = "upgrade_code")]
    public string? UpgradeCode { get; set; }

    /// <summary>MSIX/APPX package identity name (from AppxManifest Identity/@Name).</summary>
    [YamlMember(Alias = "identity_name")]
    public string? IdentityName { get; set; }
}

/// <summary>
/// Installation check item
/// </summary>
public class InstallItem
{
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

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

    /// <summary>MSIX/APPX package identity name (from AppxManifest Identity/@Name).</summary>
    [YamlMember(Alias = "identity_name")]
    public string? IdentityName { get; set; }
}

/// <summary>
/// Package information structure
/// Migrated from Go: PkgsInfo struct in cmd/makecatalogs/main.go
/// </summary>
public class PkgsInfo
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "display_name")]
    public string? DisplayName { get; set; }

    [YamlMember(Alias = "identifier")]
    public string? Identifier { get; set; }

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "catalogs")]
    public List<string> Catalogs { get; set; } = new();

    [YamlMember(Alias = "category")]
    public string? Category { get; set; }

    [YamlMember(Alias = "developer")]
    public string? Developer { get; set; }

    [YamlMember(Alias = "requires")]
    public List<string>? Requires { get; set; }

    [YamlMember(Alias = "update_for")]
    public List<string>? UpdateFor { get; set; }

    [YamlMember(Alias = "installs")]
    public List<InstallItem>? Installs { get; set; }

    [YamlMember(Alias = "blocking_applications")]
    public List<string>? BlockingApplications { get; set; }

    [YamlMember(Alias = "supported_architectures")]
    public List<string>? SupportedArchitectures { get; set; }

    [YamlMember(Alias = "unattended_install")]
    public bool UnattendedInstall { get; set; }

    [YamlMember(Alias = "unattended_uninstall")]
    public bool UnattendedUninstall { get; set; }

    /// <summary>
    /// Opt-in unused-software removal (unused_software_removal_info).
    /// Requires unattended_uninstall and usage data (ReportMate usagetracker).
    /// Null disables the feature for this package.
    /// </summary>
    [YamlMember(Alias = "unused_software_removal_info")]
    public UnusedSoftwareRemovalInfo? UnusedSoftwareRemovalInfo { get; set; }

    [YamlMember(Alias = "minimum_os_version")]
    public string? MinOSVersion { get; set; }

    [YamlMember(Alias = "maximum_os_version")]
    public string? MaxOSVersion { get; set; }

    [YamlMember(Alias = "minimum_cimian_version")]
    public string? MinCimianVersion { get; set; }

    [YamlMember(Alias = "installer")]
    public Installer? Installer { get; set; }

    [YamlMember(Alias = "uninstaller")]
    public List<Installer>? Uninstaller { get; set; }

    [YamlMember(Alias = "preinstall_script")]
    public string? PreinstallScript { get; set; }

    [YamlMember(Alias = "postinstall_script")]
    public string? PostinstallScript { get; set; }

    [YamlMember(Alias = "preuninstall_script")]
    public string? PreuninstallScript { get; set; }

    [YamlMember(Alias = "postuninstall_script")]
    public string? PostuninstallScript { get; set; }

    [YamlMember(Alias = "installcheck_script")]
    public string? InstallCheckScript { get; set; }

    [YamlMember(Alias = "uninstallcheck_script")]
    public string? UninstallCheckScript { get; set; }

    [YamlMember(Alias = "uninstallable")]
    public bool? Uninstallable { get; set; }

    [YamlMember(Alias = "install_window")]
    public InstallWindow? InstallWindow { get; set; }

    [YamlMember(Alias = "OnDemand")]
    public bool OnDemand { get; set; }

    // Recurring items are idempotent maintenance actions (cache clears, time sync,
    // account/user checks) that are DESIGNED to run every session, so their
    // installcheck legitimately returns "install needed" run after run. This flag
    // exempts them from LoopGuard suppression (see UpdateEngine) without the OnDemand
    // no-receipt/never-installed semantics. Round-trips pkgsinfo -> catalog here.
    [YamlMember(Alias = "recurring")]
    public bool Recurring { get; set; }

    /// <summary>
    /// Source file path (not serialized)
    /// </summary>
    [YamlIgnore]
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>
/// Unused-software removal opt-in (paths gate removal by recorded usage;
/// minimum_history_days is a Cimian extension).
/// </summary>
public class UnusedSoftwareRemovalInfo
{
    [YamlMember(Alias = "removal_days")]
    public int? RemovalDays { get; set; }

    [YamlMember(Alias = "paths")]
    public List<string>? Paths { get; set; }

    [YamlMember(Alias = "minimum_history_days")]
    public int? MinimumHistoryDays { get; set; }
}

/// <summary>
/// Time window during which installation is allowed
/// </summary>
public class InstallWindow
{
    [YamlMember(Alias = "start")]
    public string Start { get; set; } = string.Empty;

    [YamlMember(Alias = "end")]
    public string End { get; set; } = string.Empty;

    [YamlMember(Alias = "weekdays")]
    public List<string>? Weekdays { get; set; }
}

/// <summary>
/// Catalog file wrapper
/// </summary>
public class CatalogFile
{
    [YamlMember(Alias = "items")]
    public List<PkgsInfo> Items { get; set; } = new();
}
