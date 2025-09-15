# Progressive Reporting Implementation for ReportMate Integration

## Overview

This implementation addresses the problem of incomplete Cimian runs not providing visibility into their progress, making it impossible for ReportMate to accurately track system state when runs hang or fail partway through.

## Problem Solved

### Before
- `/reports` folder only created at the END of runs
- `items.json`, `sessions.json`, `events.json` only generated after complete success
- Hanging runs provided zero visibility to ReportMate
- No intermediate progress tracking during installations
- Session finish timestamps missing for incomplete runs

### After
- `/reports` folder created immediately when session starts
- Progressive report generation at multiple phases
- Per-item updates during installation process
- Complete visibility into incomplete runs

## Implementation Details

### 1. Early Report Directory Creation
**Location**: `cmd/managedsoftwareupdate/main.go` - Right after session start
```go
// PROGRESSIVE REPORTING: Initialize reports directory immediately after session start
baseDir := filepath.Join(os.Getenv("ProgramData"), "ManagedInstalls", "logs")
exporter := reporting.NewDataExporter(baseDir)
if err := exporter.EnsureReportsDirectoryExists(); err != nil {
    logging.Warn("Failed to initialize reports directory: %v", err)
}
```

### 2. Progressive Report Generation Phases

#### Phase 1: Post-Checkonly Reports
**When**: Immediately after `--checkonly` phase completes
**Purpose**: Captures complete inventory state with pending actions
```go
if err := exporter.ExportProgressiveReports(3, "post-checkonly"); err != nil {
    logging.Warn("Failed to export post-checkonly reports: %v", err)
}
```

#### Phase 2: Pre-Execution Reports  
**When**: Before any installations begin
**Purpose**: Captures planned actions before execution starts
```go
if err := exporter.ExportProgressiveReports(3, "pre-execution"); err != nil {
    logging.Warn("Failed to export pre-execution reports: %v", err)
}
```

#### Phase 3: Execution-Start Reports
**When**: At the beginning of installation phase
**Purpose**: Marks transition from planning to execution
```go
if err := exporter.ExportProgressiveReports(3, "execution-start"); err != nil {
    logging.Warn("Failed to export execution-start reports: %v", err)
}
```

#### Phase 4: Per-Item Progress Updates
**When**: After each individual package installation
**Purpose**: Real-time tracking of installation progress
**Location**: `pkg/process/process.go` - In `InstallsWithAdvancedLogic`
```go
if exportErr := exporter.ExportItemProgressUpdate(3, itemName, itemStatus); exportErr != nil {
    logging.Debug("Failed to export progressive item update", "item", itemName, "error", exportErr)
}
```

#### Phase 5: Session-Complete Reports
**When**: After session finalization (includes final traditional reports)
**Purpose**: Final comprehensive state with proper finish timestamps
```go
if err := exporter.ExportProgressiveReports(3, "session-complete"); err != nil {
    logger.Warning("Failed to export session-complete reports: %v", err)
}
```

### 3. New Reporting Functions

#### `EnsureReportsDirectoryExists()`
- Creates `/reports` folder early in process
- Safe to call multiple times
- Ensures progressive reports can be written

#### `ExportProgressiveReports(limitDays, phase)`
- Generates current state of all three JSON files
- Non-blocking - doesn't fail main process if reporting fails

#### `ExportItemProgressUpdate(limitDays, completedItem, status)`
- Updates `items.json` after each package installation
- Provides real-time visibility into progress
- Tracks individual item completion status

### 3. Clean Progressive Reporting

Progressive reporting only updates the 3 main JSON files that ReportMate reads:
- `sessions.json` - Updated throughout the process with current session state
- `events.json` - Updated with events from recent sessions  
- `items.json` - Updated with current package status, including per-item progress

**No additional files or folders are created** - this keeps the reporting system simple and focused on what ReportMate actually needs.

## ReportMate Integration Benefits

### 1. Incomplete Run Detection
ReportMate can now detect incomplete runs by:
- Checking for sessions without `end_time` 
- Identifying sessions stuck in specific phases
- Tracking items that started but never completed

