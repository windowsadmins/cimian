using Cimian.Core;
using YamlDotNet.Serialization;

namespace Cimian.CLI.managedsoftwareupdate.Models;

/// <summary>
/// Represents the Cimian configuration (Config.yaml)
/// </summary>
public class CimianConfig
{
    [YamlMember(Alias = "SoftwareRepoURL")]
    public string SoftwareRepoURL { get; set; } = string.Empty;

    [YamlMember(Alias = "ClientIdentifier")]
    public string ClientIdentifier { get; set; } = string.Empty;

    [YamlMember(Alias = "CachePath")]
    public string CachePath { get; set; } = CimianPaths.CacheDir;

    [YamlMember(Alias = "CatalogsPath")]
    public string CatalogsPath { get; set; } = CimianPaths.CatalogsDir;

    [YamlMember(Alias = "ManifestsPath")]
    public string ManifestsPath { get; set; } = CimianPaths.ManifestsDir;

    [YamlMember(Alias = "LogLevel")]
    public string LogLevel { get; set; } = "INFO";

    [YamlMember(Alias = "Verbose")]
    public bool Verbose { get; set; }

    [YamlMember(Alias = "Debug")]
    public bool Debug { get; set; }

    [YamlMember(Alias = "Catalogs")]
    public List<string> Catalogs { get; set; } = new();

    [YamlMember(Alias = "NoPreflight")]
    public bool NoPreflight { get; set; }

    [YamlMember(Alias = "NoPostflight")]
    public bool NoPostflight { get; set; }

    [YamlMember(Alias = "PreflightFailureAction")]
    public string PreflightFailureAction { get; set; } = "continue";

    [YamlMember(Alias = "PostflightFailureAction")]
    public string PostflightFailureAction { get; set; } = "continue";

    [YamlMember(Alias = "CheckOnly")]
    public bool CheckOnly { get; set; }

    [YamlMember(Alias = "LocalOnlyManifest")]
    public string? LocalOnlyManifest { get; set; }

    [YamlMember(Alias = "SkipSelfService")]
    public bool SkipSelfService { get; set; }

    [YamlMember(Alias = "AuthToken")]
    public string? AuthToken { get; set; }

    [YamlMember(Alias = "AuthUser")]
    public string? AuthUser { get; set; }

    [YamlMember(Alias = "AuthPassword")]
    public string? AuthPassword { get; set; }

    [YamlMember(Alias = "InstallerTimeout")]
    public int InstallerTimeout { get; set; } = 900; // 15 minutes default

    [YamlMember(Alias = "UseCache")]
    public bool UseCache { get; set; } = true;

    [YamlMember(Alias = "CacheRetentionDays")]
    public int CacheRetentionDays { get; set; } = 30;

    // sbin-installer configuration (matches Go: config.Configuration)
    [YamlMember(Alias = "SbinInstallerPath")]
    public string? SbinInstallerPath { get; set; }

    [YamlMember(Alias = "SbinInstallerTargetRoot")]
    public string? SbinInstallerTargetRoot { get; set; } = "/";

    [YamlMember(Alias = "ForceChocolatey")]
    public bool ForceChocolatey { get; set; }

    [YamlMember(Alias = "PreferSbinInstaller")]
    public bool PreferSbinInstaller { get; set; } = true;

    [YamlMember(Alias = "PkgRequireSignature")]
    public bool PkgRequireSignature { get; set; }

    [YamlMember(Alias = "AutoRemove")]
    public bool AutoRemove { get; set; }

    // SSL client certificate authentication
    [YamlMember(Alias = "UseClientCertificate")]
    public bool UseClientCertificate { get; set; }

    [YamlMember(Alias = "ClientCertificatePath")]
    public string? ClientCertificatePath { get; set; }

    [YamlMember(Alias = "ClientCertificatePassword")]
    public string? ClientCertificatePassword { get; set; }

    [YamlMember(Alias = "ClientCertificateThumbprint")]
    public string? ClientCertificateThumbprint { get; set; }

    [YamlMember(Alias = "ClientKeyPath")]
    public string? ClientKeyPath { get; set; }

    [YamlMember(Alias = "SoftwareRepoCACertificate")]
    public string? SoftwareRepoCACertificate { get; set; }

