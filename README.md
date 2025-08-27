# Cimian

<img src="cimian.png" alt="Cimian" width="300">

Cimian is an open-source software deployment solution designed specifically for managing and automating software installations on Windows systems. **Heavily** inspired by the wonderful and dearly loved [Munki](https://github.com/munki/munki) project, Cimian allows Windows administrators to efficiently manage software packages through a webserver-based repository of packages and metadata, enabling automated deployments, updates, and removals at scale.

Cimian simplifies the software lifecycle management process, from creating packages to deploying them securely via Microsoft Intune or other cloud providers, ensuring consistency and reliability across large-scale Windows deployments.

## Key Features

- **Automated Package Management**: Streamline software packaging, metadata management, and distribution
- **Flexible YAML Configuration**: Easily configure and manage settings through clear, YAML-based config files
- **Multi-format Installer Support**: Supports MSI, MSIX, EXE, PowerShell scripts, and NuGet package formats
- **Bootstrap Mode**: Windows equivalent of Munki's bootstrap system for zero-touch deployment and system provisioning
- **Conditional Items**: NSPredicate-style conditional evaluation for dynamic software deployment based on system facts (hostname, architecture, OS version, domain, etc.)
- **Modern GUI**: Native WPF status application with Windows 11-inspired design
- **Enterprise Integration**: Built for Microsoft Intune and other MDM platforms with .intunewin support
- **Real-time Monitoring**: Responsive Windows service for near-instantaneous deployment triggers

## Architecture Overview

Cimian consists of a comprehensive suite of command-line tools, services, and GUI applications that work together to provide a complete software management solution:

### Core Binaries

All binaries are built for both x64 and ARM64 architectures and installed to `C:\Program Files\Cimian\`.

#### Package Management Tools

**`cimiimport.exe`** - *Package Import and Metadata Generator*
- Automates importing software installers and generating deployment metadata
- Supports MSI, EXE, PowerShell scripts, and file-based installations
- Extracts metadata automatically (version, product codes, dependencies)
- Generates YAML pkginfo files with installation instructions
- Integrates with cloud storage (AWS S3, Azure Blob Storage) for package distribution
- Handles script integration (pre/post install/uninstall scripts)
- Features interactive and non-interactive configuration modes

**`cimipkg.exe`** - *NuGet Package Creator*
- Creates deployable NuGet packages (.nupkg) from project directories
- Supports both installer-type and copy-type package deployments
- Generates Chocolatey-compatible installation scripts
- Handles PowerShell script signing and embedding
- Creates .intunewin packages for Microsoft Intune deployment
- Manages version normalization and metadata validation

**`makecatalogs.exe`** - *Software Catalog Generator*
- Scans repository and generates software catalogs from pkginfo metadata
- Creates organized YAML catalog files (Testing, Production, All, etc.)
- Validates package payload integrity and reports missing files
- Supports catalog-based software targeting and deployment

**`makepkginfo.exe`** - *Legacy Package Info Generator*
- Creates pkginfo metadata files for software packages
- Provides compatibility layer for legacy workflows
- Extracts installer metadata and generates deployment configurations

#### Client-Side Management

**`managedsoftwareupdate.exe`** - *Primary Client Agent*
- Core client-side component for software installation and management
- Handles automatic software updates, installations, and removals
- Supports bootstrap mode for zero-touch deployments
- Manages software manifests and catalog synchronization
- Features comprehensive logging and error handling
- Supports multiple installer types (MSI, EXE, PowerShell, file copying)
- Includes self-update capabilities and privilege elevation
- Provides command-line interface for administrative tasks

**`manifestutil.exe`** - *Manifest Management Tool*
- Command-line utility for managing deployment manifests
- Creates, modifies, and maintains software deployment lists
- Supports adding/removing packages from managed installations
- Handles self-service software request management
- Provides manifest validation and organization

#### Deployment Triggers and Monitoring

**`cimitrigger.exe`** - *Deployment Trigger Tool*
- Initiates software deployment processes on-demand
- Supports both GUI and headless deployment modes
- Handles privilege elevation and process management
- Provides diagnostic capabilities for troubleshooting
- Integrates with CimianWatcher service for responsive deployments

**`cimiwatcher.exe`** - *Bootstrap Monitoring Service*
- Windows service that monitors for deployment trigger files
- Enables near-real-time software deployment via MDM platforms
- Supports dual-mode operation (GUI and headless bootstrap)
- Handles automatic service recovery and error management
- Integrates with self-update system for service maintenance
- Provides comprehensive event logging for enterprise monitoring

#### User Interface

**`cimistatus.exe`** - *Modern Status GUI Application*
- Native WPF application with Windows 11-inspired design
- Real-time status monitoring for software operations
- Modern UI with theme-aware colors and animations
- Supports both interactive user sessions and background service mode
- Single-instance application with window management
- Integration with logging and status reporting systems
- Built using Modern WPF UI framework for contemporary appearance

## Bootstrap System

Cimian includes a sophisticated bootstrap system similar to Munki's, designed for zero-touch deployment scenarios where machines must complete all required software installations before users can log in.

### How Bootstrap Works

1. **Trigger Files**: 
   - `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` - Bootstrap with GUI status window
   - `C:\ProgramData\ManagedInstalls\.cimian.headless` - Bootstrap without GUI (silent)

2. **CimianWatcher Service**: A Windows service monitors bootstrap trigger files every 10 seconds and automatically initiates software deployment

3. **Dual Mode Operation**: 
   - **GUI Mode**: Shows CimianStatus window for visual progress monitoring
   - **Headless Mode**: Silent operation for automated scenarios

4. **Automatic Process Management**: The service handles process elevation, error recovery, and cleanup automatically

5. **Integration Points**: Works seamlessly with MDM platforms like Microsoft Intune for enterprise deployment

### Bootstrap Commands

| Action | Command | Description |
|--------|---------|-------------|
| Enter GUI Bootstrap | `managedsoftwareupdate.exe --set-bootstrap-mode` | Creates GUI bootstrap trigger |
| Enter Headless Bootstrap | `cimitrigger.exe headless` | Initiates silent bootstrap |
| Clear Bootstrap | `managedsoftwareupdate.exe --clear-bootstrap-mode` | Removes bootstrap flags |
| Trigger GUI Update | `cimitrigger.exe gui` | Force GUI update process |
| Diagnostic Mode | `cimitrigger.exe debug` | Run diagnostics |

### Enterprise Use Cases

- **Zero-touch deployment**: Ship Windows machines with only Cimian installed; bootstrap completes the configuration
- **System rebuilds**: Ensure all required software is installed before first user login  
- **Provisioning automation**: Integrate with deployment tools for fully automated system setup
- **MDM Integration**: Deploy via Microsoft Intune with .intunewin packages
- **Responsive Updates**: Near-real-time software deployment when triggered by management systems

## Installation and Deployment

### Distribution Formats

Cimian is distributed in multiple formats to support different deployment scenarios:

- **MSI Packages**: `Cimian-x64-{version}.msi` and `Cimian-arm64-{version}.msi` for traditional Windows deployment
- **NuGet Packages**: `CimianTools-x64-{version}.nupkg` and `CimianTools-arm64-{version}.nupkg` for Chocolatey-based deployment  
- **Intune Packages**: `.intunewin` files for Microsoft Intune deployment

### Quick Start

1. **Download and Install**: Deploy the appropriate MSI package for your architecture
2. **Configure Repository**: Edit `C:\ProgramData\ManagedInstalls\Config.yaml` with your repository settings
3. **Import Software**: Use `cimiimport.exe` to import software packages into your repository
4. **Generate Catalogs**: Run `makecatalogs.exe` to create software catalogs
5. **Deploy**: Use bootstrap mode or direct execution for software deployment

## Development and Building

### Build System

The project uses a comprehensive PowerShell build system (`build.ps1`) that:

- Builds all binaries for x64 and ARM64 architectures
- Handles code signing with enterprise certificates
- Creates MSI and NuGet packages automatically
- Supports development mode for rapid iteration
- Integrates with CI/CD pipelines

### Build Commands

```powershell
# Full build with automatic signing
.\build.ps1

# Development mode (fast iteration)
.\build.ps1 -Dev -Install

# Build specific binary only
.\build.ps1 -Binary cimistatus -Sign

# Create Intune packages
.\build.ps1 -IntuneWin

# Package existing binaries
.\build.ps1 -PackageOnly
```


## Configuration

Cimian uses a YAML-based configuration system located at `C:\ProgramData\ManagedInstalls\Config.yaml`:

```yaml
# Basic Configuration
software_repo_url: https://cimian.yourdomain.com/
client_identifier: MyComputer-01
force_basic_auth: false
default_arch: x64
default_catalog: Testing

# Repository Settings  
repo_path: "C:\\CimianRepo"
cloud_provider: none  # Options: aws, azure, none
managed_installs_dir: "C:\\ProgramData\\ManagedInstalls"

# Update Behavior
auto_update_enabled: true
auto_update_check_interval: 24  # hours
installer_timeout_minutes: 30
max_concurrent_downloads: 3

# Logging Configuration
log_level: INFO  # Options: DEBUG, INFO, WARNING, ERROR
log_max_size_mb: 10
log_retention_days: 30

# Bootstrap Settings
bootstrap_timeout_minutes: 120
bootstrap_retry_attempts: 3

# Advanced Options
use_tls_verification: true
proxy_server: ""
custom_user_agent: "Cimian/1.0"
```

### Configuration Management

- **Interactive Setup**: Run `cimiimport.exe --config` for guided configuration
- **Automatic Setup**: Use `cimiimport.exe --config-auto` for non-interactive configuration
- **Validation**: Configuration is validated on startup with detailed error reporting

## Repository Structure

A typical Cimian repository follows this structure:

```
CimianRepo/
├── catalogs/           # Generated catalog files
│   ├── All.yaml
│   ├── Testing.yaml
│   └── Production.yaml
├── manifests/          # Client deployment manifests
│   ├── site_default.yaml
│   └── computer_groups/
├── pkgs/              # Software package storage
│   ├── Adobe/
│   ├── Microsoft/
│   └── ...
└── pkgsinfo/          # Package metadata
    ├── Adobe/
    └── Microsoft/
```

## API and Integration

### Command Line Examples

```powershell
# Import a new software package
cimiimport.exe "C:\Installers\Firefox.exe" --arch x64

# Create a NuGet package from project directory
cimipkg.exe "C:\Projects\MyApp"

# Generate catalogs from repository
makecatalogs.exe

# Add software to managed installations
manifestutil.exe --add-pkg "Firefox" --manifest "site_default"

# Check for and install updates
managedsoftwareupdate.exe --auto

# Trigger GUI update process
cimitrigger.exe gui

# Run diagnostic tests
cimitrigger.exe debug
```

### PowerShell Integration

```powershell
# Example: Automated package import workflow
$packages = Get-ChildItem "C:\Installers" -Filter "*.exe"
foreach ($package in $packages) {
    & "C:\Program Files\Cimian\cimiimport.exe" $package.FullName --arch x64
}

# Generate catalogs after import
& "C:\Program Files\Cimian\makecatalogs.exe"
```

## Monitoring and Logging

### Event Logging

Cimian components write to Windows Event Log under:
- **Application Log**: General application events
- **System Log**: Service-related events  
- **Custom Logs**: Detailed operation logs in `C:\ProgramData\ManagedInstalls\Logs\`

### Log Files

| Component | Log Location | Purpose |
|-----------|-------------|---------|
| Client Agent | `C:\ProgramData\ManagedInstalls\Logs\managedsoftwareupdate.log` | Installation operations |
| Watcher Service | Windows Event Log | Service monitoring events |
| Status GUI | `C:\ProgramData\ManagedInstalls\Logs\cimistatus.log` | UI operations |
| Import Tool | Console output + file logs | Package import operations |

### Status Monitoring

The CimianStatus GUI provides real-time monitoring with:
- **Installation Progress**: Visual progress bars and status updates
- **Package Queue**: List of pending installations
- **Error Reporting**: Detailed error messages and troubleshooting guidance  
- **System Information**: Hardware, OS, and configuration details

## Troubleshooting

### Common Issues

1. **Service Not Running**: `sc start CimianWatcher`
2. **Permission Issues**: Ensure Local System has access to repository paths
3. **GUI Not Visible**: Check for Session 0 isolation in service environments
4. **Package Import Failures**: Verify repository paths and write permissions
5. **Network Connectivity**: Check repository URL and proxy settings

### Diagnostic Tools

```powershell
# Service status check
Get-Service CimianWatcher

# Process monitoring
tasklist /fi "imagename eq cimistatus.exe"

# Trigger diagnostic mode  
cimitrigger.exe debug

# Manual update check
managedsoftwareupdate.exe --checkonly --verbose
```

## Enterprise Deployment

### Microsoft Intune Integration

1. **Package Creation**: Use `cimipkg.exe -intunewin` to create .intunewin packages
2. **Upload to Intune**: Import .intunewin files into Microsoft Endpoint Manager
3. **Assignment**: Deploy to device groups with appropriate targeting
4. **Monitoring**: Use Intune reporting for deployment status

### Group Policy Integration

Deploy Cimian configuration via Group Policy:
- **Registry Settings**: Configure via Group Policy Preferences
- **File Deployment**: Deploy config.yaml via Group Policy file copy
- **Service Management**: Use Group Policy to manage service startup

### SCCM Integration

Cimian can complement SCCM deployments:
- **Package Distribution**: Use Cimian for rapid updates between SCCM cycles
- **Self-Service**: Provide user-initiated software installation
- **Reporting**: Centralized logging and status reporting

## Performance and Scalability

### Optimization Features

- **Concurrent Downloads**: Configurable parallel download limits
- **Delta Updates**: Only download changed package components
- **Bandwidth Throttling**: Configurable download speed limits
- **Retry Logic**: Automatic retry with exponential backoff
- **Cache Management**: Intelligent local caching for frequently used packages

### Scalability Metrics

- **Clients per Repository**: Tested with 10,000+ concurrent clients
- **Package Repository Size**: Supports multi-TB repositories
- **Update Frequency**: Sub-minute update deployment capability
- **Concurrent Operations**: Handles multiple simultaneous installations

## Security Features

### Code Signing

- **Binary Signing**: All executables signed with enterprise certificates  
- **Package Verification**: Automatic signature validation for imported packages
- **PowerShell Signing**: Automatic script signing during package creation
- **Certificate Management**: Automatic enterprise certificate detection

### Security Hardening

- **Privilege Elevation**: Minimal privilege escalation with automatic de-elevation
- **Input Validation**: Comprehensive input sanitization and validation
- **Secure Communications**: TLS encryption for repository communications
- **Audit Logging**: Comprehensive audit trails for all operations

### Compliance Features

- **Change Tracking**: Full audit trail of software changes
- **Approval Workflows**: Configurable approval processes for software deployment
- **Compliance Reporting**: Built-in reporting for regulatory compliance
- **Access Controls**: Role-based access control for administrative functions


## License

Cimian is distributed under the MIT License. See [LICENSE](LICENSE) for details.

## Contributing

We welcome contributions! Feel free to submit pull requests, report issues, or suggest improvements via our [GitHub repository](https://github.com/windowsadmins/cimian).
