````instructions
# Cimian Development Guide

## Overview
Cimian is a Windows software deployment solution inspired by Munki, currently in transition from Go to C#. The codebase contains both Go (legacy/current) and C# (future) implementations. This guide covers essential knowledge for AI agents working on this enterprise-scale software management system.

## CRITICAL Development Rules

**ALWAYS SIGN BINARIES**: Use the `-Sign` parameter when building. Unsigned binaries cannot run in this system.

**NO RANDOM TEST FILES**: Never create standalone test files or binaries. Always work within the actual project structure, rebuild project binaries, and test iteratively until issues are resolved.

**ENTERPRISE SCALE MINDSET**: This system manages 10,000+ computers. Solutions must be:
- **Fully Automated**: No manual interventions at scale
- **Self-Healing**: Automatically detect and correct inconsistencies  
- **Multi-Source Aware**: Handle packages from Cimian, Chocolatey, MSI, winget, and other package managers
- **Version Tracking Unified**: Single source of truth for installed versions regardless of installation method

```pwsh
# Essential build commands
.\build.ps1 -Sign -Binary managedsoftwareupdate  # Build core client
.\build.ps1 -Sign -Binary cimistatus             # Build C# GUI
.\build.ps1 -Sign -Binaries                      # Build all binaries
.\build.ps1 -Sign                                # Full build with packages
```

## Architecture & Critical Systems

### Go-to-C# Migration in Progress
The codebase is actively migrating from Go to C#. Key patterns:
- **Go Legacy**: `cmd/*/main.go` - Original implementations
- **C# Future**: `src/Cimian.CLI.*/Program.cs` - New implementations  
- **Hybrid State**: Both may exist for the same component during transition

### Bootstrap System (CORE ENTERPRISE FEATURE)
**File-Based Trigger System**: CimianWatcher service monitors trigger files every 10 seconds
```
C:\ProgramData\ManagedInstalls\.cimian.bootstrap  # GUI mode
C:\ProgramData\ManagedInstalls\.cimian.headless   # Silent mode
```

**Service Architecture**: `cimiwatcher.exe` native Windows service enables zero-touch deployment
- Near real-time response (10-15 seconds maximum)
- Automatic process elevation and recovery
- Enterprise MDM integration (Intune, SCCM)

### Data Reporting System (3400+ lines)
**Location**: `pkg/reporting/reporting.go` - Complex external monitoring integration
- **Sessions Table**: Installation session tracking with metadata
- **Events Table**: Granular event logging with error details  
- **Items Table**: Comprehensive package state management
- **Progressive Reporting**: Real-time status for monitoring tools

### Configuration Hierarchy
**Primary**: `C:\ProgramData\ManagedInstalls\Config.yaml`
**Fallback**: Registry-based CSP OMA-URI settings for enterprise policy
**Multi-Architecture**: Separate configs for x64/arm64

Key Config Patterns:
```go
// pkg/config/config.go - Central configuration management
const ConfigPath = `C:\ProgramData\ManagedInstalls\Config.yaml`
const CSPRegistryPath = `SOFTWARE\Cimian\Config`
```

### Conditional Items System (Advanced Deployment Logic)
**Complex Expression Engine**: Dynamic software deployment based on system facts
```yaml
# Advanced conditional deployment patterns
conditional_items:
  # Complex expressions with OR/AND
  - condition: hostname CONTAINS "Design-" OR hostname CONTAINS "Studio-" OR hostname CONTAINS "Edit-"
    managed_installs:
      - CreativeApplications
  
  # Nested conditionals for hierarchical logic
  - condition: enrolled_usage == "Shared"
    conditional_items:
      - condition: enrolled_area != "Classroom" OR enrolled_area != "Podium"
        managed_installs:
          - CollaborativeTools
```

**Available System Facts**: hostname, arch, os_version, domain, machine_type, joined_type, enrolled_usage, enrolled_area

## Project Structure & Key Locations

