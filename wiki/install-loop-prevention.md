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
| 8+ installs of same version (any session count) | 7 days (the `LoopMaxTime` cap) |
| 5+ total installs across 4+ sessions (any version) | 24 hours |
| 8+ total installs across 5+ sessions (any version) | 7 days (the `LoopMaxTime` cap) |

#### What a loop reports

A loop is reported as two messages on the same item, so neither has to be read as a
paragraph. The first says what the loop is; the second says what the package's own
checks keep finding, which is the part an admin can act on:

```
Looping install detected: FortiClient v7.4.3.4799 — 3 installs within 2 hours; paused for 12h 59m
Needs install because installs[0] file C:\Program Files\Fortinet\FortiClient\FortiClient.exe: file version 7.4.3.1790 is older than the catalog's 7.4.3.4799 [version_outdated, unchanged over all 3 attempts]
```

They arrive as two WARN lines in the session log, as `warning_messages` in items.json
(with `warning_message` holding them joined, for consumers that expect one string), and
as two lines in `--loop-status`.

The cause comes from the `StatusService` result that scheduled the install. Its detail
names the specific `installs` entry by index and identity field, the `installcheck_script`
output, or the product code that missed; the bracketed tags carry the machine-readable
reason code and how consistent the answer has been. `unchanged over all N attempts` means
the criterion is stuck — the pkgsinfo describes something the installer does not produce.
Several different reasons across attempts means the machine is changing underneath the
item instead.

The cause is refreshed on every run, including runs where the package stayed suppressed
and was never installed, so it reports what the item wants **now** rather than what it
wanted when the window opened. State written by a client older than this reports the cause
as not recorded until the next check.

Neither message repeats the fix advice. Any catalog change clears suppression fleet-wide
(see below); `managedsoftwareupdate --clear-loop <name>` clears one machine.

#### Auto-Clear on Catalog Change

When the pkgsinfo behind a package changes, LoopGuard **clears that package's loop
history** — the root cause may have been fixed. This is the central lever: publish the
fix and the whole fleet starts installing again on its next run. Nobody has to reach
individual machines with `--clear-loop`.

**How it works**: `makecatalogs` stamps each catalog item with a `loop_fingerprint` —
a hash of that item's entire catalog content. The client stores the fingerprint of the
item it last acted on and compares on every run. Because the hash covers the whole item,
*any* field that reaches the catalog is covered: version, scripts, installer
hash/location/type, `product_code`/`upgrade_code`, installer `switches`/`arguments`/
`success_codes`, `installs`, `check`, `blocking_applications`, `installer_timeout`,
`requires`, and anything added later. (A hand-picked field list used to miss most of
those, so fixes to them left the package suppressed.) Editing only the description also
clears — that errs toward retrying an install, which is the right side to err on.

Two details worth knowing:

- The check runs **whether or not the package is currently suppressed**, and performs
  exactly the same reset as `--clear-loop` (counters, per-version counts, timestamps and
  the processed-session set), so a fix is never judged on pre-fix history.
- Catalogs written by a `makecatalogs` older than this feature carry no
  `loop_fingerprint`. The client then falls back to hashing the install-behavior fields
  it can see locally — narrower, but the old behavior, so nothing regresses while the
  published `makecatalogs` is being rolled forward.

The running Cimian agent version is folded into the comparison too: a client update can
itself be the fix, so the first run after an update clears standing suppressions once.

#### Expiry: retry, then escalate

A suppression window is finite. When it expires, the counters that produced it are
retired with it — otherwise the very next evaluation re-tripped the same thresholds on
the same history and re-suppressed without the package ever being retried. A persistent
`suppression_cycles` count survives that reset and floors the next window (one served
cycle → at least 24 hours, two or more → the 7-day cap), so a genuinely-broken package
converges on a few attempts a week and then goes quiet, while a fixed one starts from the
bottom tier again (a catalog change or an explicit clear resets the cycles to zero).

#### Bootstrap Exemption

During **bootstrap mode** (first-run provisioning via CimianWatcher), LoopGuard is completely disabled. Many packages are legitimately installed back-to-back during initial machine setup.

#### Global Disable (admin opt-out)

Admins who want loop suppression off fleet-wide can set it in `config.yaml`:

```yaml
LoopGuardEnabled: false
```

Default is `true`. When set to `false`, LoopGuard never suppresses any package and ignores any persisted backoff state — every run behaves as if no loop history exists, and no new backoff state is formed while disabled (so re-enabling later won't instantly suppress packages based on attempts logged during the disabled window). The setting is read on each run by `UpdateEngine`, which logs a `LoopGuard disabled by config` notice near the start of the run; the message is written to the session log (`run.log`) and is shown on the console at `-v` or higher. Passive loop detection (the `install_loop_detected` flag in `items.json` for dashboards) is **not** affected — reporting still flags loops even when active suppression is off.

Confirm the effective value with:

```pwsh
managedsoftwareupdate --show-config
```

Note: this is the global kill-switch. To bypass suppression for a single package on a single run without disabling LoopGuard everywhere, use `--item <name>` instead; to clear a specific suppression, use `--clear-loop <name>`.

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
- Catalog fingerprint (`loop_fingerprint` of the catalog item, folded with the agent version)
- Suppression cycles served (the escalation floor for the next window)
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

1. If the catalog fingerprint changed: **clear the loop history** and allow the install
2. Otherwise, LoopGuard checks whether the package is currently suppressed
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
  Needs install because installs[0] file C:\Windows\System32\nvapi64.dll: hash mismatch — expected 4f2a…, found 9c81… [hash_mismatch, unchanged over all 8 attempts]
  Trigger last seen: 2/25/2026 4:00 AM
  Cache: HIT — C:\ProgramData\ManagedInstalls\Cache\NvidiaGeforce\dbInstaller.exe
  Diagnosis: Loop is install/status-check issue, not download (cached installer exists)
  Suppressed until: indefinite
  Reason: installed 8 times across 6 sessions
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
- The fix is outside the catalog entirely (machine state, an external dependency), so no
  pkgsinfo field changed
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
