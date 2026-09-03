# Item Status Reference

Every item Cimian manages carries a status, and the same item carries slightly
different status vocabularies depending on where you read it: the console, the
structured event stream, or `items.json`. This page lists all of them, says what each
one means, what causes it, and what an admin should do. Read it before you build an
alert on a status value, and read it when a status does not seem to match reality.

The schemas these values live in are in
[Reporting-Data-Contract](Reporting-Data-Contract).

## Three vocabularies, one item

| Where | Values |
|---|---|
| Detection result, per check | `installed`, `pending`, `error`, `unknown` |
| Event stream (`events.jsonl`, `events.json`) | Detection values on `status_check` events; `pending`, `completed`, `failed` on `install` events |
| Item report (`items.json`) | `Installed`, `Pending`, `Warning`, `Error`, `Removed`, `Not Available` |

The item report is the summarised view, and it is the one most consumers read. Its six
values are what the rest of this page is organised around. Alongside them the client
records a `status_reason_code`, which is what actually tells you *why*.

## Installed

The item is present at the expected version, or an install completed successfully
during this run.

Reached when a detection check confirmed presence — `installs` entries all passed, a
registry or file check matched, a `check.script` exited 0, a version comparison found
nothing newer in the catalog — or when an install, update or removal outcome for this
run reported success.

Reason codes that produce it: `file_match`, `registry_match`, `product_code_match`,
`directory_match`, `hash_match`, `version_match`, `script_confirmed`, `wmi_match`,
`self_update_current`, `no_checks`, `install_completed`.

Nothing to do. Two caveats:

`no_checks` means the item declares no way of verifying itself. A script-only item —
installer type `nopkg`, `script`, or empty — with no `installs` array, no
`installcheck_script` and no `check` block is *assumed* installed. It will be reported
`Installed` on a machine where the script never ran successfully. If that matters, give
the item something to check.

An item whose only verification is a file whose version cannot be read, and which
carries no hash, can never be confirmed. Cimian says so in the reason rather than
silently passing.

## Pending

The item is not confirmed installed. It either needs action on the next run, or it was
deliberately held back this run.

This is the widest bucket, and it is also the fallback: any status the client cannot
map to one of the other five becomes `Pending`. Always read `status_reason_code` before
acting on it.

### Pending because the item genuinely needs installing

| Reason code | Meaning | What to do |
|---|---|---|
| `not_installed` | No trace of the item found | Normal for a new assignment. It installs on the next run |
| `update_available` | A newer version is in the catalog | Normal |
| `version_outdated` | The installed version is older than the catalog's | Normal |
| `version_mismatch` | The installed version differs from what was expected | Normal |
| `file_missing` | A path in the `installs` array does not exist | Normal, unless it repeats — see the loop section below |
| `directory_missing` | A directory in the `installs` array does not exist | As above |
| `registry_missing` | The registry entry named by `check.registry.name` was not found | As above |
| `product_code_missing` | The MSI product or upgrade code is not registered | As above |
| `hash_mismatch` | A file exists but does not match the declared checksum | The file on disk is not the file the pkgsinfo describes |
| `installcheck_needed` | The item's `installcheck_script` exited 0, meaning "install needed" | The script is the authority. If this repeats forever, the script is the bug |
| `on_demand` | The item is `on_demand: true` | Expected. See below |
| `dependency_missing` | A `requires` dependency is not installed | It installs first, then the item |

### Pending because Cimian chose not to act this run

**A deferred item reports as `Pending`, never as `Installed`.** This is the single most
important thing on this page. Deferral removes the item from the run's action lists, so
without an explicit rule it would fall through to the default "Installed" — reporting a
package the status check had just found missing as present. A deferred item is pending
by definition: it is not on the machine.

| Reason code | Cause | What to do |
|---|---|---|
| `deferred_install_window` | The current time is outside the item's `install_window` | Nothing. It installs inside the window, or sooner if `force_install_after_date` has passed |
| `blocking_apps` | One of the item's `blocking_applications` is running | Nothing, unless it never clears. The item is skipped for the whole run and retried on the next one |
| `deferred_user_active` | An automatic run found a user active, and the item is not marked `unattended_install`, or its `restart_action` would interrupt the session | Nothing. It installs on an unattended run, or mark the item unattended if it is safe to install under a user |
| `user_deferred` | A user postponed it | Nothing |
| `pending_reboot` | Windows is waiting for a restart | Restart the machine. Also see the loop section below |
| `disk_space` | Not enough free space — Cimian wants twice the installer size | Free space on the machine |
| `network_metered` | The connection is metered and the download is large | Nothing |
| `admin_hold` | The item is on hold | Nothing, unless you placed the hold |
| `download_pending` / `download_failed` | The payload has not arrived | See [Troubleshooting](Troubleshooting) |
| `schedule_waiting` | Waiting for a maintenance window | Nothing |

