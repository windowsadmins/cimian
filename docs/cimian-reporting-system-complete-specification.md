# Cimian Reporting System - Complete Technical Specification

**Document Version**: 1.0  
**Last Updated**: September 11, 2025  
**Target Audience**: ReportMate Integration, System Administrators, Developers

## Overview

The Cimian reporting system generates comprehensive telemetry data across three core JSON files, providing complete visibility into managed software deployment, package status, and system events. This document serves as the definitive technical specification for integrating with and interpreting Cimian's reporting outputs.

---

## File Locations and Structure

### Primary Report Files
- **Location**: `C:\ProgramData\ManagedInstalls\reports\`
- **Files Generated**: 
  - `sessions.json` - Session-level metadata and statistics
  - `events.jsonl` - Event stream data (JSON Lines format)
  - `items.json` - Package-level status and metrics

### File Update Frequency
- **Real-time**: `events.jsonl` (appended during operations)
- **Per-session**: `sessions.json` and `items.json` (updated at session completion)
- **Retention**: Files are rotated/archived based on system configuration

---

## 1. Sessions.json - Session Metadata and Statistics

### Purpose
Tracks high-level session information, system state, and aggregate statistics for each Cimian execution.

### File Format
```json
{
  "session_id": "session_20250911_143022",
  "start_time": "2025-09-11T14:30:22Z",
  "end_time": "2025-09-11T14:45:18Z",
  "duration_seconds": 896,
  "hostname": "ANIM-STD-LAB-12",
  "username": "SYSTEM",
  "manifest_path": "Shared/Curriculum/Animation/C3234/CintiqLab16",
  "total_managed_packages": 47,
  "packages_processed": 11,
  "successful_installs": 8,
  "successful_updates": 2,
  "failed_operations": 1,
  "warnings_generated": 3,
  "errors_encountered": 1,
  "install_loops_detected": 1,
  "architecture_mismatches": 2,
  "system_architecture": "arm64",
  "cimian_version": "2024.3.1",
  "execution_mode": "checkonly",
  "preflight_skipped": true,
  "manifest_validation_status": "success",
  "catalog_refresh_required": false,
  "conditional_items_evaluated": 15,
  "conditional_items_matched": 8,
  "network_connectivity_status": "online",
  "disk_space_available_gb": 256.7,
  "memory_usage_peak_mb": 84.2,
  "exit_code": 0,
  "exit_reason": "normal_completion"
}
```

### Key Fields Explanation

#### Session Identification
- **session_id**: Unique identifier format `session_YYYYMMDD_HHMMSS`
- **start_time/end_time**: ISO 8601 timestamps with timezone
- **duration_seconds**: Total execution time

#### System Context  
- **hostname**: Machine identifier for lab/deployment tracking
- **username**: Execution context (typically SYSTEM for scheduled runs)
- **manifest_path**: Hierarchical manifest path showing deployment context
- **system_architecture**: `x64` or `arm64` for compatibility analysis

#### Package Statistics
- **total_managed_packages**: Total packages in manifest (critical for deployment completeness)
- **packages_processed**: Packages that had actions attempted
- **successful_installs/updates**: Successful operations count
- **failed_operations**: Failed installation/update attempts
- **install_loops_detected**: Number of packages in install loops

#### System Health Metrics
- **network_connectivity_status**: `online`, `offline`, `limited`
- **disk_space_available_gb**: Available disk space for installations
- **memory_usage_peak_mb**: Peak memory usage during session
- **manifest_validation_status**: `success`, `warning`, `error`

#### Execution Context
- **execution_mode**: `install`, `checkonly`, `cleanup`, `selfupdate`
- **preflight_skipped**: Whether preflight checks were bypassed
- **exit_code**: System exit code (0 = success, non-zero = error)
- **exit_reason**: Human-readable exit explanation

---

## 2. Events.jsonl - Real-Time Event Stream

### Purpose
Provides granular, real-time event logging for detailed operational analysis and troubleshooting.

### File Format
JSON Lines format - each line is a complete JSON object representing a single event.

### Event Types and Examples

#### System Events
```json
{"timestamp": "2025-09-11T14:30:22.123Z", "event_type": "system_startup", "session_id": "session_20250911_143022", "hostname": "ANIM-STD-LAB-12", "message": "Cimian managed software update starting", "details": {"version": "2024.3.1", "execution_mode": "checkonly", "manifest": "Shared/Curriculum/Animation/C3234/CintiqLab16"}}

