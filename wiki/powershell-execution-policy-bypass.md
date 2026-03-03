# Cimian PowerShell Execution Policy Bypass

## Overview

Cimian includes **built-in PowerShell execution policy bypass** functionality to ensure reliable script execution in enterprise environments where execution policies may prevent scripts from running. This feature automatically applies `-ExecutionPolicy Bypass` to all PowerShell script executions within Cimian.

## Problem Statement

In many enterprise environments, PowerShell execution policies are configured to restrict script execution for security purposes. Common policies include:

- **Restricted**: No scripts can run (Windows client default)
- **AllSigned**: Only scripts signed by trusted publishers can run
- **RemoteSigned**: Local scripts can run, remote scripts must be signed
- **Unrestricted**: All scripts can run (not recommended for production)

These policies can prevent Cimian from executing necessary installation, configuration, and management scripts, causing deployments to fail.

## Solution

Cimian automatically bypasses execution policy restrictions by:

1. **Adding `-ExecutionPolicy Bypass`** to all PowerShell command invocations
2. **Applying consistently** across all script execution functions
3. **Maintaining security** by only affecting Cimian-specific script execution
4. **Providing configuration control** via `ForceExecutionPolicyBypass` setting

## Affected Script Types

The execution policy bypass is automatically applied to all PowerShell script executions:

### Installation Scripts
- **.ps1 installer packages** - Direct PowerShell script installers
- **chocolateyBeforeInstall.ps1** - Preinstall scripts in .nupkg packages
- **PreScript content** - Embedded preinstall scripts in catalog items

### Management Scripts
- **Check scripts** - Installation verification scripts
- **Uninstall scripts** - PowerShell-based uninstallers
- **nopkg scripts** - Script-only packages without payloads

### System Scripts
- **Preflight scripts** - System preparation scripts
- **Postflight scripts** - System finalization scripts

## Implementation Details

### Command Line Format

All PowerShell executions use this standard format:
```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass [additional_args] -File script.ps1
```

### Function-Level Implementation

The bypass is implemented consistently across all script execution functions:

```go
// Standard PowerShell arguments with execution policy bypass
func buildStandardPowerShellArgs(args ...string) []string {
    baseArgs := []string{"-NoProfile", "-ExecutionPolicy", "Bypass"}
    baseArgs = append(baseArgs, args...)
    return baseArgs
}
```

### Configuration-Aware Implementation

For functions with access to configuration, the bypass can be controlled:

```go
// Configurable PowerShell arguments
func buildPowerShellArgs(cfg *config.Configuration, args ...string) []string {
    baseArgs := []string{"-NoProfile"}
    
    // Add execution policy bypass if configured (default: true)
    if cfg == nil || cfg.ForceExecutionPolicyBypass {
        baseArgs = append(baseArgs, "-ExecutionPolicy", "Bypass")
    }
    
    baseArgs = append(baseArgs, args...)
    return baseArgs
}
```

## Configuration Options

### YAML Configuration

In `C:\ProgramData\ManagedInstalls\Config.yaml`:

```yaml
# PowerShell execution policy settings
# Forces execution policy bypass for all PowerShell script executions
ForceExecutionPolicyBypass: true  # Default: true
```

### CSP OMA-URI Configuration

For enterprise deployment via Intune or Group Policy:

```
Name: Cimian Force Execution Policy Bypass
Description: Force PowerShell execution policy bypass for all scripts
OMA-URI: ./Device/Vendor/MSFT/Policy/Config/Software/Cimian/Config/ForceExecutionPolicyBypass
Data type: Integer
Value: 1 (enabled) or 0 (disabled)
```

### Registry Configuration

Direct registry configuration:

```
Key: HKEY_LOCAL_MACHINE\SOFTWARE\Cimian\Config
Value: ForceExecutionPolicyBypass
Type: REG_DWORD
Data: 1 (enabled) or 0 (disabled)
```

## Security Considerations

### Scope of Bypass

- **Limited to Cimian processes**: Only affects scripts executed by Cimian tools
- **No system-wide changes**: Does not modify global PowerShell execution policy
- **Process-specific**: Bypass applies only to individual PowerShell.exe processes spawned by Cimian
- **Temporary**: No permanent changes to system security settings

### Execution Context

- **Runs with service privileges**: Scripts execute with the same privileges as the calling Cimian process
- **Typically SYSTEM account**: When run via Windows Service or scheduled tasks
- **Respects NTFS permissions**: Script files must still be readable by the executing account
- **Maintains audit trail**: All script executions are logged for security auditing

### Risk Mitigation

