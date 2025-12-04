# Cimian Go to C# Migration Plan

> **STATUS: ✅ SUBSTANTIALLY COMPLETE (December 2024)**  
> Migration is ~90% complete. All 10 CLI tools migrated with 586 tests passing.
> See `MIGRATION_CHECKLIST.md` for detailed current status.

This document outlines the comprehensive strategy for migrating Cimian from Go to C# while preserving all refined logic and functionality. The migration is designed to be surgical and systematic to ensure no functionality is lost during the transition.

## Executive Summary

Cimian currently consists of approximately **1,200+ commits** of refined Go code across **10 command-line tools**, **25+ package modules**, and sophisticated systems for conditional manifest evaluation, installer handling, download management, and enterprise deployment. The migration to C# will create a truly native Windows solution while maintaining 100% feature parity.

## Migration Progress (December 2024)

| Phase | Status | Details |
|-------|--------|---------|
| Data Models | ✅ Complete | All YAML/JSON models ported |
| Core Engine | ✅ Complete | Predicates, Installers, Downloads |
| CLI Tools | ✅ Complete | 10/10 tools migrated |
| Testing | ✅ Complete | 586 tests passing |
| GUI | 🔄 Pending | WPF app needs integration |
| Service | 🔄 Pending | Windows service wrapper |

## Current Architecture Analysis

