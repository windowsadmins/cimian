# Cimian ReportMate Status Specification

**Document Version:** 1.0  
**Date:** September 4, 2025  
**Author:** Cimian Development Team  
**Purpose:** Status vocabulary specification for ReportMate dashboard integration

## Overview

This document defines the complete status vocabulary and data structures that Cimian writes to the `/reports` directory for consumption by ReportMate dashboards. All status values have been standardized and enhanced as of the latest reporting system update.

## Report File Structure

Cimian generates three primary JSON files in `C:\ProgramData\ManagedInstalls\reports\`:

- **`sessions.json`** - Session-level execution summaries
- **`events.json`** - Detailed event logs with package context
- **`items.json`** - Comprehensive package status inventory

## Status Vocabularies

### 1. Event Status Values (`events.json`)

The `status` field in event records uses these standardized values:

| Status | Description | Trigger Conditions |
|--------|-------------|-------------------|
| `"Success"` | Operation completed successfully | Status contains: "completed", "success", "ok", "installed"<br/>Log level: "INFO", "DEBUG" |
| `"Failed"` | Operation failed with error | Error message present<br/>Log level: "ERROR"<br/>Status contains: "fail" |
| `"Warning"` | Operation completed with warnings | Log level: "WARN", "WARNING"<br/>Status contains: "warn" |
| `"Pending"` | Operation waiting/queued | Status contains: "pending", "waiting", "blocked", "queued" |
| `"Skipped"` | Operation bypassed/skipped | Status contains: "skip", "bypass" |
| `"Unknown"` | Default/unrecognized status | Fallback for unmatched conditions |

#### Event Status Normalization Logic

```go
// Cimian applies this priority order:
1. Error conditions (errorMsg != "" OR level == "error") → "Failed"
2. Warning conditions (level == "warn/warning") → "Warning" 
3. Success conditions (status matches success patterns) → "Success"
4. Pending conditions (status matches pending patterns) → "Pending"
5. Skipped conditions (status matches skip patterns) → "Skipped"
6. Default by log level → "Success"/"Failed"/"Warning"/"Unknown"
```

### 2. Package Status Values (`items.json`)

The `current_status` field represents the definitive package state:

| Status | Description | Business Logic |
|--------|-------------|----------------|
| `"Installed"` | Package successfully installed and operational | Last installation/update attempt was successful |
| `"Failed"` | Package installation/update failed | Most recent attempt failed (persists until success) |
| `"Warning"` | Package installed but with warnings | Non-critical issues during installation (doesn't override "Failed") |
| `"Install Loop"` | Package stuck in reinstall cycle | ≥3 attempts in 7 days with <50% success rate |
| `"Not Installed"` | Package removed or never installed | Last operation was removal or no install history |
| `"Pending Install"` | Package available but not yet installed | "Not Installed" + latest version available in catalog |
| `"Error"` | Package version/state unknown | Missing version information or catalog lookup failed |

#### Package Status Priority Logic

```go
// Status determination follows this hierarchy:
1. Install Loop Detection → "Install Loop"
2. Version Unknown → "Error" 
3. Not Installed + Available → "Pending Install"
4. Last Attempt Status → "Installed"/"Failed"/"Warning"/"Not Installed"
```

### 3. Session Status Indicators (`sessions.json`)

Session-level status tracking includes multiple dimensions:

#### Session Status Field
| Status | Description |
|--------|-------------|
| `"completed"` | Session finished normally |
| `"failed"` | Session encountered critical errors |
| `"terminated"` | Session stopped unexpectedly |

#### Session Summary Counters
```json
{
  "successes": 12,           // Successful package operations
  "failures": 2,             // Failed package operations  
  "total_actions": 14,       // Total package operations attempted
  "packages_installed": 10,   // Packages currently installed
  "packages_pending": 3,     // Packages awaiting installation
  "packages_failed": 2       // Packages in failed state
}
```

## Enhanced Data Structures

### Event Record Structure

```json
{
  "event_id": "session-id-package-id-timestamp",
  "session_id": "2025-09-04-143052",
  "timestamp": "2025-09-04T14:30:58Z",
  "level": "INFO|WARN|ERROR|DEBUG",
  "event_type": "install|update|remove|download|config",
  
  // Enhanced package context (NEW)
  "package_id": "firefox-browser",
  "package_name": "Firefox",
  "package_version": "119.0.1",
  
  // Status information
  "action": "install_package",
  "status": "Success|Failed|Warning|Pending|Skipped|Unknown",
  "message": "Human readable description",
  
  // Error details (NEW)
  "error_details": {
    "error_code": 1603,
    "error_type": "installer_failure",
    "command": "msiexec /i firefox.msi /quiet",
    "stderr": "Installation package corrupt",
    "retry_count": 2,
    "resolution_hint": "Check installer integrity and retry"
  },
  
  // Installation context (NEW)
  "installer_type": "msi|exe|nupkg|chocolatey|powershell",
  
  // Legacy fields (maintained for compatibility)
  "package": "Firefox",
  "version": "119.0.1"
}
```

### Package Record Structure

```json
{
  // Core identification
  "id": "firefox-browser",
  "item_name": "Firefox",
  "display_name": "Mozilla Firefox",
  "item_type": "managed_installs",
  
  // Version information  
  "current_status": "Installed|Failed|Warning|Install Loop|Not Installed|Pending Install|Error",
  "latest_version": "119.0.1",
  "installed_version": "119.0.1",
  
  // Status tracking
  "last_seen_in_session": "2025-09-04-143052", 
  "last_successful_time": "2025-09-04T14:30:58Z",
  "last_attempt_time": "2025-09-04T14:30:58Z",
  "last_attempt_status": "Success|Failed|Warning",
  
  // Statistics
  "install_count": 3,
  "update_count": 8, 
  "removal_count": 0,
  "failure_count": 1,
  "warning_count": 2,
  "total_sessions": 15,
  
  // Enhanced loop detection (NEW)
  "install_loop_detected": false,
  "loop_details": {
    "detection_criteria": "same_version_reinstalled",
    "loop_start_session": "2025-09-03-120000",
    "suspected_cause": "installer_exit_code_success_but_not_installed_119.0.1", 
    "recommendation": "check_msi_installer_silent_flags_and_admin_rights"
  },
  
  // Error context
  "last_error": "Installation failed with exit code 1603",
  "last_warning": "Registry key access delayed",
  
  // Installation method
  "install_method": "msi|exe|nupkg|chocolatey|powershell|unknown"
}
```

### Session Record Structure

```json
{
  "session_id": "2025-09-04-143052",
  "start_time": "2025-09-04T14:30:52Z",
  "end_time": "2025-09-04T14:35:28Z", 
  "run_type": "auto|manual|triggered",
  "status": "completed|failed|terminated",
  "duration": 276,
  
  // Operation summary
  "total_actions": 14,
  "installs": 5,
  "updates": 7,
  "removals": 2,
  "successes": 12,
  "failures": 2,
  
  // Environment context
  "hostname": "WORKSTATION-01",
  "user": "SYSTEM",
  "process_id": 1234,
  "log_version": "25.9.3.2134",
  
  // Enhanced summary (NEW)
  "summary": {
    "total_packages_managed": 14,
    "packages_installed": 10,
    "packages_pending": 3,
    "packages_failed": 2,
    "cache_size_mb": 1024.5,
    
    // Failed package details (NEW)
    "failed_packages": [
      {
        "package_id": "chrome-browser",
        "package_name": "Chrome", 
        "error_type": "installer_failure",
        "last_attempt": "2025-09-04T14:31:15Z",
        "error_message": "Exit code 1603"
      }
    ]
  },
  
  // Configuration context (NEW)
  "config": {
    "manifest": "default-workstation",
    "software_repo_url": "https://repo.company.com/cimian",
    "client_identifier": "workstation-standard",
    "bootstrap_mode": false,
    "cache_path": "C:\\ProgramData\\ManagedInstalls\\Cache",
    "log_level": "INFO"
  }
}
```

## ReportMate Integration Guidelines

### 1. Dashboard Status Mapping

**Package Health Overview:**
- **Green (Healthy):** `current_status` = `"Installed"`
- **Red (Critical):** `current_status` = `"Failed"` OR `"Install Loop"` OR `"Error"`
- **Yellow (Warning):** `current_status` = `"Warning"` OR `"Pending Install"`
- **Gray (Inactive):** `current_status` = `"Not Installed"`

**Event Trend Analysis:**
- **Success Rate:** Count of `status` = `"Success"` vs total events
- **Failure Rate:** Count of `status` = `"Failed"` vs total events  
- **Warning Rate:** Count of `status` = `"Warning"` vs total events

### 2. Key Performance Indicators (KPIs)

```sql
-- Package Success Rate
SELECT 
  COUNT(CASE WHEN current_status = 'Installed' THEN 1 END) * 100.0 / COUNT(*) as success_rate