    // Use the client certificate CN as the client identifier for manifest requests
    [YamlMember(Alias = "UseClientCertificateCNAsClientIdentifier")]
    public bool UseClientCertificateCNAsClientIdentifier { get; set; }

    // TODO: Localization / i18n — extract all hardcoded UI strings to resource files for multi-language support
    // TODO: License seat tracking — track available license seats per package (requires server-side component)

    public static readonly string ConfigPath = CimianPaths.ConfigYaml;
}

// TODO(pkg-sunset): Remove PkgBuildInfo, PkgProductInfo, PkgSignatureInfo, PkgCertificateInfo classes
/// <summary>
/// Build information extracted from .pkg packages (build-info.yaml)
/// Matches Go: extract.PkgBuildInfo
/// </summary>
public class PkgBuildInfo
{
    [YamlMember(Alias = "format_version")]
    public string? FormatVersion { get; set; }

    [YamlMember(Alias = "generator")]
    public string? Generator { get; set; }

    [YamlMember(Alias = "build_date")]
    public string? BuildDate { get; set; }

    [YamlMember(Alias = "product")]
    public PkgProductInfo? Product { get; set; }

    [YamlMember(Alias = "install_location")]
    public string? InstallLocation { get; set; }

    [YamlMember(Alias = "signature")]
    public PkgSignatureInfo? Signature { get; set; }

    // Convenience properties
    public string? ProductIdentifier => Product?.Identifier;
    public string? ProductVersion => Product?.Version;
    public string? Developer => Product?.Developer;
    public string? Architecture => Product?.Architecture;
}

/// <summary>
/// Product information within a .pkg package
/// </summary>
public class PkgProductInfo
{
    [YamlMember(Alias = "identifier")]
    public string? Identifier { get; set; }

    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    [YamlMember(Alias = "developer")]
    public string? Developer { get; set; }

    [YamlMember(Alias = "architecture")]
    public string? Architecture { get; set; }

    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }
}

/// <summary>
/// Signature information within a .pkg package
/// </summary>
public class PkgSignatureInfo
{
    [YamlMember(Alias = "algorithm")]
    public string? Algorithm { get; set; }

    [YamlMember(Alias = "hash")]
    public string? Hash { get; set; }

    [YamlMember(Alias = "package_hash")]
    public string? PackageHash { get; set; }

    [YamlMember(Alias = "signature")]
    public string? Signature { get; set; }

    [YamlMember(Alias = "signed_hash")]
    public string? SignedHash { get; set; }

    [YamlMember(Alias = "certificate")]
    public PkgCertificateInfo? Certificate { get; set; }
}

/// <summary>
/// Certificate information within a .pkg signature
/// </summary>
public class PkgCertificateInfo
{
    [YamlMember(Alias = "subject")]
    public string? Subject { get; set; }

    [YamlMember(Alias = "issuer")]
    public string? Issuer { get; set; }

    [YamlMember(Alias = "serial_number")]
    public string? SerialNumber { get; set; }

    [YamlMember(Alias = "thumbprint")]
    public string? Thumbprint { get; set; }

    [YamlMember(Alias = "not_before")]
    public string? NotBefore { get; set; }

    [YamlMember(Alias = "not_after")]
    public string? NotAfter { get; set; }
}

/// <summary>
/// Represents a manifest file
/// </summary>
public class ManifestFile
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "catalogs")]
    public List<string> Catalogs { get; set; } = new();

    [YamlMember(Alias = "included_manifests")]
    public List<string> IncludedManifests { get; set; } = new();

    [YamlMember(Alias = "managed_installs")]
    public List<string> ManagedInstalls { get; set; } = new();

    [YamlMember(Alias = "managed_uninstalls")]
    public List<string> ManagedUninstalls { get; set; } = new();

    [YamlMember(Alias = "managed_updates")]
    public List<string> ManagedUpdates { get; set; } = new();

    [YamlMember(Alias = "optional_installs")]
    public List<string> OptionalInstalls { get; set; } = new();

    [YamlMember(Alias = "managed_profiles")]
    public List<string> ManagedProfiles { get; set; } = new();

    [YamlMember(Alias = "managed_apps")]
    public List<string> ManagedApps { get; set; } = new();

    [YamlMember(Alias = "conditional_items")]
    public List<ConditionalItem> ConditionalItems { get; set; } = new();

    [YamlMember(Alias = "featured_items")]
    public List<string> FeaturedItems { get; set; } = new();

    [YamlMember(Alias = "default_installs")]
    public List<string> DefaultInstalls { get; set; } = new();
}

