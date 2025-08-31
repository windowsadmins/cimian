# Cimian Status Classification Implementation Guide

## Overview

This guide documents the improved status classification system implemented in Cimian to distinguish between critical software installation failures (Errors) and repository/configuration issues (Warnings).

## Status Constants

The following status constants are defined in `pkg/logging/logging.go`:

### Error Status Types (Critical)
```go
const (
    StatusInstallationError = "installation_error"  // MSI, EXE, NuGet package failed to install
    StatusScriptError       = "script_error"        // Pre/post-install script execution failed
    StatusExecutionError    = "execution_error"     // Binary execution failure during install
)
```

### Warning Status Types (Non-Critical)
```go
const (
    StatusArchitectureWarning = "architecture_warning"  // Architecture mismatch (repo issue)
    StatusDependencyWarning   = "dependency_warning"    // Missing dependencies detected
    StatusConfigWarning       = "config_warning"        // Configuration/manifest issues
    StatusDownloadWarning     = "download_warning"      // Network/download problems
    StatusCompatibilityWarning = "compatibility_warning" // OS version compatibility
)
```

### Success Status Types
```go
const (
    StatusSuccess    = "success"    // Operation completed successfully
    StatusCompleted  = "completed"  // Process finished without errors
    StatusSkipped    = "skipped"    // Operation intentionally skipped
)
```

## Helper Functions

### Status Classification Helper
```go
// DetermineInstallStatus returns appropriate status based on context
func DetermineInstallStatus(err error, context map[string]interface{}) string {
    if err == nil {
        return StatusSuccess
    }
    
    errMsg := strings.ToLower(err.Error())
    
    // Check for architecture mismatches (WARNING)
    if strings.Contains(errMsg, "architecture") || 
       strings.Contains(errMsg, "supported_arch") {
        return StatusArchitectureWarning
    }
    
    // Check for actual installation failures (ERROR)
    if strings.Contains(errMsg, "exit code") || 
       strings.Contains(errMsg, "installation failed") ||
       strings.Contains(errMsg, "msi") ||
       strings.Contains(errMsg, "exe") {
        return StatusInstallationError
    }
    
    // Check for script failures (ERROR)
    if strings.Contains(errMsg, "script") {
        return StatusScriptError
    }
    
    // Check for download issues (WARNING)
    if strings.Contains(errMsg, "download") || 
       strings.Contains(errMsg, "network") {
        return StatusDownloadWarning
    }
    
    // Check for configuration issues (WARNING)
    if strings.Contains(errMsg, "manifest") || 
       strings.Contains(errMsg, "catalog") ||
       strings.Contains(errMsg, "config") {
        return StatusConfigWarning
    }
    
    // Default to execution error for unknown failures
    return StatusExecutionError
}
```

### Script Status Helper
```go
// DetermineScriptStatus returns appropriate status for script execution
func DetermineScriptStatus(exitCode int, output string) string {
    if exitCode == 0 {
        return StatusSuccess
    }
    return StatusScriptError
}
```

### Download Status Helper
```go
// DetermineDownloadStatus returns appropriate status for download operations
func DetermineDownloadStatus(err error, httpStatus int) string {
    if err == nil && httpStatus >= 200 && httpStatus < 300 {
        return StatusSuccess
    }
    return StatusDownloadWarning
}
```

## Implementation Examples

### Architecture Check
```go
// Before (generic)
logging.LogEventEntry("install", "architecture_check", "failed", 
    fmt.Sprintf("Architecture mismatch: system arch %s not supported", sysArch))

// After (specific)
logging.LogEventEntry("install", "architecture_check", StatusArchitectureWarning,
    fmt.Sprintf("Architecture mismatch: system arch %s not supported (package supports: %v)", 
        sysArch, item.SupportedArch))
```

