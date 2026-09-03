# Install Loop Prevention

An install loop is a package that installs successfully and is still reported as needing
installation on the next run, forever. LoopGuard is the client's active defence: it watches
install attempts per package, suppresses a package that is clearly looping, and lifts the
suppression by itself when the package's metadata changes. This page covers what LoopGuard
detects, the thresholds it uses, the state it keeps, and how to diagnose and clear a loop.

## What an install loop is

Cimian installs a package because a detection check says it is not present or not current.
If the install succeeds but the check still says the same thing afterwards, the package is
queued again on the next run, and the run after that. Nothing errors. The session log looks
healthy. The package just reinstalls hourly.

The cause is almost always the detection, not the installer:

| Cause | What it looks like |
|---|---|
| A version floor the payload can never report | `version_outdated` on the same version every run |
| A checksum that can never match | `hash_mismatch`, unchanged, every run |
| A checked path the installer never creates | `file_missing` on a path that stays missing |
| An `installs` entry pointing at a shortcut or a launcher stub | passes and fails at random, or fails permanently |
| An install-check script that always exits 0 | `installcheck_needed`, unchanged, every run |
| An MSI identified by a product code that changes each build | `product_code_missing` even though the product is installed |
| Two pkgsinfo checking the same file against different expected hashes | the two packages alternate, one failing each run |
| A post-install step that never completes | the install reports success, the state it was meant to create is absent |

An item that reports Installed forever is the same defect seen from the other side. Both are
covered by
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

## What LoopGuard does

LoopGuard sits between the status check and the install queue. On every run, for every item
the status check says needs action, it:

