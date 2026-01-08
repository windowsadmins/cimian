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
    public string CachePath { get; set; } = @"C:\ProgramData\ManagedInstalls\Cache";

    [YamlMember(Alias = "CatalogsPath")]
    public string CatalogsPath { get; set; } = @"C:\ProgramData\ManagedInstalls\catalogs";

    [YamlMember(Alias = "ManifestsPath")]
    public string ManifestsPath { get; set; } = @"C:\ProgramData\ManagedInstalls\manifests";

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

    public static readonly string ConfigPath = @"C:\ProgramData\ManagedInstalls\Config.yaml";
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

    [YamlMember(Alias = "blocking_apps")]
    public List<string> BlockingApps { get; set; } = new();

    [YamlMember(Alias = "minimum_os_version")]
    public string? MinimumOsVersion { get; set; }

    [YamlMember(Alias = "maximum_os_version")]
    public string? MaximumOsVersion { get; set; }

    [YamlMember(Alias = "check")]
    public CheckInfo Check { get; set; } = new();

    [YamlMember(Alias = "installcheck_script")]
    public string? InstallcheckScript { get; set; }

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

    [YamlMember(Alias = "installs")]
    public List<InstallCheckItem> Installs { get; set; } = new();

    public bool IsUninstallable() => Uninstallable && (Uninstaller.Count > 0 || Check.Registry.Name != null);
}

/// <summary>
/// Install check item - used to verify installation by checking files, MSI product codes, or directories
/// </summary>
public class InstallCheckItem
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty; // "file", "msi", "directory"

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
    /// Generic command-line arguments
    /// </summary>
    [YamlMember(Alias = "args")]
    public List<string> Args { get; set; } = new();

    [YamlMember(Alias = "hash")]
    public string? Hash { get; set; }

    [YamlMember(Alias = "size")]
    public long? Size { get; set; }

    /// <summary>
    /// Gets all command-line arguments combined (switches + flags + args)
    /// Normalizes switches and flags to ensure proper prefixes:
    /// - Switches: ensures / prefix (accepts both "VERYSILENT" and "/VERYSILENT")
    /// - Flags: ensures - or -- prefix (accepts both "quiet" and "--quiet")
    /// - Args: passed through as-is
    /// </summary>
    public List<string> GetAllArgs()
    {
        var allArgs = new List<string>();
        
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
    /// Generic command-line arguments
    /// </summary>
    [YamlMember(Alias = "args")]
    public List<string> Args { get; set; } = new();

    /// <summary>
    /// Gets all command-line arguments combined (switches + flags + args)
    /// Normalizes switches and flags to ensure proper prefixes:
    /// - Switches: ensures / prefix (accepts both "SILENT" and "/SILENT")
    /// - Flags: ensures - or -- prefix (accepts both "force" and "--force")
    /// - Args: passed through as-is
    /// </summary>
    public List<string> GetAllArgs()
    {
        var allArgs = new List<string>();
        
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
/// Status check result
/// </summary>
public class StatusCheckResult
{
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool NeedsAction { get; set; }
    /// <summary>
    /// True if the item is already installed but needs an update (version mismatch, etc.)
    /// False if the item is not installed at all (new install)
    /// </summary>
    public bool IsUpdate { get; set; }
    public Exception? Error { get; set; }
}
