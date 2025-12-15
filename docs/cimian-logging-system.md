# Cimian Logging System - Complete Guide

## Overview

Cimian's enhanced logging system provides structured, timestamped logging designed for modern monitoring and analysis tools. Inspired by both MunkiReport's data collection patterns and Munki's logging practices, this system is optimized for osquery integration and ReportMate compatibility.

## Architecture & Directory Structure

```
C:\ProgramData\ManagedInstalls\logs\
├── 20250712-140530/          # Individual session data (YYYY-MM-DD-HHMMss)
│   ├── session.json          # Session metadata
│   ├── events.jsonl          # Streaming event log (JSON Lines)
│   ├── summary.json          # Session summary
│   ├── install.log           # Human-readable format
│   └── debug.log             # Debug information
├── 20250712-120000/          # Previous sessions...
├── 20250711-160000/          # Yesterday's session

C:\ProgramData\ManagedInstalls\reports\   # Pre-computed tables for external tools
├── sessions.json             # Session summary table
├── events.json               # Event detail table  
└── packages.json             # Package statistics table
```

## Key Features

### 🗂️ Timestamped Directory Structure
- **Format**: `YYYY-MM-DD-HHMMss` subdirectories
- **Retention**: Last 10 days (daily) + Last 24 hours (hourly)
- **Organization**: Automatic cleanup of old logs

### 📊 External Tool Integration
- **Compatible Tables**: Pre-configured table schemas for external monitoring tools
- **JSON Format**: All logs stored in queryable JSON format
- **Event Streaming**: Real-time event logging for monitoring
- **Aggregated Views**: Pre-computed data tables for efficient querying

### 🔄 ReportMate Compatibility
- **Data Export**: Native ReportMate format export
- **Session Tracking**: Comprehensive session management
- **Summary Reports**: Aggregated installation statistics

### 🎯 Implementation Status
- **✅ Core Architecture**: Timestamped directory structure, structured logging system
- **✅ External Tool Integration**: Table schemas, data export, query examples
- **✅ ReportMate Compatibility**: Export layer, data transformation, session summaries
- **✅ Background Cleanup**: Automatic maintenance routines

## Data Structures & Samples

### 1. Sessions Data (`sessions.json`)

```json
[
  {
    "session_id": "cimian-1736689852-20250112-143052",
    "start_time": "2025-01-12T14:30:52Z",
    "end_time": "2025-01-12T14:31:16Z",
    "run_type": "manual",
    "status": "completed",
    "duration_seconds": 24,
    "total_actions": 3,
    "installs": 1,
    "updates": 0,
    "removals": 0,
    "successes": 1,
    "failures": 1,
    "hostname": "WORKSTATION-01",
    "user": "SYSTEM",
    "process_id": 4512,
    "log_version": "2.0"
  }
]
```

**Purpose**: High-level session summaries for dashboard overviews and trending analysis.

### 2. Events Data (`events.json`)

```json
[
  {
    "event_id": "evt_001_install_firefox",
    "session_id": "cimian-1736689852-20250112-143052",
    "timestamp": "2025-01-12T14:30:53Z",
    "level": "INFO",
    "event_type": "install",
    "package": "Firefox",
    "version": "119.0.1",
    "action": "install_package",
    "status": "started",
    "message": "Package installation started",
    "duration_ms": 0,
    "progress": 0,
    "source_file": "installer.go",
    "source_function": "InstallPackage",
    "source_line": 156
  }
]
```

**Purpose**: Detailed event tracking for troubleshooting and performance analysis.

### 3. Packages Data (`packages.json`)

```json
[
  {
    "package_name": "Firefox",
    "latest_version": "119.0.1",
    "last_install_time": "2025-01-12T14:30:58Z",
    "last_update_time": "2025-01-08T09:15:22Z",
    "install_count": 3,
    "update_count": 8,
    "removal_count": 0,
    "last_install_status": "success",
    "total_sessions": 15
  }
]
```

**Purpose**: Aggregated package statistics for inventory management and success rate tracking.

## Session Management