### Core Binaries (10 executables)
1. **managedsoftwareupdate.exe** - Primary client agent
2. **cimiimport.exe** - Package import and metadata generator  
3. **cimipkg.exe** - NuGet package creator
4. **makecatalogs.exe** - Software catalog generator
5. **makepkginfo.exe** - Package info generator
6. **cimitrigger.exe** - Deployment trigger tool
7. **cimiwatcher.exe** - Bootstrap monitoring service
8. **cimistatus.exe** - WPF GUI status application (already C#)
9. **manifestutil.exe** - Manifest management tool
10. **repoclean.exe** - Repository cleanup utility

### Core Package Modules (25+ packages)
- **pkg/auth/** - Authentication and authorization
- **pkg/blocking/** - Installation blocking/dependencies
- **pkg/catalog/** - Catalog processing and validation
- **pkg/config/** - YAML configuration management
- **pkg/download/** - Network download with retry/timeout logic
- **pkg/extract/** - Archive extraction utilities
- **pkg/filter/** - Package filtering logic
- **pkg/installer/** - Multi-format installer support
- **pkg/logging/** - Structured logging system
- **pkg/manifest/** - Manifest processing and conditional evaluation
- **pkg/pkginfo/** - Package metadata handling
- **pkg/predicates/** - Conditional items evaluation engine
- **pkg/process/** - Process management utilities
- **pkg/progress/** - Progress reporting interfaces
- **pkg/reporter/** - Status reporting system
- **pkg/reporting/** - Analytics and telemetry
- **pkg/retry/** - Exponential backoff retry logic
- **pkg/rollback/** - Installation rollback mechanisms
- **pkg/scripts/** - PowerShell script execution
- **pkg/selfservice/** - Self-service functionality
- **pkg/selfupdate/** - Self-update mechanisms
- **pkg/status/** - Status tracking and management
- **pkg/usage/** - Usage analytics
- **pkg/utils/** - Common utilities
- **pkg/version/** - Version management

## Critical Systems Analysis

### 1. Conditional Items Evaluation Engine
**Complexity**: High - NSPredicate-style evaluation with complex expression parsing

**Current Implementation**:
- Complex OR/AND expression parsing in single condition strings
- Nested conditional items for hierarchical logic
- Support for 15+ operators (CONTAINS, BEGINSWITH, IN, etc.)
- System facts collection (hostname, arch, domain, enrollment data)
- Version-aware comparisons
- Legacy format compatibility

**Migration Considerations**:
- Expression parser must handle parentheses, operator precedence
- Nested evaluation logic with recursive processing
- Performance optimization for large manifest files
- Extensive test coverage for edge cases

### 2. Multi-Format Installer Support
**Complexity**: High - Support for MSI, EXE, PowerShell, NuGet, MSIX

**Current Implementation**:
- Smart installer flag processing with human-friendly syntax
- Timeout-aware command execution (dynamic timeouts based on file size)
- Windows elevation inheritance through PowerShell wrappers
- Exit code interpretation and error mapping
- Progress reporting integration
- PowerShell script integration (pre/post install/uninstall)

**Migration Considerations**:
- Process management with proper elevation
- Timeout handling for large installers
- Output capture and error interpretation
- Script execution with security considerations

### 3. Download Management System
**Complexity**: High - Robust networking with retry logic

**Current Implementation**:
- Exponential backoff retry with non-retryable error detection
- Concurrent downloads with configurable limits
- Hash validation with automatic re-download on corruption
- Dynamic timeout calculation based on file size
- Atomic file operations with temp file handling
- HTTP client configuration with connection pooling
- Cloud storage integration (AWS S3, Azure Blob)

**Migration Considerations**:
- HTTP client configuration and connection management
- Async/await patterns for concurrent downloads
- File I/O with atomic operations
- Progress reporting callbacks
- Error classification for retry logic

### 4. Package Metadata Processing
**Complexity**: Medium-High - MSI property extraction, version parsing

**Current Implementation**:
- MSI property extraction using Windows Installer APIs
- Automatic version detection from multiple sources
- File hash calculation and validation
- Dependency analysis and tracking
- Install/uninstall script integration
- Multiple installer type support

**Migration Considerations**:
- Windows Installer API interop
- File metadata extraction
- Version comparison logic
- Script validation and execution

### 5. Logging and Telemetry System
**Complexity**: Medium - Structured logging with session management

**Current Implementation**:
- Session-based logging with timestamped directories
- JSON and human-readable log formats
- Event tracking with context
- Log rotation and cleanup
- Performance metrics collection
- Error aggregation and reporting

**Migration Considerations**:
- Structured logging framework (Serilog recommended)
- Session management
- Log file management and rotation
- Performance counter integration

## Migration Strategy

### Phase 1: Foundation and Infrastructure (Weeks 1-2)
**Goal**: Establish C# project structure and core infrastructure

#### 1.1 Project Structure Setup
```
src/
├── Cimian.Core/                    # Core shared libraries
│   ├── Configuration/              # YAML config management  
│   ├── Logging/                    # Structured logging
│   ├── Models/                     # Data models (Catalog, Manifest, etc.)
│   ├── Utilities/                  # Common utilities
│   └── Services/                   # Shared service interfaces
├── Cimian.Infrastructure/          # Infrastructure implementations
│   ├── Download/                   # HTTP download services
│   ├── FileSystem/                 # File operations
│   ├── Installers/                 # Installer management
│   ├── Networking/                 # Network utilities
│   └── Cloud/                      # AWS/Azure integrations
├── Cimian.Engine/                  # Core business logic
│   ├── Predicates/                 # Conditional evaluation engine
│   ├── Manifest/                   # Manifest processing
│   ├── Catalog/                    # Catalog management
│   ├── Package/                    # Package management
│   └── Deployment/                 # Deployment orchestration
├── Cimian.CLI.managedsoftwareupdate/  # Primary client
├── Cimian.CLI.cimiimport/          # Package import tool
├── Cimian.CLI.cimipkg/             # NuGet creator
├── Cimian.CLI.makecatalogs/        # Catalog generator
├── Cimian.CLI.makepkginfo/         # PkgInfo generator
├── Cimian.CLI.cimitrigger/         # Trigger tool
├── Cimian.CLI.manifestutil/        # Manifest utility
├── Cimian.CLI.repoclean/           # Repository cleaner
├── Cimian.Service.CimianWatcher/   # Windows Service
├── Cimian.GUI.CimianStatus/        # WPF Status GUI (existing)
└── Cimian.Tests/                   # Comprehensive test suite
    ├── Unit/                       # Unit tests
    ├── Integration/                # Integration tests
    └── TestData/                   # Test manifests/catalogs
```

#### 1.2 Core Dependencies Selection
- **Configuration**: Microsoft.Extensions.Configuration with YamlDotNet (9.0.0+)
- **Logging**: Serilog with structured logging (4.0.0+)
- **HTTP**: HttpClient with Polly for retry logic (8.0.0+)
- **CLI**: CommandLineParser or System.CommandLine (2.9.0+)
- **DI Container**: Microsoft.Extensions.DependencyInjection (9.0.0+)
- **JSON/YAML**: System.Text.Json + YamlDotNet (16.0.0+)
- **Testing**: xUnit + Moq + FluentAssertions (latest)

#### 1.3 Data Models Migration
Migrate all Go structs to C# classes:
- `catalog.Item` → `Cimian.Core.Models.CatalogItem`
- `manifest.Manifest` → `Cimian.Core.Models.Manifest` 
- `config.Configuration` → `Cimian.Core.Models.Configuration`
- Conditional item structures with proper inheritance

### Phase 2: Core Engine Migration (Weeks 3-5)
**Goal**: Migrate critical business logic engines

#### 2.1 Conditional Items Evaluation Engine
**Priority**: Critical - This is the most complex component

**Go Source**: `pkg/predicates/predicates.go`
**C# Target**: `Cimian.Engine.Predicates`

**Implementation Steps**:
1. **Expression Parser**: Create recursive descent parser for complex expressions
2. **Fact Collection**: System facts gathering with Windows APIs
3. **Evaluation Engine**: Recursive conditional item evaluation
4. **Operator Support**: All 15+ operators with version-aware comparisons
5. **Performance Optimization**: Caching and lazy evaluation

**Code Structure**:
```csharp
public interface IPredicateEngine
{
    Task<bool> EvaluateAsync(ConditionalItem item, SystemFacts facts);
    Task<ConditionalEvaluationResult> EvaluateManifestAsync(Manifest manifest);
}

public class ExpressionParser
{
    public ParsedExpression Parse(string expression);
}

public class SystemFactsCollector 
{
    public Task<SystemFacts> CollectAsync();
}
```

#### 2.2 Package Management Engine
**Go Source**: `pkg/pkginfo/`, `pkg/catalog/`
**C# Target**: `Cimian.Engine.Package`

**Implementation Steps**:
1. **Metadata Extraction**: MSI properties, file versions, hashes
2. **Catalog Processing**: YAML parsing and validation
3. **Dependency Resolution**: Package dependency tracking
4. **Version Comparison**: Semantic version handling

#### 2.3 Installer Management Engine  
**Go Source**: `pkg/installer/installer.go`
**C# Target**: `Cimian.Engine.Installers`

**Implementation Steps**:
1. **Process Management**: Elevated process execution
2. **Timeout Handling**: Dynamic timeouts for large files
3. **Output Capture**: Real-time output processing
4. **Error Handling**: Exit code interpretation and retry logic
5. **Script Integration**: PowerShell execution with security

### Phase 3: Network and Download Infrastructure (Weeks 6-7)
**Goal**: Robust networking with enterprise features

#### 3.1 Download Engine Migration
**Go Source**: `pkg/download/download.go`
**C# Target**: `Cimian.Infrastructure.Download`

**Implementation Steps**:
1. **HTTP Client Configuration**: Connection pooling, timeouts
2. **Retry Logic**: Exponential backoff with Polly
3. **Concurrent Downloads**: Async/await with semaphore limiting
4. **Hash Validation**: SHA256 verification with re-download
5. **Atomic Operations**: Temp file handling and atomic moves
6. **Progress Reporting**: Real-time progress callbacks

**Key Code Structure**:
```csharp
public interface IDownloadService
{
    Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken);
    Task<Dictionary<string, string>> DownloadBatchAsync(Dictionary<string, string> items);
}

public class DownloadService : IDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly IRetryPolicy _retryPolicy;
    private readonly SemaphoreSlim _downloadSemaphore;
}
```

#### 3.2 Cloud Storage Integration
**Go Source**: AWS/Azure CLI wrappers in cimiimport
**C# Target**: `Cimian.Infrastructure.Cloud`

**Implementation Steps**:
1. **AWS S3 SDK Integration**: Direct SDK usage instead of CLI
2. **Azure Blob SDK Integration**: Direct SDK usage instead of CLI  
3. **Upload Management**: Progress tracking and error handling
4. **Authentication**: Service principal and managed identity support

### Phase 4: Command-Line Tools Migration (Weeks 8-12)
**Goal**: Migrate all CLI tools with identical functionality

#### 4.1 ManagedsoftwareUpdate (Weeks 8-9)
**Priority**: Critical - Primary client component
**Go Source**: `cmd/managedsoftwareupdate/main.go`
**C# Target**: `Cimian.CLI.managedsoftwareupdate`

**Implementation Steps**:
1. **Command-line Parsing**: All existing flags and arguments
2. **Bootstrap Mode**: Service integration and GUI launching
3. **Installation Engine**: Complete installer pipeline
4. **Self-update Logic**: MSI detection and upgrade handling
5. **Logging Integration**: Session-based logging
6. **Progress Reporting**: GUI and console progress

#### 4.2 CimiImport (Week 10)
**Go Source**: `cmd/cimiimport/main.go`
**C# Target**: `Cimian.CLI.cimiimport`

**Implementation Steps**:
1. **Interactive Configuration**: User prompts and validation
2. **Package Analysis**: Metadata extraction and validation
3. **Cloud Upload**: S3/Azure integration
4. **Git Integration**: Repository management
5. **Script Processing**: PowerShell script validation

#### 4.3 CimiPkg (Week 10)
**Go Source**: `cmd/cimipkg/`
**C# Target**: `Cimian.CLI.cimipkg`

**Implementation Steps**:
1. **NuGet Package Creation**: .nupkg generation
2. **Chocolatey Compatibility**: Chocolatey script generation
3. **Script Signing**: PowerShell script signing
4. **Intune Integration**: .intunewin package creation

#### 4.4 MakeCatalogs (Week 11)
**Go Source**: `cmd/makecatalogs/main.go`
**C# Target**: `Cimian.CLI.makecatalogs`

#### 4.5 Remaining Tools (Week 12)
- MakePkgInfo
- CimiTrigger  
- ManifestUtil
- RepoClean

### Phase 5: Service and GUI Integration (Weeks 13-14)
**Goal**: Complete Windows service and GUI integration

#### 5.1 CimianWatcher Service Migration
**Go Source**: `cmd/cimiwatcher/main.go`
**C# Target**: `Cimian.Service.CimianWatcher`

**Implementation Steps**:
1. **Windows Service Framework**: .NET service host
2. **File Monitoring**: FileSystemWatcher for trigger files
3. **Process Management**: Elevated process launching
4. **Service Recovery**: Automatic restart and error handling
5. **Event Logging**: Windows Event Log integration

#### 5.2 GUI Integration Enhancement
**Current**: WPF application already in C#
**Enhancements**: Deeper integration with new C# backend

### Phase 6: Testing and Validation (Weeks 15-16)
**Goal**: Comprehensive testing and validation

#### 6.1 Test Suite Development
1. **Unit Tests**: All core logic components
2. **Integration Tests**: End-to-end workflow testing
3. **Performance Tests**: Download and installation performance
4. **Compatibility Tests**: Existing manifests and catalogs
5. **Regression Tests**: All documented functionality

#### 6.2 Validation Scenarios
1. **Bootstrap Deployment**: Zero-touch system provisioning
2. **Conditional Manifests**: Complex conditional logic scenarios
3. **Large File Downloads**: Timeout and retry validation
4. **Multi-format Installers**: MSI, EXE, PowerShell, NuGet
5. **Enterprise Integration**: Intune and SCCM compatibility

## Risk Mitigation Strategies

### 1. Feature Parity Validation
- **Automated Testing**: Comprehensive test suite comparing Go vs C# output
- **Side-by-side Deployment**: Run both versions in parallel during migration
- **Checksum Validation**: Ensure identical catalog and manifest processing
- **Performance Benchmarking**: Validate performance characteristics

### 2. Complex Logic Preservation
- **Line-by-line Analysis**: Detailed review of critical algorithms
- **Test-driven Migration**: Write tests first, then implement
- **Incremental Validation**: Validate each component before proceeding
- **Expert Review**: Code review by original developers

### 3. Enterprise Deployment Continuity
- **Backward Compatibility**: Maintain existing file formats and APIs
- **Gradual Migration**: Optional C# components with Go fallback
- **Documentation Updates**: Comprehensive migration documentation
- **Training Materials**: Updated deployment guides

## Implementation Timeline

| Phase | Duration | Deliverable | Risk Level |
|-------|----------|-------------|------------|
| 1 | 2 weeks | Project structure, core models | Low |
| 2 | 3 weeks | Conditional engine, package management | High |
| 3 | 2 weeks | Download infrastructure, cloud integration | Medium |
| 4 | 5 weeks | All CLI tools | Medium |
| 5 | 2 weeks | Service and GUI integration | Medium |
| 6 | 2 weeks | Testing and validation | Low |

**Total Duration**: 16 weeks (4 months)

## Success Criteria

### Functional Requirements
- [ ] 100% feature parity with existing Go implementation
- [ ] All 10 CLI tools migrated with identical command-line interfaces
- [ ] Complex conditional manifests process identically
- [ ] All installer types supported (MSI, EXE, PowerShell, NuGet, MSIX)
- [ ] Backward compatibility with existing manifests and catalogs
- [ ] Enterprise deployment scenarios work without changes

### Performance Requirements
- [ ] Download performance within 5% of Go implementation
- [ ] Installation processing time within 10% of Go implementation
- [ ] Memory usage reasonable for enterprise deployment
- [ ] Startup time optimized for frequent execution

### Quality Requirements
- [ ] Comprehensive test coverage (>80% code coverage)
- [ ] Zero critical security vulnerabilities
- [ ] Full Windows Event Log integration
- [ ] Proper error handling and recovery
- [ ] Performance monitoring and telemetry

## Post-Migration Benefits

### Developer Experience
- **Native Windows Development**: First-class Windows API integration
- **Modern Tooling**: Visual Studio, IntelliSense, debugging
- **Package Management**: NuGet ecosystem integration
- **Team Productivity**: C# expertise within Windows admin teams

### Runtime Benefits
- **Memory Management**: Garbage collection vs manual memory management
- **Exception Handling**: Structured exception handling
- **Async Programming**: First-class async/await support
- **Windows Integration**: Native Windows service development

### Enterprise Benefits
- **Security**: Code signing and Windows security integration
- **Monitoring**: Performance counters and ETW integration
- **Deployment**: ClickOnce and MSI installer improvements
- **Maintenance**: Single-language codebase maintenance

## Conclusion

This migration plan provides a systematic approach to converting 1,200+ commits of refined Go code to C# while maintaining 100% functionality. The phased approach minimizes risk while ensuring all critical systems are properly migrated and validated.

The key to success is the detailed analysis of each component, comprehensive testing at each phase, and maintaining backward compatibility throughout the migration process. With proper execution, this migration will result in a truly native Windows solution that leverages the full .NET ecosystem while preserving all the sophisticated logic that makes Cimian effective for enterprise software deployment.
