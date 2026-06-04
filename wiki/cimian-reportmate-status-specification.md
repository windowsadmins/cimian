# Cimian ReportMate Status Specification

**Document Version:** 1.0  
**Date:** September 4, 2025  
**Author:** Cimian Development Team  
**Purpose:** Status vocabulary specification for ReportMate dashboard integration

## Overview

This document defines the complete status vocabulary and data structures that Cimian writes to the `/reports` directory for consumption by ReportMate dashboards. All status values have been standardized and enhanced as of the latest reporting system update.

## Report File Structure

Cimian generates these primary JSON files in `C:\ProgramData\ManagedInstalls\reports\`:

- **`sessions.json`** - Session-level execution summaries
- **`events.json`** - Detailed event logs with package context
- **`items.json`** - Comprehensive package status inventory
- **`packages.json`** - Aggregated package statistics
- **`run.log`** - Truncated each session — latest run trace

## Status Vocabularies

### 1. Event Status Values (`events.json`)

Each record in `events.json` is built from one line of the session's `events.jsonl` stream (see `DataExporter.GenerateEventsTable` in `shared/core/Services/DataExporter.cs`). The `status` field is **passed through from the event source** — Cimian does not enforce a fixed enum here. Common observed values include:

| Status | Typical Meaning |
|--------|------------------|
| `started` | Action began (download, install, script execution) |
| `completed` / `success` | Action finished successfully |
| `failed` / `error` | Action failed |
| `warning` | Action emitted a non-fatal issue (architecture mismatch, deferred, etc.) |
| `skipped` | Action bypassed |
| `pending` | Action queued or awaiting prerequisite |

For triage, combine `status` with `level` (`DEBUG`/`INFO`/`WARN`/`ERROR`), `action`, and `event_type` (`install`, `update`, `remove`, `download`, `config`, etc.).

This event-level vocabulary is **distinct from per-item `current_status`** in `items.json` (described below).

### 2. Per-Item Status (`items.json`)

The `current_status` field is normalized via `NormalizeItemStatus` (see `shared/core/Services/SessionLogger.cs`) and takes one of these values:

| Status | Description | Source Conditions |
|--------|-------------|-------------------|
| `"Installed"` | Verification succeeded | StatusService returned `installed` (file/registry/script/MSI match, or script-only item with no verifier) |
| `"Pending"` | Item needs action | StatusService returned `pending` (not installed, version outdated, blocking apps, dependency missing, etc.) — also used for `skipped` and `not installed` inputs |
| `"Error"` | Action failed or check errored | StatusService returned `error`, or a session attempt was `failed`/`error` |
| `"Warning"` | Non-fatal issue during session | Session attempt was `warning` (architecture mismatch logged as warning, etc.) |
| `"Removed"` | Package successfully uninstalled | Session attempt was `removed`/`uninstalled` |
| `"Not Available"` | Item not present in any catalog | Input status `not available` |

The underlying per-item `StatusService.Status` (lowercase) is one of `installed`, `pending`, `error`; the accompanying `ReasonCode` (see `shared/core/Models/StatusReasonCode.cs`) explains *why* and is exposed on the record as `status_reason_code`.

#### Install Loop is a Flag, Not a Status

Install loops are tracked as a **boolean** on each item record: `install_loop_detected` plus a `loop_details` sub-object describing the detection criteria and recommendation. `DataExporter.DetectInstallLoopEnhanced` fires on patterns such as repeated same-version reinstalls or sustained low success-rate over recent attempts. Install loop is NOT a `current_status` value.

#### Default Behavior for Installers Without a Verification Path

As of commit `abdac1e`, an item with a real installer payload (`msi`/`exe`/`pkg`/`nupkg`/`copy`) and **no** verification metadata (no `installs[]`, no `installcheck_script`, no `Check.*`, no MSI ProductCode, and no `ManagedInstalls` registry entry) defaults to `Status=pending` with `ReasonCode=not_installed` — surfacing as `current_status: "Pending"` in `items.json`. Script-only items (`installer.type` of `nopkg`, `script`, or empty) still default to `Installed` with `ReasonCode=no_checks`.

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
  "status": "started|completed|failed|warning|skipped|pending",
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
  "current_status": "Installed|Pending|Error|Warning|Removed|Not Available",
  "latest_version": "119.0.1",
  "installed_version": "119.0.1",

  // Status reason — machine-readable explanation of current_status
  "status_reason_code": "version_match|not_installed|version_outdated|architecture_mismatch|loop_suppressed|check_failed|...",
  "status_reason": "Human-readable explanation",
  "detection_method": "registry|file|directory|wmi|script|msi|msix|self_update|installs_array|managed_installs|none",

  // Status tracking
  "last_seen_in_session": "2025-09-04-1430",
  "last_successful_time": "2025-09-04T14:30:58Z",
  "last_attempt_time": "2025-09-04T14:30:58Z",
  "last_attempt_status": "Installed|Pending|Error|Warning|Removed|Not Available",
  
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
  "session_id": "2025-09-04-1430",
  "start_time": "2025-09-04T14:30:52Z",
  "end_time": "2025-09-04T14:35:28Z", 
  "run_type": "auto|manual|bootstrap|checkonly|installonly",
  "status": "completed|failed|terminated|running",
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
- **Red (Critical):** `current_status` = `"Error"` OR `install_loop_detected = true`
- **Yellow (Warning):** `current_status` IN (`"Warning"`, `"Pending"`)
- **Gray (Inactive):** `current_status` IN (`"Removed"`, `"Not Available"`)

**Event Trend Analysis:**
- **Success Rate:** Count of event `status` IN (`completed`, `success`) vs total events
- **Failure Rate:** Count of event `status` IN (`failed`, `error`) or `level = ERROR` vs total events
- **Warning Rate:** Count of event `status` = `warning` or `level = WARN` vs total events

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
- Package `current_status` = `"Error"` for >24 hours
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
- **Implementation References:**
- `shared/core/Models/Reporting.cs` — `SessionRecord`, `EventRecord`, `ItemRecord`, `InstallLoopDetail`
- `shared/core/Models/StatusReasonCode.cs` — machine-readable `ReasonCode` values
- `shared/core/Services/SessionLogger.cs` — `NormalizeItemStatus`, session lifecycle, log paths
- `shared/core/Services/DataExporter.cs` — `GenerateEventsTable`, `DetectInstallLoopEnhanced`, items export
- `cli/managedsoftwareupdate/Services/StatusService.cs` — per-item status determination

---

**Note:** This specification reflects the enhanced reporting system implemented in Cimian v25.9.3+ with standardized status vocabulary and enhanced package context for improved ReportMate dashboard integration.
