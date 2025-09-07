# RepoClean

A C# implementation of the Munki `repoclean` tool for cleaning up repositories by removing older, unused software items.

## Overview

RepoClean is a command-line tool that analyzes your repository to identify and optionally remove:

- Older versions of software packages (keeping a configurable number of recent versions)
- Orphaned package files that are not referenced by any pkginfo files
- Unused pkginfo items that are not referenced in any manifests

This helps maintain repository hygiene and saves disk space by removing outdated software packages.

## Features

- **Manifest Analysis**: Examines all manifest files to identify software items that are actively used
- **PkgInfo Analysis**: Analyzes pkginfo files to understand package relationships and dependencies
- **Version Management**: Keeps the most recent N versions of each software package (default: 2)
- **Orphan Detection**: Finds package files that are not referenced by any pkginfo file
- **Safe Deletion**: Preserves packages that are required by manifests or other packages
- **Interactive Mode**: Prompts for confirmation before deletion (unless auto mode is enabled)
- **Dry Run**: Shows what would be deleted without actually performing the deletion

## Installation

Build the project using .NET 9.0:

```powershell
dotnet build --configuration Release
```

## Usage

```powershell
repoclean [OPTIONS] [REPOSITORY_PATH]
```

### Options

- `-r, --repo-url <path>`: Repository URL or path to the repository root
- `-k, --keep <number>`: Keep this many versions of each software package (default: 2)
- `--show-all`: Show all items even if none will be deleted
- `-a, --auto`: Do not prompt for confirmation before deleting items (use with caution)
- `-V, --version`: Print the version and exit
- `--plugin <name>`: Plugin to connect to repo (default: FileRepo)

### Examples

```powershell
# Analyze repository and show what would be deleted (dry run)
repoclean --repo-url "C:\CimianRepo"

# Keep 3 versions of each package instead of the default 2
repoclean --repo-url "C:\CimianRepo" --keep 3

# Show all packages, even those that won't be deleted
repoclean --repo-url "C:\CimianRepo" --show-all

# Automatically delete items without prompting (use with caution)
repoclean --repo-url "C:\CimianRepo" --auto
```

## How It Works

1. **Manifest Analysis**: Scans all manifest files to identify which software packages are actively deployed
2. **PkgInfo Analysis**: Examines pkginfo files to understand package metadata, dependencies, and file locations
3. **Package Analysis**: Identifies orphaned package files that aren't referenced by any pkginfo
4. **Cleanup Planning**: Determines which items can be safely deleted while preserving:
   - Required packages referenced in manifests
   - Dependencies needed by other packages
   - The most recent N versions of each package variant
5. **Safe Deletion**: Removes identified items and rebuilds catalogs

## Safety Features

- **Dependency Preservation**: Never deletes packages that are required by other packages
- **Manifest Protection**: Preserves packages that are referenced in active manifests
- **Version Retention**: Always keeps the most recent versions of software packages
- **Confirmation Prompts**: Requires explicit confirmation before deletion (unless in auto mode)
- **Error Handling**: Gracefully handles errors and reports issues during processing

## Architecture

The tool is built using modern C# patterns:

- **Dependency Injection**: Uses Microsoft.Extensions.DependencyInjection for IoC
- **Logging**: Integrated logging using Microsoft.Extensions.Logging
- **Command Line**: Uses System.CommandLine for robust argument parsing
- **Async/Await**: Fully asynchronous operations for better performance
- **Service Pattern**: Modular services for different analysis tasks

## Differences from Original Munki repoclean

This C# implementation maintains the same core functionality as the original Python Munki repoclean tool but includes some enhancements:

- **Modern Architecture**: Built with dependency injection and service patterns
- **Better Error Handling**: More robust error handling and reporting
- **Cross-Platform**: Runs on Windows, Linux, and macOS with .NET 9.0
- **Performance**: Async operations for better performance on large repositories
- **Extensibility**: Modular design allows for easy extension and customization

## Contributing

This tool is part of the broader Cimian toolset migration from Go to C#. Contributions are welcome to improve functionality, performance, and compatibility.

## License

Licensed under the Apache License, Version 2.0. See the main Cimian project for full license details.
