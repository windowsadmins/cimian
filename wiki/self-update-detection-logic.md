# Cimian Self-Update Package Detection - Updated Logic

## Overview

The self-update detection has been refined to only trigger for the **main Cimian installation packages**, not supporting tools or components.

## Updated Detection Logic

### Main Cimian Packages (Triggers Self-Update)
✅ **Exact matches:**
- `cimian`
- `cimiantools`

✅ **Exact matches with suffixes:**
- `cimian-msi`
- `cimian-nupkg` 
- `cimiantools-msi`
- `cimiantools-nupkg`
- `cimian.msi`
- `cimian.nupkg`

✅ **Installer location patterns:**
- `/cimian-*.msi`
- `/cimian-*.nupkg`
- `/cimiantools-*.msi`
- `/cimiantools-*.nupkg`
- `/cimian.msi`
- `/cimian.nupkg`

### Supporting Tools (Excluded from Self-Update)
❌ **Explicitly excluded prefixes:**
- `cimianpreflight`
- `cimianauth`
- `cimianbrowser`
- `cimianhelper`
- `cimianconfig`
- `cimianreport`
- `cimianlog`

## Testing Results

### Test Environment
- **System**: ARM64 Windows machine
- **Repository**: https://cimian.ecuad.ca/deployment
- **Test Date**: August 18, 2025

### Packages in Repository
| Package Name | Version | Self-Update Triggered? | Reason |
|-------------|---------|----------------------|---------|
| `Cimian` | 25.8.18 | ❌ No | Architecture mismatch (would trigger on x64) |
| `CimianPreflight` | 2025.8.12 | ❌ No | ✅ Correctly excluded (supporting tool) |
| `CimianAuth` | 2025.8.12 | ❌ No | ✅ Correctly excluded (supporting tool) |
| `OneDrivePrefs` | 1.0.0 | ❌ No | ✅ Correctly excluded (not Cimian package) |

### Verification Commands
```bash
# Test refined self-update detection
sudo managedsoftwareupdate.exe -vv --checkonly

# Check self-update status
sudo managedsoftwareupdate.exe --selfupdate-status

# Expected output: "No self-update pending" (unless main Cimian package available)
```

## Expected Behavior

### When Main Cimian Package Available
```bash
# If repository contains updated "Cimian" or "CimianTools" package:
sudo managedsoftwareupdate.exe -vv

# Expected log messages:
[INFO] Detected Cimian self-update package (exact match) item: Cimian
[INFO] Scheduling Cimian self-update for next service restart item: Cimian version: X.Y.Z
[SUCCESS] Self-update scheduled successfully. Cimian will update on next service restart.
```

### When Only Supporting Tools Available
```bash
# If repository contains only CimianPreflight, CimianAuth, etc.:
sudo managedsoftwareupdate.exe -vv

# Expected behavior:
- Normal installation/update processing
- NO self-update scheduling
- NO special self-update treatment
```

## Architecture Considerations

The main `Cimian` package in the repository is currently:
- **Architecture**: x64 only
- **Current Version**: 25.8.18
- **Registry Match**: Exact match found

On ARM64 systems, the package is correctly detected but skipped due to architecture mismatch. This is expected behavior - self-update would trigger on x64 systems where the package is compatible.

## Benefits of Refined Logic

1. **Precise Targeting**: Only main Cimian packages trigger self-update
2. **Reduced False Positives**: Supporting tools no longer trigger unnecessary self-updates
3. **Clear Separation**: Distinction between core platform updates and component updates
4. **Maintainable**: Easy to add new exclusions or main package patterns
5. **Safe**: Conservative approach prevents accidental self-update triggers

## Summary

The self-update system now correctly identifies when the **core Cimian platform** needs updating while allowing normal update processing for supporting tools and components. This provides the precise behavior requested: self-update treatment only for `Cimian` or `CimianTools` packages.