{"timestamp": "2025-09-11T14:30:23.456Z", "event_type": "manifest_loaded", "session_id": "session_20250911_143022", "message": "Manifest loaded successfully", "details": {"managed_installs_count": 47, "conditional_items_count": 15, "managed_profiles_count": 2}}

{"timestamp": "2025-09-11T14:30:24.789Z", "event_type": "architecture_detection", "session_id": "session_20250911_143022", "message": "System architecture detected", "details": {"detected_architecture": "arm64", "compatible_packages": 45, "incompatible_packages": 2}}
```

#### Package Installation Events
```json
{"timestamp": "2025-09-11T14:32:15.234Z", "event_type": "install_start", "session_id": "session_20250911_143022", "package_name": "Blender", "message": "Starting installation of Blender", "details": {"version": "4.2.1", "installer_type": "msi", "size_mb": 285.7}}

{"timestamp": "2025-09-11T14:35:42.567Z", "event_type": "install_success", "session_id": "session_20250911_143022", "package_name": "Blender", "message": "Blender installed successfully", "details": {"version": "4.2.1", "duration_seconds": 207, "exit_code": 0}}

{"timestamp": "2025-09-11T14:37:18.890Z", "event_type": "install_failure", "session_id": "session_20250911_143022", "package_name": "CUDA", "error": "Installation failed: NVIDIA GPU required but not detected", "details": {"version": "12.2", "exit_code": 1603, "installer_log": "C:\\Windows\\Temp\\CUDA_install.log"}}
```

#### Warning Events
```json
{"timestamp": "2025-09-11T14:33:45.123Z", "event_type": "architecture_mismatch", "session_id": "session_20250911_143022", "package_name": "Solidworks", "message": "Architecture mismatch: package supports x64, system is arm64", "details": {"package_architectures": ["x64"], "system_architecture": "arm64", "status": "skipped"}}

{"timestamp": "2025-09-11T14:38:22.456Z", "event_type": "install_loop_detected", "session_id": "session_20250911_143022", "package_name": "Cinema4D", "message": "Install loop detected: same_version_reinstalled_2025.1.0_4_times", "details": {"detection_criteria": "same_version_reinstalled_2025.1.0_4_times", "suspected_cause": "installer_reports_success_but_app_not_detected_v2025.1.0", "recommendation": "Verify installer exit codes and app detection logic in pkginfo"}}
```

#### Update Events  
```json
{"timestamp": "2025-09-11T14:40:15.789Z", "event_type": "update_available", "session_id": "session_20250911_143022", "package_name": "AdobeDesignCore", "message": "Update available for AdobeDesignCore", "details": {"current_version": "2024.12.1", "available_version": "2025.1.0", "update_size_mb": 1247.3}}

{"timestamp": "2025-09-11T14:42:33.012Z", "event_type": "update_success", "session_id": "session_20250911_143022", "package_name": "VLC", "message": "VLC updated successfully", "details": {"previous_version": "3.0.19", "new_version": "3.0.20", "duration_seconds": 138}}
```

#### Error Events
```json
{"timestamp": "2025-09-11T14:39:45.345Z", "event_type": "download_failure", "session_id": "session_20250911_143022", "package_name": "MayaComponents", "error": "Download failed: Connection timeout", "details": {"url": "https://packages.example.com/maya/2024.2.1.msi", "retry_count": 3, "last_http_status": 408}}

