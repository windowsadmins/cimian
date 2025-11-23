# ReportMate Integration: Full Verbose Run Log

**Date:** November 21, 2025  
**Feature:** Dedicated Verbose Run Log for External Reporting  
**Status:** Implemented in Cimian v2025.11.21+

## Overview

To support deep-dive troubleshooting and detailed analysis within ReportMate, Cimian now generates a dedicated **Full Run Log** for every execution. This log captures **all** log messages (DEBUG, INFO, WARN, ERROR) regardless of the command-line verbosity flags used (e.g., even during silent `--auto` runs).

This resolves the "black box" issue where automatic scheduled runs produced very little logging, making it difficult to diagnose issues without manually running a verbose session.

## Technical Specification

### File Location
The log file is always located at:
```
C:\ProgramData\ManagedInstalls\reports\run.log
```

### Behavior & Lifecycle
1.  **Always Verbose**: The log level for this file is effectively forced to `DEBUG`. It captures every internal decision, check, and operation.
2.  **Overwrite Mode**: The file is **truncated and overwritten** at the start of every Cimian run (`managedsoftwareupdate`). It always contains the log of the *most recent* execution.
3.  **Run Type Agnostic**: Generated for all run types:
    *   `--auto` (Scheduled tasks)
    *   `--checkonly` (Dry runs)
    *   Manual console runs
    *   Bootstrap sessions

### Log Format
The format mirrors the standard console output but includes timestamps for every line.

**Example:**
```text
[2025-11-21 14:05:30] INFO  CIMIAN MANAGED SOFTWARE UPDATE
[2025-11-21 14:05:30] INFO  Version: 2025.11.21.1619
[2025-11-21 14:05:30] DEBUG Loading configuration from C:\ProgramData\ManagedInstalls\Config.yaml
[2025-11-21 14:05:30] DEBUG Checking network connectivity to repo.company.com...
[2025-11-21 14:05:31] INFO  Starting managed software update check...
[2025-11-21 14:05:31] DEBUG Processing manifest: workstation-standard
[2025-11-21 14:05:31] DEBUG Item 'Firefox' (119.0.1) - Installed state: true, Version match: true
[2025-11-21 14:05:31] INFO  No pending changes found.
```

## Integration Guide for ReportMate

### Recommended UI Implementation

We recommend adding a **"View Run Log"** or **"Debug Last Run"** feature to the ReportMate dashboard.

1.  **Dropdown/Modal**: Since the log can be large (hundreds of lines), it is best displayed in a collapsed accordion, a dedicated tab, or a modal window.
2.  **Lazy Loading**: Read the file only when the user requests it to avoid unnecessary I/O on the dashboard load.
3.  **Syntax Highlighting**:
    *   Lines containing `ERROR` or `FAIL` should be highlighted in **Red**.
    *   Lines containing `WARN` should be highlighted in **Yellow/Orange**.
    *   Lines containing `SUCCESS` should be highlighted in **Green**.

### Code Logic (Pseudocode)

```javascript
const fs = require('fs');
const logPath = 'C:\\ProgramData\\ManagedInstalls\\reports\\run.log';

function getLastRunLog() {
    if (fs.existsSync(logPath)) {
        try {
            const logContent = fs.readFileSync(logPath, 'utf8');
            return {
                available: true,
                content: logContent,
                lastModified: fs.statSync(logPath).mtime
            };
        } catch (error) {
            return { available: false, error: "Unable to read log file" };
        }
    }
    return { available: false, message: "No run log available" };
}
```

## Benefits

*   **Instant Troubleshooting**: Admins can see exactly why a package failed or was skipped during the last scheduled run without needing to remote into the machine and run commands manually.
*   **Transparency**: "Silent" failures (e.g., logic skipping an install due to a condition) are now visible in the debug trace.
*   **Zero Config**: No changes needed to `Config.yaml` or scheduled tasks; it works out of the box.
