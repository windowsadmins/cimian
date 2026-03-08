# Install Loop Prevention

Cimian includes an active install loop prevention system (**LoopGuard**) that detects when the same package is being installed repeatedly and automatically suppresses it with exponential backoff. This protects endpoints from wasted bandwidth, CPU churn, and user disruption.

## What Is an Install Loop?

An install loop occurs when Cimian keeps reinstalling the same package every update cycle because the post-install state never satisfies the status check. Common causes:

| Cause | Example |
|---|---|
| **Hash mismatch** | Two pkgsinfo entries for different GPU families both verify against `dbInstaller.exe` with different md5 checksums |
| **Missing installcheck_script** | pkgsinfo has no version or receipt check — Cimian always thinks install is needed |
| **Faulty postinstall** | Install succeeds but postinstall_script fails, so status check still reports "not installed" |
| **Version confusion** | Installed version doesn't match catalog version string (e.g., `572.61` vs `32.0.15.8216`) |
| **Broken uninstall/reinstall cycle** | A dependency chain causes repeated uninstall-then-reinstall |

### Real-World Case: NVIDIA Hash War

Two pkgsinfo entries (`NvidiaGeforce` and `NvidiaQuadroASeries`) both pointed to the same `dbInstaller.exe` file but with different md5 checksums. Each hourly cycle, one entry's hash check would fail, triggering reinstall. The next cycle, the other entry would fail. Result: GPU drivers reinstalling **every hour during business hours**, causing mid-session crashes on lab machines.

## How Loop Detection Works

### Passive Detection (items.json enrichment)

`DataExporter.DetectInstallLoopEnhanced()` analyzes `events.jsonl` history and flags four loop scenarios in `items.json`:

1. **Same version reinstalled across multiple sessions** — package keeps getting completed but reappears
2. **Continuous failure** — package fails install repeatedly
3. **Version mismatch** — different versions installed across sessions (possible catalog conflict)
4. **Rapid reinstall** — same package installed multiple times within a single session

These are written to `items.json` with `warning` fields for reporting and dashboard visibility.

### Active Prevention (LoopGuard)

LoopGuard runs inside `UpdateEngine.IdentifyActions()` and actively blocks packages from being scheduled for install.

#### Backoff Thresholds

| Condition | Suppression Duration |
|---|---|
| 3+ installs within 2 hours (rapid-fire) | 12 hours |
| 3+ installs of same version across 3+ sessions | 6 hours |
| 5+ installs of same version across 5+ sessions | 24 hours |
| 8+ installs of same version (any session count) | Indefinite — requires manual clear or version change |
| 5+ total installs across 4+ sessions (any version) | 24 hours |
| 8+ total installs across 5+ sessions (any version) | Indefinite — requires manual clear or version change |

#### Auto-Clear on Catalog Change

When ANY install-behavior field changes in the pkgsinfo for a suppressed package, LoopGuard **automatically clears** the suppression and resets history. This covers:

- Version changes (e.g., `572.61` → `572.83`)
- `installcheck_script` fixes (the most common loop fix)
- `installs` array changes (different file paths, hashes, or version checks)
- `check` info changes (registry path, file check)
- Installer hash or URL changes (different binary)
- Script changes (`install_script`, `postinstall_script`, `preinstall_script`)

**How it works**: UpdateEngine computes a SHA256 fingerprint of all install-behavior fields in the catalog item. LoopGuard stores this fingerprint alongside the suppression state. On each run, if the fingerprint differs from the stored one, suppression is cleared — the pkgsinfo was modified and the root cause may be fixed.

This means you don't need to SSH into machines to run `--clear-loop` after fixing a pkgsinfo. Just update the catalog (change the version, fix the script, update the hash, etc.) and the next scheduled run will pick it up.

#### Bootstrap Exemption

During **bootstrap mode** (first-run provisioning via CimianWatcher), LoopGuard is completely disabled. Many packages are legitimately installed back-to-back during initial machine setup.

## How It Works Internally

### State Persistence

LoopGuard persists its state to:
```
C:\ProgramData\ManagedInstalls\reports\state.json
```

The state file uses a nested structure (`CimianState`) to allow future extensibility:
```json
{
  "loop_guard": {
    "last_updated": "2025-02-26T04:00:00Z",
    "packages": {
      "nvidia_geforce": {
        "package_name": "NvidiaGeforce",
        "catalog_fingerprint": "a1b2c3d4e5f67890",
        ...
      }
    }
  }
}
```

This file tracks per-package:
- Total attempt count and session count
- Per-version attempt counts
- Catalog fingerprint (SHA256 of install-behavior fields)
- Recent timestamps (for rapid-fire detection)
- Suppression status and expiry time
- Which event sessions have been processed (deduplication)

### History Building

On startup, LoopGuard scans `events.jsonl` files from the last 7 days:
```
C:\ProgramData\ManagedInstalls\logs\
  2025-02-25\
    0400\events.jsonl
    0500\events.jsonl
  2025-02-26\
    0400\events.jsonl
```

It builds a per-package history of install attempts, versions, and timestamps. This history is merged with any existing `loop_state.json` data, with session deduplication to avoid double-counting.

### Integration Point

In `UpdateEngine.IdentifyActions()`, after `StatusService.CheckStatus()` determines a package needs action:

