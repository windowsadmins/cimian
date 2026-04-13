# RepoClean Tool Documentation

## Overview
Successfully ported the Munki `repoclean` tool to C# for the Cimian project. This represents a key milestone in the migration from Go to C# for the entire Cimian codebase. The tool maintains full compatibility with Munki repository structures while adding modern .NET capabilities.

## Project Structure
```
cli/repoclean/
├── Cimian.CLI.repoclean.csproj   # Project file
├── Program.cs                    # Main entry point with command-line parsing
├── Models/                       # Data models and options classes
└── Services/
    ├── Interfaces.cs             # Service interfaces
    ├── RepositoryCleaner.cs      # Main cleanup orchestrator
    ├── ManifestAnalyzer.cs       # Manifest file analysis
    ├── PkgInfoAnalyzer.cs        # Package info analysis with YAML support
    ├── PackageAnalyzer.cs        # Package file analysis
    └── FileRepository.cs         # File system operations
```

## Features Implemented

### Core Functionality
- **Manifest Analysis**: Scans manifest files to identify actively used software packages
- **PkgInfo Analysis**: Examines package metadata with robust YAML parsing for Cimian format
- **Package Discovery**: Finds orphaned packages not referenced by any pkginfo files
- **Version Management**: Groups packages by name and identifies older versions for cleanup
- **Safe Cleanup**: Preserves required packages while removing older versions
- **Munki Compatibility**: Maintains full compatibility with Munki repository behavior

### Command Line Interface (Munki-Compatible)
- `--repo-url/-r`: Repository path specification
- `--keep/-k`: Number of versions to retain (default: 2)
- `--show-all`: Display all items including those not marked for deletion
- `--remove`: Actually perform deletions (default is dry-run mode)
- `--auto/-a`: Automatic deletion without prompts when using --remove
- `--version/-v`: Version information
- `--help/-h`: Usage help

### YAML Parsing Enhancements
- **Cimian Format Support**: Handles nested installer: {location:} structure
- **Robust Parsing**: Multiple fallback strategies for complex YAML files
- **Error Resilience**: Continues processing even when individual files fail
- **Path Normalization**: Consistent handling of Windows and Unix path separators

### Architecture Benefits
- **Dependency Injection**: Modern IoC container with Microsoft.Extensions.DependencyInjection
- **Async/Await**: Full asynchronous operations for better performance
- **Logging Integration**: Built-in logging with Microsoft.Extensions.Logging
- **Service Pattern**: Modular, testable service architecture
- **Configuration Support**: JSON-based configuration with Microsoft.Extensions.Configuration

## Technical Implementation

### Key Classes
1. **RepositoryCleaner**: Main orchestrator that coordinates all cleanup operations
2. **ManifestAnalyzer**: Parses manifest files to identify required packages
3. **PkgInfoAnalyzer**: Analyzes package metadata with advanced YAML parsing
4. **PackageAnalyzer**: Identifies orphaned package files
5. **FileRepository**: Abstracted file system operations

### YAML Parsing Strategy
The tool uses a multi-layered approach for parsing YAML files:

1. **Simplified Extraction**: Direct text parsing for basic fields (name, version, installer location)
2. **YamlDotNet Fallback**: Full YAML parsing using YamlDotNet library
3. **Error Handling**: Graceful degradation when files cannot be parsed
4. **Path Normalization**: Consistent path handling across Windows and Unix formats

### Safety Features
- **Default Dry-Run**: Shows what would be deleted without actually deleting (Munki behavior)
- **Explicit Deletion**: Requires --remove flag to actually perform deletions
- **Dependency Preservation**: Never deletes required packages
- **Manifest Protection**: Preserves packages referenced in active manifests
- **Version Retention**: Keeps the most recent N versions
- **Timeout Protection**: 30-second timeout on confirmation prompts
- **Error Logging**: Detailed logging of parsing errors and issues

### Modern C# Features
- **Record Types**: Immutable data structures for package information
- **Nullable Reference Types**: Null safety throughout the codebase
- **Pattern Matching**: Clean conditional logic
- **LINQ**: Efficient data processing and filtering
- **Async Streams**: Handling of large data sets

## How It Works

### Analysis Process
1. **Repository Validation**: Verifies repository structure and accessibility
2. **Manifest Analysis**: Scans all manifest files to identify referenced packages
3. **PkgInfo Processing**: Parses all YAML pkginfo files to extract package metadata
4. **Package Discovery**: Scans pkgs directory to find all package files
5. **Relationship Mapping**: Matches packages to their metadata and identifies orphans
6. **Version Grouping**: Groups packages by name and identifies older versions
7. **Cleanup Planning**: Determines what should be deleted vs preserved

### Output Categories
- **Processed Packages**: Shows packages grouped by name with version information
- **Older Versions**: Marked [to be DELETED] when more than --keep versions exist
- **Orphaned Packages**: Package files with no corresponding pkginfo files
- **Statistics**: Summary of items, space savings, and cleanup recommendations

