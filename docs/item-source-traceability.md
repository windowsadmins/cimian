# Item Source Traceability

This document explains the item source traceability feature added to help debug where items are coming from during verbose logging runs.

## Overview

When running `managedsoftwareupdate.exe` with verbose logging (`-vvv`), you can now see where each item originates from in the logs. This helps with debugging scenarios where items are not found in catalogs or when understanding complex dependency chains.

## What Information is Tracked

For each item being processed, the system tracks:

1. **Source Manifest**: Which manifest file the item came from
2. **Source Type**: How the item was referenced (e.g., `managed_installs`, `managed_updates`, `requires`, `update_for`)
3. **Source Chain**: The full dependency chain that led to the item being processed
4. **Parent Item**: If the item is a dependency, what item required it

## Source Types

- `managed_installs`: Item was listed in the `managed_installs` section of a manifest
- `managed_updates`: Item was listed in the `managed_updates` section of a manifest  
- `managed_uninstalls`: Item was listed in the `managed_uninstalls` section of a manifest
- `optional_installs`: Item was listed in the `optional_installs` section of a manifest
- `requires`: Item was required as a dependency by another item
- `update_for`: Item is an update for another item
- `dependent_removal`: Item is being removed because another item depends on it
- `update_removal`: Item is being removed as an update of another item

## Example Log Output

Before this change, you would see:
```
[2025-07-11 16:22:09] ERROR Item not found in any catalog item=K-Lite_Codec
```

After this change, you will see:
```
[2025-07-11 16:22:09] ERROR Item not found in any catalog item=K-Lite_Codec source="from managed_installs in manifest 'edge-device-manifest.yaml'"
```

For dependency chains:
```
[2025-07-11 16:22:09] INFO Installing required dependency item=FFmpeg source="from managed_installs in manifest 'edge-device-manifest.yaml' -> requires:dependency-chain->K-Lite_Codec"
```

## Implementation Details

### Key Components

1. **ItemSource struct** (`pkg/catalog/catalog.go`): Tracks the origin information for each item
2. **Source tracking functions** (`pkg/process/process.go`): Helper functions to set, get, and log source information
3. **Manifest processing** (`pkg/process/process.go`): Updates the Manifests function to track sources when processing manifest items
4. **Dependency processing**: Updates advanced dependency logic to track sources for requires, update_for, and dependent items

### Source Tracking Functions

- `SetItemSource()`: Records the initial source information for an item
- `AddItemSourceChain()`: Adds to the source chain for dependency tracking
- `GetItemSource()`: Retrieves source information for an item
- `LogItemSource()`: Logs source information with a custom message
- `ClearItemSources()`: Clears tracking state between runs

### Integration Points

The source tracking is integrated at several key points:

1. **Manifest Processing**: When items are parsed from manifests, their source is recorded
2. **Dependency Resolution**: When requires dependencies are processed, the chain is extended
3. **Update Processing**: When update_for items are processed, the chain is extended
4. **Error Handling**: When items are not found, source information is included in error messages

## Usage

No special configuration is needed. The source tracking is automatically enabled and will appear in verbose logging output when using `-vvv` flag.

Example command:
```bash
managedsoftwareupdate.exe -vvv
```

The enhanced logging will help administrators understand:
- Which manifest files contain problematic items
- How dependency chains are resolved
- Why certain items are being processed (direct vs. dependency)
