# Cimian Project Structure

> **Last Updated:** December 14, 2025  
> **Branch:** `csharp` (100% C# / .NET - No Go code)

## Overview

Cimian is a Windows software deployment and management system, similar to Munki for macOS. The `csharp` branch contains a complete C# implementation with all Go code removed.

## Directory Structure

```
CimianTools/
│
├── apps/                                # GUI Applications
│   ├── ManagedSoftwareCenter/           # End-user self-service app (WPF)
│   │   ├── Cimian.GUI.ManagedSoftwareCenter.csproj
│   │   ├── Models/
│   │   ├── ViewModels/
│   │   ├── Views/
│   │   └── Services/
│   └── CimianStatus/                    # Admin status/progress UI (WPF)
│       └── Cimian.GUI.CimianStatus.csproj
│
├── cli/                                 # Command-Line Tools
│   ├── managedsoftwareupdate/           # Main update client
│   │   └── Cimian.CLI.managedsoftwareupdate.csproj
│   ├── makepkginfo/                     # Package info generator
│   ├── makecatalogs/                    # Catalog builder
│   ├── manifestutil/                    # Manifest editor
│   ├── repoclean/                       # Repository cleanup
│   ├── cimipkg/                         # Package management
│   ├── cimiimport/                      # Package importer
│   ├── cimitrigger/                     # Trigger utility
│   ├── cimiwatcher/                     # Windows service / file watcher
│   └── cimistatus/                      # CLI status tool
│
├── shared/                              # Shared Libraries
│   ├── core/                            # Domain models, interfaces, constants
│   │   ├── Cimian.Core.csproj
│   │   ├── Models/                      # CatalogItem, Manifest, etc.
│   │   ├── Configuration/               # IConfigurationService
│   │   └── Services/                    # ISystemFactsCollector
│   ├── engine/                          # Business logic
│   │   ├── Cimian.Engine.csproj
│   │   ├── Predicates/                  # Conditional item evaluation
│   │   ├── Catalog/                     # Catalog processing
│   │   └── Install/                     # Installation logic
│   └── infrastructure/                  # External concerns
│       ├── Cimian.Infrastructure.csproj
│       ├── Download/                    # HTTP downloads
│       ├── Installers/                  # MSI, EXE, PS1, MSIX
│       ├── FileSystem/                  # File I/O
│       └── Registry/                    # Windows Registry
│
├── tests/                               # Test Projects & Fixtures
│   ├── Cimian.Tests.csproj              # Main test project
│   ├── Cimipkg/                         # cimipkg-specific tests
│   ├── Makepkginfo/                     # makepkginfo-specific tests
│   ├── Managedsoftwareupdate/           # MSU-specific tests
│   ├── Manifestutil/                    # manifestutil-specific tests
│   ├── fixtures/                        # Test data files
│   ├── golden/                          # Golden master test outputs
│   └── comparison/                      # Go vs C# comparison scripts
│
├── docs/                                # Documentation
│   ├── PROJECT_STRUCTURE.md             # This file
│   ├── MIGRATION_CHECKLIST.md           # Migration status
│   ├── CSHARP_MIGRATION_PLAN.md         # Migration strategy
│   └── ...                              # Feature-specific docs
│
├── CimianTools.sln                      # Visual Studio Solution
├── Directory.Build.props                # Shared MSBuild properties
├── README.md                            # Project README
└── LICENSE                              # License file
```

## Solution Organization

The solution file (`CimianTools.sln`) organizes projects into folders:

```
Solution
├── 📁 shared
│   ├── Cimian.Core
│   ├── Cimian.Engine
│   └── Cimian.Infrastructure
├── 📁 cli
│   ├── Cimian.CLI.managedsoftwareupdate
│   ├── Cimian.CLI.makepkginfo
│   └── ... (10 total)
├── 📁 apps
│   ├── Cimian.GUI.ManagedSoftwareCenter
│   └── Cimian.GUI.CimianStatus
└── 📁 tests
    └── Cimian.Tests
```

## Namespaces

| Project | Namespace |
|---------|-----------|
| shared/core | `Cimian.Core` |
| shared/engine | `Cimian.Engine` |
| shared/infrastructure | `Cimian.Infrastructure` |
| cli/* | `Cimian.CLI.<ToolName>` |
| apps/ManagedSoftwareCenter | `Cimian.GUI.ManagedSoftwareCenter` |
| tests | `Cimian.Tests` |

## Output Executables

| Project | Output |
|---------|--------|
| cli/managedsoftwareupdate | `managedsoftwareupdate.exe` |
| cli/makepkginfo | `makepkginfo.exe` |
| cli/makecatalogs | `makecatalogs.exe` |
| cli/manifestutil | `manifestutil.exe` |
| cli/repoclean | `repoclean.exe` |
| cli/cimipkg | `cimipkg.exe` |
| cli/cimiimport | `cimiimport.exe` |
| cli/cimitrigger | `cimitrigger.exe` |
| cli/cimiwatcher | `cimiwatcher.exe` |
| cli/cimistatus | `cimistatus.exe` |
| apps/ManagedSoftwareCenter | `ManagedSoftwareCenter.exe` |
| apps/CimianStatus | `CimianStatus.exe` |

## Build Commands

```powershell
# Build entire solution
dotnet build CimianTools.sln

# Build specific project
dotnet build apps/ManagedSoftwareCenter/Cimian.GUI.ManagedSoftwareCenter.csproj

# Run tests
dotnet test tests/Cimian.Tests.csproj

# Publish release build
dotnet publish cli/managedsoftwareupdate/Cimian.CLI.managedsoftwareupdate.csproj -c Release -r win-x64 --self-contained
```

## Key Dependencies

- **.NET 8.0 / 10.0** - Target framework
- **WPF** - GUI framework for apps
- **System.CommandLine** - CLI argument parsing
- **YamlDotNet** - YAML serialization
- **xUnit** - Unit testing
- **Moq** - Mocking framework
- **FluentAssertions** - Test assertions

## Migration Status

✅ **Complete** - All Go code has been removed from this branch. The codebase is 100% C# / .NET.

See `MIGRATION_CHECKLIST.md` for detailed migration status and verification results.
