# chocolateyBeforeInstall.ps1 Support in Cimian

## Overview

Cimian's `managedsoftwareupdate.exe` now includes automatic detection and execution of `chocolateyBeforeInstall.ps1` scripts embedded within `.nupkg` packages. This feature ensures consistent preinstall script execution regardless of Chocolatey's internal decision tree logic.

## Background

Chocolatey has complex conditional logic for when to execute `chocolateyBeforeInstall.ps1` scripts. In some scenarios, these scripts may not run when expected, which can cause installation issues for packages that depend on preinstall setup.

## Solution

Cimian now inspects every `.nupkg` file before installation and:

1. **Extracts** any `tools/chocolateyBeforeInstall.ps1` script found in the package
2. **Executes** the script with elevated PowerShell before proceeding with installation
3. **Continues** with normal Chocolatey installation even if the preinstall script fails

## Implementation Details

### Code Location
- Primary implementation: `pkg/installer/installer.go`
- Function: `extractAndRunChocolateyBeforeInstall()`

### Execution Flow
1. **Pre-Install Check**: Before calling `choco install` or `choco upgrade`, Cimian inspects the `.nupkg` file
2. **Script Extraction**: If `tools/chocolateyBeforeInstall.ps1` exists, it's extracted to a temporary file
3. **Script Execution**: The script runs with PowerShell using `-ExecutionPolicy Bypass`
4. **Cleanup**: The temporary script file is automatically removed
5. **Normal Installation**: Chocolatey installation proceeds normally

### Error Handling
- **Non-Fatal Failures**: Script execution failures don't prevent package installation
- **Logging**: All operations are logged with appropriate levels (DEBUG, INFO, ERROR)
- **Graceful Degradation**: Missing or empty scripts are handled silently

### Security Considerations
- Scripts run with the same privileges as `managedsoftwareupdate.exe`
- Temporary script files are created in the system temp directory
- Files are cleaned up immediately after execution
- PowerShell execution policy is bypassed only for the specific script

## Usage

No configuration changes are required. The feature is automatically enabled for all `.nupkg` installations when using `managedsoftwareupdate.exe`.

### Example Package Structure
```
example-package.1.0.0.nupkg
├── tools/
│   ├── chocolateyInstall.ps1
│   └── chocolateyBeforeInstall.ps1  ← This script will be executed first
├── example-package.nuspec
└── [Content_Types].xml
```

### Log Output Example
```
INFO: Found chocolateyBeforeInstall.ps1 in .nupkg, extracting and running, item=MyPackage
INFO: Executing chocolateyBeforeInstall.ps1 script, item=MyPackage, script=C:\Users\...\Temp\chocolateyBeforeInstall_MyPackage.ps1
INFO: chocolateyBeforeInstall.ps1 script completed successfully, item=MyPackage, output=Script execution completed
```

## Testing

Test cases are included in `pkg/installer/chocolatey_before_install_test.go` covering:
- Packages with `chocolateyBeforeInstall.ps1`
- Packages without the script
- Packages with empty scripts
- Error handling scenarios

## Compatibility

This feature is compatible with:
- All existing `.nupkg` packages
- Standard Chocolatey package structures
- Cimian's existing installation workflow

The feature adds functionality without breaking existing behavior.
