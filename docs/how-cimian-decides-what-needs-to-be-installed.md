# How Cimian Decides What Needs To Be Installed

## Overview

This document explains how Cimian's `managedsoftwareupdate` tool determines what software packages need to be installed, updated, or removed on a managed Windows system. Understanding this process is crucial for administrators who want to predict and troubleshoot Cimian's behavior.

## Table of Contents

1. [High-Level Process Flow](#high-level-process-flow)
2. [Manifest Processing](#manifest-processing)
3. [Catalog Processing](#catalog-processing)
4. [Version Selection Logic](#version-selection-logic)
5. [Architecture Compatibility](#architecture-compatibility)
6. [Status Checking](#status-checking)
7. [Dependency Resolution](#dependency-resolution)
8. [Decision Tree Examples](#decision-tree-examples)
9. [Troubleshooting](#troubleshooting)

---

## High-Level Process Flow

When `managedsoftwareupdate` runs, it follows this general process:

```
1. Download Manifests → 2. Download Catalogs → 3. Process Items → 4. Make Decisions → 5. Execute Actions
```

### Step 1: Manifest Download
- Downloads the client's specific manifest hierarchy (e.g., `Assigned/Staff/IT/B1115/RodChristiansen.yaml`)
- Processes inherited manifests (parent manifests in the hierarchy)
- Collects all `managed_installs`, `managed_updates`, `managed_uninstalls`, and `optional_installs` items

### Step 2: Catalog Download  
- Downloads all catalogs referenced in manifests
- Parses YAML catalog files containing software package definitions
- Creates both deduplicated and full-version catalog maps

### Step 3: Item Processing
- Deduplicates manifest items (removes duplicate entries)
- Maps manifest items to catalog entries
- Filters by architecture compatibility
- Applies version selection logic

### Step 4: Decision Making
- Determines what needs to be installed, updated, or removed
- Resolves dependencies
- Checks current installation status
- Plans execution order

### Step 5: Action Execution
- Downloads required installers
- Executes installations/updates/removals
- Verifies results
- Updates local state

---

## Manifest Processing

### Manifest Hierarchy

Cimian processes manifests in hierarchical order, from most general to most specific:

```
CoreManifest.yaml
  └─ Assigned.yaml
      └─ Staff.yaml
          └─ IT.yaml
              └─ B1115.yaml
                  └─ RodChristiansen.yaml
```

**Key Principle**: More specific manifests override more general ones.

### Manifest Item Types

Cimian recognizes these manifest arrays:

- **`managed_installs`**: Items that should be installed and kept current
- **`managed_updates`**: Items that should be updated if already installed
- **`managed_uninstalls`**: Items that should be removed if present
- **`optional_installs`**: Items available for self-service installation

### Item Source Tracking

Each item is tagged with its source manifest and type for debugging:
```yaml
# Example manifest item processing
- Name: "Chrome"
  SourceManifest: "IT"
  SourceType: "managed_installs"
```

---

## Catalog Processing

### Catalog Structure

Catalogs are YAML files containing arrays of software package definitions:

```yaml
items:
  - name: Chrome
    version: "139.0.7258.139"
    installer:
      location: "\\mgmt\\Chrome-arm64-139.0.7258.139.msi"
      type: "msi"
    supported_arch: ["x64", "arm64"]
    installs:
      - path: "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"
        md5checksum: "a1b2c3d4e5f6..."
```

### Local Catalog Map Creation

The `loadLocalCatalogItems()` function creates a deduplicated map:

1. **Collection**: Reads all `.yaml` files from the catalogs directory
2. **Multi-mapping**: Groups all versions of each item by name (case-insensitive)
3. **Architecture Filtering**: Filters items compatible with system architecture
4. **Deduplication**: Selects highest version for each item name
5. **Final Map**: Creates `map[string]catalog.Item` with one entry per item

**Example**:
```
Input: Chrome 139.0.1, Chrome 139.0.2, Chrome 139.0.3
Architecture Filter: All compatible with arm64
Deduplication: Selects Chrome 139.0.3 (highest version)
Output: localCatalogMap["chrome"] = Chrome 139.0.3
```

---

## Version Selection Logic

### Primary Version Selection: `DeduplicateCatalogItems()`

When multiple versions of the same item exist, Cimian uses semantic version comparison:

```go
func DeduplicateCatalogItems(items []catalog.Item) catalog.Item {
    best := items[0]
    for _, candidate := range items[1:] {
        if IsOlderVersion(best.Version, candidate.Version) {
            best = candidate
        }
    }
    return best
}
```

### Version Normalization

Versions are normalized before comparison:
- `"2025.9.0"` → `"2025.9"`
- `"2025.10.0"` → `"2025.10"`
- `"1.2.3.0"` → `"1.2.3"`

**Rule**: Trailing `.0` segments are removed for comparison.

### Architecture-Aware Selection: `firstItem()`

For direct item requests (e.g., `--item Chrome`), the `firstItem()` function:

1. **Collects Candidates**: Finds all catalog items matching the name
2. **Architecture Filter**: Keeps only architecture-compatible items
3. **Version Selection**: Chooses highest version among compatible items
4. **Fallback**: If no compatible items, selects highest version overall with warning

**Example Process**:
```
Request: --item Chrome
Candidates Found:
  - Chrome 139.0.1 [x64]
  - Chrome 139.0.2 [x64, arm64]  
  - Chrome 139.0.3 [x64]

System Architecture: arm64
Compatible Items: Chrome 139.0.2
Selected: Chrome 139.0.2 (only compatible option)
```

---

## Architecture Compatibility

### Supported Architecture Values
- `x64`: Intel/AMD 64-bit
- `arm64`: ARM 64-bit  
- `x86`: 32-bit (legacy)

### Compatibility Rules

1. **Exact Match**: Item lists system architecture → Compatible
2. **Universal Package**: Item lists multiple architectures including system → Compatible  
3. **Mismatch**: Item doesn't list system architecture → Incompatible (with warning)

### Architecture Detection

System architecture is detected at runtime:
```go
func GetSystemArchitecture() string {
    if runtime.GOARCH == "amd64" {
        return "x64"
    }
    return runtime.GOARCH // "arm64", "386", etc.
}
```

---

## Status Checking

### Installation Status Determination

For each manifest item, Cimian determines current status using `CheckStatus()`:

#### Primary Methods (in order of preference):

1. **Registry Version Check**:
   ```
   Location: HKLM\SOFTWARE\Cimian\InstalledItems\[ItemName]
   Value: InstalledVersion
   ```

2. **Installs Array Verification**:
   ```yaml
   installs:
     - path: "C:\\Program Files\\App\\app.exe"
       md5checksum: "expected_hash"
   ```
   - Checks file existence
   - Verifies MD5 hash matches expected value
   - Validates file version metadata (if available)

3. **InstallCheck Script**:
   ```yaml
   installcheck_script: |
     # PowerShell script to check installation
     $installed = Test-Path "C:\Program Files\App\app.exe"
     if ($installed) { exit 0 } else { exit 1 }
   ```
   - Exit code 0 = installed
   - Exit code 1 = not installed

### Update Decision Logic

```
IF (registry version < catalog version) OR
   (installs array hash mismatch) OR  
   (installcheck script returns 1)
THEN
   Item needs update
ELSE
   Item is current
```

### File Hash Verification

When using `installs` arrays:
- Cimian calculates MD5 hash of actual file
- Compares against expected hash in catalog
- Hash mismatch triggers reinstallation
- Missing files trigger installation

---

## Dependency Resolution

### Dependency Types

1. **Requires**: Must be installed before this item
2. **Update For**: Items that should be updated when this item changes
3. **Requires Removal**: Items that must be removed before installing

### Dependency Processing Order

```
1. Process Requires dependencies first
2. Install main item  
3. Process Update For items
4. Handle conflicts (Requires Removal)
```

### Circular Dependency Detection

Cimian tracks processed items to prevent infinite loops:
```go
processedInstalls := make(map[string]bool)
if processedInstalls[itemName] {
    // Already processed, skip to avoid circular dependency
    return nil
}
```

### Example Dependency Chain

```yaml
# Main item
- name: "VisualStudio"
  requires: 
    - "dotnet-runtime-8.0"
    - "windows-sdk"
  
# Dependency resolution order:
# 1. Install dotnet-runtime-8.0
# 2. Install windows-sdk  
# 3. Install VisualStudio
```

---

## Decision Tree Examples

### Example 1: New Installation

**Scenario**: Chrome appears in manifest but not installed

```
Manifest Item: Chrome (version unspecified)
Local Status: Not installed
Catalog Versions: 139.0.1, 139.0.2, 139.0.3
System Arch: arm64

Decision Process:
1. Chrome not in localCatalogMap → Identified as new install
2. Architecture check: All versions support arm64 → Compatible
3. Version selection: Choose 139.0.3 (highest version)
4. Result: Install Chrome 139.0.3
```

### Example 2: Update Required

**Scenario**: Chrome installed but outdated

```
Manifest Item: Chrome
Local Status: Chrome 139.0.1 installed (registry)
Catalog Versions: 139.0.1, 139.0.2, 139.0.3
Local Catalog Map: Chrome → 139.0.3 (deduplicated)

Decision Process:  
1. Chrome in localCatalogMap → Not a new install
2. Registry version 139.0.1 < catalog version 139.0.3 → Update needed
3. Result: Update Chrome from 139.0.1 to 139.0.3
```

### Example 3: Architecture Mismatch

**Scenario**: Item only supports incompatible architecture

```
Manifest Item: LegacyApp
Local Status: Not installed  
Catalog Entry: LegacyApp 1.0 [x86 only]
System Arch: arm64

Decision Process:
1. LegacyApp not installed → Should install
2. Architecture check: x86 not compatible with arm64
3. Warning logged: "Architecture mismatch, skipping install"
4. Result: Skip installation, log warning
```

### Example 4: Hash Mismatch Reinstall

**Scenario**: File exists but hash doesn't match

```
Manifest Item: SecurityTool
Local Status: Installed per registry
File Check: C:\Tools\security.exe exists
Expected Hash: abc123
Actual Hash: def456

Decision Process:
1. Registry shows installed → Check files
2. File exists but hash mismatch → Corruption detected
3. Result: Reinstall SecurityTool to restore correct version
```

---

## Troubleshooting

### Common Issues and Diagnosis

#### Issue: Wrong Version Selected

**Symptom**: Older version installed instead of newer version

**Diagnosis**:
```bash
# Check catalog contents
managedsoftwareupdate --item ItemName -vvv --checkonly

# Look for these debug messages:
# "Found item in catalog item: ItemName catalog: Development installer_location: ..."
# "Added candidate item item: ItemName ... candidate_count: X"  
# "Selected architecture-compatible item item: ItemName ... version: X.X.X"
```

**Common Causes**:
- Architecture incompatibility filtering out newer versions
- Catalog contains only older compatible versions
- Bug in version comparison logic (should be rare after recent fixes)

#### Issue: Item Not Installing

**Symptom**: Item in manifest but no installation attempted

**Diagnosis**:
```bash  
# Check manifest processing
managedsoftwareupdate -vvv --checkonly

# Look for:
# "Skipping non-install item item: ItemName action: profile"
# "Architecture mismatch, skipping new install"
# "Item not found in any catalog"
```

**Common Causes**:
- Item not in any downloaded catalog
- Architecture incompatibility  
- Wrong action type in manifest (should be blank or "install")
- Catalog download failure

#### Issue: Unexpected Reinstalls

**Symptom**: Item keeps reinstalling on every run

**Diagnosis**:
```bash
# Check status verification
managedsoftwareupdate --item ItemName -vvv --checkonly

# Look for:
# "Hash verification passed/failed"
# "File verification failed - reinstallation required" 
# "InstallCheck script returned: X"
```

**Common Causes**:
- Hash mismatch in `installs` array
- InstallCheck script always returning failure
- Registry version not being updated after installation
- File permissions preventing proper hash calculation

### Debug Logging

Enable maximum verbosity for troubleshooting:
```bash
managedsoftwareupdate -vvv --checkonly
```

Key debug messages to watch for:
- `"Setting item source"` - Shows where items come from
- `"Found newer compatible version"` - Version selection process
- `"CheckStatus explicitly indicates"` - Installation status decisions
- `"Architecture mismatch"` - Compatibility issues
- `"Selected architecture-compatible item"` - Final item selection

---

## Advanced Topics

### Custom InstallCheck Scripts

For complex software that doesn't fit standard detection methods:

```yaml
installcheck_script: |
  # Complex detection logic
  $service = Get-Service -Name "MyAppService" -ErrorAction SilentlyContinue
  $registry = Get-ItemProperty -Path "HKLM:\SOFTWARE\MyApp" -Name "Version" -ErrorAction SilentlyContinue
  
  if ($service -and $service.Status -eq "Running" -and $registry.Version -eq "2.1.0") {
      exit 0  # Installed and correct version
  } else {
      exit 1  # Not installed or wrong version  
  }
```

### Conditional Items

Using conditions to control installation:

```yaml
- name: "DeveloperTools"
  condition: "os.hostname.contains('DEV-')"
  supported_arch: ["x64", "arm64"]
```

### Self-Service Items

Items available for user-initiated installation:

```yaml
optional_installs:
  - "GoogleChrome"
  - "MozillaFirefox" 
  - "VideoPlayer"
```

---

## Summary

Cimian's decision-making process prioritizes:

1. **Latest Versions**: Always selects the highest available version
2. **Architecture Compatibility**: Ensures software runs on target systems  
3. **Accurate Status Detection**: Uses multiple methods to verify installation state
4. **Dependency Safety**: Resolves dependencies in correct order
5. **Predictable Behavior**: Consistent logic that administrators can understand and predict

Understanding this process enables administrators to:
- Design effective software catalogs
- Troubleshoot installation issues
- Predict Cimian's behavior
- Optimize manifest structures
- Debug version selection problems

For additional help, refer to the debug logging output with `-vvv` verbosity, which provides detailed insight into every decision Cimian makes during the process.