A machine that reports the same item as `Pending` with `deferred_install_window` every
hour is behaving correctly. A machine that reports it as `Pending` with `blocking_apps`
for days is not: the blocking application is never closing, and the item will never
install.

### Pending because the item is not eligible

| Reason code | Meaning | What to do |
|---|---|---|
| `architecture_mismatch` | The item does not support this machine's architecture | Nothing, if the assignment is intentional. Otherwise fix the manifest |
| `os_version_mismatch`, `os_version_too_old`, `os_version_too_new` | The machine falls outside the item's `minimum_os_version` / `maximum_os_version` | Nothing, or widen the range |
| `agent_version_too_old` | The running Cimian client is older than the item's `minimum_cimian_version` | Update the client — see [Updating-Cimian](Updating-Cimian) |

### Pending because of an on-demand item

An item marked `on_demand: true` is never tracked as installed. It is checked before
any other rule in the detection cascade and always comes back `pending` with reason
code `on_demand`, so that it runs every time it is requested. Its `installcheck_script`
is deliberately not consulted.

This is correct behaviour, not a fault. An on-demand item that shows as `Pending`
forever is doing exactly what it was asked to do, and it is exempt from loop
protection for the same reason. Do not alert on it. See
[On-Demand-Items](On-Demand-Items).

## Warning

The item was acted on, or deliberately held back, and needs a human to look at it — but
nothing failed outright. There are three ways to reach it.

### A package that reported its own warning

An install that succeeded but whose postinstall script emitted a `CIMIAN-WARNING:`
marker line is recorded as `Warning` rather than `Installed`. The install itself is
counted as successful; the message is in `last_warning` and `warning_messages`.

This is how a package says "I installed, but something about the result needs
attention" — a configuration step that could not complete, a credential that did not
match, an optional component skipped.

What to do: read the message. The remedy is specific to the package.

### A package suppressed by loop protection

**An item suppressed by loop protection is not a failure.** Nothing errored; the client
detected that the item was being installed over and over without ever converging, and
stopped retrying it for a while. The status is `Warning` and the reason code is
`loop_suppressed`.

The item carries two messages, and they say different things:

- the *reason* — the counting rule that tripped and how long the pause lasts, for
  example "Rapid-fire loop: 3 installs within 2 hours; paused for 12h";
- the *cause* — what the package's own checks keep finding, for example
  "Needs install because installs[0] file `C:\Program Files\Example App\example.exe`
  not found".

The cause is the defect. In almost every case it is the item's own detection criteria
disagreeing with what the installer actually puts on disk: a path in the `installs`
array that the installer never creates, a version that can never match, a pinned
product code that changes with every build, an `installcheck_script` that always exits
0.

What to do: fix the pkgsinfo so the check matches reality, then clear the suppression
so the item can be retried immediately.

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --loop-status
```

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --clear-loop "Example App"
```

Changing the item's install behaviour in the repo clears suppression on its own — the
client fingerprints the catalog entry and a change to it auto-clears the window. So
does a client update, once, fleet-wide. Suppression windows also expire by themselves,
and an item that starts converging retires its own history.

A first install that succeeds and *immediately* still reports as needing action is
caught on the spot rather than after several sessions, with the reason "installcheck
still reported action needed immediately after a successful install". That is the same
defect found faster.

See [Install-Loop-Prevention](Install-Loop-Prevention) for the thresholds and the
clearing rules.

### A dependency that was suppressed

An item pulled in by another item's `requires` and then suppressed appears in
`items.json` in its own right, as `Warning` with `loop_suppressed`, even though it is
not a manifest entry on that machine.

## Error

Something failed. Either an install, update or removal returned an unsuccessful result,
or the detection itself could not complete.