## Migration Benefits

### From Python Original
- **Performance**: Compiled C# vs interpreted Python
- **Type Safety**: Strong typing vs dynamic typing
- **Tooling**: Rich IDE support and debugging
- **Integration**: Better integration with Windows ecosystems
- **Maintenance**: Easier refactoring and code analysis

### From Go Version
- **Ecosystem**: Rich .NET ecosystem vs limited Go packages
- **Object-Oriented**: Full OOP support vs Go's limited OOP
- **Memory Management**: Automatic garbage collection
- **Cross-Platform**: Runs on Windows, Linux, macOS with .NET 8.0

## Build Integration

The tool is fully integrated with the Cimian build system:

```powershell
# Build and sign the repoclean binary
.\build.ps1 -Sign -Binary repoclean

# Full build with all packages
.\build.ps1 -Sign
```

### Signing Requirements
- **Digital Signing**: All binaries must be signed (cannot run unsigned binaries)
- **Enterprise Certificate**: Uses enterprise code signing certificate
- **Build Process**: Automated signing during build process

## Usage Examples

### Basic Usage (Munki-Compatible)
```powershell
# Show what would be cleaned (default behavior)
repoclean --repo-url "C:\CimianRepo"

# Keep 3 versions instead of default 2
repoclean --repo-url "C:\CimianRepo" --keep 3

# Actually perform cleanup
repoclean --repo-url "C:\CimianRepo" --remove

# Automatic cleanup without prompts
repoclean --repo-url "C:\CimianRepo" --remove --auto

# Show all packages, even those not marked for deletion
repoclean --repo-url "C:\CimianRepo" --show-all
```

### Example Output
```
Using repository: C:\CimianRepo
Analyzing manifest files...
Analyzing pkginfo files...
Analyzing installer items...

name: Cyberduck
[not in any manifests]
versions:
    9.1.7.43306
    9.1.4.43177
    9.1.3.42945 (pkgsinfo\apps\utilities\Cyberduck-x64-9.1.3.42945.yaml) [to be DELETED]

The following packages are not referred to by any pkginfo item:
        mgmt\Cimian-x64-25.2.23.msi
        apps\animation\Houdini-20.5.654.exe
        apps\dev\PowerShell-x64-7.4.6.0.msi

Total pkginfo items:     238
Item variants:           218
pkginfo items to delete: 1
pkgs to delete:          1
pkginfo space savings:   694 bytes
pkg space savings:       57.2 KB

Run with --remove to actually delete these items.
```

## Future Enhancements

### Short Term
1. **Catalog Rebuilding**: Implement equivalent of `makecatalogs`
2. **Plugin System**: Support for different repository backends
3. **Enhanced Configuration**: More comprehensive configuration options
4. **Unit Tests**: Comprehensive test suite

### Long Term
1. **Web Interface**: Optional web-based management UI
2. **API Integration**: REST API for programmatic access
3. **Cloud Support**: Azure/AWS storage backend support
4. **Advanced Reporting**: Detailed cleanup reports and analytics

## Troubleshooting

### Common Issues
1. **YAML Parsing Errors**: Complex YAML files may cause parsing warnings but processing continues
2. **Path Separator Issues**: Tool handles both Windows and Unix path separators automatically
3. **Timeout Issues**: Confirmation prompts have 30-second timeout to prevent hanging
4. **Permission Issues**: Ensure proper file system permissions for repository access

### Debug Information
- **Verbose Logging**: Built-in logging shows detailed processing information
- **Error Messages**: Clear error messages with context for troubleshooting
- **Statistics**: Detailed statistics help verify processing results

## Integration with Cimian

### Project Structure
- Added to existing CimianTools.sln solution
- Follows established project conventions
- Uses .NET 8.0 target framework
- Consistent naming and structure patterns

### Code Signing
- Integrated with Cimian's enterprise code signing process
- Required for all binary execution in the Cimian environment
- Automated through build system

## Conclusion

The RepoClean implementation successfully demonstrates the viability of migrating Cimian tools from Go to C#. The new implementation provides:

1. **Feature Parity**: All core functionality from the original Munki repoclean
2. **Enhanced Compatibility**: Full support for Cimian's YAML-based pkginfo format
3. **Modern Architecture**: Leverages contemporary .NET patterns and practices
4. **Enhanced Safety**: Better error handling and user protection
5. **Improved Performance**: Compiled code with async operations
6. **Better Maintainability**: Clean separation of concerns and testable design
7. **Munki Compatibility**: Maintains exact behavior compatibility with Munki's repoclean

This serves as a template for migrating other Cimian tools to C#, establishing patterns and practices for the broader migration effort. The success of this implementation validates the approach and provides a foundation for future C# migrations within the Cimian ecosystem.
