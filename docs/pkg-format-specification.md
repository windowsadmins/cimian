# .pkg Package Format Specification

## Overview

The `.pkg` format is a modern ZIP-based package format designed for direct Windows software deployment through **sbin-installer**. Unlike traditional NuGet packages, .pkg packages embed cryptographic signatures directly in the package metadata, eliminating the need for external signature files.

## Package Structure

### Basic .pkg Archive Layout

```
package-name-1.0.0.pkg (ZIP archive)
├── build-info.yaml           # Package metadata and embedded signature
├── payload/                  # Files to be installed
│   ├── application.exe
│   ├── config.ini
│   └── data/
│       └── default.db
└── scripts/                  # Installation scripts (optional)
    ├── preinstall.ps1
    └── postinstall.ps1
```

### File Format Details

#### Archive Format
- **Container**: Standard ZIP archive (RFC 4354)
- **Compression**: DEFLATE algorithm (configurable)
- **Extension**: `.pkg`
- **MIME Type**: `application/x-pkg-package`

#### Encoding Requirements
- **Text Files**: UTF-8 without BOM
- **File Names**: UTF-8 with forward slashes for cross-platform compatibility
- **Metadata**: YAML 1.2 specification compliance

## build-info.yaml Specification

The `build-info.yaml` file contains all package metadata, including embedded cryptographic signatures.

### Core Metadata Structure

```yaml
product:
  name: "ApplicationName"           # Human-readable package name
  version: "1.2.3"                 # Semantic version
  identifier: "com.company.app"    # Reverse domain identifier
  developer: "Company Name"        # Developer/publisher name
  description: "Package description" # Optional description
  category: "Productivity"         # Optional category
  architecture: "x64"              # Target architecture (x64, arm64, universal)
  
install_location: "C:\\Program Files\\App" # Target installation path (optional)
postinstall_action: "none"         # none, logout, restart
signing_certificate: "Company EV Certificate" # Certificate friendly name (optional)

# For installer-type packages (when install_location is empty)
installer:
  type: "msi"                      # msi, exe, ps1, msix
  silent_args: "/qn"               # Silent installation arguments
  exit_codes: [0, 3010]            # Acceptable exit codes
```

### Signature Metadata Structure

When packages are signed with `cimipkg -sign`, the following signature section is added:

```yaml
signature:
  algorithm: "SHA256withRSA"       # Cryptographic algorithm
  certificate:
    subject: "CN=Company Name, O=Organization, C=US"
    issuer: "CN=Certificate Authority"
    thumbprint: "A1B2C3D4E5F6789012345678901234567890ABCD"
    serial_number: "1234567890ABCDEF"
    not_before: "2024-01-01T00:00:00Z"
    not_after: "2025-12-31T23:59:59Z"
  package_hash: "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
  content_hash: "sha256:a665a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3"
  signed_hash: "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b"
  timestamp: "2024-12-20T10:30:45Z"
  version: "1.0"                   # Signature format version
```

### Field Specifications

#### Required Fields

| Field | Type | Description | Example |
|-------|------|-------------|---------|
| `product.name` | string | Package display name | "Adobe Acrobat Reader" |
| `product.version` | string | Semantic version | "1.2.3" |
| `product.identifier` | string | Unique package ID | "com.adobe.reader" |
| `product.developer` | string | Publisher name | "Adobe Inc." |

#### Optional Fields

| Field | Type | Description | Default |
|-------|------|-------------|---------|
| `install_location` | string | Target install path | empty (installer mode) |
| `postinstall_action` | enum | Post-install action | "none" |
| `product.description` | string | Package description | empty |
| `product.category` | string | Software category | "Utilities" |
| `product.architecture` | enum | Target architecture | "x64" |

#### Signature Fields (Auto-Generated)

| Field | Type | Description |
|-------|------|-------------|
| `signature.algorithm` | string | Always "SHA256withRSA" |
| `signature.package_hash` | string | SHA256 of entire package contents |
| `signature.content_hash` | string | SHA256 of payload + scripts |
| `signature.signed_hash` | string | RSA signature of package_hash |
| `signature.timestamp` | string | RFC3339 signing timestamp |