| Reason code | Cause | What to do |
|---|---|---|
| `check_failed` | The detection cascade threw. Also produced by an `installs` entry with no type and no identifying field, and by an MSIX entry with no `identity_name` | The pkgsinfo is malformed. Fix the entry |
| `script_error` | A detection script failed or timed out | An `installcheck_script` that times out yields an error and, deliberately, **does not** trigger an install — the machine is left alone rather than reinstalled on a guess. The timeout is two minutes |
| *(no code)* | An installer returned a non-zero exit code that is not in the item's `success_codes` | Read `last_error` and the installer log under `logs\installs\` |

For an MSI failure, `last_error` carries `MSI_EXIT=<code>`, extracted diagnostics from
the verbose log, and the log's path. See [Troubleshooting](Troubleshooting).

A failed item is counted in the session's `failures`, and the session ends as
`partial_failure` rather than `completed`.

## Removed

The item was uninstalled successfully this run, or the manifest asks for its removal
and it is not present.

Reason codes: `uninstall_confirmed`, `registry_removed`, `file_removed`,
`script_confirmed_removal`.

Nothing to do. Note that an item can only be removed if Cimian has a way to remove it —
an `uninstaller` block, an `uninstall_script`, a registered MSI product, an MSIX
identity, or an entry in Programs and Features it can drive. An item with
`uninstallable: false` is never removed regardless of the manifest. See
[Uninstalling-Software](Uninstalling-Software).

## Not Available

The item cannot be obtained for this machine. In practice this means the manifest names
something the loaded catalogs do not offer, or offer only for a different architecture.

What to do: confirm the item name spelling in the manifest, confirm the item is in one
of the catalogs the client is configured to read, and confirm the catalog has been
regenerated since the pkgsinfo was added. See [Using-Catalogs](Using-Catalogs).

## Statuses that mislead, in one place

**A deferred item is `Pending`, not `Installed`.** It is not on the machine. Whatever
deferred it — install window, blocking application, active user — the item is still
missing.

**A loop-suppressed item is `Warning`, not `Error`.** Nothing failed. The client
stopped retrying a package whose own checks never agree that it is installed. Fix the
package's detection criteria; do not treat the device as broken.

**A pending-restart item is `Pending`, not `Warning`.** When loop protection holds an
item back purely because it is waiting for a reboot, the install succeeded and a
restart will finalise it. It shares the suppression machinery but is not a warning, and
its reason code is `pending_reboot` rather than `loop_suppressed`.

**An on-demand item is always `Pending`.** By design. It has no receipt and no
installed state, and it is exempt from loop protection.

**An item with `no_checks` is `Installed` on the client's word alone.** Nothing was
verified. A script-only item with no verification is assumed to have worked.

**`Pending` is also the fallback for anything unrecognised.** If a status value cannot
be mapped, it becomes `Pending`. Read `status_reason_code` rather than inferring from
the status alone.

**`last_seen_in_session` is empty for items that were only checked.** An item can be
current and healthy and still carry an empty value there; it means this run did not act
on it, not that it was not evaluated.

## Detection methods

Every status carries the method that produced it, which is often the fastest way to see
why a check disagrees with reality.

| Value | The check that answered |
|---|---|
| `installs_array` | The item's `installs` entries |
| `file` | A file path, version or hash |
| `directory` | A directory path |
| `registry` | A `check.registry` block, or a scan of Programs and Features |
| `msi` | An MSI product code or upgrade code |
| `msix` | An MSIX or APPX package identity |
| `script` | An `installcheck_script`, `version_script` or `check.script` |
| `managed_installs` | Cimian's own install receipt for the item |
| `self_update` | The running client version compared against the catalog |
| `wmi` | A WMI query |
| `reportmate_usage` | Per-user usage data, used by unused-software removal |
| `none` | No check ran — the status came from a rule, not a probe |

`detection_method: none` on a `Pending` item is the signature of a deferral or an
on-demand item: the status was decided by policy, not by looking at the machine.

## See also

- [How-Cimian-Decides-What-Needs-To-Be-Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Reporting-Data-Contract](Reporting-Data-Contract)
- [Troubleshooting](Troubleshooting)
- [Install-Loop-Prevention](Install-Loop-Prevention)
- [Installs-Arrays](Installs-Arrays)
- [On-Demand-Items](On-Demand-Items)
- [Force-Installs-And-Deadlines](Force-Installs-And-Deadlines)
- [Blocking-Applications](Blocking-Applications)
- [Logging](Logging)