/// <summary>
/// Represents a conditional item in a manifest
/// </summary>
public class ConditionalItem
{
    [YamlMember(Alias = "condition")]
    public string Condition { get; set; } = string.Empty;

    [YamlMember(Alias = "managed_installs")]
    public List<string> ManagedInstalls { get; set; } = new();

    [YamlMember(Alias = "managed_uninstalls")]
    public List<string> ManagedUninstalls { get; set; } = new();

    [YamlMember(Alias = "managed_updates")]
    public List<string> ManagedUpdates { get; set; } = new();

    [YamlMember(Alias = "optional_installs")]
    public List<string> OptionalInstalls { get; set; } = new();
}

/// <summary>
/// Represents a manifest item with action type
/// </summary>
public class ManifestItem
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // install, update, uninstall, profile, app, optional
    public string SourceManifest { get; set; } = string.Empty;
    public string InstallerLocation { get; set; } = string.Empty;
    public List<string> SupportedArch { get; set; } = new();
    public List<string> ManagedInstalls { get; set; } = new();
    public List<string> ManagedUninstalls { get; set; } = new();
    public List<string> ManagedUpdates { get; set; } = new();
    public List<string> OptionalInstalls { get; set; } = new();
    public List<string> ManagedProfiles { get; set; } = new();
    public List<string> ManagedApps { get; set; } = new();
    public List<string> Catalogs { get; set; } = new();
    public List<string> Includes { get; set; } = new();
}

