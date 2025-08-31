# Cimian Error Reporting Guide for ReportMate Integration

## Overview

This document provides the structure and location of error data in Cimian's `/reports` directory for ReportMate integration. Cimian uses an improved status classification system that distinguishes between actual software installation failures (Errors) and repository/configuration issues (Warnings).

## Status Classification System

Cimian uses a simplified 5-status classification system to categorize installation states:

### **The 5 Status Types**

1. **`installed`** - Package is successfully installed and current
2. **`warning`** - Repository/configuration issues (e.g., architecture mismatches, package obsolete)  
3. **`error`** - Actual software installation failures (installer execution problems)
4. **`pending`** - Waiting for installation or in progress
5. **`removed`** - Package has been uninstalled

### **Status Usage Guidelines**

**When to use `warning` vs `error`:**
- **`warning`**: Repository configuration issues, architecture mismatches, package not available, policy restrictions
- **`error`**: Actual installer execution failures, download corruption, dependency failures

This simplified classification helps distinguish between:
- **Real software installation problems** (`error`) - Issues requiring software fixes
- **Repository/configuration issues** (`warning`) - Issues requiring environment/policy changes

### Important: Status vs Log Level

**For ReportMate integration, focus only on the `status` field:**
- `status`: Uses our 5-type classification (`installed`, `warning`, `error`, `pending`, `removed`)
- `level`: Console logging severity (`DEBUG`, `INFO`, `WARN`, `ERROR`) - **ignore for reporting**

## Report Directory Structure

```
C:\ProgramData\ManagedInstalls\reports\
├── sessions.json     # Session-level summaries
├── events.json       # Aggregated event data
└── items.json        # Per-package status and history
```

## Architecture Mismatch Warning Example

### Console Warning Log
```
[2025-08-30 20:33:04] WARN  Architecture mismatch, skipping item: PowerShell systemArch: arm64 supportedArch: [x64]
[2025-08-30 20:33:04] WARN  Architecture mismatch, skipping item: Cimian systemArch: arm64 supportedArch: [x64]
```

### Event Data Structure (`events.jsonl`)
```json
{
  "event_type": "install",
  "action": "architecture_check", 
  "status": "warning",
  "message": "Architecture mismatch: system arch arm64 not supported (package supports: [x64])",
  "context": {
    "item": "PowerShell",
    "system_arch": "arm64", 
    "supported_arch": "[x64]"
  }
}
```

**Note**: The `level` field (DEBUG, INFO, WARN, ERROR) is just for console logging severity. For ReportMate integration, focus only on the `status` field which uses our 5-status classification system.

### Installation Failure Error Example

### Console Error Log
```
[2025-08-30 20:33:04] ERROR installerInstall returned error item: Git error: system arch arm64 not in supported_arch=[x64] for item Git
[2025-08-30 20:33:04] ✗ Installation failed: failed: [Git DotNetRuntime] succeeded: 0 total: 2
```

## Data Sources for ReportMate

### 1. Architecture Warning Event Data (`events.json`)

**Location**: `C:\ProgramData\ManagedInstalls\reports\events.json`

**Structure**:
```json
{
  "event_id": "2025-08-30-194622-1756608391099171800",
  "session_id": "2025-08-30-194622",
  "timestamp": "2025-08-30T19:46:31.0991718-07:00",
  "level": "WARN",
  "event_type": "install",
  "action": "architecture_check",
  "status": "warning",
  "message": "Architecture mismatch: system arch arm64 not supported (package supports: [x64])",
  "context": {
    "item": "DotNetRuntime",
    "supported_arch": "[x64]",
    "system_arch": "arm64"
  },
  "source": {
    "file": "logging.go",
    "function": "logging.LogEventEntry",
    "line": 817
  }
}
```

### 2. Installation Error Event Data (`events.json`)

**Structure**:
```json
{
  "event_id": "2025-08-30-194622-1756608391099171801",
  "session_id": "2025-08-30-194622",
  "timestamp": "2025-08-30T19:46:35.1234567-07:00",
  "level": "ERROR",
  "event_type": "install",
  "action": "execute",
  "status": "error",
  "message": "MSI installation failed with exit code 1603: Fatal error during installation",
  "context": {
    "item": "SomePackage",
    "installer_type": "msi",
    "exit_code": 1603,
    "installer_output": "Error 1603. Fatal error during installation."
  },
  "source": {
    "file": "installer.go",
    "function": "InstallMSI",
    "line": 245
  }
}
```