### 2. Real-Time Progress Monitoring
- `items.json` updates after each package installation
- Live visibility into which packages are hanging
- Progress tracking: "Installing package 15 of 23"

### 3. Hang Detection
ReportMate can identify:
- Sessions that started but never progressed past checkonly
- Installations that began but stopped at specific packages
- Services hanging during MSI installations

### 4. Enhanced Visibility
- Complete package inventory even from incomplete runs
- Phase-by-phase progression tracking
- Detailed error context for failed items

## File Locations

### Primary Reports (ReportMate reads these)
- `C:\ProgramData\ManagedInstalls\reports\sessions.json`
- `C:\ProgramData\ManagedInstalls\reports\events.json`
- `C:\ProgramData\ManagedInstalls\reports\items.json`

These 3 files are updated progressively throughout Cimian runs and contain all the data ReportMate needs.

## Session Finish Timestamp Tracking

### Incomplete Sessions
Sessions without `end_time` indicate incomplete runs:
```json
{
  "session_id": "cimian-1726392582-20250915-143022",
  "start_time": "2025-09-15T14:30:22Z",
  "end_time": null,  // <- Missing end_time indicates incomplete run
  "status": "in_progress"
}
```

### Completed Sessions
Properly finished sessions have both timestamps:
```json
{
  "session_id": "cimian-1726392582-20250915-143022", 
  "start_time": "2025-09-15T14:30:22Z",
  "end_time": "2025-09-15T14:45:18Z",  // <- Present indicates completion
  "duration_seconds": 896,
  "status": "completed"
}
```

## Performance Considerations

### Non-Blocking Design
- Progressive reporting failures don't stop installations
- Warnings logged but process continues
- ReportMate gets best-effort visibility

### Optimized Frequency
- Per-item updates only during actual installations (not checkonly)
- 3-day limit on historical data for performance
- Phase backups for debugging without affecting main reports

### Resource Usage
- Minimal overhead per package installation
- JSON file generation optimized for frequent updates
- No impact on installation success/failure logic

## Usage Examples

### ReportMate Query: Find Incomplete Runs
```javascript
const sessions = JSON.parse(sessionsJsonData);
const incompleteRuns = sessions.filter(session => 
    !session.end_time && 
    new Date(session.start_time) > new Date(Date.now() - 24*60*60*1000) // Last 24 hours
);
```

### ReportMate Query: Track Installation Progress
```javascript
const items = JSON.parse(itemsJsonData);
const currentlyInstalling = items.filter(item => 
    item.current_status === "installing" || 
    item.last_attempt_status === "in_progress"
);
```

### ReportMate Query: Identify Hanging Packages
```javascript
const events = eventsJsonlData.split('\n')
    .filter(line => line.trim())
    .map(line => JSON.parse(line));
    
const hangingPackages = events
    .filter(event => event.event_type === "install" && event.status === "started")
    .filter(event => !events.some(e => 
        e.package_name === event.package_name && 
        e.event_type === "install" && 
        (e.status === "completed" || e.status === "failed")
    ));
```

## Testing Recommendations

### 1. Test Progressive Reporting
```powershell
# Start a Cimian run and interrupt it partway through
sudo .\release\arm64\managedsoftwareupdate.exe -v
# Check that reports directory exists immediately
Test-Path "C:\ProgramData\ManagedInstalls\reports"
```

### 2. Verify Per-Item Updates
```powershell
# Monitor items.json during installation
while ($true) {
    Get-Content "C:\ProgramData\ManagedInstalls\reports\items.json" | ConvertFrom-Json | 
        Select-Object package_name, current_status, last_attempt_time
    Start-Sleep 5
}
```

### 3. Check Phase Backups
```powershell
# Verify phase-specific debug files are created
Get-ChildItem "C:\ProgramData\ManagedInstalls\reports\phases" | Sort-Object LastWriteTime
```

This implementation ensures ReportMate has complete visibility into Cimian operations, even when runs don't complete successfully, providing the accurate reporting data needed for enterprise-scale deployment management.
