# Cimian Status Classification Implementation Guide

## Overview

This guide documents the improved status classification system implemented in Cimian to distinguish between critical software installation failures (Errors) and repository/configuration issues (Warnings).

## Status Constants

The following status constants are defined in `shared/core/Models/StatusReasonCode.cs`:

### Error Status Types (Critical)
```csharp
public static class StatusValue
{
    public const string InstallationError = "installation_error";  // MSI, EXE, NuGet package failed to install
    public const string ScriptError       = "script_error";        // Pre/post-install script execution failed
    public const string ExecutionError    = "execution_error";     // Binary execution failure during install
}
```

### Warning Status Types (Non-Critical)
```csharp
public static class StatusValue
{
    public const string ArchitectureWarning  = "architecture_warning";   // Architecture mismatch (repo issue)
    public const string DependencyWarning    = "dependency_warning";     // Missing dependencies detected
    public const string ConfigWarning        = "config_warning";         // Configuration/manifest issues
    public const string DownloadWarning      = "download_warning";       // Network/download problems
    public const string CompatibilityWarning = "compatibility_warning";  // OS version compatibility
}
```

### Success Status Types
```csharp
public static class StatusValue
{
    public const string Success   = "success";    // Operation completed successfully
    public const string Completed = "completed";  // Process finished without errors
    public const string Skipped   = "skipped";    // Operation intentionally skipped
}
```

## Helper Functions

### Status Classification Helper
```csharp
// DetermineInstallStatus returns the appropriate status based on context.
public static string DetermineInstallStatus(Exception? err, IDictionary<string, object?>? context)
{
    if (err is null)
    {
        return StatusValue.Success;
    }

    var errMsg = err.Message.ToLowerInvariant();

    // Check for architecture mismatches (WARNING)
    if (errMsg.Contains("architecture") || errMsg.Contains("supported_arch"))
    {
        return StatusValue.ArchitectureWarning;
    }

    // Check for actual installation failures (ERROR)
    if (errMsg.Contains("exit code") ||
        errMsg.Contains("installation failed") ||
        errMsg.Contains("msi") ||
        errMsg.Contains("exe"))
    {
        return StatusValue.InstallationError;
    }

    // Check for script failures (ERROR)
    if (errMsg.Contains("script"))
    {
        return StatusValue.ScriptError;
    }

    // Check for download issues (WARNING)
    if (errMsg.Contains("download") || errMsg.Contains("network"))
    {
        return StatusValue.DownloadWarning;
    }

    // Check for configuration issues (WARNING)
    if (errMsg.Contains("manifest") || errMsg.Contains("catalog") || errMsg.Contains("config"))
    {
        return StatusValue.ConfigWarning;
    }

    // Default to execution error for unknown failures
    return StatusValue.ExecutionError;
}
```

### Script Status Helper
```csharp
// DetermineScriptStatus returns the appropriate status for script execution.
public static string DetermineScriptStatus(int exitCode, string output)
{
    return exitCode == 0 ? StatusValue.Success : StatusValue.ScriptError;
}
```

### Download Status Helper
```csharp
// DetermineDownloadStatus returns the appropriate status for download operations.
public static string DetermineDownloadStatus(Exception? err, int httpStatus)
{
    if (err is null && httpStatus >= 200 && httpStatus < 300)
    {
        return StatusValue.Success;
    }
    return StatusValue.DownloadWarning;
}
```

## Implementation Examples

Call sites pass the classified status through `SessionLogger.LogEventEntry()` instead of a hardcoded `"failed"` literal. The typical patterns:

- **Architecture check** - use `StatusValue.ArchitectureWarning` when `SupportedArch` does not include the system arch; include the package's supported-arch list in the message context.
- **MSI installation** - exit code `3010` is treated as `StatusValue.Success` (reboot required); any other non-zero exit code maps to `StatusValue.InstallationError`.
- **Script execution** - route through `DetermineScriptStatus(exitCode, output)` so exit 0 is `Success` and anything else is `ScriptError`.
- **Download operations** - route through `DetermineDownloadStatus(err, httpStatus)` so 2xx is `Success` and anything else is `DownloadWarning`.

For concrete call sites, see `cli/managedsoftwareupdate/Services/InstallerService.cs`, `ScriptService.cs`, and `DownloadService.cs`.

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
