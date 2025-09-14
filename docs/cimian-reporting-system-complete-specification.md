# Cimian Reporting System - Complete Technical Specification

**Document Version**: 1.1  
**Last Updated**: September 13, 2025  
**Target Audience**: ReportMate Integration, System Administrators, Developers  
**Critical Update**: Data inconsistency bug resolved - installation failures now accurately reported

## Overview

The Cimian reporting system generates comprehensive telemetry data across three core JSON files, providing complete visibility into managed software deployment, package status, and system events. This document serves as the definitive technical specification for integrating with and interpreting Cimian's reporting outputs.

**CRITICAL UPDATE (September 13, 2025):** A major data inconsistency bug has been resolved where installation failures were incorrectly reported as successful operations. All session status indicators, event logs, and success/failure counts now accurately reflect actual installation results, eliminating false positive monitoring alerts.

---

## File Locations and Structure

### Primary Report Files
- **Location**: `C:\ProgramData\ManagedInstalls\reports\`
- **Files Generated**: 
  - `sessions.json` - Session-level metadata and statistics
  - `events.jsonl` - Event stream data (JSON Lines format)
  - `items.json` - Package-level status and metrics

### File Update Frequency
- **Real-time**: `events.jsonl` (appended during operations) - ✅ **FIXED:** Events now properly written in JSONL format
- **Per-session**: `sessions.json` and `items.json` (updated at session completion) - ✅ **FIXED:** Generated after session finalization
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
- **successful_installs/updates**: Successful operations count - ✅ **FIXED:** Accurate counts based on actual results
- **failed_operations**: Failed installation/update attempts - ✅ **FIXED:** Properly incremented for MSI 1603 and other failures
- **install_loops_detected**: Number of packages in install loops

#### System Health Metrics
- **network_connectivity_status**: `online`, `offline`, `limited`
- **disk_space_available_gb**: Available disk space for installations
- **memory_usage_peak_mb**: Peak memory usage during session
- **manifest_validation_status**: `success`, `warning`, `error`

#### Execution Context
- **execution_mode**: `install`, `checkonly`, `cleanup`, `selfupdate`, `installonly`
- **preflight_skipped**: Whether preflight checks were bypassed
- **exit_code**: System exit code (0 = success, non-zero = error) - ✅ **FIXED:** Accurately reflects installation results
- **exit_reason**: Human-readable exit explanation

---

## 2. Events.jsonl - Real-Time Event Stream

### Purpose
Provides granular, real-time event logging for detailed operational analysis and troubleshooting.

### File Format
JSON Lines format - each line is a complete JSON object representing a single event.

### Event Types and Examples

**Data Integrity Note:** All events now properly populate the `level` field with accurate values (`ERROR`, `WARN`, `INFO`, `DEBUG`) and are written in consistent JSONL format for reliable parsing.

#### System Events
```json
{"timestamp": "2025-09-11T14:30:22.123Z", "event_type": "system_startup", "session_id": "session_20250911_143022", "hostname": "ANIM-STD-LAB-12", "message": "Cimian managed software update starting", "details": {"version": "2024.3.1", "execution_mode": "checkonly", "manifest": "Shared/Curriculum/Animation/C3234/CintiqLab16"}}

{"timestamp": "2025-09-11T14:30:23.456Z", "event_type": "manifest_loaded", "session_id": "session_20250911_143022", "message": "Manifest loaded successfully", "details": {"managed_installs_count": 47, "conditional_items_count": 15, "managed_profiles_count": 2}}

{"timestamp": "2025-09-11T14:30:24.789Z", "event_type": "architecture_detection", "session_id": "session_20250911_143022", "message": "System architecture detected", "details": {"detected_architecture": "arm64", "compatible_packages": 45, "incompatible_packages": 2}}
```

#### Package Installation Events
```json
{"timestamp": "2025-09-13T19:37:15.234Z", "event_type": "install_start", "session_id": "session_20250913_193719", "package_name": "Chrome", "level": "INFO", "status": "started", "message": "Starting installation of Chrome", "details": {"version": "139.0.7258.139", "installer_type": "msi", "size_mb": 95.2}}

{"timestamp": "2025-09-13T19:37:42.567Z", "event_type": "install_failure", "session_id": "session_20250913_193719", "package_name": "Chrome", "level": "ERROR", "status": "error", "message": "Installation of Chrome failed: MSI installation failed with exit code 1603", "details": {"version": "139.0.7258.139", "duration_seconds": 27, "exit_code": 1603}}

{"timestamp": "2025-09-13T19:37:15.234Z", "event_type": "install_success", "session_id": "session_20250911_143022", "package_name": "Blender", "level": "INFO", "status": "success", "message": "Blender installed successfully", "details": {"version": "4.2.1", "duration_seconds": 207, "exit_code": 0}}
```

**Fixed Data Integrity Issues:**
- Events now have proper `level` values (`ERROR` for failures, `INFO` for successes)
- Installation failure events are reliably written and parsed
- MSI 1603 errors and other installer failures properly populate error details

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
- **level**: Log level (`ERROR`, `WARN`, `INFO`, `DEBUG`) - ✅ **FIXED:** Properly populated based on event status
- **status**: Event status (`error`, `success`, `warning`, etc.) - ✅ **FIXED:** Consistently populated 
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
- **failed_operations > 10%**: Deployment health issue - ✅ **FIXED:** Now accurately detects actual failure rates
- **architecture_mismatches**: Hardware compatibility planning
- **exit_code != 0**: Session-level failures - ✅ **FIXED:** Correctly set based on installation results
- **session status = "failed"**: Installation failures detected - ✅ **NEW:** Reliable failure detection

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

#### Get All Failed Sessions Today
```javascript
// Parse sessions.json - ✅ FIXED: Failed sessions now properly marked as "failed"
const sessions = JSON.parse(sessionsJsonData);
const failedToday = sessions.filter(session => 
  (session.status === "failed" || session.status === "partial_failure") && 
  new Date(session.start_time).toDateString() === new Date().toDateString()
);
```

#### Monitor Installation Failures in Real-Time
```javascript  
// Parse events.jsonl - ✅ FIXED: Error events now have proper level and status fields
const events = eventsJsonlData.split('\n')
  .filter(line => line.trim())
  .map(line => JSON.parse(line));
  
