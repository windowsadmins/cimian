# Item Source Traceability

This document explains the item source traceability feature added to help debug where items are coming from during verbose logging runs.

## Overview

When running `managedsoftwareupdate.exe` with verbose logging (`-vvv`), you can now see where each item originates from in the logs. This helps with debugging scenarios where items are not found in catalogs or when understanding complex dependency chains.

## What Information is Tracked

For each item being processed, the system tracks two pieces of information per item name:

1. **Source Manifest**: Which manifest file (path/name) the item came from
2. **Source Type**: How the item was referenced in that manifest

Items injected by the dependency resolver carry `SourceManifest = "dependency"` on their `ManifestItem` record (see `UpdateEngine.cs`).

## Source Types

The following source types are recorded by `ManifestService.cs` when parsing manifests:

- `managed_installs` — Item listed in the `managed_installs` section of a manifest
- `managed_updates` — Item listed in `managed_updates`
- `managed_uninstalls` — Item listed in `managed_uninstalls`
- `optional_installs` — Item listed in `optional_installs`
- `default_installs` — Item listed in `default_installs`
- `conditional_managed_installs` — Item listed under a conditional `managed_installs` block
- `conditional_managed_uninstalls` — Item listed under a conditional `managed_uninstalls` block
- `conditional_managed_updates` — Item listed under a conditional `managed_updates` block
- `conditional_optional_installs` — Item listed under a conditional `optional_installs` block

Additionally, items added by the dependency resolver in `UpdateEngine.cs` have `SourceManifest = "dependency"` set on the `ManifestItem` itself (separate from the `ManifestService` source map).

## Example Debug Output

When `SetItemSource` is called, a debug log line is emitted:

```
[2025-07-11 16:22:09] DEBUG Setting item source item: K-Lite_Codec sourceManifest: edge-device-manifest.yaml sourceType: managed_installs
```

Dependency-injected items are also surfaced in `UpdateEngine.cs`:

```
[2025-07-11 16:22:09] INFO Added dependencies: FFmpeg, K-Lite_Codec
```

## Implementation Details

### Key Components

1. **`ManifestItem.SourceManifest`** (`shared/core/Models/Reporting.cs`): String field on the manifest-item record indicating where the item was declared. Set to the manifest path for direct entries, or the literal string `"dependency"` for items injected by the dependency resolver.
2. **`ManifestService._itemSources`** (`cli/managedsoftwareupdate/Services/ManifestService.cs`): An internal `Dictionary<string, string>` keyed by lowercased item name. Each value is `"<sourceManifest>:<sourceType>"`.
3. **Dependency injection** (`cli/managedsoftwareupdate/Services/UpdateEngine.cs`): When a `requires` dependency is added, the synthetic `ManifestItem` is created with `SourceManifest = "dependency"`.

### Source Tracking Functions on `ManifestService`

- `SetItemSource(string itemName, string sourceManifest, string sourceType)` — Records the source for an item (overwrites any previous value).
- `GetItemSource(string itemName) → (SourceManifest, SourceType)` — Returns the recorded tuple, or `("Unknown", "unknown")` if not tracked.
- `ClearItemSources()` — Resets the internal source map between runs.

There is no `AddItemSourceChain` or `LogItemSource` helper — dependency chains are not assembled into a single string. Dependency provenance is instead surfaced via `ManifestItem.SourceManifest = "dependency"` on the injected item.

### Integration Points

1. **Manifest Processing**: Each `managed_installs` / `managed_updates` / `managed_uninstalls` / `optional_installs` / `default_installs` entry triggers `SetItemSource(name, manifestPath, sourceType)`.
2. **Conditional Items**: Conditional blocks call `SetItemSource` with `conditional_*` source types.
3. **SelfServe**: SelfServe promotion/demotion writes `managed_installs` or `managed_uninstalls` with the SelfServe manifest as the source.
4. **Dependency Resolution**: Added items get `SourceManifest = "dependency"` on the `ManifestItem` record itself.

## Usage

No special configuration is needed. The source tracking is always populated. To see the `Setting item source ...` debug lines, run with verbose logging:

```pwsh
sudo .\managedsoftwareupdate.exe -vvv --checkonly
```

The source data helps administrators understand:
- Which manifest files contain problematic items (via `SourceManifest`)
- How a given item was declared (via `SourceType`)
- Which items were added by dependency resolution (`SourceManifest == "dependency"` on the `ManifestItem`)