## Installation Scripts

### Script Execution Order

1. **Pre-Installation**: `preinstall.ps1` (if exists)
2. **Installation**: File copy OR installer execution
3. **Post-Installation**: `postinstall.ps1` (if exists)

### Script Requirements

- **Language**: PowerShell (.ps1)
- **Execution Policy**: Bypass (handled by installer)
- **Privileges**: Run with administrator privileges
- **Signing**: Automatically signed if package signing is enabled
- **Error Handling**: Non-zero exit codes fail the installation

### Script Environment

Scripts execute with the following environment:

```powershell
# Available variables
$PSScriptRoot     # Path to scripts directory
$PackageRoot      # Path to extracted package
$PayloadPath      # Path to payload directory  
$InstallLocation  # Target installation path (if specified)
$PackageName      # Package identifier
$PackageVersion   # Package version
```

### Script Examples

#### preinstall.ps1
```powershell
# Stop services before installation
Stop-Service -Name "MyAppService" -Force -ErrorAction SilentlyContinue

# Remove old versions
$oldInstall = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" | 
              Where-Object { $_.DisplayName -like "MyApp*" }
if ($oldInstall) {
    Start-Process -FilePath "msiexec.exe" -ArgumentList "/x", $oldInstall.PSChildName, "/qn" -Wait
}

# Create required directories
New-Item -Path "C:\ProgramData\MyApp" -ItemType Directory -Force
```

#### postinstall.ps1
```powershell
# Configure application
Copy-Item -Path "$PSScriptRoot\..\payload\config.xml" -Destination "C:\ProgramData\MyApp\" -Force

# Create desktop shortcuts
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut("$([Environment]::GetFolderPath('CommonDesktop'))\MyApp.lnk")
$shortcut.TargetPath = "$InstallLocation\MyApp.exe"
$shortcut.Save()

# Start services
Start-Service -Name "MyAppService"

# Register with Windows
New-ItemProperty -Path "HKLM:\SOFTWARE\MyApp" -Name "InstallPath" -Value $InstallLocation -Force
```

## Package Types

### 1. Copy-Type Packages

Used for file deployment (fonts, config files, data):

```yaml
product:
  name: "Corporate Fonts"
  version: "2024.12.20"
  identifier: "corp.fonts.standard"
  developer: "IT Department"

install_location: "C:\\Windows\\Fonts"
postinstall_action: "none"
```

**Behavior**: Files in `payload/` are copied to `install_location`

### 2. Installer-Type Packages

Used for wrapping existing installers:

```yaml
product:
  name: "Adobe Reader"
  version: "24.0.1"
  identifier: "com.adobe.reader"
  developer: "Adobe Inc."

install_location: ""              # Empty = installer mode
installer:
  type: "msi"
  silent_args: "/qn ALLUSERS=1"
  exit_codes: [0, 3010]
postinstall_action: "none"
```

**Behavior**: Installer in `payload/` is executed directly

### 3. Script-Type Packages

Used for configuration or maintenance tasks:

```yaml
product:
  name: "System Configuration"
  version: "1.0.0"
  identifier: "corp.sysconfig"
  developer: "IT Department"

install_location: ""              # No files to copy
postinstall_action: "restart"    # Restart after scripts
```

**Behavior**: Only scripts are executed, no file operations

## Cryptographic Signature Implementation

### Hash Algorithm

- **Algorithm**: SHA-256
- **Implementation**: Standard crypto/sha256 library
- **Output Format**: Lowercase hexadecimal

### Signature Algorithm  