{"timestamp": "2025-09-11T14:41:12.678Z", "event_type": "dependency_missing", "session_id": "session_20250911_143022", "package_name": "Harmony", "error": "Missing dependency: UninstallHarmony2025 must complete first", "details": {"blocking_package": "UninstallHarmony2025", "dependency_type": "sequential"}}
```

### Event Field Standards

#### Common Fields (All Events)
- **timestamp**: ISO 8601 with milliseconds and timezone
- **event_type**: Standardized event classification
- **session_id**: Links to session metadata  
- **message**: Human-readable event description

#### Package-Specific Fields
- **package_name**: Exact package identifier from manifest
- **version**: Package version being processed
- **error**: Error message for failure events
- **details**: Additional structured data specific to event type

---

## 3. Items.json - Package Status and Metrics

### Purpose
Comprehensive per-package status, metrics, and operational history for deployment dashboard and troubleshooting.

### File Format  
JSON array containing detailed package records.

### Complete Package Record Structure

```json
{
  "item_name": "Blender",
  "display_name": "Blender 3D Animation Suite", 
  "current_status": "Installed",
  "install_count": 1,
  "update_count": 3,
  "failure_count": 0,
  "warning_count": 1,
  "last_error": "",
  "last_warning": "Update requires restart - scheduling for maintenance window",
  "last_install_date": "2025-08-15T14:22:00Z",
  "last_update_date": "2025-09-11T14:35:42Z",
  "version": "4.2.1",
  "supported_architectures": ["x64", "arm64"],
  "install_loop_detected": false,
  "loop_details": null,
  "recent_attempts": [
    {
      "session_id": "session_20250911_143022",
      "timestamp": "2025-09-11T14:35:42Z",
      "action": "update",
      "status": "success", 
      "version": "4.2.1"
    }
  ],
  "last_attempt_time": "2025-09-11T14:35:42Z",
  "last_attempt_status": "success",
  "install_method": "msi",
  "type": "managed_install",
  "size_mb": 285.7,
  "download_url": "https://download.blender.org/release/Blender4.2/blender-4.2.1-windows-x64.msi",
  "catalog_source": "production",
  "dependencies": [],
  "conflicts": [],
  "system_requirements": {
    "minimum_ram_gb": 4,
    "minimum_disk_gb": 2,
    "supported_os": ["Windows 10", "Windows 11"]
  }
}
```

### Status Classifications

#### Current Status Values
- **"Installed"**: Successfully installed and operational
- **"Update Available"**: Newer version available for installation  
- **"Install Failed"**: Installation attempts failed
- **"Update Failed"**: Update attempts failed
- **"Not Available"**: Package incompatible with system (architecture mismatch)
- **"Install Loop"**: Package stuck in reinstallation loop
- **"Pending Install"**: Scheduled for installation
- **"Pending Update"**: Scheduled for update
- **"Pending Removal"**: Scheduled for uninstallation

#### Metrics Interpretation
- **install_count**: Successful installations performed
- **update_count**: Successful updates performed  
- **failure_count**: Failed installation/update attempts
- **warning_count**: Non-fatal issues encountered

#### Error and Warning Context
- **last_error**: Most recent error message with technical details
- **last_warning**: Most recent warning with operational context
- **Timestamps**: ISO 8601 format for temporal analysis

### Install Loop Detection

#### Loop Detection Structure
```json
{
  "install_loop_detected": true,
  "loop_details": {
    "detection_criteria": "same_version_reinstalled_2025.1.0_4_times",
    "loop_start_session": "session_20250911_142230", 
    "suspected_cause": "installer_reports_success_but_app_not_detected_v2025.1.0",
    "recommendation": "Verify installer exit codes and app detection logic in pkginfo; check if silent install parameters are correct"
  },
  "recent_attempts": [
    {
      "session_id": "session_20250911_142230",
      "timestamp": "2025-09-11T14:22:30Z",
      "action": "install",
      "status": "success",
      "version": "2025.1.0"
    }
  ]
}
```

#### Loop Detection Criteria
1. **Repeated Failures**: 3+ attempts with <50% success rate
2. **Version Reinstallation**: Same version installed 3+ times  
3. **Rapid Consecutive Attempts**: 3+ attempts within 1-hour window

#### Suspected Causes (Automated Analysis)
- `installer_reports_success_but_app_not_detected_vX.X.X`
- `adobe_licensing_or_creative_cloud_conflict`
- `microsoft_installer_service_or_office_conflict`
- `java_version_conflict_or_registry_corruption`
- `system_instability_or_automated_retry_loop`
- `intermittent_system_conditions_or_timing_issues`

### Architecture Compatibility

#### Architecture Mismatch Handling
```json
{
  "current_status": "Not Available",
  "warning_count": 1,
  "last_warning": "Architecture mismatch: package supports x64, system is arm64",
  "supported_architectures": ["x64"],
  "system_architecture": "arm64"
}
```

#### Compatibility Matrix
- **Full Compatibility**: Package architecture matches system architecture
- **Partial Compatibility**: Multi-architecture packages on compatible system
- **No Compatibility**: x64-only packages on arm64 systems (marked "Not Available")

---

## Integration Guidelines for ReportMate

### Data Correlation Strategy

#### Session-to-Events Correlation
```sql
-- Correlate session with events
SELECT s.*, e.* 
FROM sessions.json s
JOIN events.jsonl e ON s.session_id = e.session_id
WHERE s.session_id = 'session_20250911_143022'
```

#### Package Status Dashboard
```json
// Dashboard metrics from items.json
{
  "total_packages": 47,
  "installed": 42,
  "failed": 3, 
  "install_loops": 1,
  "architecture_incompatible": 2,
  "pending_updates": 8,
  "warnings_active": 5
}
```

### Key Monitoring Patterns

#### Critical Alerts
- **install_loops_detected > 0**: Immediate investigation required
- **failed_operations > 10%**: Deployment health issue  
- **architecture_mismatches**: Hardware compatibility planning
- **exit_code != 0**: Session-level failures

#### Trending Analysis  
- **Package success rates** over time from items.json metrics
- **Session duration trends** for performance monitoring
- **Error pattern analysis** from events.jsonl categorization
- **Architecture migration tracking** for hardware refresh planning

#### Operational Intelligence
- **Peak installation times** from session timestamps
- **Most problematic packages** from failure_count sorting
- **Manifest effectiveness** from conditional_items evaluation
- **System resource utilization** from memory/disk metrics

### ReportMate Query Examples

#### Get All Failed Installations Today
```javascript
// Parse items.json
const items = JSON.parse(itemsJsonData);
const failedToday = items.filter(item => 
  item.current_status.includes("Failed") && 
  new Date(item.last_attempt_time).toDateString() === new Date().toDateString()
);
```

#### Identify Packages Needing Attention
```javascript  
// Packages with warnings or errors
const needsAttention = items.filter(item =>
  item.install_loop_detected || 
  item.failure_count > 0 || 
  item.current_status === "Not Available"
);
```

#### Session Success Rate Analysis
```javascript
// Calculate session-level success metrics
const sessionData = JSON.parse(sessionsJsonData);
const successRate = (sessionData.successful_installs + sessionData.successful_updates) / 
                   sessionData.packages_processed * 100;