FROM items_table;

-- Session Reliability  
SELECT
  AVG(successes * 100.0 / total_actions) as avg_session_success_rate
FROM sessions_table 
WHERE total_actions > 0;

-- Install Loop Detection
SELECT 
  COUNT(*) as packages_in_loop
FROM items_table 
WHERE install_loop_detected = true;
```

### 3. Alert Thresholds

**Critical Alerts:**
- Package `current_status` = `"Failed"` for >24 hours
- Package `install_loop_detected` = `true`
- Session `failures` > 50% of `total_actions`

**Warning Alerts:**  
- Package `current_status` = `"Warning"` for >7 days
- Package `failure_count` > 3 in recent sessions
- Session success rate < 80%

### 4. Data Refresh Frequency

- **Events:** Real-time (generated during package operations)
- **Sessions:** Per session completion (typically 15-60 minutes)
- **Items:** Daily summary refresh (consolidated package states)

## Error Code Reference

### Common Error Types (`error_details.error_type`)

| Error Type | Description | Resolution Hint |
|------------|-------------|-----------------|
| `permission_denied` | Access/privilege issues | Run as administrator or check file permissions |
| `installer_failure` | Installer exit code error | Check installer logs and verify package integrity |
| `timeout` | Operation timed out | Check network connectivity and retry |
| `network_failure` | Download/connectivity issues | Verify internet connection and proxy settings |
| `dependency_missing` | Required components missing | Install required dependencies or check manifest |
| `registry_error` | Windows registry issues | Check registry permissions and integrity |
| `file_not_found` | Missing files/paths | Verify file paths and cache integrity |

### MSI Error Codes

| Exit Code | Status Mapping | Description |
|-----------|----------------|-------------|
| 0 | Success | Installation completed successfully |
| 1603 | Failed | Fatal error during installation |
| 1618 | Failed | Another installation is in progress |
| 1639 | Failed | Invalid command line argument |
| 3010 | Warning | Restart required to complete installation |

## Implementation Notes

### Backward Compatibility

Cimian maintains legacy field names for compatibility:
- `package` field mirrors `package_name`
- `version` field mirrors `package_version`
- Old status values are normalized to new vocabulary

### Performance Considerations

- Events.json limited to 48 hours of data (performance optimization)
- Items.json provides comprehensive historical view
- Sessions.json includes last 30 days by default

### Data Validation

ReportMate should validate:
- Status values match defined vocabulary
- Timestamp formats follow RFC3339
- Required fields are present and non-empty
- Numeric fields are within expected ranges

## Support Contact

For questions about this specification or ReportMate integration:
- **Development Team:** Cimian Core Team
- **Documentation:** `/docs/cimian-reportmate-status-specification.md`
- **Implementation Reference:** `/pkg/reporting/reporting.go`

---

**Note:** This specification reflects the enhanced reporting system implemented in Cimian v25.9.3+ with standardized status vocabulary and enhanced package context for improved ReportMate dashboard integration.
