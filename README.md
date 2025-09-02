# Cimian

<img src="cimian.png" alt="Cimian" width="300">

Cimian is an open-source software deployment solution designed specifically for managing and automating software installations on Windows systems. **Heavily** inspired by the wonderful and dearly loved [Munki](https://github.com/munki/munki) project, Cimian allows Windows administrators to efficiently manage software packages through a webserver-based repository of packages and metadata, enabling automated deployments, updates, and removals at scale.

Cimian simplifies the software lifecycle management process, from creating packages to deploying them securely via Microsoft Intune or other cloud providers, ensuring consistency and reliability across large-scale Windows deployments.

## Key Features

- **Automated Package Management**: Streamline software packaging, metadata management, and distribution
- **Flexible YAML Configuration**: Easily configure and manage settings through clear, YAML-based config files
- **Multi-format Installer Support**: Supports MSI, MSIX, EXE, PowerShell scripts, and NuGet package formats
- **Bootstrap Mode**: for zero-touch deployment and system provisioning
- **Conditional Items**: Advanced evaluation system for dynamic software deployment based on system facts (hostname, architecture, OS version, domain, machine type, etc.) with simplified string syntax
- **Modern GUI**: Native WPF status application with Windows 11-inspired design
- **Enterprise Integration**: Built for Microsoft Intune with .intunewin support and any other Deployment Management Services.
- **Simplified Conditional Syntax**: New streamlined string format for conditional items (e.g., `"hostname DOES_NOT_CONTAIN Camera"`)

## Architecture Overview

Cimian consists of a comprehensive suite of command-line tools, services, and GUI applications that work together to provide a complete software management solution:

### Core Binaries

All binaries are built for both x64 and ARM64 architectures and installed to `C:\Program Files\Cimian\`.

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

#### Package Management Tools

**`cimiimport.exe`** - *Package Import and Metadata Generator*
- Automates importing software installers and generating deployment metadata
- Supports MSI, EXE, PowerShell scripts, and file-based installations
- Extracts metadata automatically (version, product codes, dependencies)
- Generates YAML pkginfo files with installation instructions
- Integrates with cloud storage (AWS S3, Azure Blob Storage) for package distribution
- Handles script integration (pre/post install/uninstall scr
- Features interactive and non-interactive configuration modes

**`cimipkg.exe`** - *NuGet Package Creator*
- Creates deployable NuGet packages (.nupkg) from project directories
- Supports both installer-type and copy-type package deployments
- Generates Chocolatey-compatible installation scripts
- Handles PowerShell script signing and embedding
- Optionally creates .intunewin packages for Microsoft Intune deployment
- Manages version normalization and metadata validation

**`makecatalogs.exe`** - *Software Catalog Generator*
- Scans repository and generates software catalogs from pkginfo metadata
- Creates organized YAML catalog files (Testing, Production, All, etc.)
- Validates package payload integrity and reports missing files
- Supports catalog-based software targeting and deployment

**`makepkginfo.exe`** - *Package Info Generator*
- Creates pkginfo metadata files for software packages
- Extracts installer metadata and generates deployment configurations
- Supports direct package analysis and metadata generation workflows

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

## Conditional Items System

Cimian features a powerful conditional items system inspired by Munki's NSPredicate-style conditions, allowing dynamic software deployment based on system facts like hostname, architecture, domain membership, and more. The enhanced system now supports complex expressions with OR/AND operators in single condition strings and nested conditional items for hierarchical logic.

### Simple String Format

Use natural language patterns: `"key operator value"`

```yaml
conditional_items:
  # Basic hostname exclusion
  - condition: "hostname DOES_NOT_CONTAIN Camera"
    managed_installs:
      - StandardSoftware
      
  # Architecture-specific deployment
  - condition: "arch == x64"
    managed_installs:
      - ModernApplication
      
  # Multiple values using IN operator
  - condition: "hostname IN LAB-01,LAB-02,LAB-03"
    managed_installs:
      - LabSoftware
```

### Enhanced Complex Expression Support

The enhanced conditional system now supports complex OR/AND expressions within a single condition string:

```yaml
conditional_items:
  # Complex OR expression in single condition
  - condition: hostname CONTAINS "Design-" OR hostname CONTAINS "Studio-" OR hostname CONTAINS "Edit-"
    managed_installs:
      - CreativeApplications
      - AdvancedTools
      
  # Complex AND expression
  - condition: os_vers_major >= 11 AND arch == "x64"
    managed_installs:
      - ModernApplications
      - x64OptimizedTools
      
  # Mixed AND/OR with special operators
  - condition: NOT hostname CONTAINS "Kiosk" AND (domain == "CORP" OR domain == "EDU")
    managed_installs:
      - EnterpriseApplications
      
  # ANY operator for catalog checking
  - condition: ANY catalogs != "Testing"
    managed_installs:
      - ProductionSoftware
```

### Nested Conditional Items

Create hierarchical conditional logic with nested conditional items:

```yaml
conditional_items:
  # Main condition with nested subconditions
  - condition: enrolled_usage == "Shared"
    conditional_items:
      # Nested conditions within the main condition
      - condition: enrolled_area != "Classroom" OR enrolled_area != "Podium"
        managed_installs:
          - CollaborativeTools
          - GroupSoftware
      
      # Architecture-specific nested deployment
      - condition: machine_type == "desktop"
        conditional_items:
          # Further nesting for granular control
          - condition: os_vers_major >= 11 AND arch == "x64"
            managed_installs:
              - HighEndApplications
              - PerformanceTools
        
        managed_installs:
          - BasicDesktopTools
    
    # Items for all machines matching the main condition
    managed_installs:
      - SharedMachineConfiguration
      - SecurityHardening
```

### Multiple CONTAINS Examples

```yaml
conditional_items:
  # Install on machines that contain BOTH "LAB" AND "RENDER" in hostname
  - conditions:
      - "hostname CONTAINS LAB"
      - "hostname CONTAINS RENDER"
    condition_type: "AND"
    managed_installs:
      - RenderFarmSoftware
      - LabManagementTools
      
  # Install on machines that contain ANY of these keywords in hostname
  - conditions:
      - "hostname CONTAINS DEV"
      - "hostname CONTAINS TEST"
      - "hostname CONTAINS STAGING"
    condition_type: "OR"
    managed_installs:
      - DeveloperTools
      - TestingUtilities
      
  # Complex: Must contain "WORKSTATION" but NOT contain "CAMERA" or "KIOSK"
  - conditions:
      - "hostname CONTAINS WORKSTATION"
      - "hostname DOES_NOT_CONTAIN CAMERA"
      - "hostname DOES_NOT_CONTAIN KIOSK"
    condition_type: "AND"
    managed_installs:
      - WorkstationSuite
      - OfficeTools
```

### Advanced Mixed AND/OR Logic

For complex scenarios, you can create multiple conditional items that work together:

```yaml
conditional_items:
  # Executive machines: Must be corporate domain AND (starts with EXEC OR starts with CEO)
  - conditions:
      - "domain == CORPORATE"
      - "hostname BEGINSWITH EXEC"
    condition_type: "AND"
    managed_installs:
      - ExecutiveSuite
      
  - conditions:
      - "domain == CORPORATE" 
      - "hostname BEGINSWITH CEO"
    condition_type: "AND"
    managed_installs:
      - ExecutiveSuite
      
  # Creative labs: Must contain "ART" or "DESIGN" but also be x64 architecture
  - conditions:
      - "hostname CONTAINS ART"
      - "arch == x64"
    condition_type: "AND"
    managed_installs:
      - CreativeSuite
      - AdobeTools
      
  - conditions:
      - "hostname CONTAINS DESIGN"
      - "arch == x64"
    condition_type: "AND"
    managed_installs:
      - CreativeSuite
      - AdobeTools
      
  # Engineering workstations: Multiple identification patterns
  - conditions:
      - "hostname CONTAINS ENG"
      - "hostname CONTAINS WORKSTATION"
      - "machine_model CONTAINS Precision"
    condition_type: "AND"
    managed_installs:
      - CADSoftware
      - EngineeringTools
```

### Real-World Complex Examples

```yaml
conditional_items:
  # Media production suites (must meet ALL criteria)
  - conditions:
      - "hostname CONTAINS MEDIA"
      - "arch == x64"
      - "machine_type == desktop"
      - "hostname DOES_NOT_CONTAIN BACKUP"
    condition_type: "AND"
    managed_installs:
      - AvidMediaComposer
      - ProTools
      - AfterEffects
    managed_uninstalls:
      - BasicVideoPlayer
      
  # Student lab machines (flexible identification)
  - conditions:
      - "hostname CONTAINS STUDENT"
      - "hostname CONTAINS LAB"  
      - "hostname CONTAINS CLASS"
    condition_type: "OR"
    managed_installs:
      - EducationalSoftware
      - StudentPortal
    managed_profiles:
      - StudentRestrictions
      
  # Exclude camera/kiosk/display systems entirely
  - conditions:
      - "hostname CONTAINS CAMERA"
      - "hostname CONTAINS KIOSK"
      - "hostname CONTAINS DISPLAY" 
      - "hostname CONTAINS SIGNAGE"
    condition_type: "OR"
    managed_uninstalls:
      - AllStandardSoftware
      - OfficeApps
      - UserTools
```

### Complex OR + AND Logic Patterns

Since each conditional item can only use either AND or OR, you achieve complex logic by using multiple conditional items together. Here are common patterns:

#### Pattern 1: (A OR B) AND C
Install software if hostname contains "DEV" OR "TEST", but ONLY if it's also x64 architecture:

```yaml
conditional_items:
  # Dev machines that are x64
  - conditions:
      - "hostname CONTAINS DEV"
      - "arch == x64"
    condition_type: "AND"
    managed_installs:
      - DeveloperTools
      
  # Test machines that are x64  
  - conditions:
      - "hostname CONTAINS TEST"
      - "arch == x64"
    condition_type: "AND"
    managed_installs:
      - DeveloperTools
```

#### Pattern 2: A AND (B OR C OR D)
Install on corporate domain machines that have ANY creative designation:

```yaml
conditional_items:
  # Corporate + Art designation
  - conditions:
      - "domain == CORPORATE"
      - "hostname CONTAINS ART"
    condition_type: "AND"
    managed_installs:
      - CreativeSuite
      
  # Corporate + Design designation
  - conditions:
      - "domain == CORPORATE"
      - "hostname CONTAINS DESIGN"
    condition_type: "AND"
    managed_installs:
      - CreativeSuite
      
  # Corporate + Media designation
  - conditions:
      - "domain == CORPORATE"
      - "hostname CONTAINS MEDIA"
    condition_type: "AND"
    managed_installs:
      - CreativeSuite
```

#### Pattern 3: (A AND B) OR (C AND D)
Install premium software on either executive machines OR high-end workstations:

```yaml
conditional_items:
  # Executive machines (Corporate domain + Executive hostname)
  - conditions:
      - "domain == CORPORATE"
      - "hostname CONTAINS EXECUTIVE"
    condition_type: "AND"
    managed_installs:
      - PremiumSoftwareSuite
      
  # High-end workstations (Precision model + x64 arch)
  - conditions:
      - "machine_model CONTAINS Precision"
      - "arch == x64"
    condition_type: "AND"
    managed_installs:
      - PremiumSoftwareSuite
```

#### Pattern 4: Complex Exclusions with Exceptions
Install standard software everywhere EXCEPT certain machine types, but with exceptions:

```yaml
conditional_items:
  # Standard installation (exclude problematic machine types)
  - conditions:
      - "hostname DOES_NOT_CONTAIN CAMERA"
      - "hostname DOES_NOT_CONTAIN KIOSK"
      - "hostname DOES_NOT_CONTAIN SIGNAGE"
      - "hostname DOES_NOT_CONTAIN DISPLAY"
    condition_type: "AND"
    managed_installs:
      - StandardSoftware
      
  # Exception: Special lab cameras that need some software
  - conditions:
      - "hostname CONTAINS CAMERA"
      - "hostname CONTAINS LAB"
      - "hostname DOES_NOT_CONTAIN PUBLIC"
    condition_type: "AND"
    managed_installs:
      - LabCameraTools
      - BasicUtilities
```

#### Pattern 5: Department-Based with Role Variations
Different software for the same department based on role:

```yaml
conditional_items:
  # Engineering department - all get base tools
  - condition: "hostname CONTAINS ENG"
    managed_installs:
      - EngineeringBase
      - CADViewer
      
  # Engineering workstations - additional power tools
  - conditions:
      - "hostname CONTAINS ENG"
      - "hostname CONTAINS WORKSTATION"
      - "arch == x64"
    condition_type: "AND"
    managed_installs:
      - AdvancedCAD
      - SimulationSoftware
      
  # Engineering managers - get management tools too
  - conditions:
      - "hostname CONTAINS ENG"
      - "hostname CONTAINS MGR"
    condition_type: "AND"
    managed_installs:
      - ProjectManagement
      - BudgetingTools
```

### Supported Operators

- **==** / **EQUALS**: Exact equality
- **!=** / **NOT_EQUALS**: Not equal  
- **CONTAINS**: String contains substring
- **DOES_NOT_CONTAIN**: String does not contain substring
- **BEGINSWITH**: String starts with value
- **ENDSWITH**: String ends with value
- **>** / **<** / **>=** / **<=**: Comparison operators
- **IN**: Value is in a comma-separated list
- **LIKE**: Wildcard pattern matching

### Available System Facts

- **hostname**: System hostname
- **arch**: System architecture (x64, arm64, x86)
- **os_version**: Windows OS version
- **domain**: Windows domain name
- **machine_type**: "laptop" or "desktop"
- **machine_model**: Computer model
- **joined_type**: "domain", "hybrid", "entra", or "workgroup"
- **catalogs**: Available catalogs (array)
- **username**: Current username
- **date**: Current date and time

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

## Manifests and Package Info Examples

### Sample Manifest Files

Manifests define what software should be installed on specific groups of computers. They are stored in the `manifests/` directory of your repository.

#### Basic Site Default Manifest (`manifests/site_default.yaml`)

```yaml
name: "Site Default - Corporate Baseline"
catalogs:
  - Production

# Base software for all systems
managed_installs:
  - Firefox
  - GoogleChrome
  - AdobeReader
  - 7zip
  - WindowsUpdates

# Optional software available for installation
optional_installs:
  - VLCMediaPlayer
  - GIMP
  - NotePadPlusPlus

# Software to remove if found
managed_uninstalls:
  - OldSoftware
  - BloatwareApp
```

#### Advanced Conditional Manifest (`manifests/computer_groups/lab_computers.yaml`)

```yaml
name: "Lab Computers - Educational Environment"
catalogs:
  - Testing
  - Education

# Base software for all lab computers
managed_installs:
  - BaseSecurityAgent
  - StudentMonitoringSoftware
  - SharedPrinterDrivers

# Conditional software based on system facts
conditional_items:
  # Engineering labs get CAD software
  - condition: "hostname CONTAINS ENG-LAB"
    managed_installs:
      - AutoCAD
      - SolidWorks
      - MATLAB
    
  # Art labs get creative software  
  - condition: "hostname CONTAINS ART-LAB"
    managed_installs:
      - AdobeCreativeSuite
      - Blender
      - SketchUp
      
  # x64 systems get modern applications
  - condition: "arch == x64"
    managed_installs:
      - ModernVideoEditor
    managed_uninstalls:
      - LegacyX86Software

# Microsoft Intune integration
managed_profiles:
  - EducationalWiFiProfile
  - StudentEmailProfile
  
managed_apps:
  - "Microsoft Teams"
  - "Microsoft Office"
  - "OneNote"
```

#### Enterprise Workstation Manifest (`manifests/workstations.yaml`)

```yaml
name: "Corporate Workstations"
catalogs:
  - Production
  - Corporate

managed_installs:
  - EnterpriseAntivirus
  - OfficeTools
  - CorporateVPN

# Complex conditional logic
conditional_items:
  # Executive workstations
  - conditions:
      - "hostname BEGINSWITH EXEC"
      - "domain == CORPORATE"
    condition_type: "AND"
    managed_installs:
      - ExecutiveSuite
      - PremiumOfficeAddins
      - VIPSupportTools
      
  # Developer machines
  - condition: "hostname CONTAINS DEV"
    managed_installs:
      - VisualStudio
      - GitForWindows
      - DockerDesktop
    optional_installs:
      - AdvancedDebugger
      - PerformanceProfiler
      
  # Domain join type specific software
  - condition: "joined_type == entra"
    managed_installs:
      - EntraIDTools
      - AzureADSync
  - condition: "joined_type == domain"
    managed_installs:
      - TraditionalDomainTools
```

### Sample Package Info Files

Package info files define individual software packages and their installation details. They are stored in the `pkgsinfo/` directory.

#### MSI Package Example (`pkgsinfo/Adobe/AdobeReader.yaml`)

```yaml
name: AdobeReader
display_name: "Adobe Acrobat Reader DC"
identifier: com.adobe.reader.dc
version: "24.003.20054"
description: "Adobe Acrobat Reader DC - Free PDF viewer"
catalogs:
  - Testing
  - Production
category: "PDF Tools"
developer: "Adobe Inc."

# Installation details
installer:
  type: msi
  size: 245760000
  location: Adobe/AcroRdrDC_2400320054_MUI.msi
  hash: "sha256:a1b2c3d4e5f6789..."
  product_code: "{AC76BA86-7AD7-1033-7B44-AC0F074E4100}"
  upgrade_code: "{AC76BA86-7AD7-1033-7B44-AC0F074E4100}"

# What files this package installs
installs:
  - type: file
    path: "C:\Program Files\Adobe\Acrobat DC\Reader\AcroRd32.exe"
    md5checksum: "d41d8cd98f00b204e9800998ecf8427e"
    version: "24.003.20054"

# System requirements
supported_architectures:
  - x64
  - x86
minimum_os_version: "10.0.19041"
unattended_install: true
unattended_uninstall: true
```

#### EXE Package with Scripts (`pkgsinfo/Development/VisualStudio.yaml`)

```yaml
name: VisualStudio
display_name: "Microsoft Visual Studio Community 2022"
identifier: com.microsoft.visualstudio.community.2022
version: "17.8.3"
description: "Microsoft Visual Studio Community 2022 - Free IDE"
catalogs:
  - Development
  - Testing
category: "Development Tools"
developer: "Microsoft Corporation"

# EXE installer with custom arguments
installer:
  type: exe
  size: 4294967296
  location: Microsoft/VisualStudio/vs_community_2022.exe
  hash: "sha256:f8e7d6c5b4a39..."
  flags:
    - quiet
    - wait
    - add Microsoft.VisualStudio.Workload.ManagedDesktop
    - add Microsoft.VisualStudio.Workload.NetWeb

# Installation tracking
installs:
  - type: file
    path: "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
    version: "17.8.34316.72"

# Custom scripts
preinstall_script: |
  # Check system requirements
  $ram = (Get-WmiObject -Class Win32_ComputerSystem).TotalPhysicalMemory / 1GB
  if ($ram -lt 8) {
    throw "Visual Studio requires at least 8GB RAM"
  }
  Write-Host "System meets requirements"

postinstall_script: |
  # Configure Visual Studio settings
  $vsPath = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community"
  if (Test-Path $vsPath) {
    Write-Host "Visual Studio installed successfully"
    # Additional configuration here
  }

# Uninstaller
uninstaller:
  location: Microsoft/VisualStudio/vs_community_2022.exe
  arguments:
    - uninstall
    - --quiet
    - --wait

supported_architectures:
  - x64
minimum_os_version: "10.0.17763"
unattended_install: true
unattended_uninstall: true
```

#### PowerShell Script Package (`pkgsinfo/Scripts/SystemConfiguration.yaml`)

```yaml
name: SystemConfiguration
display_name: "Corporate System Configuration"
identifier: com.company.systemconfig
version: "1.2.0"
description: "Applies corporate system configuration settings"
catalogs:
  - Production
category: "System Configuration"
developer: "IT Department"

# PowerShell script installer
installer:
  type: ps1
  size: 8192
  location: Scripts/SystemConfiguration.ps1
  hash: "sha256:1a2b3c4d5e6f..."

# Script content (can also be external file)
preinstall_script: |
  # Backup current settings
  $backupPath = "C:\Temp\SystemBackup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
  New-Item -ItemType Directory -Path $backupPath -Force

postinstall_script: |
  # Verify configuration applied successfully
  Write-Host "System configuration completed"
  
  # Set registry values
  Set-ItemProperty -Path "HKLM:\SOFTWARE\Company\Config" -Name "LastUpdate" -Value (Get-Date)

# This is a script-only package - no file tracking needed
supported_architectures:
  - x64
  - arm64
  - x86
unattended_install: true
unattended_uninstall: false  # Scripts can't be "uninstalled"
OnDemand: false  # Run during normal update cycles
```

#### Complex Package with Dependencies (`pkgsinfo/Microsoft/Office365.yaml`)

```yaml
name: Office365
display_name: "Microsoft 365 Apps for Enterprise"
identifier: com.microsoft.office365.enterprise
version: "16.0.17126.20132"
description: "Microsoft 365 Apps for Enterprise suite"
catalogs:
  - Production
  - Corporate
category: "Office Applications"
developer: "Microsoft Corporation"

# Complex installer with configuration
installer:
  type: exe
  size: 6442450944
  location: Microsoft/Office365/setup.exe
  hash: "sha256:9f8e7d6c5b4a3..."
  arguments:
    - /configure
    - /quiet
    - /config:configuration.xml

# Multiple file installations tracked
installs:
  - type: file
    path: "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE"
    version: "16.0.17126.20132"
  - type: file  
    path: "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE"
    version: "16.0.17126.20132"
  - type: file
    path: "C:\Program Files\Microsoft Office\root\Office16\POWERPNT.EXE" 
    version: "16.0.17126.20132"

# Prerequisites and dependencies
requires:
  - DotNetFramework48
  - VisualCPPRedistributable

# Advanced uninstall handling
uninstaller:
  type: exe
  location: Microsoft/Office365/setup.exe
  arguments:
    - /configure
    - /quiet
    - /uninstall

uninstalls:
  - type: directory
    path: "C:\Program Files\Microsoft Office"
    recursive: true
    force: true
  - type: registry
    path: "HKLM\SOFTWARE\Microsoft\Office"
    recursive: true

# System requirements
supported_architectures:
  - x64
minimum_os_version: "10.0.19041"
maximum_os_version: ""
unattended_install: true
unattended_uninstall: true
```

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

## Command Line Examples

`managedsoftwareupdate`:

```powershell
sudo managedsoftwareupdate --help   
Usage of C:\Program Files\Cimian\managedsoftwareupdate.exe:
      --auto                         Perform automatic updates.
      --cache-status                 Show cache status and statistics.
      --check-selfupdate             Check if self-update is pending.
      --checkonly                    Check for updates, but don't install them.
      --clear-bootstrap-mode         Disable bootstrap mode.
      --clear-selfupdate             Clear pending self-update flag.
      --installonly                  Install pending updates without checking for new ones.
      --item strings                 Install only the specified package name(s). Can be repeated or given as a comma-separated list.
      --local-only-manifest string   Use specified local manifest file instead of server manifest.
      --manifest string              Process only the specified manifest from server (e.g., 'Shared/Curriculum/RenderingFarm'). Automatically skips preflight.
      --no-postflight                Skip postflight script execution.
      --no-preflight                 Skip preflight script execution.
      --perform-selfupdate           Perform pending self-update (internal use).
      --postflight-only              Run only the postflight script and exit.
      --preflight-only               Run only the preflight script and exit.
      --restart-service              Restart CimianWatcher service and exit.
      --selfupdate-status            Show self-update status and exit.
      --set-bootstrap-mode           Enable bootstrap mode for next boot.
      --show-config                  Display the current configuration and exit.
      --show-status                  Show status window during operations (bootstrap mode).
      --validate-cache               Validate cache integrity and remove corrupt files.
  -v, --verbose count                Increase verbosity (e.g. -v, -vv, -vvv, -vvvv)
      --version                      Print the version and exit.
pflag: help requested
```

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

# Run only preflight script for testing
managedsoftwareupdate.exe --preflight-only

# Run only postflight script for testing
managedsoftwareupdate.exe --postflight-only

# Trigger GUI update process
cimitrigger.exe gui

# Run diagnostic tests
cimitrigger.exe debug
```

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

## Bootstrap System

Cimian includes a bootstrap system similar to Munki's, designed for zero-touch deployment scenarios where machines must complete all required software installations before users can log in.

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

## Monitoring and Logging

### Event Logging

Cimian components write to Windows Event Log under:
- **Application Log**: General application events
- **System Log**: Service-related events  
- **Custom Logs**: Detailed operation logs in `C:\ProgramData\ManagedInstalls\Logs\`

### Log Files

Cimian uses a modern structured logging system with timestamped session directories:

| Component | Log Location | Purpose |
|-----------|-------------|---------|
| **Session Logs** | `C:\ProgramData\ManagedInstalls\logs\{YYYY-MM-DD-HHMMss}\` | Individual session data with structured JSON and human-readable logs |
| **Primary Log** | `C:\ProgramData\ManagedInstalls\logs\{session}\install.log` | Human-readable installation operations log |
| **Session Metadata** | `C:\ProgramData\ManagedInstalls\logs\{session}\session.json` | Session start/end times, status, statistics |
| **Event Stream** | `C:\ProgramData\ManagedInstalls\logs\{session}\events.jsonl` | Detailed event tracking for troubleshooting |
| **Summary Reports** | `C:\ProgramData\ManagedInstalls\reports\sessions.json` | Pre-computed session summaries for monitoring tools |
| **Event Reports** | `C:\ProgramData\ManagedInstalls\reports\events.json` | Aggregated event data for analysis |
| **CimianWatcher Service** | Windows Event Log (Application) | Service monitoring and bootstrap events |
| **Status Runtime** | `C:\ProgramData\ManagedInstalls\LastRunTime.txt` | Last execution timestamp |

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