1. LoopGuard checks if the package is currently suppressed
2. If catalog fingerprint changed since suppression: **auto-clear** and allow install
3. If suppressed: logs a WARN, records `loop_suppressed` reason code, skips the package
4. If not suppressed: package proceeds to install
5. After install completes: `RecordAttempt()` logs the result + fingerprint for future detection

## Diagnosing Loop Issues

### Check current suppression status

```powershell
sudo .\managedsoftwareupdate.exe --loop-status
```

Output:
```
NvidiaGeforce:
  Attempts: 8 across 6 sessions
  Last version: 572.61
  Last attempt: 2/25/2026 4:00 AM
  Last success: True
  Versions attempted: 572.61 (8x)
  Cache: HIT — C:\ProgramData\ManagedInstalls\Cache\NvidiaGeforce\dbInstaller.exe
  Diagnosis: Loop is install/status-check issue, not download (cached installer exists)
  Suppressed until: indefinite
  Reason: Persistent loop: version 572.61 installed 8 times across 6 sessions (indefinite)
```

### Check items.json for warnings

```powershell
Get-Content "C:\ProgramData\ManagedInstalls\items.json" | ConvertFrom-Json |
  Where-Object { $_.warning } | Select-Object name, warning
```

### Check events.jsonl for install history

```powershell
# Find recent install events for a specific package
Get-ChildItem "C:\ProgramData\ManagedInstalls\logs" -Recurse -Filter "events.jsonl" |
  ForEach-Object { Get-Content $_.FullName } |
  ConvertFrom-Json |
  Where-Object { $_.package -eq "NvidiaGeforce" -and $_.action -eq "install" } |
  Format-Table timestamp, status, version
```

### Check state.json directly

```powershell
Get-Content "C:\ProgramData\ManagedInstalls\reports\state.json" | ConvertFrom-Json |
  Select-Object -ExpandProperty loop_guard | Select-Object -ExpandProperty Packages
```

## Clearing Suppressions

### Clear a specific package

```powershell
sudo .\managedsoftwareupdate.exe --clear-loop NvidiaGeforce
```

### Clear all suppressions

```powershell
sudo .\managedsoftwareupdate.exe --clear-loop all
```

### When to clear

In most cases, **you don't need to manually clear**. If you update the pkgsinfo (change the version, fix the installcheck_script, update the hash, modify the installs array, etc.), LoopGuard auto-clears when it sees the new catalog fingerprint.

Manual clear is only needed when:
- You fixed the issue without changing any fingerprinted field
- You want to force a retry before the backoff expires

Clearing without fixing the root cause will just trigger the loop again, and LoopGuard will re-suppress with the same or higher backoff.

## Common Patterns and Fixes

### Hash mismatch between GPU families

**Symptom**: Two GPU driver packages alternately reinstall each cycle.

**Fix**: Ensure each pkgsinfo has unique `installs` entries pointing to different files, or use `installcheck_script` that checks the actual installed driver version rather than file hashes.

### Missing version check

**Symptom**: Package reinstalls every cycle even though it's already installed.

**Fix**: Add an `installcheck_script` that checks installed version:
```powershell
# installcheck_script
$installed = (Get-ItemProperty "HKLM:\SOFTWARE\...\MyApp").Version
if ($installed -eq "3.0.0") { exit 1 }  # Already installed, skip
exit 0  # Not installed, proceed
```

### Postinstall failure

**Symptom**: Package installs successfully but status check still says it's needed.

**Fix**: Check `events.jsonl` — look for `"status": "completed"` followed by a new `"action": "install"` in the next session. The postinstall_script may be failing silently. Add error handling and explicit exit codes.

## Architecture

```
UpdateEngine.IdentifyActions()
  │
  ├─ StatusService.CheckStatus()  →  "needs install"
  │
  ├─ LoopGuard.ShouldSuppress()   →  check history + thresholds
  │     │
  │     ├─ Check persisted suppression (reports/state.json)
  │     ├─ Auto-clear if catalog fingerprint changed
  │     ├─ Analyze rapid-fire (3 in 2h)
  │     ├─ Analyze version-based escalation (3/5/8 threshold)
  │     └─ Analyze total-based escalation (5/8 threshold)
  │
  ├─ If suppressed: WARN log + skip
  │
  └─ If allowed: install → RecordAttempt(success/failure)
```

## Files

| File | Purpose |
|---|---|
| `shared/core/Services/LoopGuard.cs` | Active loop prevention with backoff + cache analysis |
| `shared/core/Services/DataExporter.cs` | Passive loop detection for items.json reporting |
| `shared/core/Models/StatusReasonCode.cs` | `LoopSuppressed` and `InstallCompleted` constants |
| `cli/managedsoftwareupdate/Services/UpdateEngine.cs` | Integration point |
| `cli/managedsoftwareupdate/Program.cs` | `--clear-loop` and `--loop-status` CLI |
| `tests/LoopGuardTests.cs` | Unit tests (35 tests) |

## State file location

```
C:\ProgramData\ManagedInstalls\reports\state.json
```

To reset completely (nuclear option):
```powershell
Remove-Item "C:\ProgramData\ManagedInstalls\reports\state.json" -Force
```
