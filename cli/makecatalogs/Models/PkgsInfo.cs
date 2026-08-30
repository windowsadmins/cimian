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
    /// <summary>Additional process exit codes to treat as a successful install, beyond 0 and 3010 (e.g. an installer that returns 2 for "installed, reboot recommended").</summary>
    [YamlMember(Alias = "success_codes")]
    public List<int>? SuccessCodes { get; set; }

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

    /// <summary>
    /// ARP DisplayName fallback for wrapper MSIs (empty File table; payload
    /// installed by an embedded setup.exe, e.g. Mozilla Firefox) that keep no
    /// Windows Installer registration. The client (managedsoftwareupdate) opts
    /// in per entry for its ARP substring match; makecatalogs must carry the
    /// field through so it is not stripped out of the generated catalog.
    /// </summary>
    [YamlMember(Alias = "display_name")]
    public string? DisplayName { get; set; }

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

    /// <summary>
    /// Icon filename in the repo's icons directory. Carried through to the
    /// generated catalogs so clients can resolve icons whose filename differs
    /// from the package name (icon_name != "&lt;name&gt;.png").
    /// </summary>
    [YamlMember(Alias = "icon_name")]
    public string? IconName { get; set; }

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

    // The client's CatalogItem parses several fields this model was missing, so
    // makecatalogs silently dropped them between pkgsinfo and catalog and the
    // fleet never received them (same defect class as icon_name previously).
    // All are nullable passthroughs: absent in the pkgsinfo stays absent in the
    // catalog because the shared serializer omits nulls.
    [YamlMember(Alias = "install_script")]
    public string? InstallScript { get; set; }

    [YamlMember(Alias = "uninstall_script")]
    public string? UninstallScript { get; set; }

    [YamlMember(Alias = "version_script")]
    public string? VersionScript { get; set; }

    [YamlMember(Alias = "restart_action")]
    public string? RestartAction { get; set; }

    /// <summary>Per-item override of the fleet InstallerTimeout, in seconds, for payloads that legitimately run long.</summary>
    [YamlMember(Alias = "installer_timeout")]
    public int? InstallerTimeout { get; set; }

    [YamlMember(Alias = "force_install_after_date")]
    public DateTime? ForceInstallAfterDate { get; set; }

    [YamlMember(Alias = "precache")]
    public bool? Precache { get; set; }

    [YamlMember(Alias = "check")]
    public CheckInfo? Check { get; set; }

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
    /// Content fingerprint of this catalog item, stamped by makecatalogs (see
    /// CatalogBuilder.StampLoopFingerprints). Hashes the whole serialized item, so ANY
    /// pkgsinfo edit that reaches the catalog changes it.
    /// <para>
    /// The client's LoopGuard clears a package's loop suppression when this value
    /// changes: publishing a fix is what gets a suppressed package installing again
    /// fleet-wide, with no per-machine <c>--clear-loop</c>. Excluded from its own hash
    /// (it is nulled before hashing), and ignored by older clients, which tolerate
    /// unknown catalog keys.
    /// </para>
    /// </summary>
    [YamlMember(Alias = "loop_fingerprint")]
    public string? LoopFingerprint { get; set; }

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
/// check: block — registry/file/script detection, mirroring the client's
/// CheckInfo shape (fully nullable here so this tool stays a passthrough).
/// </summary>
public class CheckInfo
{
    [YamlMember(Alias = "registry")]
    public RegistryCheck? Registry { get; set; }

    [YamlMember(Alias = "file")]
    public FileCheck? File { get; set; }

    [YamlMember(Alias = "script")]
    public string? Script { get; set; }
}

public class RegistryCheck
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    [YamlMember(Alias = "value")]
    public string? Value { get; set; }
}

public class FileCheck
{
    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    [YamlMember(Alias = "hash")]
    public string? Hash { get; set; }
}

/// <summary>
/// Catalog file wrapper
/// </summary>
public class CatalogFile
{
    [YamlMember(Alias = "items")]
    public List<PkgsInfo> Items { get; set; } = new();
}
