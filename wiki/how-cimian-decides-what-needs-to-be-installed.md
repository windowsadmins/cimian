# How Cimian Decides What Needs To Be Installed

When `managedsoftwareupdate` processes an item from a manifest, it must decide whether the item needs to be installed, updated, or skipped. This document describes the full decision logic — the priority chain, per-type detection methods, and how each installer type is handled differently.

## Overview

For each item in the manifest, Cimian walks through a set of checks in strict priority order. **The first check that reaches a definitive answer wins** — Cimian does not continue evaluating lower-priority checks once a result is returned.

The canonical source of this logic is `StatusService.CheckStatus()` in `packages/CimianTools/cli/managedsoftwareupdate/Services/StatusService.cs`.

---

## The Priority Chain

### Priority 0 — Self-Update Guard *(CimianTools only)*

Before anything else, if the item being evaluated is the `CimianTools` or `Cimian` package itself, Cimian compares the **running binary version** against the catalog version.

- Running version `>=` catalog version → **installed** (skip)
- Running version `<` catalog version → fall through to normal checks

This prevents the running agent from mistakenly downgrading itself or triggering a redundant reinstall.

---

### Priority 1 — `installcheck_script`

If the pkgsinfo contains an `installcheck_script`, it is executed. Exit codes are interpreted as predicates:

| Exit code | Meaning |
|-----------|---------|
| `0` | **Install is needed** — Cimian schedules the install |
| Non-zero | **Install not needed** — Cimian skips the item |

> **This check is authoritative.** If `installcheck_script` is defined, nothing below this point runs. This makes it appropriate for items that need custom detection logic — pref domains, registry keys, running processes, etc.

---

### Priority 2 — `installs` Array

If the pkgsinfo defines an `installs` array, Cimian iterates each entry and verifies the item against the local system. If **any** entry fails verification, the item is marked as needing action. If all entries pass, the item is considered installed.