### Core Executables (All architectures: x64/arm64)
- **`managedsoftwareupdate.exe`** - Primary client (Go → C# migration)
- **`cimistatus.exe`** - WPF GUI (.NET 8, MVVM pattern)
- **`cimiwatcher.exe`** - Bootstrap monitoring service  
- **`cimiimport.exe`** - Package import and metadata generation
- **`cimipkg.exe`** - NuGet package creation
- **`cimitrigger.exe`** - Bootstrap trigger utility

### Critical Go Packages (pkg/)
- **`installer/`** - Multi-format installer engine (MSI, EXE, PS1, MSIX)
- **`reporting/`** - 3400+ line external monitoring integration
- **`config/`** - YAML + Registry configuration hierarchy
- **`logging/`** - Structured session-based logging system
- **`manifest/`** - Hierarchical deployment manifest processing

### C# Migration Progress
- **`src/Cimian.CLI.managedsoftwareupdate/`** - New C# implementation
- **`cmd/cimistatus/`** - WPF application with dependency injection
- **Modern Patterns**: Serilog, CommandLineParser, hosted services

## Build System

### Build Script Options
The `build.ps1` script is the central build system with many options:

```pwsh
# SIGNING OPTIONS
-Sign                    # Build + sign (auto-detects cert)
-NoSign                  # Disable signing even if cert found
-Thumbprint XX          # Override cert auto-detection

# BUILD SCOPE
-Binaries               # Build only .exe files, skip packaging
-Binary <name>          # Build only specific binary (e.g., cimistatus)
-Task <build|package|all> # Specify build phase

# PACKAGING OPTIONS
-PackageOnly            # Package existing binaries (skip build)
-NupkgOnly             # Create only NuGet packages
-MsiOnly               # Create only MSI packages
-SkipMSI               # Skip MSI creation
-IntuneWin             # Create .intunewin packages

# DEPLOYMENT
-Install               # Install MSI after building

# UTILITIES
-SignMSI               # Sign existing MSI files only
```

### Common Development Workflows

#### 1. Quick Development Iteration
```pwsh
# For development testing - always sign binaries
.\build.ps1 -Sign -Binary managedsoftwareupdate

# Test the built binary safely
sudo .\release\arm64\managedsoftwareupdate.exe -v --checkonly
```

#### 2. Full Production Build
```pwsh
# Complete build with all packages
.\build.ps1 -Sign -IntuneWin
```

#### 3. Building Specific Components
```pwsh
# Build only the Go binaries
.\build.ps1 -Sign -Binaries

# Build only CimianStatus (C# GUI)
.\build.ps1 -Sign -Binary cimistatus

# Build specific tool
.\build.ps1 -Sign -Binary cimiimport
```

#### 4. Packaging Without Rebuilding
```pwsh
# Package existing binaries
.\build.ps1 -PackageOnly -Sign

# Create only NuGet packages
.\build.ps1 -NupkgOnly -Sign
```

#### IMPORTANT: Use `sudo` for inline administrative operations
**NEVER use `Start-Process powershell -Verb RunAs`** - Always use `sudo` for administrative commands that need to run inline. This includes registry operations, service management, file system operations requiring elevation, etc. The `sudo` command provides seamless elevation without spawning separate processes.

## System Design Philosophy

**ENTERPRISE SCALE MINDSET**: Cimian is designed for 10,000+ computers. Solutions must be:
- **Fully Automated**: No manual interventions at scale
- **Self-Healing**: Systems must detect and correct inconsistencies automatically  
- **Multi-Source Aware**: Must handle packages installed by Cimian, Chocolatey, MSI, or other package managers
- **Version Tracking Unified**: Single source of truth for installed versions regardless of installation method

**NO MANUAL FIXES**: The word "manual" does not exist in enterprise vocabulary. Every edge case must have an automated solution that works across thousands of machines.

## Testing and Debugging

### Safe Testing
Always use the `--checkonly` flag when testing `managedsoftwareupdate` to avoid triggering actual installations:

```pwsh
# Test with verbose output, no actual installs
sudo .\release\arm64\managedsoftwareupdate.exe -v --checkonly

# Test specific functionality
sudo .\release\arm64\managedsoftwareupdate.exe --no-preflight -vvv
```

### Development Mode Testing
```pwsh
# Always sign binaries for testing
.\build.ps1 -Sign -Binary managedsoftwareupdate
sudo .\release\arm64\managedsoftwareupdate.exe -v --checkonly
```

### Log Analysis
Check logs in standard Windows locations:
- Application logs: Event Viewer
- Cimian-specific logs: Check `pkg/logging/` for log file locations

## Code Organization

### Go Modules
- **Main module**: `github.com/windowsadmins/cimian`
- **Submodules**: Some components have their own `go.mod` files
- **Dependencies**: See `go.mod` for current dependency list

### Key Packages
- **github.com/spf13/pflag** - Command-line flag parsing
- **gopkg.in/yaml.v3** - YAML configuration handling
- **github.com/shirou/gopsutil/v3** - System information
- **golang.org/x/sys** - Windows system APIs

### C# Component
- **Target**: .NET 8 Windows
- **UI Framework**: WPF
- **Architecture**: MVVM pattern
- **Build**: Uses standard `dotnet build` with runtime-specific targeting

## Architecture & Patterns

### Version Information
Build process injects version information:
```go
// pkg/version/ provides build-time version info
// Set via build flags during compilation
```

### Configuration System
- **YAML-based**: `config.yaml` for all settings
- **Hierarchical**: Supports defaults and overrides
- **Multi-architecture**: Separate configs for x64/arm64

### Cross-platform Considerations
- **Native Windows**: All components target Windows specifically
- **Architecture support**: Both x64 and arm64
- **Signing required**: All binaries must be signed for deployment

## Contributing Guidelines

### Code Style
- **Go**: Follow standard Go conventions (`gofmt`, `golint`)
- **C#**: Follow Microsoft C# conventions
- **PowerShell**: Use approved verbs, proper error handling

### Branch Strategy
- **main**: Stable release branch
- **dev**: Development branch (current working branch)
- **feature/***: Feature development branches

### Pull Request Process
1. Work on feature branch
2. Ensure all tests pass
3. Build with signing succeeds
4. Update documentation if needed
5. Submit PR to `dev` branch

### Testing Requirements
- Build must complete successfully with signing
- All binaries must be tested with `--checkonly` flag
- C# components must build for both x64 and arm64
- MSI packages must install cleanly

## Advanced Topics

### Bootstrap System
Cimian includes a zero-touch deployment system:
- **Bootstrap mode**: Automated software deployment
- **CimianWatcher service**: Monitors and triggers installations
- **Flag file**: `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`

### Package Management
- **Multiple formats**: MSI, MSIX, EXE, NuGet
- **Metadata**: YAML-based package descriptions
- **Catalogs**: Organized software collections
- **Conditional deployment**: Based on system facts

### Signing and Security
- **Enterprise certificates**: Required for all binaries
- **MSI signing**: Separate signing step for MSI packages
- **NuGet signing**: Repository-level package signing
- **Timestamp servers**: Multiple TSA servers for reliability

## Troubleshooting

### Common Build Issues
1. **Unsigned binary errors**: Always use `-Sign` parameter
2. **File locking**: Restart PowerShell session or stop conflicting services
3. **Certificate issues**: Check enterprise certificate in personal store
4. **Go module issues**: Run `go mod tidy` and `go mod download`

### Testing Issues
1. **Permission errors**: Use `sudo` for administrative operations
2. **Service conflicts**: Stop CimianWatcher service before testing
3. **File locks**: Use development mode or restart PowerShell session

### Debugging Tips
- Use verbose flags (`-v`, `--debug`) for detailed output
- Check Windows Event Viewer for service-related issues
- Examine build logs for compilation problems
- Use `--checkonly` to avoid side effects during testing

## Additional Resources

### Documentation
- `/docs/` - Comprehensive documentation library
- `README.md` - Project overview and basic usage
- Individual component READMEs in respective directories

### Configuration
- `config.yaml` - Main configuration template
- `build/msi/config.yaml` - MSI build configuration
- Architecture-specific nuspec files in `build/nupkg/`

This development guide should get you started with Cimian development. Remember: **always sign your binaries** and use safe testing practices!
````