### MSI Installation
```go
// Before (generic)
if exitCode != 0 {
    logging.LogEventEntry("install", "execute", "failed",
        fmt.Sprintf("MSI installation failed with exit code %d", exitCode))
}

// After (specific)
if exitCode != 0 {
    if exitCode == 3010 {
        // Reboot required - this is actually success
        logging.LogEventEntry("install", "execute", StatusSuccess,
            fmt.Sprintf("MSI installation succeeded with reboot required (exit code %d)", exitCode))
    } else {
        // Actual failure
        logging.LogEventEntry("install", "execute", StatusInstallationError,
            fmt.Sprintf("MSI installation failed with exit code %d: %s", exitCode, output))
    }
}
```

### Script Execution
```go
// Before (generic)
if err := runScript(scriptPath); err != nil {
    logging.LogEventEntry("install", "preinstall_script", "failed",
        fmt.Sprintf("Script execution failed: %v", err))
}

// After (specific)
if exitCode, output, err := runScriptWithOutput(scriptPath); err != nil {
    status := DetermineScriptStatus(exitCode, output)
    logging.LogEventEntry("install", "preinstall_script", status,
        fmt.Sprintf("Script execution failed with exit code %d: %s", exitCode, output))
}
```

### Download Operations
```go
// Before (generic)
if err := downloadFile(url, dest); err != nil {
    logging.LogEventEntry("download", "fetch", "failed",
        fmt.Sprintf("Download failed: %v", err))
}

// After (specific)
if err := downloadFile(url, dest); err != nil {
    status := DetermineDownloadStatus(err, httpStatus)
    logging.LogEventEntry("download", "fetch", status,
        fmt.Sprintf("Download failed from %s: %v", url, err))
}
```

## Usage Guidelines

### When to Use ERROR Status
- **Software installation actually failed**: MSI returned non-zero exit code (except 3010)
- **Binary execution failed**: EXE installer crashed or returned error
- **Script execution failed**: Pre/post-install scripts failed to run
- **Package deployment failed**: NuGet or other package failed to deploy

### When to Use WARNING Status
- **Architecture mismatches**: Package supports x64 but system is ARM64
- **Missing dependencies**: Dependencies not found but installation may proceed
- **Download issues**: Network problems, temporary server issues
- **Configuration problems**: Manifest parsing errors, catalog load issues
- **Compatibility concerns**: OS version mismatches

### Implementation Checklist

1. **Replace hardcoded "failed" status** with specific status constants
2. **Add context information** to help categorize the issue
3. **Use helper functions** for consistent status determination
4. **Include relevant details** in the context field
5. **Test both error and warning paths** to ensure proper classification

## Testing the Implementation

### Test Architecture Warning
```powershell
# On ARM64 system, try to install x64-only package
sudo .\release\arm64\managedsoftwareupdate.exe -v --checkonly
# Should show WARN instead of ERROR for architecture mismatches
```

### Test Installation Error
```powershell
# Force an installation failure and check logs
# Should show ERROR for actual installation failures
```

### Verify Log Output
Check that console output shows appropriate levels:
- `[2025-08-30 20:20:43] WARN  Architecture mismatch, skipping item: ...`
- `[2025-08-30 20:20:43] ERROR MSI installation failed: ...`

### Verify JSON Reports
Check `/reports/events.json` for proper status classification:
```json
{
  "status": "architecture_warning",  // Not "failed"
  "level": "WARN",
  "message": "Architecture mismatch: ..."
}
```

## Benefits of This Implementation

1. **Precise Alerting**: ReportMate can distinguish between critical and non-critical issues
2. **Better Analytics**: Separate tracking of installation failures vs repository issues
3. **Improved Troubleshooting**: Clear categorization helps identify root causes
4. **Enhanced Reporting**: Stakeholders get accurate information about issue severity
5. **Operational Efficiency**: IT teams can prioritize actual failures over configuration warnings

## Migration Notes

- **Backward Compatibility**: Old "failed" status still works but should be updated
- **Gradual Migration**: Update high-impact areas first (installation execution)
- **Testing Required**: Verify both error and warning scenarios work correctly
- **Documentation**: Update any ReportMate or monitoring systems to use new statuses

This implementation provides a foundation for accurate, actionable error reporting that distinguishes between software installation failures and repository management issues.