**Key Status Indicators**:
- `status`: Uses a 5-status classification (`installed`, `warning`, `error`, `pending`, `removed`)
- `level`: Console log level (`WARN` vs `ERROR`)
- `event_type`: Type of operation (`install`, `update`, `remove`, etc.)
- `action`: Specific action (`architecture_check`, `execute`, `download`, etc.)

### 3. Item-Level Summary Data (`items.json`)

**Location**: `C:\ProgramData\ManagedInstalls\reports\items.json`

**Structure**:
```json
{
  "id": "dotnetruntime",
  "item_name": "DotNetRuntime",
  "display_name": "DotNetRuntime",
  "item_type": "managed_installs",
  "current_status": "warning",
  "latest_version": "9.0.8.35115",
  "installed_version": "9.0.8.35115",
  "last_attempt_time": "2025-08-30T14:58:48-07:00",
  "last_attempt_status": "warning",
  "warning_count": 3,
  "failure_count": 0,
  "last_warning": "Architecture mismatch: arm64 not in [x64]",
  "recent_attempts": [
    {
      "session_id": "2025-08-30-145842",
      "timestamp": "2025-08-30T14:58:48-07:00",
      "action": "install",
      "status": "warning"
    }
  ]
}
```

**Status Field Mapping**:
- `current_status`: Uses our 5-status classification (`installed`, `warning`, `error`, `pending`, `removed`)
- `last_attempt_status`: Same simplified status classification (`installed`, `warning`, `error`, `pending`, `removed`)
- `failure_count`: Count of actual installation failures (errors only)
- `warning_count`: Count of warnings (repository/config issues)

### 4. Session-Level Data (`sessions.json`)

**Location**: `C:\ProgramData\ManagedInstalls\reports\sessions.json`

**Structure**:
```json
{
  "session_id": "2025-08-30-194622",
  "start_time": "2025-08-30T19:46:22-07:00",
  "run_type": "manual",
  "status": "warning",
  "duration_seconds": 24,
  "failures": 0,
  "warnings": 2,
  "packages_handled": ["PowerShell", "Cimian", "DotNetRuntime"],
  "config": {
    "manifest": "Assigned/Staff/IT/B1115/RodChristiansen",
    "software_repo_url": "https://cimian.ecuad.ca/deployment"
  }
}
```

**Key Fields**:
- `failures`: Count of actual installation failures (errors only)
- `warnings`: Count of warning events (repository/config issues)
- `status`: Session status reflecting error vs warning distinction
- `packages_handled`: List of packages processed

## Error Detection Patterns for ReportMate

### Critical Errors (Require Immediate Attention)

**Installation Failures** (`events.json`):
```python
# Filter criteria for actual software installation failures
event_type == "install" AND
action == "execute" AND
status == "error"
```

**Script Execution Failures** (`events.json`):
```python
# Pre/post-install script failures
event_type == "install" AND
action IN ["preinstall_script", "postinstall_script"] AND
status == "error"
```

### Warnings (Monitor but Non-Critical)

**Architecture Mismatches** (`events.json`):
```python
# Repository configuration issues
event_type == "install" AND
action == "architecture_check" AND
status == "warning"
```

**Download/Network Issues** (`events.json`):
```python
# Network or repository access problems
event_type == "download" AND
status == "warning"
```

**Configuration Problems** (`events.json`):
```python
# Manifest or catalog issues
action IN ["manifest_parse", "catalog_load"] AND
status == "warning"
```

### Error Categories for ReportMate

| Error Type | Event Type | Action | Status | Key Context Fields |
|------------|------------|--------|--------|-------------------|
| **CRITICAL ERRORS** |
| MSI Installation Failure | `install` | `execute` | `error` | `exit_code`, `installer_output` |
| EXE Installation Failure | `install` | `execute` | `error` | `exit_code`, `installer_output` |
| Script Execution Failure | `install` | `preinstall_script`/`postinstall_script` | `error` | `exit_code`, `script_output` |
| NuGet Package Failure | `install` | `execute` | `error` | `package_id`, `error_details` |
| **WARNINGS** |
| Architecture Mismatch | `install` | `architecture_check` | `warning` | `system_arch`, `supported_arch` |
| Dependency Missing | `install` | `dependency_check` | `warning` | `missing_dependencies` |
| Download Failure | `download` | `fetch` | `warning` | `url`, `http_status` |
| Manifest Parse Error | `general` | `manifest_parse` | `warning` | `manifest_path`, `parse_error` |
| Catalog Load Error | `general` | `catalog_load` | `warning` | `catalog_name`, `load_error` |
| Permission Issue | `install` | `execute` | `warning` | `elevation_required` |

## ReportMate Integration Recommendations

### 1. Error Aggregation Strategy

