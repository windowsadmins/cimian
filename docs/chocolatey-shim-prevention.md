# Chocolatey Shim Prevention Solution

## Problem
When installing CimianTools via Chocolatey (.nupkg), Chocolatey automatically creates shims in `C:\ProgramData\chocolatey\bin\` for all `.exe` files in the package. These shims interfere with local development and can cause "Access is denied" errors when they try to execute the target binaries.

## Root Cause
Chocolatey's default behavior is to create shims for any executable files it finds in a package, regardless of whether the package's install script handles deployment differently.

## Solution: Use .ignore Files
By creating `.exe.ignore` files for each executable, we tell Chocolatey to skip creating automatic shims for those executables. Our custom `chocolateyInstall.ps1` script handles the proper installation to `C:\Program Files\Cimian\` and PATH management.

## Files Created/Modified

### 1. .ignore Files (NEW)
- `cimiimport.exe.ignore`
- `cimipkg.exe.ignore` 
- `cimistatus.exe.ignore`
- `cimitrigger.exe.ignore`
- `cimiwatcher.exe.ignore`
- `makecatalogs.exe.ignore` ✓ (NEW)
- `makepkginfo.exe.ignore` ✓ (NEW)
- `managedsoftwareupdate.exe.ignore` ✓ (NEW)
- `manifestutil.exe.ignore` ✓ (NEW)

### 2. Updated chocolateyInstall.ps1
- Added comments explaining the shim prevention strategy
- Improved executable discovery logic
- More robust error handling

### 3. Updated .nuspec files
Both `nupkg.x64.nuspec` and `nupkg.arm64.nuspec` now include:
- `<file src="nupkg\scripts\*.exe.ignore" target="tools" />`
- Proper version placeholder: `{{VERSION}}`

## How It Works

1. **Package Creation**: When building the .nupkg, all `.exe.ignore` files are included
2. **Installation**: Chocolatey sees the `.ignore` files and skips creating shims for those executables
3. **Custom Install**: Our `chocolateyInstall.ps1` script copies executables to `C:\Program Files\Cimian\` and adds it to PATH
4. **Clean Environment**: No conflicting shims in `C:\ProgramData\chocolatey\bin\`

## Benefits

- ✅ Prevents Chocolatey shim conflicts
- ✅ Executables install to proper location (`C:\Program Files\Cimian\`)
- ✅ PATH is properly managed
- ✅ No interference with local development
- ✅ Clean uninstallation process

## Testing

To test the solution:

1. Build new .nupkg packages with the updated configuration
2. Install via Chocolatey
3. Verify no shims are created in `C:\ProgramData\chocolatey\bin\`
4. Verify executables work from `C:\Program Files\Cimian\`
5. Verify executables are in PATH

## Future Prevention

This solution prevents the shim conflict issue in production deployments. The `.ignore` files are now part of the package and will prevent automatic shim creation in all future installations.
