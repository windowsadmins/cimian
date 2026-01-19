// InstallationStateResult.cs - Rich result type for installation status detection
// Captures the full context of why a package has its current state

using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace Cimian.Core.Models;

/// <summary>
/// Comprehensive result of an installation state check.
/// This captures not just whether a package is installed, but WHY we determined that status.
/// </summary>
public class InstallationStateResult
{
    /// <summary>
    /// The high-level installation state
    /// </summary>
    [JsonPropertyName("state")]
    [YamlMember(Alias = "state")]
    public InstallationState State { get; set; }

    /// <summary>
    /// Human-readable explanation of how status was determined.
    /// Example: "File at C:\Program Files\App\app.exe verified at version 1.2.3"
    /// </summary>
    [JsonPropertyName("reason")]
    [YamlMember(Alias = "reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Machine-readable reason code for programmatic handling.
    /// Example: "file_match", "registry_missing", "update_available"
    /// </summary>
    [JsonPropertyName("reason_code")]
    [YamlMember(Alias = "reason_code")]
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>
    /// The detection method used to determine status.
    /// Example: "file", "registry", "script", "msi"
    /// </summary>
    [JsonPropertyName("detection_method")]
    [YamlMember(Alias = "detection_method")]
    public string DetectionMethod { get; set; } = string.Empty;

    /// <summary>
    /// The currently installed version, if detected
    /// </summary>
    [JsonPropertyName("installed_version")]
    [YamlMember(Alias = "installed_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// The target version from the catalog
    /// </summary>
    [JsonPropertyName("target_version")]
    [YamlMember(Alias = "target_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetVersion { get; set; }

    /// <summary>
    /// Additional details about the detection (path checked, script output, etc.)
    /// </summary>
    [JsonPropertyName("details")]
    [YamlMember(Alias = "details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    /// <summary>
    /// True if an action (install/update/remove) is needed
    /// </summary>
    [JsonPropertyName("needs_action")]
    [YamlMember(Alias = "needs_action")]
    public bool NeedsAction { get; set; }

    /// <summary>
    /// True if this is an update (package installed but needs newer version),
    /// False if this is a new installation
    /// </summary>
    [JsonPropertyName("is_update")]
    [YamlMember(Alias = "is_update")]
    public bool IsUpdate { get; set; }

    /// <summary>
    /// Timestamp when status was determined
    /// </summary>
    [JsonPropertyName("determined_at")]
    [YamlMember(Alias = "determined_at")]
    public DateTime DeterminedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Error that occurred during status check, if any
    /// </summary>
    [JsonIgnore]
    [YamlIgnore]
    public Exception? Error { get; set; }

    #region Factory Methods

    /// <summary>
    /// Creates a result indicating the package is not installed
    /// </summary>
    public static InstallationStateResult NotInstalled(
        string reason,
        string reasonCode,
        string? detectionMethod = null,
        string? targetVersion = null)
    {
        return new InstallationStateResult
        {
            State = InstallationState.NotInstalled,
            Reason = reason,
            ReasonCode = reasonCode,
            DetectionMethod = detectionMethod ?? Core.Models.DetectionMethod.None,
            TargetVersion = targetVersion,
            NeedsAction = true,
            IsUpdate = false
        };
    }

    /// <summary>
    /// Creates a result indicating the package is installed
    /// </summary>
    public static InstallationStateResult Installed(
        string reason,
        string reasonCode,
        string? installedVersion,
        string? detectionMethod = null,
        string? targetVersion = null)
    {
        return new InstallationStateResult
        {
            State = InstallationState.Installed,
            Reason = reason,
            ReasonCode = reasonCode,
            DetectionMethod = detectionMethod ?? Core.Models.DetectionMethod.None,
            InstalledVersion = installedVersion,
            TargetVersion = targetVersion,
            NeedsAction = false,
            IsUpdate = false
        };
    }

    /// <summary>
    /// Creates a result indicating a newer version than target is installed
    /// </summary>
    public static InstallationStateResult NewerInstalled(
        string reason,
        string reasonCode,
        string installedVersion,
        string targetVersion,
        string? detectionMethod = null)
    {
        return new InstallationStateResult
        {
            State = InstallationState.NewerVersionInstalled,
            Reason = reason,
            ReasonCode = reasonCode,
            DetectionMethod = detectionMethod ?? Core.Models.DetectionMethod.None,
            InstalledVersion = installedVersion,
            TargetVersion = targetVersion,
            NeedsAction = false,
            IsUpdate = false
        };
    }

    /// <summary>
    /// Creates a result indicating an update is needed
    /// </summary>
    public static InstallationStateResult UpdateNeeded(
        string reason,
        string reasonCode,
        string installedVersion,
        string targetVersion,
        string? detectionMethod = null)
    {
        return new InstallationStateResult
        {
            State = InstallationState.NotInstalled, // Needs action = not at target state
            Reason = reason,
            ReasonCode = reasonCode,
            DetectionMethod = detectionMethod ?? Core.Models.DetectionMethod.None,
            InstalledVersion = installedVersion,
            TargetVersion = targetVersion,
            NeedsAction = true,
            IsUpdate = true
        };
    }

    /// <summary>
    /// Creates a result indicating the check failed with an error
    /// </summary>
    public static InstallationStateResult Failed(
        string reason,
        Exception? error = null,
        string? detectionMethod = null)
    {
        return new InstallationStateResult
        {
            State = InstallationState.NotInstalled,
            Reason = reason,
            ReasonCode = StatusReasonCode.CheckFailed,
            DetectionMethod = detectionMethod ?? Core.Models.DetectionMethod.None,
            NeedsAction = true, // On error, assume needs action
            Error = error
        };
    }

    /// <summary>
    /// Creates a result for blocking apps preventing installation
    /// </summary>
    public static InstallationStateResult BlockedByApps(
        IEnumerable<string> runningApps,
        string? targetVersion = null)
    {
        var appList = string.Join(", ", runningApps);
        return new InstallationStateResult
        {
            State = InstallationState.NotInstalled,
            Reason = $"Waiting for {appList} to close",
            ReasonCode = StatusReasonCode.BlockingApps,
            DetectionMethod = Core.Models.DetectionMethod.None,
            TargetVersion = targetVersion,
            NeedsAction = true,
            IsUpdate = false,
            Details = appList
        };
    }

    /// <summary>
    /// Creates a result for pending reboot
    /// </summary>
    public static InstallationStateResult PendingReboot(string? targetVersion = null)
    {
        return new InstallationStateResult
        {
            State = InstallationState.NotInstalled,
            Reason = "System requires reboot before installation can proceed",
            ReasonCode = StatusReasonCode.PendingReboot,
            DetectionMethod = Core.Models.DetectionMethod.None,
            TargetVersion = targetVersion,
            NeedsAction = true,
            IsUpdate = false
        };
    }

    /// <summary>
    /// Creates a result for missing dependencies
    /// </summary>
    public static InstallationStateResult MissingDependencies(
        IEnumerable<string> missingDeps,
        string? targetVersion = null)
    {
        var depList = string.Join(", ", missingDeps);
        return new InstallationStateResult
        {
            State = InstallationState.NotInstalled,
            Reason = $"Waiting for dependencies: {depList}",
            ReasonCode = StatusReasonCode.DependencyMissing,
            DetectionMethod = Core.Models.DetectionMethod.None,
            TargetVersion = targetVersion,
            NeedsAction = true,
            IsUpdate = false,
            Details = depList
        };
    }

    /// <summary>
    /// Creates a result for insufficient disk space
    /// </summary>
    public static InstallationStateResult InsufficientDiskSpace(
        long requiredBytes,
        long availableBytes,
        string? targetVersion = null)
    {
        var requiredMb = requiredBytes / (1024 * 1024);
        var availableMb = availableBytes / (1024 * 1024);
        return new InstallationStateResult
        {
            State = InstallationState.NotInstalled,
            Reason = $"Insufficient disk space (need {requiredMb}MB, have {availableMb}MB)",
            ReasonCode = StatusReasonCode.DiskSpace,
            DetectionMethod = Core.Models.DetectionMethod.None,
            TargetVersion = targetVersion,
            NeedsAction = true,
            IsUpdate = false,
            Details = $"Required: {requiredBytes} bytes, Available: {availableBytes} bytes"
        };
    }

    #endregion
}