### Session Types
- **`auto`**: Scheduled automatic runs
- **`manual`**: User-initiated runs
- **`bootstrap`**: Zero-touch deployment mode
- **`ondemand`**: Triggered by external events

### Session Lifecycle
1. **Start**: Create timestamped directory and session.json
2. **Logging**: Stream events to events.jsonl
3. **Progress**: Update session.json with progress
4. **Completion**: Generate summary.json and cleanup

## API Usage

### Basic Logging
```go
import "github.com/windowsadmins/cimian/pkg/logging"

// Initialize logging system
cfg := &config.Configuration{LogLevel: "INFO"}
logging.Init(cfg)
defer logging.CloseLogger()

// Basic logging with key-value pairs
logging.Info("Package installation started", "package", "Firefox", "version", "119.0.1")
logging.Error("Installation failed", "error_code", 1603, "package", "Chrome")
```

### Structured Logging
```go
// For external tool compatibility with explicit properties
properties := map[string]interface{}{
    "package_name":      "Firefox",
    "installer_type":   "MSI",
}
logging.LogStructured(logging.LevelInfo, "Package installation completed", properties)
```

### Session Management
```go
// Get current session information
logDir := logging.GetCurrentLogDir()
sessionID := logging.GetSessionID()

// Update run type during execution
logging.SetRunType("manual") // manual, scheduled, auto
```

## External Tool Integration

### osquery Integration

#### Table Schemas
```sql
CREATE TABLE cimian_sessions (
    session_id TEXT PRIMARY KEY,
    start_time BIGINT,
    end_time BIGINT,
    run_type TEXT,
    status TEXT,
    duration_seconds INTEGER,
    total_actions INTEGER,
    installs INTEGER,
    updates INTEGER,
    removals INTEGER,
    successes INTEGER,
    failures INTEGER,
    hostname TEXT,
    user TEXT,
    process_id INTEGER,
    log_version TEXT
);

CREATE TABLE cimian_events (
    event_id TEXT PRIMARY KEY,
    session_id TEXT,
    timestamp BIGINT,
    level TEXT,
    event_type TEXT,
    package TEXT,
    version TEXT,
    action TEXT,
    status TEXT,
    message TEXT,
    duration_ms INTEGER,
    progress INTEGER,
    source_file TEXT,
    source_function TEXT,
    source_line INTEGER
);

CREATE TABLE cimian_packages (
    package_name TEXT PRIMARY KEY,
    latest_version TEXT,
    last_install_time BIGINT,
    last_update_time BIGINT,
    install_count INTEGER,
    update_count INTEGER,
    removal_count INTEGER,
    last_install_status TEXT,
    total_sessions INTEGER
);
```

#### Example Queries

**Recent Installation Activity:**
```sql
-- Query sessions
SELECT * FROM file 
WHERE path = 'C:\ProgramData\ManagedInstalls\reports\sessions.json';

-- Query events
SELECT * FROM file 
WHERE path = 'C:\ProgramData\ManagedInstalls\reports\events.json';

-- Query packages
SELECT * FROM file 
WHERE path = 'C:\ProgramData\ManagedInstalls\reports\packages.json';
```

**Installation Success Rate:**
```sql
SELECT 
    run_type,
    COUNT(*) as total_sessions,
    SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) as successful,
    ROUND(
        (SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) * 100.0) / COUNT(*), 2
    ) as success_rate
FROM cimian_sessions
WHERE start_time > strftime('%s', 'now', '-30 days')
GROUP BY run_type;
```

**Package Status Overview:**
```sql
SELECT 
    package_name,
    latest_version,
    datetime(last_install_time, 'unixepoch') as last_installed,
    install_count,
    last_install_status
FROM cimian_packages
ORDER BY last_install_time DESC;
```

### ReportMate Integration

The data is automatically exported in a format compatible with ReportMate's data collection system. The JSON structure maps directly to database tables for efficient storage and querying.

**Export Format:**
```json
{
  "hostname": "DESKTOP-ABC123",
  "timestamp": 1720800930,
  "session_id": "cimian-1720800900-20250712-140530",
  "event_type": "install",
  "package_name": "Firefox",
  "version": "119.0.1",
  "status": "success",
  "duration_ms": 15000
}
```

