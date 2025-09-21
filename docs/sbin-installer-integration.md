# sbin-installer Integration in Cimian

This document describes the integration of the new `sbin-installer` tool as the primary package installation system in Cimian, replacing Chocolatey as the default installer while maintaining backward compatibility.

## Overview

Cimian now supports the lightweight `sbin-installer` tool (from https://github.com/windowsadmins/sbin-installer) as the preferred method for installing both `.nupkg` and `.pkg` packages. This provides a cleaner, more predictable installation experience without the complexity and overhead of Chocolatey.

## Key Benefits

- **No Cache Management**: Runs directly from package location without maintaining state
- **Deterministic**: Predictable, simple operation inspired by macOS `/usr/sbin/installer`
- **Lightweight**: Single executable with no external dependencies
- **Direct Package Handling**: No need for "source" concepts - operates directly on package files
- **Native .pkg Support**: First-class support for custom `.pkg` packages created by `cimipkg`

## Installation Hierarchy

The system follows this installation preference order:

### For `.pkg` packages:
1. **sbin-installer only** - `.pkg` files are exclusive to sbin-installer
2. **Error if unavailable** - No fallback for `.pkg` files

### For `.nupkg` packages:
1. **sbin-installer (primary)** - If available and preferred
2. **Chocolatey (fallback)** - If sbin-installer fails or is unavailable
3. **Maintain compatibility** - Existing Chocolatey packages continue to work

## Configuration Options

Add these settings to your `Config.yaml`:

```yaml
# Package installer preferences
ForceChocolatey: false              # Force Chocolatey for all packages (default: false)
PreferSbinInstaller: true           # Prefer sbin-installer when available (default: true)
SbinInstallerPath: ""               # Custom path to installer.exe (default: auto-detect)
SbinInstallerTargetRoot: "/"        # Target root for installations (default: "/")
```

### Configuration Details

- **ForceChocolatey**: When `true`, forces all package installations through Chocolatey
- **PreferSbinInstaller**: When `false`, disables sbin-installer preference 
- **SbinInstallerPath**: Override auto-detection of installer.exe location
- **SbinInstallerTargetRoot**: Controls the `--target` parameter for sbin-installer

## Auto-Detection

The system automatically detects sbin-installer in these locations:
1. Configured path (`SbinInstallerPath`)
2. `C:\Program Files\sbin\installer.exe`
3. `C:\Program Files (x86)\sbin\installer.exe` (on x64 systems)
4. `installer.exe` from system PATH

## Package Types Supported

### .pkg Packages (sbin-installer exclusive)
- Created using `cimipkg` tool
- ZIP archive with structured content:
  - `payload/` - Files to install
  - `scripts/preinstall.ps1` - Pre-installation script
  - `scripts/postinstall.ps1` - Post-installation script
  - `build-info.yaml` - Package metadata

### .nupkg Packages (both installers)
- Standard NuGet package format
- sbin-installer primary, Chocolatey fallback
- Maintains compatibility with existing Chocolatey scripts
- Extracts and runs `chocolateyBeforeInstall.ps1` when using sbin-installer

## Compatibility

### Existing Chocolatey Packages
- Continue to work unchanged
- `chocolateyBeforeInstall.ps1` scripts are extracted and executed manually by Cimian
- `chocolateyInstall.ps1` scripts are handled automatically by sbin-installer's package processing
- Fallback ensures no disruption

### Script Execution
- Pre/post-install scripts from catalog manifests still execute
- Package-internal `chocolateyBeforeInstall.ps1` scripts are extracted and executed
- Package-internal `chocolateyInstall.ps1` scripts are processed by sbin-installer automatically
- PowerShell execution policy bypass maintained

## Logging and Monitoring

### Enhanced Logging
- Installation attempts are logged with installer type
- Fallback scenarios are tracked and reported
- Failed sbin-installer attempts trigger warning events
- ReportMate integration for monitoring and alerting

### Event Types
- `sbin_installer_pkg` - .pkg installation via sbin-installer
- `sbin_installer_nupkg` - .nupkg installation via sbin-installer
- `sbin_installer_fallback` - Fallback from sbin-installer to Chocolatey
- `chocolatey_install` / `chocolatey_upgrade` - Chocolatey installations

## Enterprise Deployment

### MSI Installation
Install sbin-installer via MSI for enterprise deployment:
```powershell
msiexec /i sbin-installer.msi /quiet
```

### Path Management
- Installs to `C:\Program Files\sbin\` by default
- Automatically adds to system PATH
- Uses `%ProgramW6432%` for proper architecture detection

### Zero-Touch Configuration
No additional configuration required - system auto-detects and prefers sbin-installer when available.

## Migration Strategy

### Phase 1: Transparent Integration (Current)
- sbin-installer preferred when available
- Automatic fallback to Chocolatey
- No configuration changes required

### Phase 2: Gradual Adoption
- Deploy sbin-installer via MSI
- Monitor installation success rates
- Package authors can start creating .pkg packages

### Phase 3: Full Migration (Future)
- Consider deprecating Chocolatey dependency
- Move entirely to .pkg format for custom packages
- Maintain .nupkg support for third-party packages

## Troubleshooting

### sbin-installer Not Found
- Check installation in `C:\Program Files\sbin\`
- Verify PATH includes sbin directory
- Use `SbinInstallerPath` config to override

### Installation Failures
- Check event logs for `sbin_installer_*` events
- Review Chocolatey fallback success
- Verify package format compatibility

### Configuration Issues
- Validate YAML syntax in Config.yaml
- Check boolean values (true/false, not True/False)
- Verify file paths use forward slashes or escaped backslashes

## Technical Implementation

### Code Location
- Configuration: `pkg/config/config.go`
- Installation Logic: `pkg/installer/installer.go`
- Functions:
  - `detectSbinInstaller()` - Auto-detection logic
  - `isSbinInstallerAvailable()` - Availability check
  - `installOrUpgradePackage()` - Unified installation handler

### Integration Points
- Main Install function routes packages through unified handler
- Package type detection by file extension and manifest type
- Timeout handling via `InstallerTimeoutMinutes` configuration
- Registry integration for version tracking

This integration provides a smooth transition path while improving installation reliability and reducing system complexity.