const installFailures = events.filter(event =>
  event.level === "ERROR" && 
  event.event_type === "install_failure"
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
// Calculate session-level success metrics - ✅ FIXED: Now based on accurate counts
const sessionData = JSON.parse(sessionsJsonData);
const sessionsWithActions = sessionData.filter(s => s.packages_processed > 0);
const successRate = sessionsWithActions.reduce((acc, session) => {
  const sessionSuccessRate = (session.successful_installs + session.successful_updates) / 
                            session.packages_processed * 100;
  return acc + sessionSuccessRate;
}, 0) / sessionsWithActions.length;

// Identify problematic sessions
const problemSessions = sessionsWithActions.filter(s => 
  s.status === "failed" || s.failed_operations > s.successful_installs + s.successful_updates
);
```

---

## File Monitoring and Automation

### File Change Detection
- **sessions.json**: Monitor for new sessions (file modification time)
- **events.jsonl**: Tail file for real-time event processing
- **items.json**: Parse for status changes and metric updates

### Automated Alert Triggers
1. **Install loop detected**: `install_loop_detected: true` in items.json
2. **High failure rate**: `failed_operations / packages_processed > 0.2` in sessions.json - ✅ **FIXED:** Now reliable  
3. **Architecture mismatches**: `current_status: "Not Available"` with architecture warning
4. **System resource alerts**: `disk_space_available_gb < 10` in sessions.json
5. **Installation failures**: `status: "failed"` in sessions.json - ✅ **NEW:** Reliable failure detection
6. **MSI error detection**: `level: "ERROR"` with `exit_code: 1603` in events.jsonl - ✅ **NEW:** Specific MSI failure alerts

### Performance Considerations  
- **File sizes**: events.jsonl can grow large; implement log rotation monitoring
- **Parsing frequency**: items.json/sessions.json change per-session, not continuously  
- **Network efficiency**: Only process changed data using file modification timestamps
- **Storage planning**: Factor in 7-day retention with typical sizes (sessions.json ~2KB, items.json ~50KB, events.jsonl variable)

---

## Troubleshooting and Diagnostics

### Fixed Data Integrity Issues (September 13, 2025)

#### Session Status Accuracy ✅ RESOLVED
- **Previous Issue**: Installation failures showed as `"completed"` sessions with `"failures": 0`
- **Root Cause**: Reports generated before session finalization; install-only mode assumed success
- **Resolution**: Reports now generated after session completion; accurate success/failure tracking
- **Verification**: Chrome MSI 1603 failures now show `"status": "failed"` with correct failure counts

#### Event Stream Reliability ✅ RESOLVED  
- **Previous Issue**: Error events missing from `events.json` due to parsing failures
- **Root Cause**: Events written in pretty-printed JSON format, parsed as JSONL; empty `level` fields
- **Resolution**: Events written in consistent JSONL format with proper level mapping
- **Verification**: MSI 1603 errors now appear as `{"level":"ERROR","status":"error","message":"..."}`

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
- Metric summation validation - ✅ **FIXED:** (successful + failed ≈ packages_processed)
- Status consistency - ✅ **FIXED:** (`status = "failed"` when failures > 0)

#### Event Stream Completeness
- Every install_start has corresponding success/failure event
- Session boundaries (system_startup and session_complete events)
- Timestamp ordering within session_id groups
- Level field population - ✅ **FIXED:** (all events have valid `level` values)
- JSONL format consistency - ✅ **FIXED:** (single-line JSON format)

#### Package Status Consistency  
- current_status aligns with last_attempt_status
- Metric counts match recent_attempts history
- Architecture compatibility logic correctness

---

## Conclusion

This specification provides ReportMate with comprehensive understanding of Cimian's reporting outputs. The three-file system delivers complete operational visibility:

- **sessions.json**: High-level session metadata and aggregate metrics - ✅ **FIXED:** Accurate failure reporting
- **events.jsonl**: Granular real-time operational events - ✅ **FIXED:** Reliable error event capture  
- **items.json**: Detailed per-package status and historical metrics

**Critical Data Integrity Improvements (September 13, 2025):**
The major bug causing installation failures to be reported as successes has been completely resolved. MSI 1603 errors, installer failures, and other deployment issues now trigger proper failure reporting across all data structures, enabling reliable monitoring and alerting.

The enhanced install loop detection, architecture compatibility analysis, and comprehensive error/warning system, combined with the **fixed data integrity**, provide the operational intelligence needed for effective managed software deployment monitoring and troubleshooting.

**ReportMate Integration Impact:**
- Installation failures now reliably trigger alerts instead of showing false positive successes
- Error events appear consistently in event streams with proper severity levels
- Session status and metrics accurately reflect deployment health
- Exit codes and failure counts enable automated remediation workflows

For technical support or integration questions, refer to the Cimian development team or system documentation.