### Custom Tools Integration

Any tool that can consume JSON data can easily integrate with this structure. The clean separation of concerns makes it easy to:
- Monitor session health (`sessions.json`)
- Analyze specific failures (`events.json`)
- Track package deployment status (`packages.json`)

## Data Retention & Performance

### Retention Policy
- **Sessions**: Last 30 days
- **Events**: Last 7 days (for performance)
- **Packages**: Continuously updated aggregated view
- **Individual Logs**: Per retention policy (10 days daily + 24 hours hourly)

### Performance Impact
- **Memory**: ~10MB for structured logger
- **CPU**: <1% overhead during logging  
- **Disk**: 2-5MB per session
- **I/O**: Batched writes every 5 seconds

### Resource Efficiency
The Windows Service monitor is designed to be lightweight:
- **CPU Usage**: < 0.1% average
- **Memory Usage**: < 5MB 
- **Disk I/O**: Minimal (file existence check every 10 seconds)
- **Network Usage**: None (local monitoring only)

## Benefits Over Legacy System

### Before (Single File)
```
C:\ProgramData\ManagedInstalls\logs\
└── install.log    # Everything dumped here - grows indefinitely
```

### After (Structured & Organized)
```
C:\ProgramData\ManagedInstalls\logs\
├── 20250712-140530/    # Session directory
│   ├── session.json      # Session metadata
│   ├── events.jsonl      # Event stream
│   ├── summary.json      # Session summary  
│   └── install.log       # Human-readable log
├── 20250712-120000/    # Previous session
C:\ProgramData\ManagedInstalls\reports\   # Pre-computed aggregated views
├── sessions.json
├── events.json
└── packages.json
```

### Key Improvements
1. **Performance**: Pre-computed aggregated views eliminate need to parse hundreds of individual session files
2. **Separation**: Reports are separate from operational logs, reducing I/O contention
3. **Simplicity**: Clean filenames without tool-specific prefixes
4. **Compatibility**: Standard JSON format works with any monitoring tool
5. **Readability**: Pretty-formatted JSON for human inspection and debugging

## Migration Strategy

### Backward Compatibility
- **Legacy Path Support**: Old `install.log` path still works
- **API Compatibility**: All existing logging calls unchanged
- **Gradual Migration**: Systems can transition gradually
- **Fallback Mechanisms**: Graceful degradation if new system unavailable

### Automatic Migration Process
1. **Backup Legacy**: Existing `install.log` backed up automatically
2. **Parse History**: Extract historical events where possible  
3. **Convert Format**: Transform to new structured format
4. **Maintain Compatibility**: Run both systems during transition

## Implementation Status

### ✅ Completed Components
```
pkg/logging/
├── logging.go          ✅ Enhanced core logging with structured integration
├── events.go           ✅ Structured logging implementation  
├── osquery.go         ✅ osquery table generation and export
└── reportmate.go      ✅ ReportMate compatibility layer
```

### ⏳ Next Steps
1. **Integration**: Update existing Cimian components to use new logging
2. **Testing**: Comprehensive testing with signed binaries
3. **Deployment**: Roll out to production environments
4. **Monitoring Setup**: Connect to monitoring dashboards
5. **Documentation**: Update API documentation and examples

## Troubleshooting

### Health Monitoring
```sql
-- Cimian Logging Health Check
SELECT 'Cimian Logging Health' as check_name,
       CASE WHEN COUNT(*) > 0 THEN 'OK' ELSE 'CRITICAL' END as status
FROM cimian_sessions 
WHERE start_time > strftime('%s', 'now', '-1 hour');
```

### Common Issues
1. **Missing Log Directories**: Automatically created on first use
2. **Permission Issues**: Service runs as SYSTEM with appropriate access
3. **Disk Space**: Automatic cleanup prevents excessive storage usage
4. **Performance**: Batched logging minimizes I/O impact

This unified logging system provides comprehensive monitoring capabilities while maintaining backward compatibility and supporting modern external tool integration.
