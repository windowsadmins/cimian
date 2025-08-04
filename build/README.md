# Build Directory Structure

This directory contains organized build resources for creating Cimian packages.

## Directory Structure

### `./msi/` - MSI Package Resources
Contains all files related to MSI package creation using WiX:
- `Cimian.wixproj` - Main WiX v6 project file for MSI creation
- `Cimian.wxs` - WiX v6 source file (current/recommended)
- `msi.wxs.backup` - WiX v3 backup source file
- `intunewin.ps1` - Script to convert MSI to Intune package format
- `config.yaml` - Default configuration file included in MSI
- `*.wixobj` - Compiled WiX object files

### `./nupkg/` - NuGet Package Resources
Contains all files related to NuGet package (.nupkg) creation:
- `nupkg.nuspec` - NuGet package specification
- `nupkg.ps1` - Installation script for NuGet package
- `config.yaml` - Default configuration file included in NuGet package

### Root Files
- `config.yaml` - Master configuration file
- `bin/` - Build output directory
- `obj/` - Build intermediate files directory

## Usage

### Building MSI Package
```powershell
# From the msi directory
cd msi
dotnet build Cimian.wixproj -p:ProductVersion=1.0.0 -p:BinDir=path\to\binaries
```

### Building NuGet Package
```powershell
# From the nupkg directory  
cd nupkg
nuget pack nupkg.nuspec -Version 1.0.0
```

## WiX Version Support

The MSI build system uses WiX v6:
- **WiX v6** (current): Uses `msi/Cimian.wixproj` and `msi/Cimian.wxs`

The build script automatically detects the available WiX version and uses the appropriate files.
