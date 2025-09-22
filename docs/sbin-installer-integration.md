# sbin-installer Integration in Cimian

This document describes the integration of the new `sbin-installer` tool as the primary package installation system in Cimian, with enhanced support for modern `.pkg` packages and cryptographic signature verification.

## Overview

Cimian now supports the lightweight `sbin-installer` tool (from https://github.com/windowsadmins/sbin-installer) as the preferred method for installing both modern `.pkg` packages and legacy `.nupkg` packages. This provides a cleaner, more predictable installation experience with enterprise-grade security features.

## Key Benefits

- **Modern Package Support**: Native `.pkg` package format with embedded cryptographic signatures
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

## Modern .pkg Package Format

### Package Structure
Modern `.pkg` packages created by `cimipkg` have this structure:
```
package-name-1.0.0.pkg (ZIP archive)
├── build-info.yaml           # Package metadata with embedded signature
├── payload/                  # Files to be installed
│   └── application files...
└── scripts/                  # Installation scripts (optional)
    ├── preinstall.ps1
    └── postinstall.ps1
```

### Cryptographic Signature Integration

When packages are built with `cimipkg -sign`, comprehensive signature metadata is embedded directly in the `build-info.yaml` file:

```yaml
signature:
  algorithm: "SHA256withRSA"
  certificate:
    subject: "CN=Company Name, O=Organization"
    thumbprint: "A1B2C3D4E5F6789012345678901234567890ABCD"
    not_before: "2024-01-01T00:00:00Z"
    not_after: "2025-12-31T23:59:59Z"
  package_hash: "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
  content_hash: "sha256:a665a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3"
  signed_hash: "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b"
  timestamp: "2024-12-20T10:30:45Z"
  version: "1.0"
```

### Security Benefits

- **Embedded Signatures**: No external signature files needed
- **Package Integrity**: SHA256 hashes verify complete package contents
- **Certificate Validation**: Full certificate chain verification
- **Tamper Detection**: Any modification invalidates the signature
- **Enterprise Trust**: Uses Windows Certificate Store infrastructure

## Configuration Options

Add these settings to your `Config.yaml`:

```yaml
# Package installer preferences
ForceChocolatey: false              # Force Chocolatey for all packages (default: false)
PreferSbinInstaller: true           # Prefer sbin-installer when available (default: true)
SbinInstallerPath: ""               # Custom path to installer.exe (default: auto-detect)
SbinInstallerTargetRoot: "/"        # Target root for installations (default: "/")

# .pkg package signature verification
RequireSignedPackages: true         # Require cryptographic signatures (default: false)
SignatureVerification: "required"   # required, optional, disabled
TrustedCertificates:                # Optional: specific trusted certificates
  - thumbprint: "A1B2C3D4E5F6789012345678901234567890ABCD"
    name: "Company Code Signing Certificate"
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