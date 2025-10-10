# Improved Error Handling for `nopkg` Packages

## Problem Statement

When a `nopkg` package was defined in the catalog with only an `installs` check (no scripts or installer location), Cimian would report a confusing error:

```
[2025-10-10 11:34:35] DEBUG Found item in catalog item: Harmony catalog: Development installer_location:  supported_arch: [x64 arm64]
[2025-10-10 11:34:35] DEBUG Found item in catalog (fallback) item: Harmony catalog: Development installer_location:  supported_arch: [x64 arm64]
[2025-10-10 11:34:35] ERROR Item not found in any catalog item: Harmony source: from managed_installs in manifest 'StudioLab11' catalogs_searched: Development
```

The logs showed that the item **was found** in the catalog, but then reported as "not found in any catalog" - this was extremely confusing for troubleshooting.

### Example Problematic Package

```yaml
name: Harmony
display_name: Harmony 24
version: 24.0.1.23019
description: Protective package for existing Harmony 2024 installations (not managed by Cimian)
category: Animation
developer: Toon Boom Animation
installs:
  - type: 'file'
    path: 'C:\Program Files (x86)\Toon Boom Animation\Toon Boom Harmony 24 Premium\win64\bin\HarmonyPremium.exe'
    md5checksum: 'd2636e92296c8372f26a311f196e53c1'
unattended_install: false
unattended_uninstall: false
uninstallable: false
installer:
  type: nopkg  # No installer file - script-only package type
```

## Root Cause

The catalog lookup logic in `pkg/process/process.go` validated items based on:

1. **validInstallItem**: Has `installer.type` and `installer.location`
2. **validUninstallItem**: Has uninstallers defined
3. **validScriptOnlyItem**: Has scripts (preinstall_script, postinstall_script, or installcheck_script)

However, it did NOT recognize `nopkg` packages with only `installs` checks as valid. These are **protective packages** - they don't install anything new, but prevent Cimian from removing existing installations by defining what files should exist.

## Solution

### 1. Added Support for `nopkg` Packages with `installs` Checks

Added a new validation category:

```go
// nopkg packages with installs checks are valid (protective packages)
validNopkgWithInstalls := (item.Installer.Type == "nopkg" && len(item.Installs) > 0)
```

Now items are considered valid if they match ANY of these criteria:
- Has installer type and location (normal packages)
- Has uninstallers defined
- Has installation scripts
- **NEW**: Is a `nopkg` package with `installs` checks defined

### 2. Improved Error Messages

When an item is found but lacks a valid installation mechanism, the system now:

1. **Tracks invalid items separately** in `foundButInvalidItems` array
2. **Logs detailed diagnostics** about what's missing:
   ```
   [WARN] Item found but has no installation mechanism
     item: PackageName
     catalog: Development  
     installer_type: nopkg
     has_location: false
     has_uninstallers: false
     has_scripts: false
     has_installs_checks: false
     issue: nopkg package must have either scripts or installs checks defined
   ```

3. **Provides clear error message** to users:
   ```
   [ERROR] Item found but cannot be installed - missing installation mechanism
     item: PackageName
     source: from managed_installs in manifest 'ManifestName'
     catalogs_searched: Development
     reasons: installer type is 'nopkg' (no package), no installer location, 
              no 'installs' checks defined, no installation scripts
   
   Error: item PackageName found in catalog but cannot be installed: [reasons]. 
   A 'nopkg' package must have either scripts (preinstall_script/postinstall_script/
   installcheck_script) or 'installs' checks defined
   ```

## Benefits

1. **Protective packages now work correctly** - `nopkg` packages with `installs` checks are recognized as valid
2. **Clear error messages** - No more "not found" when item was actually found
3. **Actionable diagnostics** - Detailed logging shows exactly what's missing from misconfigured packages
4. **Better troubleshooting** - Distinguishes between "truly not found" vs "found but invalid"

## Valid `nopkg` Package Patterns

After this fix, these patterns are all valid:

### 1. Protective Package (installs checks only)
```yaml
installer:
  type: nopkg
installs:
  - type: file
    path: 'C:\Program Files\App\app.exe'
```

### 2. Script-Only Package
```yaml
installer:
  type: nopkg
preinstall_script: |
  Write-Host "Running setup"
```

### 3. Install Check Script Package
```yaml
installer:
  type: nopkg
installcheck_script: |
  Test-Path "C:\App\app.exe"
```

## Invalid `nopkg` Package (Will Now Show Clear Error)

```yaml
installer:
  type: nopkg
  # MISSING: No location, no scripts, no installs checks!
```

Error message will clearly explain:
```
Item found but cannot be installed: installer type is 'nopkg' (no package), 
no installer location, no 'installs' checks defined, no installation scripts. 
A 'nopkg' package must have either scripts or 'installs' checks defined.
```

## Files Modified

- `pkg/process/process.go`:
  - Added `validNopkgWithInstalls` validation check
  - Added `foundButInvalidItems` tracking array
  - Enhanced error handling to distinguish between "not found" and "found but invalid"
  - Added detailed diagnostic logging for misconfigured packages

## Testing

To test with the example Harmony package:
1. Package will now be recognized as valid
2. `installs` checks will be evaluated during status determination
3. No installation attempt will be made (nopkg behavior)
4. Package will be marked as installed if the file exists with matching checksum

For misconfigured `nopkg` packages with no installation mechanism, you'll see clear errors explaining exactly what's missing.