```python
# Pseudo-code for critical error collection
def collect_installation_errors():
    events = load_json("events.json")
    critical_errors = []
    
    for event in events:
        if (event.event_type == "install" and 
            event.status == "error"):
            
            error_info = {
                "package": event.context.item,
                "status": event.status,  # Use only 5-type status classification
                "timestamp": event.timestamp,
                "session_id": event.session_id,
                "message": event.message,
                "installer_details": event.context
            }
            critical_errors.append(error_info)
    
    return critical_errors

# Pseudo-code for warning collection  
def collect_configuration_warnings():
    events = load_json("events.json")
    warnings = []
    
    for event in events:
        if event.status == "warning":
            
            warning_info = {
                "package": event.context.item,
                "status": event.status,
                "timestamp": event.timestamp,
                "session_id": event.session_id,
                "message": event.message,
                "context": event.context
            }
            warnings.append(warning_info)
    
    return warnings
```

### 2. Trending and Analytics

- **Critical Failure Rate by Package**: Track `failure_count` in `items.json` (errors only)
- **Warning Frequency**: Track `warning_count` in `items.json` (warnings only)
- **Error vs Warning Distribution**: Analyze `status` field patterns in `events.json`
- **System Compatibility Issues**: Map architecture mismatches for inventory planning
- **Time-based Analysis**: Use `timestamp` fields for trend analysis and failure clustering

### 3. Alerting Thresholds

- **🚨 CRITICAL (Immediate Action Required)**: 
  - Any `status == "error"`
  - Software that users need is failing to install
  
- **⚠️ WARNING (Monitor and Plan)**:
  - `status == "warning"`: Repository has packages incompatible with system architecture, configuration issues, network problems, missing dependencies

- **📊 INFO (Trending Analysis)**:
  - Warning count > 5 for any package (repository cleanup needed)
  - New error types or unusual patterns
  - System architecture distribution analysis

## File Update Frequency

- **events.json**: Updated after each session completion
- **items.json**: Updated after each session completion
- **sessions.json**: Updated at session start and completion

## Error Context Enhancement

Each error event includes:
- **Source Code Location**: `source.file`, `source.function`, `source.line`
- **Environmental Context**: System architecture, package version, configuration
- **Temporal Context**: Session ID for correlation, precise timestamps
- **Operational Context**: Action being performed when error occurred

## Sample ReportMate Queries

### Critical Installation Failures
```sql
SELECT 
    context.item as package_name,
    status as error_type,
    COUNT(*) as failure_count,
    MAX(timestamp) as last_failure,
    context.exit_code,
    context.installer_output
FROM events 
WHERE status = 'error'
GROUP BY package_name, error_type, exit_code
ORDER BY failure_count DESC, last_failure DESC
```

### Architecture Compatibility Analysis
```sql
SELECT 
    context.item as package_name,
    context.system_arch,
    context.supported_arch,
    COUNT(*) as occurrence_count
FROM events 
WHERE status = 'warning' AND action = 'architecture_check'
GROUP BY package_name, system_arch, supported_arch
ORDER BY occurrence_count DESC
```

### Package Health Summary
```sql
SELECT 
    item_name,
    current_status,
    failure_count as critical_failures,
    warning_count as warnings,
    last_attempt_time,
    last_attempt_status
FROM items 
WHERE failure_count > 0 OR warning_count > 5
ORDER BY failure_count DESC, warning_count DESC, last_attempt_time DESC
```

### Session Success vs Failure Trends
```sql
SELECT 
    DATE(start_time) as date,
    COUNT(*) as total_sessions,
    SUM(CASE WHEN failures > 0 THEN 1 ELSE 0 END) as sessions_with_failures,
    SUM(CASE WHEN warnings > 0 AND failures = 0 THEN 1 ELSE 0 END) as sessions_with_warnings_only,
    SUM(failures) as total_failures,
    SUM(warnings) as total_warnings
FROM sessions 
GROUP BY DATE(start_time)
ORDER BY date DESC
```

This structured approach with improved status classification enables ReportMate to provide comprehensive error monitoring, trending, and alerting for Cimian managed software deployments. The distinction between critical errors (software installation failures) and warnings (repository/configuration issues) ensures accurate prioritization and appropriate response actions.

## Status Classification Benefits

1. **Accurate Alerting**: Critical errors trigger immediate alerts, warnings are monitored for trends
2. **Better Analytics**: Separate tracking of installation failures vs configuration issues
3. **Improved Troubleshooting**: Clear distinction between software problems and repository management
4. **Enhanced Reporting**: Stakeholders get precise information about actual vs potential issues
5. **Operational Efficiency**: IT teams can prioritize real installation failures over configuration warnings