- **Source verification**: Scripts come from trusted catalogs and manifests
- **Content scanning**: Scripts should be reviewed before inclusion in catalogs
- **Least privilege**: Cimian processes should run with minimal required privileges
- **Monitoring**: Enable verbose logging to track script execution

## Troubleshooting

### Common Issues

#### Scripts Still Fail to Execute

1. **Check file permissions**: Ensure script files are readable by the executing account
2. **Verify script syntax**: PowerShell syntax errors will still cause failures
3. **Review dependencies**: Scripts may fail due to missing modules or dependencies
4. **Check system resources**: Insufficient memory or disk space can cause failures

#### Execution Policy Still Blocks Scripts

1. **Verify Cimian version**: Ensure you're running a version with bypass support
2. **Check configuration**: Confirm `ForceExecutionPolicyBypass` is set to `true`
3. **Review logs**: Look for execution policy bypass in debug output
4. **Test manually**: Try running PowerShell with `-ExecutionPolicy Bypass` manually

### Debug Logging

Enable verbose logging to see execution policy bypass in action:

```cmd
managedsoftwareupdate.exe -vv
```

Look for log entries showing PowerShell arguments:
```
DEBUG: runPS1Installer => final command exe=powershell.exe args=-NoProfile -ExecutionPolicy Bypass -File script.ps1
```

### Manual Testing

Test script execution manually with the same arguments Cimian uses:

```cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\path\to\script.ps1"
```

## Best Practices

### Script Development

1. **Test with bypass**: Always test scripts with `-ExecutionPolicy Bypass`
2. **Minimize dependencies**: Reduce reliance on external modules or files
3. **Error handling**: Include proper error handling in all scripts
4. **Logging**: Add appropriate logging for troubleshooting

### Enterprise Deployment

1. **Enable by default**: Leave `ForceExecutionPolicyBypass` enabled for reliability
2. **Document exceptions**: If disabling bypass, document the reasoning
3. **Monitor failures**: Watch for script execution failures in environments with strict policies
4. **Test thoroughly**: Test all scripts in target environment execution policy settings

### Security Management

1. **Script review**: Review all scripts before adding to catalogs
2. **Source control**: Use version control for all PowerShell scripts
3. **Digital signing**: Consider signing scripts even with bypass enabled
4. **Regular audits**: Periodically audit script execution logs

## Compatibility

### PowerShell Versions

- **Windows PowerShell 5.1**: Fully supported
- **PowerShell Core 6+**: Supported via `pwsh.exe` detection
- **PowerShell ISE**: Not used by Cimian

### Windows Versions

- **Windows 10**: Fully supported
- **Windows 11**: Fully supported
- **Windows Server 2016+**: Fully supported
- **Windows Server 2012 R2**: Supported with PowerShell 5.1

### Execution Policy Levels

The bypass works with all execution policy levels:
- Restricted
- AllSigned
- RemoteSigned
- Unrestricted

## Example Scenarios

### Scenario 1: Corporate Environment with Restricted Policy

**Environment**: Windows 10 Enterprise with Group Policy setting execution policy to "Restricted"

**Problem**: Cimian scripts fail with execution policy errors

**Solution**: Cimian automatically bypasses the restriction, allowing scripts to run

### Scenario 2: SCCM-Managed Workstations

**Environment**: Workstations managed by SCCM with "AllSigned" execution policy

**Problem**: Unsigned scripts in Cimian packages cannot execute

**Solution**: Built-in bypass allows scripts to run regardless of signing status

### Scenario 3: High-Security Environment

**Environment**: Financial institution with locked-down PowerShell policies

**Problem**: Need to run Cimian while maintaining strict security posture

**Solution**: Configure `ForceExecutionPolicyBypass: false` and use properly signed scripts

## Migration Guide

### From Manual Bypass to Built-in

If you're currently using manual execution policy bypass in your scripts:

1. **Remove manual bypass commands** from script headers:
   ```powershell
   # Remove these lines from your scripts:
   # Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
   ```

2. **Rely on Cimian's built-in bypass** for all script execution

3. **Test thoroughly** to ensure scripts still work correctly

### Upgrading from Older Cimian Versions

1. **Update Cimian** to a version with built-in execution policy bypass
2. **Add configuration** to `Config.yaml` if needed:
   ```yaml
   ForceExecutionPolicyBypass: true  # Explicit configuration
   ```
3. **Test all script-based packages** to ensure continued functionality

## Related Documentation

- [CSP OMA-URI Configuration Guide](csp-oma-uri-configuration.md)
- [Cimian Configuration Options](../README.md#configuration)
- [chocolateyBeforeInstall Support](chocolateyBeforeInstall-support.md)
- [Script-only Packages Guide](../README.md#script-packages)