See [Per-Type Breakdown: `installs` Array Entries](#per-type-breakdown-installs-array-entries) below for how each entry type is evaluated.

---

### Priority 3 — `check.registry`

If `check.registry.name` is defined, Cimian scans both the 64-bit and 32-bit views of `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` for an entry whose `DisplayName` **contains** the specified name (case-insensitive partial match).

| Outcome | Result |
|---------|--------|
| Not found | **Install needed** (new install) |
| Found, no `check.registry.version` set | **Installed** |
| Found, installed `DisplayVersion` < catalog version | **Update needed** |
| Found, installed `DisplayVersion` >= catalog version | **Installed** |

---

### Priority 4 — `check.file`

If `check.file.path` is defined, Cimian checks for the existence of that path.

| Outcome | Result |
|---------|--------|
| File not found | **Install needed** |
| File found, no version/hash checks | **Installed** |
| File found, `check.file.version` set, installed < catalog | **Update needed** |
| File found, `check.file.hash` set, hash mismatch | **Update needed** |
| All checks pass | **Installed** |

---

### Priority 5 — `check.script`

If `check.script` is defined, Cimian executes it. Exit codes follow the same predicate convention as `installcheck_script`:

| Exit code | Meaning |
|-----------|---------|
| `0` | **Installed** |
| Non-zero | **Install needed** |

---

### Priority 6 — ManagedInstalls Registry

If no explicit check (Priorities 1–5) is defined, Cimian looks up the item's name under `HKLM\SOFTWARE\ManagedInstalls\<Name>` — the registry key that Cimian itself writes after a successful installation.

This applies to **all installer types** (pkg, nupkg, copy, script, nopkg, msi, exe).

| Outcome | Result |
|---------|--------|
| No entry found | Fall through to Priority 7 |
| Installed version `<` catalog version | **Update needed** (`UpdateAvailable`) |
| Installed version `>=` catalog version | **Installed** |

> This is the correct fallback for `pkg` and `nupkg` items that don't have a Windows MSI product code or a file to check against. Cimian's own install record is the source of truth for items it manages.

---

### Priority 7 — NoChecks Fallback

If every priority above produced no definitive answer and there is no ManagedInstalls registry receipt, Cimian marks the item as **not installed** and schedules it for installation.

```
Status: not-installed
NeedsAction: true
Reason: "No explicit checks defined and no installation receipt in registry"
ReasonCode: no_checks
```

Once Cimian successfully installs the item, it writes a receipt to `HKLM\SOFTWARE\ManagedInstalls\<Name>`. On subsequent runs, Priority 6 finds that receipt and handles version comparison — so the item will update correctly when a new catalog version appears.

> This means a `pkg` or `nupkg` pkgsinfo with no `installs` array and no `installcheck_script` will install correctly on first run and track updates via the ManagedInstalls registry. No extra detection fields are required.

---

## Per-Type Breakdown: `installs` Array Entries

The `installs` array entries each have a `type` field that determines how they are verified.

### `type: file`

Used by: `pkg`, `nupkg`, `copy`, `exe`, `script`, `nopkg` — any installer type

```
file exists?
├─ NO  → NeedsAction=true  (FileMissing)
└─ YES →
     md5checksum defined?
     ├─ YES → hash matches?
     │         ├─ NO  → NeedsAction=true  (HashMismatch)
     │         └─ YES → hash is AUTHORITATIVE
     │                  version mismatch is informational only — item is accepted
     └─ NO  →
          version defined? (falls back to item.version if not set on entry)
          ├─ YES → get file's PE/FileVersion resource
          │         ├─ not readable + no hash → NeedsAction=true
          │         ├─ file version < catalog  → NeedsAction=true  (VersionOutdated)
          │         └─ file version >= catalog → installed
          └─ NO  → installed (file presence is sufficient)
```

**Key behaviour:** When a hash is provided and matches, version discrepancies are considered informational. The hash is the authority. This allows a file to report an internal version string that differs from the pkgsinfo version without triggering unnecessary reinstalls.

---

### `type: directory`

Used by: any installer type

```
directory exists?
├─ NO  → NeedsAction=true  (DirectoryMissing)
└─ YES → installed
```

No version checking is performed on directories.

---

### `type: msi`

Used by: `msi` installer type, or any type where an MSI product code is tracked in the installs array

```
CheckMsiWithUpgradeCode(product_code, upgrade_code, version)
│
├─ 1. ProductCode lookup
│       Scan HKLM\...\Uninstall\{ProductCode} (64-bit, then 32-bit)
│       found?
│         YES → version current/newer → installed
│               version outdated      → NeedsAction=true
│
├─ 2. UpgradeCode lookup  (handles Chrome-style auto-updaters)
│       Resolve UpgradeCode via HKLM\..\Installer\UpgradeCodes\{PackedGUID}
│       Cross-reference against Uninstall keys
│       found?
│         YES → version current/newer → installed
│               version outdated      → NeedsAction=true
│
├─ 3. DisplayName fallback
│       Scan all Uninstall keys for exact DisplayName match
│       (uses item.display_name if set, otherwise item.name)
│       found?
│         YES → version compare → installed / NeedsAction=true
│
└─ 4. Not found anywhere → NeedsAction=true  (ProductCodeMissing)
```

---

## Summary by Installer Type

| Type | Built-in detection in `installs[]` | ManagedInstalls fallback (Priority 6) | Recommended detection |
|------|-----------------------------------|-----------------------------------------|----------------------|
| **msi** | `installs[type:msi]` via ProductCode + UpgradeCode | Yes | Always set `product_code` in pkgsinfo so `installs[type:msi]` works reliably |
| **exe** | None | Yes | Use `installcheck_script`, `check.registry`, or `installs[type:file/msi]` — EXE installers have no native detection |
| **pkg** / **nupkg** | None | Yes | ManagedInstalls registry is the correct fallback. No extra checks needed — first run installs, subsequent runs compare versions via Priority 6 |
| **copy** | None | Yes | Use `installs[type:file]` to verify the copied files |
| **script** | None | Yes | Use `installcheck_script` for stateless scripts that should always run |
| **nopkg** | None | Yes | If the item should run on every check, use `installcheck_script` that always exits 0 |

---

## Important Notes

### Install loops

An `installcheck_script` that exits `0` (install needed) after the item has already been installed will cause Cimian to schedule an install every run. Ensure your `postinstall_script` puts the system into a state where `installcheck_script` returns non-zero on the next run.

### `nopkg` and stateless scripts

Items that are designed to run every time (configuration enforcement, preference writes, etc.) should **not** rely on the ManagedInstalls fallback — Cimian will write an installation record after the first run, and subsequent runs will see the version as current and skip. Use `installcheck_script` to control execution explicitly.

### Hash vs version authority

In `installs[type:file]`, if you provide both a hash and a version, the **hash wins**. If the hash matches, a version discrepancy will generate a warning in the log but will **not** trigger a reinstall. This is intentional: the hash proves the exact file is present.

### MSI ProductCode vs UpgradeCode

- **ProductCode** (`{GUID}`) — unique per version. Changes with each release.
- **UpgradeCode** (`{GUID}`) — stable across versions. Use this for apps like Chrome or Teams that silently update and change their ProductCode.

When both are present, Cimian tries ProductCode first (faster). It only falls back to UpgradeCode if the ProductCode is not found in the registry.

---

## Log Messages Reference

| Log message | Meaning |
|-------------|---------|
| `CheckStatus explicitly indicates NO update required` | Item evaluated as installed |
| `CheckStatus explicitly indicates update required` | Item will be installed/updated |
| `No explicit checks defined - assuming installed` | Priority 7 fallback hit — no detection method found |
| `Checking status via installcheck_script` | Priority 1 running |
| `Checking installs array for file verification` | Priority 2 running |
| `Found MSI via ProductCode` | MSI detected via exact product code |
| `Found MSI via UpgradeCode` | MSI detected via upgrade code (version may differ from ProductCode) |
| `Found app via display_name fallback` | App found by name in Uninstall registry |
| `Registry version X < catalog version Y` | ManagedInstalls fallback detected an update is available |
