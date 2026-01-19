// StatusReasonCode.cs - Machine-readable status reason codes
// These codes explain WHY Cimian determined a package's status

namespace Cimian.Core.Models;

/// <summary>
/// Machine-readable status reason codes for package status determination.
/// These provide programmatic access to understand why a package has its current status.
/// </summary>
public static class StatusReasonCode
{
    #region Installed Reasons - Package is confirmed installed

    /// <summary>Registry key/value confirmed package installation</summary>
    public const string RegistryMatch = "registry_match";

    /// <summary>File exists and version matches expected</summary>
    public const string FileMatch = "file_match";

    /// <summary>WMI query confirmed package installation</summary>
    public const string WmiMatch = "wmi_match";

    /// <summary>Check script confirmed package is installed (exit code != 0)</summary>
    public const string ScriptConfirmed = "script_confirmed";

    /// <summary>Installed version matches or exceeds target version</summary>
    public const string VersionMatch = "version_match";

    /// <summary>MSI product code found in registry</summary>
    public const string ProductCodeMatch = "product_code_match";

    /// <summary>Directory exists as expected</summary>
    public const string DirectoryMatch = "directory_match";

    /// <summary>MD5/SHA hash matches expected value</summary>
    public const string HashMatch = "hash_match";

    /// <summary>No checks defined - assuming installed</summary>
    public const string NoChecks = "no_checks";

    /// <summary>Running version is same or newer than catalog</summary>
    public const string SelfUpdateCurrent = "self_update_current";

    #endregion

    #region Pending Reasons - Package needs installation/update

    /// <summary>Package is not installed at all</summary>
    public const string NotInstalled = "not_installed";

    /// <summary>Newer version available in catalog</summary>
    public const string UpdateAvailable = "update_available";

    /// <summary>Installed version differs from expected</summary>
    public const string VersionMismatch = "version_mismatch";

    /// <summary>Expected registry key/value not found</summary>
    public const string RegistryMissing = "registry_missing";

    /// <summary>Expected file not found</summary>
    public const string FileMissing = "file_missing";

    /// <summary>Expected directory not found</summary>
    public const string DirectoryMissing = "directory_missing";

    /// <summary>MSI product code not found in registry</summary>
    public const string ProductCodeMissing = "product_code_missing";

    /// <summary>File/package hash doesn't match expected</summary>
    public const string HashMismatch = "hash_mismatch";

    /// <summary>One or more dependencies not installed</summary>
    public const string DependencyMissing = "dependency_missing";

    /// <summary>User chose to defer installation</summary>
    public const string UserDeferred = "user_deferred";

    /// <summary>Blocking applications are running</summary>
    public const string BlockingApps = "blocking_apps";

    /// <summary>Installer download in progress or queued</summary>
    public const string DownloadPending = "download_pending";

    /// <summary>Installer download failed</summary>
    public const string DownloadFailed = "download_failed";

    /// <summary>Waiting for maintenance window</summary>
    public const string ScheduleWaiting = "schedule_waiting";

    /// <summary>Insufficient disk space for installation</summary>
    public const string DiskSpace = "disk_space";

    /// <summary>Network is metered - large download deferred</summary>
    public const string NetworkMetered = "network_metered";

    /// <summary>Admin has placed package on hold</summary>
    public const string AdminHold = "admin_hold";

    /// <summary>System requires reboot before installation can proceed</summary>
    public const string PendingReboot = "pending_reboot";

    /// <summary>installcheck_script indicates install needed (exit code 0)</summary>
    public const string InstallcheckNeeded = "installcheck_needed";

    /// <summary>Architecture not supported on this system</summary>
    public const string ArchitectureMismatch = "architecture_mismatch";

    /// <summary>OS version not supported</summary>
    public const string OsVersionMismatch = "os_version_mismatch";

    #endregion

    #region Removed Reasons - Package confirmed removed

    /// <summary>Registry key removed - package no longer registered</summary>
    public const string RegistryRemoved = "registry_removed";

    /// <summary>Package files no longer present</summary>
    public const string FileRemoved = "file_removed";

    /// <summary>Uninstall process completed successfully</summary>
    public const string UninstallConfirmed = "uninstall_confirmed";

    /// <summary>Script confirmed package removal</summary>
    public const string ScriptConfirmedRemoval = "script_confirmed_removal";

    #endregion

    #region Error/Unknown Reasons

    /// <summary>Status check failed with error</summary>
    public const string CheckFailed = "check_failed";

    /// <summary>Detection script encountered an error</summary>
    public const string ScriptError = "script_error";

    /// <summary>Unable to determine status</summary>
    public const string Unknown = "unknown";

    #endregion
}

/// <summary>
/// Detection methods used to determine package status
/// </summary>
public static class DetectionMethod
{
    /// <summary>Registry-based detection</summary>
    public const string Registry = "registry";

    /// <summary>File-based detection (existence, version, hash)</summary>
    public const string File = "file";

    /// <summary>Directory-based detection</summary>
    public const string Directory = "directory";

    /// <summary>WMI query-based detection</summary>
    public const string Wmi = "wmi";

    /// <summary>PowerShell/script-based detection</summary>
    public const string Script = "script";

    /// <summary>MSI product code detection</summary>
    public const string Msi = "msi";

    /// <summary>Self-update version comparison</summary>
    public const string SelfUpdate = "self_update";

    /// <summary>Installs array verification</summary>
    public const string InstallsArray = "installs_array";

    /// <summary>ManagedInstalls registry tracking</summary>
    public const string ManagedInstalls = "managed_installs";

    /// <summary>No detection method used</summary>
    public const string None = "none";
}

/// <summary>
/// High-level installation states
/// </summary>
public enum InstallationState
{
    /// <summary>Package is not installed</summary>
    NotInstalled = 0,

    /// <summary>Package is installed at the expected or newer version</summary>
    Installed = 1,

    /// <summary>A newer version than the target is installed</summary>
    NewerVersionInstalled = 2
}