/// <summary>
/// Represents a catalog item with installer information
/// </summary>
public class CatalogItem
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = string.Empty;

    [YamlMember(Alias = "display_name")]
    public string? DisplayName { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "category")]
    public string? Category { get; set; }

    [YamlMember(Alias = "developer")]
    public string? Developer { get; set; }

    [YamlMember(Alias = "installer")]
    public InstallerInfo Installer { get; set; } = new();

    [YamlMember(Alias = "uninstaller")]
    public List<UninstallerInfo> Uninstaller { get; set; } = new();

    [YamlMember(Alias = "supported_architectures")]
    public List<string> SupportedArch { get; set; } = new();

    [YamlMember(Alias = "requires")]
    public List<string> Requires { get; set; } = new();

    [YamlMember(Alias = "update_for")]
    public List<string> UpdateFor { get; set; } = new();

    [YamlMember(Alias = "blocking_applications")]
    public List<string> BlockingApps { get; set; } = new();

    [YamlMember(Alias = "minimum_os_version")]
    public string? MinimumOsVersion { get; set; }

    [YamlMember(Alias = "maximum_os_version")]
    public string? MaximumOsVersion { get; set; }

    [YamlMember(Alias = "check")]
    public CheckInfo Check { get; set; } = new();

    [YamlMember(Alias = "installcheck_script")]
    public string? InstallcheckScript { get; set; }

    [YamlMember(Alias = "install_script")]
    public string? InstallScript { get; set; }

    [YamlMember(Alias = "uninstall_script")]
    public string? UninstallScript { get; set; }

    [YamlMember(Alias = "preinstall_script")]
    public string? PreinstallScript { get; set; }

    [YamlMember(Alias = "postinstall_script")]
    public string? PostinstallScript { get; set; }

    [YamlMember(Alias = "preuninstall_script")]
    public string? PreuninstallScript { get; set; }

    [YamlMember(Alias = "postuninstall_script")]
    public string? PostuninstallScript { get; set; }

    [YamlMember(Alias = "unattended_install")]
    public bool UnattendedInstall { get; set; }

    [YamlMember(Alias = "unattended_uninstall")]
    public bool UnattendedUninstall { get; set; }

    [YamlMember(Alias = "uninstallable")]
    public bool Uninstallable { get; set; } = true;

    [YamlMember(Alias = "install_window")]
    public InstallWindow? InstallWindow { get; set; }

    [YamlMember(Alias = "force_install_after_date")]
    public DateTime? ForceInstallAfterDate { get; set; }

    [YamlMember(Alias = "restart_action")]
    public string? RestartAction { get; set; }

    [YamlMember(Alias = "version_script")]
    public string? VersionScript { get; set; }

    [YamlMember(Alias = "precache")]
    public bool Precache { get; set; }

    [YamlMember(Alias = "installs")]
    public List<InstallCheckItem> Installs { get; set; } = new();

    public bool IsUninstallable() => Uninstallable && (
        Uninstaller.Count > 0
        || Check.Registry.Name != null
        // Self-uninstallable MSI: cimipkg-built MSI pkginfos carry the ProductCode in
        // installer.product_code. Same GUID is what msiexec /x needs — UninstallAsync
        // synthesizes an UninstallerInfo from it.
        || (Installer is { } msi
            && string.Equals(msi.Type, "msi", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(msi.ProductCode))
        // Self-uninstallable MSIX: installs-array entry of type msix/appx with a
        // usable identity_name. Without identity_name, UninstallAsync can't
        // synthesize an uninstaller — so in that case this clause must be false.
        || Installs.Any(i =>
            (string.Equals(i.Type, "msix", StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Type, "appx", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(i.IdentityName)));
}

/// <summary>
/// Defines a time window during which installation is allowed.
/// If omitted, no time restriction applies.
/// </summary>
public class InstallWindow
{
    [YamlMember(Alias = "start")]
    public string Start { get; set; } = string.Empty;

    [YamlMember(Alias = "end")]
    public string End { get; set; } = string.Empty;

    [YamlMember(Alias = "weekdays")]
    public List<string>? Weekdays { get; set; }

    /// <summary>
    /// Returns true if the given time falls within this install window.
    /// Start is inclusive, end is exclusive. Overnight wrapping (start > end) is supported.
    /// If Weekdays is set, the day of week must also match.
    /// </summary>
    public bool IsWithinWindow(DateTime now)
    {
        if (!TimeSpan.TryParse(Start, out var startTime) || !TimeSpan.TryParse(End, out var endTime))
            return true; // Invalid config = no restriction (fail-open)

        // Check weekday filter
        if (Weekdays is { Count: > 0 })
        {
            // For overnight windows (start > end), times after midnight belong to the
            // previous day's window. Check yesterday's abbreviation in that case.
            var isOvernight = startTime > endTime;
            var inAfterMidnightPortion = isOvernight && now.TimeOfDay < endTime;
            var checkDay = inAfterMidnightPortion ? now.AddDays(-1) : now;

            var dayAbbrev = checkDay.DayOfWeek switch
            {
                DayOfWeek.Monday => "Mon",
                DayOfWeek.Tuesday => "Tue",
                DayOfWeek.Wednesday => "Wed",
                DayOfWeek.Thursday => "Thu",
                DayOfWeek.Friday => "Fri",
                DayOfWeek.Saturday => "Sat",
                DayOfWeek.Sunday => "Sun",
                _ => ""
            };
            if (!Weekdays.Any(d => d.Equals(dayAbbrev, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        var timeOfDay = now.TimeOfDay;

        if (startTime <= endTime)
        {
            // Normal window: e.g. 04:00-06:00
            return timeOfDay >= startTime && timeOfDay < endTime;
        }
        else
        {
            // Overnight wrap: e.g. 22:00-06:00
            return timeOfDay >= startTime || timeOfDay < endTime;
        }
    }

    public override string ToString() => $"{Start}-{End}";
}

/// <summary>
/// Install check item - used to verify installation by checking files, MSI product codes, or directories
/// </summary>
public class InstallCheckItem
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty; // "file", "msi", "directory", "msix"

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

    /// <summary>MSIX/APPX package identity name (from AppxManifest Identity/@Name)</summary>
    [YamlMember(Alias = "identity_name")]
    public string? IdentityName { get; set; }
}

/// <summary>
/// Installer information
/// </summary>
public class InstallerInfo
{
    [YamlMember(Alias = "location")]
    public string Location { get; set; } = string.Empty;

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Command-line switches (Windows-style with / prefix)
    /// Used by InnoSetup and other Windows installers
    /// </summary>
    [YamlMember(Alias = "switches")]
    public List<string> Switches { get; set; } = new();

    /// <summary>
    /// Command-line flags (Unix-style with - or -- prefix)
    /// </summary>
    [YamlMember(Alias = "flags")]
    public List<string> Flags { get; set; } = new();

    /// <summary>
    /// Subcommand placed before flags/switches (e.g., "install")
    /// Used by EXE installers that require a verb/subcommand like: setup.exe install --silent
    /// </summary>
    [YamlMember(Alias = "subcommand")]
    public string? Subcommand { get; set; }

    /// <summary>
    /// Generic command-line arguments
    /// </summary>
    [YamlMember(Alias = "args")]
    public List<string> Args { get; set; } = new();

    [YamlMember(Alias = "hash")]
    public string? Hash { get; set; }

    [YamlMember(Alias = "size")]
    public long? Size { get; set; }

    /// <summary>MSI ProductCode from the .msi (authoritative install identity per version).</summary>
    [YamlMember(Alias = "product_code")]
    public string? ProductCode { get; set; }

    /// <summary>MSI UpgradeCode from the .msi (stable across versions; used to detect outdated installs).</summary>
    [YamlMember(Alias = "upgrade_code")]
    public string? UpgradeCode { get; set; }

    /// <summary>
    /// Custom temporary directory for package extraction
    /// Use shorter paths like C:\Temp to avoid Windows MAX_PATH (260 char) limit issues
    /// </summary>
    [YamlMember(Alias = "temp_dir")]
    public string? TempDir { get; set; }

    /// <summary>
    /// Gets all command-line arguments combined (subcommand + switches + flags + args)
    /// Normalizes switches and flags to ensure proper prefixes:
    /// - Subcommand: placed first, passed through as-is (e.g., "install")
    /// - Switches: ensures / prefix (accepts both "VERYSILENT" and "/VERYSILENT")
    /// - Flags: ensures - or -- prefix (accepts both "quiet" and "--quiet")
    /// - Args: passed through as-is
    /// </summary>
    public List<string> GetAllArgs()
    {
        var allArgs = new List<string>();
        
        // Subcommand goes first (e.g., "install" before any flags)
        if (!string.IsNullOrEmpty(Subcommand))
            allArgs.Add(Subcommand);
        
        // Process switches - ensure / prefix
        foreach (var sw in Switches)
        {
            allArgs.Add(NormalizeSwitch(sw));
        }
        
        // Process flags - ensure - or -- prefix
        foreach (var flag in Flags)
        {
            allArgs.Add(NormalizeFlag(flag));
        }
        
        // Args are passed through as-is
        allArgs.AddRange(Args);
        
        return allArgs;
    }

    /// <summary>
    /// Normalizes a switch to ensure it has a / prefix
    /// Supports both "VERYSILENT" and "/VERYSILENT" input formats
    /// </summary>
    private static string NormalizeSwitch(string sw)
    {
        if (string.IsNullOrWhiteSpace(sw))
            return sw;
        
        var trimmed = sw.Trim();
        
        // Already has / prefix - use as-is
        if (trimmed.StartsWith('/'))
            return trimmed;
        
        // Add / prefix
        return "/" + trimmed;
    }

    /// <summary>
    /// Normalizes a flag to ensure it has a - or -- prefix
    /// Supports both "quiet" and "--quiet" input formats
    /// Uses -- for flags longer than 1 character, - for single char
    /// </summary>
    private static string NormalizeFlag(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
            return flag;
        
        var trimmed = flag.Trim();
        
        // Already has - or -- prefix - use as-is
        if (trimmed.StartsWith('-'))
            return trimmed;
        
        // Determine prefix based on flag length (single char = -, otherwise --)
        // Handle flags with = or space (e.g., "mode=silent" -> "--mode=silent")
        var flagName = trimmed.Split('=', ' ')[0];
        var prefix = flagName.Length == 1 ? "-" : "--";
        
        return prefix + trimmed;
    }
}

/// <summary>
/// Uninstaller information
/// </summary>
public class UninstallerInfo
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [YamlMember(Alias = "product_code")]
    public string? ProductCode { get; set; }

    [YamlMember(Alias = "command")]
    public string? Command { get; set; }

    /// <summary>MSIX/APPX package identity name (from AppxManifest Identity/@Name). Used to resolve PackageFullName at uninstall.</summary>
    [YamlMember(Alias = "identity_name")]
    public string? IdentityName { get; set; }

    /// <summary>
    /// Command-line switches (Windows-style with / prefix)
    /// </summary>
    [YamlMember(Alias = "switches")]
    public List<string> Switches { get; set; } = new();

    /// <summary>
    /// Command-line flags (Unix-style with - or -- prefix)
    /// </summary>
    [YamlMember(Alias = "flags")]
    public List<string> Flags { get; set; } = new();

    /// <summary>
    /// Subcommand placed before flags/switches (e.g., "uninstall")
    /// </summary>
    [YamlMember(Alias = "subcommand")]
    public string? Subcommand { get; set; }

    /// <summary>
    /// Generic command-line arguments
    /// </summary>
    [YamlMember(Alias = "args")]
    public List<string> Args { get; set; } = new();

    /// <summary>
    /// Gets all command-line arguments combined (subcommand + switches + flags + args)
    /// Normalizes switches and flags to ensure proper prefixes:
    /// - Subcommand: placed first, passed through as-is
    /// - Switches: ensures / prefix (accepts both "SILENT" and "/SILENT")
    /// - Flags: ensures - or -- prefix (accepts both "force" and "--force")
    /// - Args: passed through as-is
    /// </summary>
    public List<string> GetAllArgs()
    {
        var allArgs = new List<string>();
        
        // Subcommand goes first
        if (!string.IsNullOrEmpty(Subcommand))
            allArgs.Add(Subcommand);
        
        // Process switches - ensure / prefix
        foreach (var sw in Switches)
        {
            allArgs.Add(NormalizeSwitch(sw));
        }
        
        // Process flags - ensure - or -- prefix
        foreach (var flag in Flags)
        {
            allArgs.Add(NormalizeFlag(flag));
        }
        
        // Args are passed through as-is
        allArgs.AddRange(Args);
        
        return allArgs;
    }

    /// <summary>
    /// Normalizes a switch to ensure it has a / prefix
    /// </summary>
    private static string NormalizeSwitch(string sw)
    {
        if (string.IsNullOrWhiteSpace(sw))
            return sw;
        
        var trimmed = sw.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    /// <summary>
    /// Normalizes a flag to ensure it has a - or -- prefix
    /// </summary>
    private static string NormalizeFlag(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
            return flag;
        
        var trimmed = flag.Trim();
        if (trimmed.StartsWith('-'))
            return trimmed;
        
        var flagName = trimmed.Split('=', ' ')[0];
        var prefix = flagName.Length == 1 ? "-" : "--";
        return prefix + trimmed;
    }
}

/// <summary>
/// Check information for installation status
/// </summary>
public class CheckInfo
{
    [YamlMember(Alias = "registry")]
    public RegistryCheck Registry { get; set; } = new();

    [YamlMember(Alias = "file")]
    public FileCheck? File { get; set; }

    [YamlMember(Alias = "script")]
    public string? Script { get; set; }
}

/// <summary>
/// Registry check information
/// </summary>
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

/// <summary>
/// File check information
/// </summary>
public class FileCheck
{
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;

    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    [YamlMember(Alias = "hash")]
    public string? Hash { get; set; }
}

/// <summary>
/// Catalog wrapper for YAML parsing
/// </summary>
public class CatalogWrapper
{
    [YamlMember(Alias = "items")]
    public List<CatalogItem> Items { get; set; } = new();
}

/// <summary>
/// Session summary for logging
/// </summary>
public class SessionSummary
{
    public int TotalActions { get; set; }
    public int Installs { get; set; }
    public int Updates { get; set; }
    public int Removals { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public List<string> PackagesHandled { get; set; } = new();
}

/// <summary>
/// Status check result with comprehensive reason tracking
/// </summary>
public class StatusCheckResult
{
    /// <summary>Status string: "installed", "pending", "error", "unknown"</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Human-readable reason for the status</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Machine-readable status reason code.
    /// See Cimian.Core.Models.StatusReasonCode for all values.
    /// </summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>
    /// Detection method used to determine status.
    /// See Cimian.Core.Models.DetectionMethod for all values.
    /// </summary>
    public string DetectionMethod { get; set; } = string.Empty;

    /// <summary>Currently installed version, if known</summary>
    public string? InstalledVersion { get; set; }

    /// <summary>Target version from catalog</summary>
    public string? TargetVersion { get; set; }

    /// <summary>Whether action is needed</summary>
    public bool NeedsAction { get; set; }

    /// <summary>
    /// True if the item is already installed but needs an update (version mismatch, etc.)
    /// False if the item is not installed at all (new install)
    /// </summary>
    public bool IsUpdate { get; set; }

    /// <summary>Error if status check failed</summary>
    public Exception? Error { get; set; }
}