1. Compares the item's current catalog fingerprint against the one recorded last time. A
   change auto-clears that package's entire loop history — see
   [Clearing and retirement](#clearing-and-retirement).
2. Checks whether a suppression window is currently open. If it is, the item is skipped for
   this run, a warning is logged, and the item is reported with reason code
   `loop_suppressed`.
3. Otherwise lets the install proceed, and records the attempt afterwards.

Separately, when the status check reports that an item needs **no** action, LoopGuard is told
the package converged and retires whatever history it had accumulated.

On construction each run, LoopGuard loads its saved state and then rebuilds package history
from the last **7 days** of session event logs, de-duplicated by session so a session is
never counted twice.

LoopGuard suppresses installs. It does not fix the package, and it does not report anything
to the repository. It buys time on the endpoint while you fix the pkgsinfo.

## Thresholds

Conditions are evaluated in this order and the first match wins.

| # | Condition | Suppression window |
|---|---|---|
| 1 | 3 or more install attempts within the last 2 hours | 12 hours |
| 2 | The same version attempted 3 or more times **and** seen in 3 or more sessions: | |
| 2a | — that version attempted 8 or more times | `LoopMaxTime` days (default 7) |
| 2b | — that version attempted 5 to 7 times | 24 hours |
| 2c | — that version attempted 3 or 4 times | 6 hours |
| 3 | Total attempts across all versions, discounting one attempt per distinct version: | |
| 3a | — 8 or more discounted attempts **and** 5 or more sessions | `LoopMaxTime` days (default 7) |
| 3b | — 5 or more discounted attempts **and** 4 or more sessions | 24 hours |

Two details that are easy to get wrong:

- **The 8-or-more tier in rule 2 is not reachable on session count alone.** Rule 2 is a
  ladder: the same version must have been attempted at least 3 times across at least 3
  distinct sessions before any of 2a, 2b or 2c applies. A package hammered 8 times inside a
  single session trips rule 1, not rule 2a.
- **Rule 3 discounts legitimate upgrades.** The count used is
  `attempts - (distinct versions - 1)`, because the first attempt at each new version is a
  real upgrade, not a repeat. A package that upgraded through five versions in five attempts
  has one discounted attempt, not five.

### Escalation

Each time a suppression window expires, the package's served-cycle count increases and floors
the next window: one served cycle means the next window is at least 24 hours, two or more
means it is the `LoopMaxTime` cap. When the floor exceeds the tier the thresholds chose, the
reason text says so.

A genuinely broken package therefore settles into a few attempts a week rather than a few an
hour. A fixed package starts from the bottom tier again, because both a catalog change and a
convergence reset the cycle count to zero.

### What LoopGuard never suppresses

- **Bootstrap runs.** During bootstrap provisioning, many packages legitimately install
  back to back, so LoopGuard never suppresses and the run is unaffected.
- **`OnDemand: true` and `recurring: true` items.** These re-run every session by design.
  They are exempt from suppression and no attempt history accumulates for them at all.
- **A run targeted with `--item <name>`.** Suppression is bypassed for that run only.
  Persistent state is left untouched, so the package is still suppressed on the next normal
  run.
- **A self-reported soft failure.** An install script that emits the `CIMIAN-WARNING:`
  marker marks its own run as a soft failure. The install still counts as successful, the
  convergence probe is skipped, and LoopGuard records nothing.
- **Anything, when LoopGuard is disabled.** See [Configuration](#configuration).

## Convergence

Convergence means the package's own checks report nothing to do. It is the only thing that
proves a package is healthy, and LoopGuard uses it in two directions.

### The first-install convergence probe

Immediately after a successful install — and only when the post-install work raised no
`CIMIAN-WARNING` — the client re-runs the same status check that scheduled the install.

| Probe result | What happens |
|---|---|
| No action needed | The package converged. Nothing is recorded. |
| Still needs action, and the item declares a restart or logout action | The package is assumed to be waiting for the restart. A pending-restart note is recorded and the reinstall is deferred rather than counted as a loop. |
| Still needs action, no restart expected | A loop is declared immediately and a suppression window of `LoopReprobeHours` (default 24) opens, with the reason that the install-check still reported action needed immediately after a successful install. |

This is why a badly-written pkgsinfo is usually caught on its **first** install rather than
three or more sessions later. On-demand items are exempt from the probe entirely; by design
they never converge.

The pending-restart note clears when the catalog fingerprint changes, when the machine boots,
or when it ages past `LoopMaxTime` days.

### Convergence retires history

When a normal status check reports that an item needs no action, LoopGuard performs a full
reset of that package: the suppression window, the attempt and session counters, the
per-version counts, the recorded timestamps, the processed-session set, the trigger history
and the served-cycle count all go away.

This is what keeps healthy packages out of the suppression report. A package that loops for a
while and is then fixed does not carry its old evidence forward, and is not owed an escalated
window if it ever loops again.

## What a suppressed package reports

A suppression produces two messages on the same item, one saying what the loop is and one
saying what the package's own checks keep finding:

```
Looping install detected: ExampleApp v4.2.1.0 — 3 installs within 2 hours; paused for 12h 59m
Needs install because installs[0] file C:\Program Files\Example App\ExampleApp.exe: file version 4.1.0.0 is older than the catalog's 4.2.1.0 [version_outdated, unchanged over all 3 attempts]
```

Both appear as warnings in the session log, in the item's warnings in `items.json`, and in
`--loop-status`.

The second message is the actionable one. It names the specific `installs` entry by index and
identity, or the install-check output, or the identifier that missed. The bracketed tags carry
the machine-readable reason code and how consistent the answer has been:
`unchanged over all N attempts` means the criterion is stuck and the pkgsinfo is describing
something the installer does not produce. Several different reasons across attempts means the
machine is changing underneath the package instead.

The cause is refreshed on every run, including runs where the package stayed suppressed and
was never installed, so it reports what the item wants now rather than what it wanted when
the window opened.

## State

### The state file

```
C:\ProgramData\ManagedInstalls\reports\state.json
```

JSON, snake_case, indented, with null fields omitted. The top level is a wrapper so other
subsystems can add their own sections later; LoopGuard owns the `loop_guard` key.

```json
{
  "loop_guard": {
    "last_updated": "2026-09-03T04:00:00Z",
    "cleared_at": "2026-08-27T11:15:00Z",
    "packages": {
      "exampleapp": {
        "package_name": "ExampleApp",
        "attempt_count": 8,
        "session_count": 6,
        "last_attempt": "2026-09-03T04:00:00Z",
        "last_version": "4.2.1.0",
        "catalog_fingerprint": "a1b2c3d4e5f67890",
        "last_success": "2026-09-03T04:00:12Z",
        "suppressed_until": "2026-09-10T04:00:00Z",
        "suppression_reason": "installed 8 times across 6 sessions",
        "version_attempts": { "4.2.1.0": 8 },
        "recent_timestamps": ["2026-09-03T02:00:00Z", "2026-09-03T03:00:00Z"],
        "processed_sessions": ["2026-09-03/0300", "2026-09-03/0400"],
        "suppression_cycles": 1,
        "cleared_at": "2026-08-27T11:15:00Z",
        "pending_restart_since": null,
        "trigger": "version_outdated",
        "trigger_last_seen": "2026-09-03T04:00:00Z",
        "trigger_counts": { "version_outdated": 8 }
      }
    }
  }
}
```

Package keys are the lowercased item name. `recent_timestamps` keeps the most recent 20
entries; `trigger_counts` keeps at most 5 distinct triggers.

An older `reports\loop_state.json` is migrated into this file on first run and then deleted.

### The suppression report

```
C:\ProgramData\ManagedInstalls\reports\loop_suppressed.json
```

Written every run, as an empty array when nothing is suppressed. Each entry carries the
package name and version, the reason, the time the window closes, the trigger and its summary,
and a literal `ClearCommand` of `managedsoftwareupdate --clear-loop <name>`.

### Where history comes from

Attempt history is rebuilt each run from `events.jsonl` in the session log directories under
`C:\ProgramData\ManagedInstalls\logs\`, covering the last 7 days, de-duplicated by session id.

## The catalog fingerprint

Every package's state carries a fingerprint of the catalog item it last acted on: a 16-hex-
character truncation of a SHA-256 hash. When the fingerprint changes, the package's loop
history is cleared.

The fingerprint has two parts.

**The catalog item's content.** `makecatalogs` stamps each catalog item with a
`loop_fingerprint` covering the whole serialised item, so *any* field that reaches the
catalog is covered — version, every script, installer hash, location, type, switches,
arguments and success codes, `product_code` and `upgrade_code`, `installs`, `check`,
`blocking_applications`, `installer_timeout`, `requires`, and anything added later. Editing
only the description clears too; that errs toward retrying an install, which is the right
side to err on.

Catalogs written by a `makecatalogs` that predates the stamp carry no `loop_fingerprint`. The
client then falls back to hashing the install-behaviour fields it can read locally. That
fallback does not cover product and upgrade codes, installer switches, or blocking
applications, so a fix limited to those fields will not clear suppression until the catalog
carries a stamp.

**The running client version.** The version of the client binary is folded into the
fingerprint. A client update is itself sometimes the fix — a detection bug corrected in the
client can only prove itself by being allowed to run — so **the first run after a client
update clears every standing suppression once, fleet-wide.**

## Clearing and retirement

A suppression ends in one of five ways.

1. **The catalog fingerprint changed.** Checked before the suppression check and whether or
   not a window is currently open, so a package with accumulated-but-not-yet-tripped history
   also starts clean. This is the central lever: publish the fix, and the whole fleet resumes
   on its next run without anyone touching a machine.
2. **The window expired.** The served-cycle count increases and the rest of the history is
   retired with the window. Retiring the counters is essential — otherwise the same evidence
   instantly re-trips the same thresholds and the package is never actually retried.
3. **The package converged.** A full reset, including the served-cycle count.
4. **An operator cleared it** with `--clear-loop <name>` or `--clear-loop all`.
5. **A legacy indefinite window was migrated.** State written by older clients as a
   never-ending window is rewritten to end `LoopMaxTime` days after the last attempt, both at
   load time and when the package is next evaluated.

### The `cleared_at` watermark

Both the whole state and each package carry a `cleared_at` timestamp. When history is rebuilt
from the event logs, install events older than the later of those two watermarks are skipped —
while still marking their session as processed, so they cannot be recounted later.

This is what makes a clear stick. Without it, the 7-day history rebuild on the very next run
would re-read the same events that produced the suppression and immediately re-create it, so
every clear would silently undo itself.

A reset also clears the processed-session set. Otherwise the session count would be recomputed
from every session ever seen, snap straight back above the threshold, and the package would
re-suppress after three fresh installs instead of three fresh sessions.

## Configuration

Set in `C:\ProgramData\ManagedInstalls\Config.yaml`.

| Key | Default | Effect |
|---|---|---|
| `LoopGuardEnabled` | `true` | When `false`, LoopGuard never suppresses, ignores any persisted state, and accumulates no new state |
| `LoopMaxTime` | `7` | The cap, in days, on any suppression window |
| `LoopReprobeHours` | `24` | The window opened by the first-install convergence probe, capped by `LoopMaxTime` |

```yaml
LoopGuardEnabled: true
LoopMaxTime: 7
LoopReprobeHours: 24
```

Turning LoopGuard off:

```yaml
LoopGuardEnabled: false
```

When disabled, the client logs a notice near the start of the run saying loop suppression is
off. Because no state accumulates while disabled, re-enabling later does not instantly
suppress packages based on attempts logged during the disabled window. Loop warnings in
`items.json` are produced by a separate reporting path and are not affected by this setting.

Confirm the effective values:

```powershell
managedsoftwareupdate --show-config
```

To bypass suppression for one package on one run without disabling anything, use
`--item <name>`.

## Operator commands

Show every suppressed package with its full diagnostic record:

```powershell
managedsoftwareupdate --loop-status
```

```
ExampleApp:
  Attempts: 8 across 6 sessions
  Last version: 4.2.1.0
  Catalog fingerprint: a1b2c3d4e5f67890
  Cycles served: 1
  Last attempt: 2026-09-03 04:00
  Last success: 2026-09-03 04:00
  Versions attempted: 4.2.1.0 (8x)
  Needs install because installs[0] file C:\Program Files\Example App\ExampleApp.exe: file version 4.1.0.0 is older than the catalog's 4.2.1.0 [version_outdated, unchanged over all 8 attempts]
  Trigger last seen: 2026-09-03 04:00
  Cache: HIT — C:\ProgramData\ManagedInstalls\Cache\ExampleApp-4.2.1.0.exe
  Diagnosis: Loop is install/status-check issue, not download
  Suppressed until: 2026-09-10 04:00
  Reason: installed 8 times across 6 sessions
```

The cache line is a triage shortcut. A cache hit means the payload is on disk, so the loop is
in the install or the status check rather than in downloading.

Clear one package:

```powershell
managedsoftwareupdate --clear-loop ExampleApp
```

Clear every package:

```powershell
managedsoftwareupdate --clear-loop all
```

Both always exit 0, reporting either how many suppressions they cleared or that there was
nothing to clear.

Read the current suppression report directly:

```powershell
Get-Content 'C:\ProgramData\ManagedInstalls\reports\loop_suppressed.json' | ConvertFrom-Json
```

Read the raw state:

```powershell
(Get-Content 'C:\ProgramData\ManagedInstalls\reports\state.json' | ConvertFrom-Json).loop_guard.packages
```

## Diagnosing a looping package

**1. Confirm it is suppressed, and read the cause.**

```powershell
managedsoftwareupdate --loop-status
```

The `Needs install because ...` line is the whole diagnosis. Note the reason code in brackets
and whether it says `unchanged over all N attempts`.

**2. Read what the code is telling you.**

| Reason code | What the pkgsinfo is claiming | Where to look |
|---|---|---|
| `version_outdated` | a version the payload never reaches | the version in the failing `installs` entry, or the item's `version`, against what the machine actually reports |
| `hash_mismatch` | a checksum the installed file never has | the `md5checksum` on the failing entry; a payload rebuilt after the pkgsinfo was written |
| `file_missing` | a path the installer never creates | the path itself — check for a per-user path, a locale-dependent path, or a typo |
| `product_code_missing` | an MSI identity the machine does not register | prefer `upgrade_code` over `product_code` for products that rebuild each release |
| `installcheck_needed` | an install-check script that always says yes | the script's exit codes; remember exit 0 means install needed |
| `not_installed` | a package the detection cannot see at all | whether an MSIX `identity_name` matches the real package identity |

`unchanged over all N attempts` means the criterion is stuck and the fix is in the pkgsinfo.
Varying reasons mean something on the machine is changing between runs — another installer,
another package's `installs` entry, or a user action.

**3. Check the run's own record.** Verify by hand, on the machine, that the thing the entry
checks is actually what the installer produces. If the entry names a path, look at the path
and read its version resource. If it names a product code, look it up in the uninstall hive.
The check has to describe something that exists after the install.

**4. Look for the classic shapes.** A checked launcher stub or shortcut, a version floor more
precise than the payload's version resource after normalisation, two pkgsinfo checking the
same file against different hashes, or a post-install step that silently did not run. See
[Installs Arrays](Installs-Arrays) and [Version Comparisons](Version-Comparisons).

**5. Fix the pkgsinfo and republish.** Correct the detection, rebuild the catalog, and
publish. The changed fingerprint clears the suppression on every machine at its next run. You
do not need to touch endpoints.

**6. Only then clear by hand, and only if you have to.** A manual clear is for the cases where
the fix was outside the catalog — machine state, an external dependency, a client update that
has not reached the machine — or when you want to retry before a window expires on a machine
you are actively working on.

**Clearing loop state without fixing the pkgsinfo does not fix anything.** The detection is
still wrong, the package installs and fails to converge again, and LoopGuard re-suppresses it
at the same or a higher tier — with the served-cycle escalation, usually a longer window than
before. Fix the metadata first; the clear is the last step, not the first.

## See also

- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Installs Arrays](Installs-Arrays)
- [Version Comparisons](Version-Comparisons)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Client Configuration](Client-Configuration)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Logging](Logging)
- [Item Status Reference](Item-Status-Reference)
- [Troubleshooting](Troubleshooting)