- **Algorithm**: RSA with SHA-256 (PKCS#1 v1.5)
- **Key Size**: 2048-bit minimum (4096-bit recommended)
- **Padding**: PKCS#1 v1.5 padding scheme

### Hash Calculation Process

#### Package Hash
1. Create temporary directory structure
2. Copy all payload files maintaining directory structure  
3. Copy all script files
4. Calculate SHA-256 of each file individually
5. Sort file paths alphabetically
6. Concatenate all file hashes
7. Calculate SHA-256 of concatenated hashes

#### Content Hash
1. Calculate SHA-256 of each payload file
2. Calculate SHA-256 of each script file  
3. Sort all hashes alphabetically
4. Concatenate sorted hashes
5. Calculate SHA-256 of concatenated result

#### Signed Hash
1. Take `package_hash` value
2. Sign with private key using RSA-SHA256
3. Encode signature as base64
4. Store in `signed_hash` field

### Certificate Requirements

- **Store Location**: Current User Personal Certificate Store
- **Enhanced Key Usage**: Code Signing (1.3.6.1.5.5.7.3.3)
- **Private Key**: Must be available and exportable  
- **Trust Chain**: Must chain to trusted root
- **Validity**: Must be valid at signing time

### Verification Process

1. **Extract Signature**: Parse `signature` section from build-info.yaml
2. **Verify Certificate**: Validate certificate chain and validity
3. **Recalculate Hashes**: Compute package_hash and content_hash
4. **Verify Signature**: Decrypt signed_hash with public key
5. **Compare Hashes**: Ensure calculated package_hash matches decrypted signature
6. **Check Timestamp**: Verify signing occurred within certificate validity

## Compatibility Requirements

### sbin-installer Integration

The .pkg format is designed for seamless integration with **sbin-installer**:

- **Extraction**: Standard ZIP extraction with UTF-8 file names
- **Metadata Parsing**: YAML parsing of build-info.yaml
- **Signature Verification**: Built-in cryptographic verification
- **Script Execution**: PowerShell execution with elevation
- **Error Handling**: Standard exit code interpretation

### Platform Requirements

- **Operating System**: Windows 10 version 1809 or later
- **PowerShell**: Windows PowerShell 5.1 or PowerShell Core 7.0+
- **Cryptography**: Windows Cryptographic API (CryptoAPI) or .NET cryptography
- **ZIP Support**: Native ZIP extraction capabilities

### File System Requirements

- **Long Path Support**: Handles paths exceeding 260 characters
- **Unicode Support**: Full Unicode file name support
- **Permissions**: Respects NTFS permissions and ACLs
- **Atomic Operations**: Transactional file operations where possible

## Versioning and Evolution

### Format Version

- **Current Version**: 1.0
- **Version Field**: `signature.version` indicates format version
- **Backward Compatibility**: Newer versions maintain compatibility with older parsers
- **Forward Compatibility**: Parsers ignore unknown fields

### Future Enhancements

Planned future enhancements maintain backward compatibility:

- **Compression Options**: Alternative compression algorithms
- **Multiple Signatures**: Support for multiple signing certificates  
- **Dependency Declaration**: Package dependency specification
- **Metadata Extensions**: Custom metadata sections
- **Localization Support**: Multi-language package descriptions

## Best Practices

### Package Design

1. **Minimize Package Size**: Use compression-friendly file organization
2. **Clear Identifiers**: Use descriptive reverse-domain identifiers
3. **Version Semantics**: Follow semantic versioning (SemVer)
4. **Script Robustness**: Handle edge cases and errors gracefully
5. **Path Handling**: Use relative paths within packages

### Security Considerations

1. **Always Sign Packages**: Use code signing certificates for all packages
2. **Verify Signatures**: Always verify package signatures before installation
3. **Script Review**: Review all PowerShell scripts for security implications  
4. **Certificate Management**: Use proper certificate storage and access controls
5. **Trust Validation**: Implement certificate trust chain validation

### Performance Optimization

1. **Efficient Compression**: Use appropriate compression levels
2. **Hash Caching**: Cache hash calculations for large packages
3. **Parallel Processing**: Process multiple packages concurrently when possible
4. **Streaming Verification**: Verify signatures while extracting when feasible

This specification provides the complete technical foundation for implementing .pkg package support across the Cimian ecosystem.