```

---

## File Monitoring and Automation

### File Change Detection
- **sessions.json**: Monitor for new sessions (file modification time)
- **events.jsonl**: Tail file for real-time event processing
- **items.json**: Parse for status changes and metric updates

### Automated Alert Triggers
1. **Install loop detected**: `install_loop_detected: true` in items.json
2. **High failure rate**: `failed_operations / packages_processed > 0.2` in sessions.json  
3. **Architecture mismatches**: `current_status: "Not Available"` with architecture warning
4. **System resource alerts**: `disk_space_available_gb < 10` in sessions.json

### Performance Considerations  
- **File sizes**: events.jsonl can grow large; implement log rotation monitoring
- **Parsing frequency**: items.json/sessions.json change per-session, not continuously  
- **Network efficiency**: Only process changed data using file modification timestamps
- **Storage planning**: Factor in 7-day retention with typical sizes (sessions.json ~2KB, items.json ~50KB, events.jsonl variable)

---

## Troubleshooting and Diagnostics

### Common Issues and Resolution

#### Missing Package Data in items.json
- **Cause**: Package only in manifest, never attempted installation
- **Solution**: Check manifest processing in sessions.json
- **Verification**: Look for manifest_loaded event in events.jsonl

#### Install Loop False Positives  
- **Cause**: Legitimate reinstallations flagged as loops
- **Solution**: Review loop_details.detection_criteria and recent_attempts timing
- **Tuning**: Adjust detection thresholds based on environment

#### Architecture Mismatch Notifications
- **Expected**: x64-only packages on arm64 systems show "Not Available"  
- **Action**: Plan architecture-compatible alternatives or hardware upgrades
- **Monitoring**: Track mismatch trends for procurement planning

### Data Validation Checks

#### Session Data Integrity
- session_id uniqueness across time periods
- start_time <= end_time consistency  
- Package counts (total_managed_packages >= packages_processed)
- Metric summation (successful + failed = packages_processed)

#### Event Stream Completeness
- Every install_start has corresponding success/failure event
- Session boundaries (system_startup and session_complete events)
- Timestamp ordering within session_id groups

#### Package Status Consistency  
- current_status aligns with last_attempt_status
- Metric counts match recent_attempts history
- Architecture compatibility logic correctness

---

## Conclusion

This specification provides ReportMate with comprehensive understanding of Cimian's reporting outputs. The three-file system delivers complete operational visibility:

- **sessions.json**: High-level session metadata and aggregate metrics
- **events.jsonl**: Granular real-time operational events  
- **items.json**: Detailed per-package status and historical metrics

The enhanced install loop detection, architecture compatibility analysis, and comprehensive error/warning system provide the operational intelligence needed for effective managed software deployment monitoring and troubleshooting.

For technical support or integration questions, refer to the Cimian development team or system documentation.
