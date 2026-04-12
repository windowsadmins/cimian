// Reporting.cs - Data reporting models for external monitoring tools
// Migrated from Go pkg/reporting/reporting.go

using System.Text.Json.Serialization;

namespace Cimian.Core.Models;

/// <summary>
/// DataTables defines the table schemas for external monitoring tool integration
/// </summary>
public class DataTables
{
    [JsonPropertyName("sessions")]
    public List<SessionRecord> CimianSessions { get; set; } = new();

    [JsonPropertyName("events")]
    public List<EventRecord> CimianEvents { get; set; } = new();

    [JsonPropertyName("items")]
    public List<ItemRecord> CimianItems { get; set; } = new();
}

/// <summary>
/// SessionRecord represents a row in the sessions table
/// </summary>
public class SessionRecord
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = string.Empty;

    [JsonPropertyName("end_time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndTime { get; set; }

    [JsonPropertyName("run_type")]
    public string RunType { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public long Duration { get; set; }

    [JsonPropertyName("total_actions")]
    public int TotalActions { get; set; }

    [JsonPropertyName("installs")]
    public int Installs { get; set; }

    [JsonPropertyName("updates")]
    public int Updates { get; set; }

    [JsonPropertyName("removals")]
    public int Removals { get; set; }

    [JsonPropertyName("successes")]
    public int Successes { get; set; }

    [JsonPropertyName("failures")]
    public int Failures { get; set; }

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("process_id")]
    public int ProcessId { get; set; }

    [JsonPropertyName("log_version")]
    public string LogVersion { get; set; } = string.Empty;

    [JsonPropertyName("packages_handled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PackagesHandled { get; set; }

    /// <summary>
    /// Catalogs used during this run (from downloaded catalog files)
    /// </summary>
    [JsonPropertyName("run_catalogs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RunCatalogs { get; set; }

    /// <summary>
    /// Enhanced fields for external reporting tools
    /// </summary>
    [JsonPropertyName("config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionConfig? Config { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionSummary? Summary { get; set; }
}

/// <summary>
/// SessionConfig represents configuration data for external reporting tool integration
/// </summary>
public class SessionConfig
{
    [JsonPropertyName("manifest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Manifest { get; set; }

    [JsonPropertyName("software_repo_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareRepoUrl { get; set; }

    [JsonPropertyName("client_identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientIdentifier { get; set; }

    [JsonPropertyName("bootstrap_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BootstrapMode { get; set; }

    [JsonPropertyName("cache_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CachePath { get; set; }

    [JsonPropertyName("default_catalog")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultCatalog { get; set; }

    [JsonPropertyName("log_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogLevel { get; set; }
}

/// <summary>
/// SessionSummary represents enhanced summary data for external reporting tool integration
/// </summary>
public class SessionSummary
{
    [JsonPropertyName("total_packages_managed")]
    public int TotalPackagesManaged { get; set; }

    [JsonPropertyName("packages_installed")]
    public int PackagesInstalled { get; set; }

    [JsonPropertyName("packages_pending")]
    public int PackagesPending { get; set; }

    [JsonPropertyName("packages_failed")]
    public int PackagesFailed { get; set; }

    [JsonPropertyName("cache_size_mb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CacheSizeMb { get; set; }

    /// <summary>
    /// Failed package details for ReportMate
    /// </summary>
    [JsonPropertyName("failed_packages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FailedPackageInfo>? FailedPackages { get; set; }
}

/// <summary>
/// FailedPackageInfo provides details about failed packages for ReportMate
/// </summary>
public class FailedPackageInfo
{
    [JsonPropertyName("package_id")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("package_name")]
    public string PackageName { get; set; } = string.Empty;

    [JsonPropertyName("error_type")]
    public string ErrorType { get; set; } = string.Empty;

    [JsonPropertyName("last_attempt")]
    public string LastAttempt { get; set; } = string.Empty;

    [JsonPropertyName("error_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// EventRecord represents a row in the events table
/// </summary>
public class EventRecord
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    // Enhanced package context for ReportMate
    [JsonPropertyName("package_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageId { get; set; }

    [JsonPropertyName("package_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageName { get; set; }

    [JsonPropertyName("package_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageVersion { get; set; }

    // Legacy fields (maintained for compatibility)
    [JsonPropertyName("package")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Package { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Duration { get; set; }

    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Progress { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("source_function")]
    public string SourceFunc { get; set; } = string.Empty;

    [JsonPropertyName("source_line")]
    public int SourceLine { get; set; }

    // Enhanced error information
    [JsonPropertyName("error_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErrorDetails? ErrorDetails { get; set; }

    // Installation method context
    [JsonPropertyName("installer_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstallerType { get; set; }

    // .pkg package enhancements
    [JsonPropertyName("package_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageFormat { get; set; }

    [JsonPropertyName("is_signed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSigned { get; set; }

    [JsonPropertyName("signature_status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureStatus { get; set; }

    [JsonPropertyName("signature_algorithm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureAlgorithm { get; set; }

    [JsonPropertyName("certificate_subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CertificateSubject { get; set; }

    // Enhanced fields for external reporting tools
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventContext? Context { get; set; }

    [JsonPropertyName("log_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogFile { get; set; }

    #region Status Reason Tracking

    /// <summary>
    /// Human-readable explanation of status determination for this event.
    /// </summary>
    [JsonPropertyName("status_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusReason { get; set; }

    /// <summary>
    /// Machine-readable status reason code.
    /// </summary>
    [JsonPropertyName("status_reason_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusReasonCode { get; set; }

    /// <summary>
    /// Detection method used to determine status.
    /// </summary>
    [JsonPropertyName("detection_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DetectionMethod { get; set; }

    #endregion
}

/// <summary>
/// ErrorDetails provides structured error information for troubleshooting
/// </summary>
public class ErrorDetails
{
    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ErrorCode { get; set; }

    [JsonPropertyName("error_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorType { get; set; }

    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; set; }

    [JsonPropertyName("stderr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stderr { get; set; }

    [JsonPropertyName("retry_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RetryCount { get; set; }

    [JsonPropertyName("resolution_hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolutionHint { get; set; }
}

/// <summary>
/// EventContext represents context information for events
/// </summary>
public class EventContext
{
    [JsonPropertyName("run_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunType { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    [JsonPropertyName("hostname")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hostname { get; set; }

    [JsonPropertyName("process_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ProcessId { get; set; }
}

/// <summary>
/// ItemRecord represents a row in the items table (comprehensive device status)
/// </summary>
public class ItemRecord
{
    // Core identification
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("item_name")]
    public string ItemName { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("item_type")]
    public string ItemType { get; set; } = string.Empty;

    // Version information
    [JsonPropertyName("current_status")]
    public string CurrentStatus { get; set; } = string.Empty;

    [JsonPropertyName("latest_version")]
    public string LatestVersion { get; set; } = string.Empty;

    [JsonPropertyName("installed_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstalledVersion { get; set; }

    // Status and timing
    [JsonPropertyName("last_seen_in_session")]
    public string LastSeenInSession { get; set; } = string.Empty;

    [JsonPropertyName("last_successful_time")]
    public string LastSuccessfulTime { get; set; } = string.Empty;

    [JsonPropertyName("last_attempt_time")]
    public string LastAttemptTime { get; set; } = string.Empty;

    [JsonPropertyName("last_attempt_status")]
    public string LastAttemptStatus { get; set; } = string.Empty;

    [JsonPropertyName("last_update")]
    public string LastUpdate { get; set; } = string.Empty;

    // Statistics
    [JsonPropertyName("install_count")]
    public int InstallCount { get; set; }

    [JsonPropertyName("update_count")]
    public int UpdateCount { get; set; }

    [JsonPropertyName("removal_count")]
    public int RemovalCount { get; set; }

    [JsonPropertyName("failure_count")]
    public int FailureCount { get; set; }

    [JsonPropertyName("warning_count")]
    public int WarningCount { get; set; }

    [JsonPropertyName("total_sessions")]
    public int TotalSessions { get; set; }

    // Enhanced install loop detection
    [JsonPropertyName("install_loop_detected")]
    public bool InstallLoopDetected { get; set; }

    [JsonPropertyName("loop_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstallLoopDetail? LoopDetails { get; set; }

    // Enhanced metadata for external reporting tools
    [JsonPropertyName("install_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstallMethod { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "cimian";

    // .pkg package enhancements
    [JsonPropertyName("package_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageFormat { get; set; }

    [JsonPropertyName("package_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageId { get; set; }

    [JsonPropertyName("developer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Developer { get; set; }

    [JsonPropertyName("architecture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Architecture { get; set; }

    [JsonPropertyName("install_location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstallLocation { get; set; }

    [JsonPropertyName("is_signed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSigned { get; set; }

    [JsonPropertyName("signature_status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureStatus { get; set; }

    [JsonPropertyName("signature_algorithm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureAlgorithm { get; set; }

    [JsonPropertyName("certificate_subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CertificateSubject { get; set; }

    [JsonPropertyName("certificate_thumbprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CertificateThumbprint { get; set; }

    [JsonPropertyName("signature_timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureTimestamp { get; set; }

    // Additional .pkg metadata fields
    [JsonPropertyName("signer_certificate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignerCertificate { get; set; }

    [JsonPropertyName("signer_common_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignerCommonName { get; set; }

    [JsonPropertyName("developer_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeveloperName { get; set; }

    [JsonPropertyName("developer_organization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeveloperOrganization { get; set; }

    [JsonPropertyName("sbin_installer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SbinInstaller { get; set; }

    [JsonPropertyName("pkg_build_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PkgBuildVersion { get; set; }

    // Error information
    [JsonPropertyName("last_error")]
    public string LastError { get; set; } = string.Empty;

    [JsonPropertyName("last_warning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastWarning { get; set; }

    [JsonPropertyName("recent_attempts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ItemAttempt>? RecentAttempts { get; set; }

    #region Status Reason Tracking

    /// <summary>
    /// Human-readable explanation of how status was determined.
    /// Example: "File at C:\Program Files\App\app.exe verified at version 1.2.3"
    /// </summary>
    [JsonPropertyName("status_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusReason { get; set; }

    /// <summary>
    /// Machine-readable reason code for programmatic handling.
    /// Example: "file_match", "registry_missing", "update_available"
    /// See StatusReasonCode class for all values.
    /// </summary>
    [JsonPropertyName("status_reason_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusReasonCode { get; set; }

    /// <summary>
    /// The detection method used to determine status.
    /// Example: "file", "registry", "script", "msi"
    /// See DetectionMethod class for all values.
    /// </summary>
    [JsonPropertyName("detection_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DetectionMethod { get; set; }

    /// <summary>
    /// When the current status was determined
    /// </summary>
    [JsonPropertyName("status_determined_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusDeterminedAt { get; set; }

    #endregion
}

/// <summary>
/// InstallLoopDetail provides enhanced information about install loops
/// </summary>
public class InstallLoopDetail
{
    [JsonPropertyName("detection_criteria")]
    public string DetectionCriteria { get; set; } = string.Empty;

    [JsonPropertyName("loop_start_session")]
    public string LoopStartSession { get; set; } = string.Empty;

    [JsonPropertyName("suspected_cause")]
    public string SuspectedCause { get; set; } = string.Empty;

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = string.Empty;
}

/// <summary>
/// ItemAttempt represents a single install/update attempt for loop detection
/// </summary>
public class ItemAttempt
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
}

/// <summary>
/// SessionPackageInfo holds comprehensive information about a package in the current session
/// </summary>
public class SessionPackageInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("item_type")]
    public string ItemType { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("installed_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstalledVersion { get; set; }

    [JsonPropertyName("error_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("warning_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WarningMessage { get; set; }

    #region Status Reason Tracking

    /// <summary>
    /// Human-readable explanation of how status was determined.
    /// </summary>
    [JsonPropertyName("status_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusReason { get; set; }

    /// <summary>
    /// Machine-readable status reason code.
    /// </summary>
    [JsonPropertyName("status_reason_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusReasonCode { get; set; }

    /// <summary>
    /// Detection method used to determine status.
    /// </summary>
    [JsonPropertyName("detection_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DetectionMethod { get; set; }

    #endregion
}

/// <summary>
/// ManagedItem represents a managed package item from manifests
/// </summary>
public class ManagedItem
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// ManifestItem represents an individual item from a manifest for reporting purposes
/// </summary>
public class ManifestReportItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("catalogs")]
    public List<string> Catalogs { get; set; } = new();

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("source_manifest")]
    public string SourceManifest { get; set; } = string.Empty;
}

/// <summary>
/// Represents a logged session with events
/// </summary>
public class LogSession
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public DateTime? EndTime { get; set; }

    [JsonPropertyName("duration_seconds")]
    public long? DurationSeconds { get; set; }

    [JsonPropertyName("run_type")]
    public string RunType { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public LogSessionSummary Summary { get; set; } = new();

    [JsonPropertyName("environment")]
    public Dictionary<string, object>? Environment { get; set; }
}

/// <summary>
/// Summary data for a log session
/// </summary>
public class LogSessionSummary
{
    [JsonPropertyName("total_actions")]
    public int TotalActions { get; set; }

    [JsonPropertyName("installs")]
    public int Installs { get; set; }

    [JsonPropertyName("updates")]
    public int Updates { get; set; }

    [JsonPropertyName("removals")]
    public int Removals { get; set; }

    [JsonPropertyName("successes")]
    public int Successes { get; set; }

    [JsonPropertyName("failures")]
    public int Failures { get; set; }

    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; set; }

    [JsonPropertyName("packages_handled")]
    public List<string> PackagesHandled { get; set; } = new();
}

/// <summary>
/// SessionStats holds statistics about session installation results
/// </summary>
public class SessionStats
{
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int WarningCount { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// SessionData represents raw session data from session.json files
/// </summary>
public class SessionData
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = string.Empty;

    [JsonPropertyName("end_time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndTime { get; set; }

    [JsonPropertyName("run_type")]
    public string RunType { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public long? DurationSeconds { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionSummary? Summary { get; set; }

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Environment { get; set; }
}

// TODO(pkg-sunset): Remove PkgRegistryMetadata class
/// <summary>
/// PkgRegistryMetadata represents enhanced .pkg package metadata from Windows registry
/// </summary>
public class PkgRegistryMetadata
{
    public string Version { get; set; } = string.Empty;
    public string PackageFormat { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Developer { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SignatureStatus { get; set; } = string.Empty;
    public string SignerCertificate { get; set; } = string.Empty;
    public string SignerCommonName { get; set; } = string.Empty;
    public string SignatureTimestamp { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
}

/// <summary>
/// ReportingManifestFile represents the structure of manifest YAML files for reporting
/// </summary>
public class ReportingManifestFile
{
    public string Name { get; set; } = string.Empty;
    public List<string> ManagedInstalls { get; set; } = new();
    public List<string> ManagedUninstalls { get; set; } = new();
    public List<string> ManagedUpdates { get; set; } = new();
    public List<string> OptionalInstalls { get; set; } = new();
    public List<string> ManagedProfiles { get; set; } = new();
    public List<string> ManagedApps { get; set; } = new();
    public List<string> IncludedManifests { get; set; } = new();
    public List<string> Catalogs { get; set; } = new();
}

/// <summary>
/// ComprehensiveItemStat tracks detailed statistics for an item across all sessions (internal use)
/// </summary>
public class ComprehensiveItemStat
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string CurrentStatus { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string LastSeenInSession { get; set; } = string.Empty;
    public string LastSuccessfulTime { get; set; } = string.Empty;
    public string LastAttemptTime { get; set; } = string.Empty;
    public string LastAttemptStatus { get; set; } = string.Empty;
    public string LastUpdate { get; set; } = string.Empty;
    public int InstallCount { get; set; }
    public int UpdateCount { get; set; }
    public int RemovalCount { get; set; }
    public int FailureCount { get; set; }
    public int WarningCount { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string LastWarning { get; set; } = string.Empty;
    public HashSet<string> Sessions { get; set; } = new();
    public List<ItemAttempt> RecentAttempts { get; set; } = new();
    
    // .pkg metadata
    public string PackageFormat { get; set; } = string.Empty;
    public string SignatureStatus { get; set; } = string.Empty;
    public string SignerCertificate { get; set; } = string.Empty;
    public string SignerCommonName { get; set; } = string.Empty;
    public string SignatureTimestamp { get; set; } = string.Empty;

    #region Status Reason Tracking

    /// <summary>
    /// Human-readable explanation of how status was determined.
    /// </summary>
    public string StatusReason { get; set; } = string.Empty;

    /// <summary>
    /// Machine-readable status reason code.
    /// </summary>
    public string StatusReasonCode { get; set; } = string.Empty;

    /// <summary>
    /// Detection method used to determine status.
    /// </summary>
    public string DetectionMethod { get; set; } = string.Empty;

    /// <summary>
    /// When the current status was determined.
    /// </summary>
    public string StatusDeterminedAt { get; set; } = string.Empty;

    #endregion
}
