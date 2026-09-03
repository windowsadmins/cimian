# Reporting Data Contract

Cimian writes a small set of machine-readable files on every client so that inventory,
monitoring and reporting systems can read the state of a machine without parsing log
text. This page is the contract: which files exist, what each one contains, how long
its contents survive, and the meaning of every field. It is written for whoever builds
the consumer, not for any particular consumer product.

For the human-readable side of the same run, see [Logging](Logging).

## The files

Everything below lives in `%ProgramData%\ManagedInstalls\reports\`, except
`InstallInfo.yaml` and `cimian_selfcheck.json`, which sit one level up in
`%ProgramData%\ManagedInstalls\`.

| File | Contents | Trimming rule |
|---|---|---|
| `sessions.json` | One record per run, newest first | The 100 most recent session directories on disk |
| `events.json` | Structured events from recent runs | Events from the 10 most recent sessions, filtered to the last 48 hours |
| `items.json` | Current snapshot of every managed item | No history trimming; rewritten in full each run |
| `loop_suppressed.json` | Items currently held back by loop protection | Rewritten in full each run, including as an empty array |
| `state.json` | Loop-protection state the client keeps between runs | Persistent; entries retire when a package converges or is cleared |
| `run.log` | Plain-text trace of the current or most recent run | Truncated at the start of every session |
| `latest_run.jsonl` | Copy of the most recent session's `events.jsonl` | Replaced when written |
| `..\InstallInfo.yaml` | The plan and outcome for the current run, in YAML | Rewritten each run |
| `..\cimian_selfcheck.json` | Result of the last `--self-check` | Rewritten each check |

**There is no `packages.json`.** Cimian has never written one. Any consumer looking
for it is looking for a file that does not exist; use `items.json` instead.

All JSON files in `reports\` are indented, use snake_case keys, and omit null-valued
fields. `cimian_selfcheck.json` is the one exception: it is compact, single-line, and
camelCase.

Timestamps written by the client are ISO 8601. Values taken from local time carry an
offset; values explicitly stamped in UTC end in `Z`. Treat a timestamp with no offset
as local to the machine.

### When the files are written

`sessions.json`, `events.json`, `items.json` and `loop_suppressed.json` are written
together, twice per completed run: once after the install pass, so that a postflight
script can read this run's own state, and once again when the session ends. A run that
crashes hard, or is killed, does not write them at all — which is why the next run
closes out the abandoned session before it starts. See [Logging](Logging).

Treat these files as "the last run that finished", not "the last run that started", and
cross-check the newest entry in `sessions.json` against the machine's clock before
trusting `items.json` as current.

## sessions.json

An array of session records, newest first. Each record is exactly the `session.json`
from that run's session directory.

| Field | Type | Notes |
|---|---|---|
| `session_id` | string | `yyyy-MM-dd-HHmm`, local time. A same-minute collision gets an `_N` suffix |
| `start_time` | string | ISO 8601 with offset |
| `end_time` | string \| absent | Absent while the session is running |
| `run_type` | string | `auto`, `manual`, `bootstrap`, `checkonly`, `installonly` |
| `status` | string | `running`, `completed`, `partial_failure`, `failed`, `aborted` |
| `duration_seconds` | integer \| absent | Absent while running |
| `summary` | object | See below |
| `environment` | object | Free-form string, number and bool map |

`summary`:

| Field | Type | Notes |
|---|---|---|
| `total_actions` | integer | Installs, updates and removals planned |
| `installs` | integer | |
| `updates` | integer | |
| `removals` | integer | |
| `successes` | integer | |
| `failures` | integer | |
| `duration` | string \| absent | `hh:mm:ss.fffffff`; omitted when zero |
| `packages_handled` | array of string | Item names touched by the run |

`environment` always carries `hostname`, `user`, `os_version`, `architecture`
(`x64` or `x86`), `process_id`, and `log_version` (currently `"2.0"`), plus the run's
own flags: `verbosity`, `bootstrap`, `check_only`, `install_only`, `auto`,
`show_status`, `skip_preflight`, `skip_postflight`, `manifest_target`,
`local_manifest`, `client_identifier`. A session closed out as abandoned adds
`aborted_reason` and `aborted_detected_by`. Treat the map as extensible: read the keys
you need and ignore the rest.

```json
{
  "session_id": "2026-03-04-1415",
  "start_time": "2026-03-04T14:15:02.1234567-08:00",
  "end_time": "2026-03-04T14:16:20.7654321-08:00",
  "run_type": "auto",
  "status": "partial_failure",
  "duration_seconds": 78,
  "summary": {
    "total_actions": 2,
    "installs": 1,
    "updates": 0,
    "removals": 0,
    "successes": 1,
    "failures": 1,
    "duration": "00:01:18.6419754",
    "packages_handled": ["Example App", "Example Suite"]
  },
  "environment": {
    "hostname": "WORKSTATION-01",
    "user": "SYSTEM",
    "os_version": "10.0.26100",
    "architecture": "x64",
    "process_id": 8124,
    "log_version": "2.0",
    "verbosity": 2,
    "auto": true,
    "client_identifier": "WORKSTATION-01"
  }
}
```

### Session status values

| Value | Meaning | How a consumer should treat it |
|---|---|---|
| `running` | The session started and has not ended | Normal for the newest entry during a run. A `running` session whose process is gone is rewritten as `aborted` by the next run |
| `completed` | The run finished with no failed items | Healthy |
| `partial_failure` | The run finished; one or more items failed | The run itself is fine, the failures are per item. Look at `items.json` |
| `failed` | The run hit an unhandled error | Reports for that run were not regenerated. Investigate the session log |
| `aborted` | The run was killed before it finished, and a later run closed it out | The machine did not complete that run. `environment.aborted_reason` names the last item it was processing |

A machine whose newest session is `aborted` has not been managed successfully, even
though `items.json` may still look healthy — those items are from the last run that
did finish.

## events.json and latest_run.jsonl

`events.json` is an array of event objects gathered from the 10 most recent session
directories and filtered to those timestamped within the last 48 hours. Sessions
contribute newest-session-first; events within one session are in the order they were
written.

`latest_run.jsonl` is a straight copy of the most recent session's `events.jsonl` —
JSON Lines, one object per line, unindented, same schema.

| Field | Type | Notes |
|---|---|---|
| `event_id` | string | `<session_id>-<ticks>`. Unique per event on one machine |
| `session_id` | string | The session that produced it |
| `timestamp` | string | ISO 8601 with offset |
| `level` | string | `TRACE`, `DEBUG`, `INFO`, `WARN`, `ERROR` |
| `event_type` | string | `status_check` or `install` |
| `package_id` | string \| absent | |
| `package_name` | string \| absent | Item name as it appears in the manifest |
| `package_version` | string \| absent | Catalog version at the time of the event |
| `action` | string | `install`, `update`, `uninstall`, or empty on a status check |
| `status` | string | See below |
| `message` | string | Human-readable |
| `duration` | string \| absent | `hh:mm:ss.fffffff` |
| `progress` | integer \| absent | Percentage |
| `error` | string \| absent | Set on a failure |
| `context` | object \| absent | `status_check` events carry `needs_action` (bool) here |
| `installer_type` | string \| absent | `msi`, `exe`, `msix`, `nupkg`, `ps1`, `nopkg`, and so on |
| `status_reason` | string \| absent | Human-readable explanation of the detection result |
| `status_reason_code` | string \| absent | Machine-readable code, see [Item-Status-Reference](Item-Status-Reference) |
| `detection_method` | string \| absent | Which check produced the answer |
| `installed_version` | string \| absent | Version found on the machine, when a check determined one |
| `target_version` | string \| absent | Version the check compared against |

`status` on an `install` event is `pending`, `completed` or `failed`. `status` on a
`status_check` event is the raw detection outcome: `installed`, `pending`, `error` or
`unknown`.

`detection_method` is one of `registry`, `file`, `directory`, `wmi`, `script`, `msi`,
`msix`, `self_update`, `installs_array`, `managed_installs`, `reportmate_usage`, or
`none`.

```json
{
  "event_id": "2026-03-04-1415-638761234512345678",
  "session_id": "2026-03-04-1415",
  "timestamp": "2026-03-04T14:16:12.1043882-08:00",
  "level": "INFO",
  "event_type": "install",
  "package_name": "Example App",
  "package_version": "3.2.1",
  "action": "install",
  "status": "completed",
  "message": "Successfully installed Example App 3.2.1",
  "installer_type": "msi",
  "status_reason_code": "install_completed",
  "detection_method": "installs_array",
  "target_version": "3.2.1"
}
```

Because the file spans several sessions and 48 hours, the same item appears many
times. To reconstruct one item's history, group by `package_name` and order by
`timestamp`; to reconstruct one run, filter on `session_id`.

## items.json

The current snapshot: one record per managed item, with counters accumulated from the
recent event history. This is the file to read for "what is on this machine and what
state is it in".

Two properties of this file trip up consumers:

- Items managed by MDM rather than by Cimian (item types `managedprofile` and
  `managedapp`) are excluded.
- If a run produces no items at all, the file is **not** written and the previous
  version stays on disk. Check the newest `sessions.json` entry to decide whether
  `items.json` is current.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Item name, lowercased, spaces removed |
| `item_name` | string | Item name as in the manifest |
| `display_name` | string \| absent | Catalog display name; falls back to `item_name` |
| `item_type` | string | `managed_installs`, `managed_updates`, `managed_uninstalls` |
| `current_status` | string | One of exactly six values, see below |
| `latest_version` | string | Version the catalog offers |
| `installed_version` | string \| absent | Version found on the machine, when detection determined one |
| `last_seen_in_session` | string | Session ID, only if this run acted on the item; empty string when the item was merely status-checked |
| `last_successful_time` | string | ISO 8601 |
| `last_attempt_time` | string | ISO 8601 |
| `last_attempt_status` | string | Same vocabulary as `current_status` |
| `last_update` | string | ISO 8601 |
| `install_count` | integer | Successful installs in the retained history |
| `update_count` | integer | |
| `removal_count` | integer | |
| `failure_count` | integer | |
| `warning_count` | integer | |
| `total_sessions` | integer | Sessions in which the item was seen |
| `warning_messages` | array of string \| absent | The warning split into its parts; a looping install produces two — what the loop is, and what the package's own checks keep finding |
| `install_loop_detected` | bool | Passive loop flag for dashboards. Not the same as active suppression |
| `loop_details` | object \| absent | `detection_criteria`, `loop_start_session`, `suspected_cause`, `recommendation`, all strings |
| `install_method` | string \| absent | |
| `type` | string | Always `cimian` for items this client manages |
| `package_format` | string \| absent | |
| `package_id` | string \| absent | |
| `developer` | string \| absent | |
| `architecture` | string \| absent | |
| `install_location` | string \| absent | |
| `is_signed` | bool | |
| `signature_status` | string \| absent | |
| `signature_algorithm` | string \| absent | |
| `certificate_subject` | string \| absent | |
| `certificate_thumbprint` | string \| absent | |
| `signature_timestamp` | string \| absent | |
| `signer_certificate` | string \| absent | |
| `signer_common_name` | string \| absent | |
| `developer_name` | string \| absent | |
| `developer_organization` | string \| absent | |
| `sbin_installer` | string \| absent | |
| `pkg_build_version` | string \| absent | |
| `last_error` | string | Empty string when there is no error |
| `last_warning` | string \| absent | The warnings joined into one string |
| `recent_attempts` | array \| absent | Objects of `session_id`, `timestamp`, `action`, `status`, `version` |
| `status_reason` | string \| absent | Human-readable explanation of `current_status` |
| `status_reason_code` | string \| absent | Machine-readable code |
| `detection_method` | string \| absent | |
| `status_determined_at` | string \| absent | ISO 8601 |

```json
{
  "id": "exampleapp",
  "item_name": "Example App",
  "display_name": "Example App",
  "item_type": "managed_installs",
  "current_status": "Installed",
  "latest_version": "3.2.1",
  "installed_version": "3.2.1",
  "last_seen_in_session": "2026-03-04-1415",
  "last_successful_time": "2026-03-04T22:16:12Z",
  "last_attempt_time": "2026-03-04T22:16:12Z",
  "last_attempt_status": "Installed",
  "last_update": "2026-03-04T22:16:12Z",
  "install_count": 1,
  "update_count": 0,
  "removal_count": 0,
  "failure_count": 0,
  "warning_count": 0,
  "total_sessions": 14,
  "install_loop_detected": false,
  "type": "cimian",
  "is_signed": true,
  "last_error": "",
  "status_reason": "All 2 install checks passed",
  "status_reason_code": "file_match",
  "detection_method": "installs_array",
  "status_determined_at": "2026-03-04T22:16:12Z"
}
```

### The status vocabulary in items.json

`current_status` and `last_attempt_status` are normalised to exactly six values.
Anything the client cannot map lands on `Pending`, so treat `Pending` as "not confirmed
installed" rather than as a precise statement.

| Value | Meaning | How a consumer should treat it |
|---|---|---|
| `Installed` | Detection confirmed the item is present at the expected version, or an install completed successfully this run | Compliant |
| `Pending` | The item needs action, or was deferred, or the status could not be resolved | Not compliant, but not an error. Read `status_reason_code` before alerting |
| `Warning` | The item was acted on or held back and needs follow-up, but nothing failed outright | Surface it. This is where loop-suppressed and self-reported-warning items appear |
| `Error` | An install, update or removal failed, or detection itself errored | Alert. `last_error` carries the detail |
| `Removed` | The item was uninstalled, or the manifest asks for its removal and it is gone | Compliant with a removal intent |
| `Not Available` | The item is not obtainable for this machine | Investigate the catalog and manifest |

Two of these carry meaning that is easy to misread, and both are covered in detail in
[Item-Status-Reference](Item-Status-Reference):

- A deferred item — outside its install window, blocked by a running application, or
  held back because a user is active — reports as `Pending`, never as `Installed`. It
  is not on the machine.
- An item suppressed by loop protection reports as `Warning`, not as `Error`. Nothing
  failed; the client deliberately stopped retrying. The exception is a suppression that
  is only waiting for a restart, which reports as `Pending`.

## loop_suppressed.json

An array of the packages currently inside an active loop-suppression window. It is
written on every run, including as `[]` when nothing is suppressed, so an empty array
is a positive statement rather than a missing file.

Entries are pulled from the client's persistent state, not from this run's work, so a
package suppressed several runs ago still appears while its window stands.

| Field | Type | Notes |
|---|---|---|
| `name` | string | Item name |
| `version` | string | Version that was being attempted |
| `reason` | string | The counting rule that tripped, and for how long |
| `suppressed_until` | string \| absent | UTC time the window expires |
| `trigger` | object \| absent | The detection result that keeps deciding the package must run |
| `trigger_summary` | string | Human-readable form of the trigger, including how consistent it has been |
| `clear_command` | string | The literal command an admin can run to clear it |

`trigger` is `reason_code` (a status reason code), `detection_method`, `detail` (the
path, GUID or script output that decided it, flattened to one line and truncated), and
optionally `installed_version`.

```json
{
  "name": "Example App",
  "version": "3.2.1",
  "reason": "Rapid-fire loop: 3 installs within 2 hours; paused for 12h",
  "suppressed_until": "2026-03-05T02:16:12Z",
  "trigger": {
    "reason_code": "file_missing",
    "detection_method": "installs_array",
    "detail": "installs[0] file C:\\Program Files\\Example App\\example.exe not found",
    "installed_version": "3.2.1"
  },
  "trigger_summary": "installs[0] file C:\\Program Files\\Example App\\example.exe not found [file_missing, unchanged over all 3 attempts]",
  "clear_command": "managedsoftwareupdate --clear-loop Example App"
}
```

The distinction that matters when building an alert: `reason` says why the client
stopped retrying, `trigger` says why the package keeps asking to be installed. The
second is the defect; the first is the brake.

## state.json

The client's persistent loop-protection state. It is an implementation surface rather
than a reporting surface — the reportable view is `loop_suppressed.json` — but it is
readable and stable enough to inspect.

The root object is `{"loop_guard": { ... }}`, containing `last_updated`, an optional
`cleared_at` watermark, and `packages`, a map keyed by lowercased item name. Each
package entry records attempt and session counts, the versions attempted, recent
attempt timestamps, the current suppression window and reason, a fingerprint of the
package's install behaviour, and the trigger history.

Do not write to this file. Clear suppression with `managedsoftwareupdate --clear-loop`,
which maintains the watermarks that stop a clear from being undone by the next run's
history rebuild.

An older `loop_state.json` may exist on a machine that has not run a current client; it
is migrated into `state.json` and then removed.

## run.log

Plain text, not JSON. The formatted trace of the current or most recent run, at a fixed
path so tooling can tail it without discovering a session directory first. It is
deleted and recreated at the start of each session, so it never grows without bound and
never holds more than one run. Format and levels are described in [Logging](Logging).

## InstallInfo.yaml

`%ProgramData%\ManagedInstalls\InstallInfo.yaml` is the run's plan and outcome in YAML,
written for the Managed Software Center GUI. It is the machine's answer to "what does
this device think it should have", as opposed to `items.json`, which answers "what
state is each item in".

Top-level keys: `managed_installs`, `managed_updates`, `removals`, `optional_installs`,
`problem_items`, `processed_installs`, `processed_uninstalls`, `featured_items`,
`last_check`.

Items carry `name`, `display_name`, `version_to_install`, `installed_version`,
`description` and the rest of the catalog metadata the GUI renders.

One caveat for consumers: the file is written beside the download cache's parent
directory rather than strictly to `ManagedInstalls`. On a machine with a default
`CachePath` those are the same place. On a machine with a relocated cache, look
alongside the cache directory.

## cimian_selfcheck.json

Written by `managedsoftwareupdate --self-check`, which the watchdog scheduled task runs
periodically. It confirms the client's own binaries are present.

Unlike everything else here it is compact single-line JSON with camelCase keys:
`timestamp` (UTC), `machine`, `installDir`, `requiredBinaries`, `missingBinaries`,
`healthy`, `cimianVersion`.

```json
{"timestamp":"2026-03-04T22:20:00.0000000Z","machine":"WORKSTATION-01","installDir":"C:\\Program Files\\Cimian","requiredBinaries":["managedsoftwareupdate.exe","cimitrigger.exe","cimiwatcher.exe"],"missingBinaries":[],"healthy":true,"cimianVersion":"2026.03.01.0900"}
```

`--self-check` exits 0 when healthy, 2 when a binary is missing, and 3 on an internal
error.

## Building a consumer

A few rules follow from the above.

Read the first entry of `sessions.json` before anything else. Its `status` and
`end_time` tell you whether the rest of the directory is trustworthy and current.

Treat every schema as extensible. Fields are added; read what you need and ignore what
you do not recognise.

Absent is not empty. Null-valued fields are omitted entirely rather than written as
`null`, so distinguish "key missing" from "value empty".

Do not derive compliance from `current_status` alone. `Pending` covers both "will
install on the next run" and "deferred outside its window"; `status_reason_code`
separates them.

Do not treat `Warning` as a failure, or `loop_suppressed.json` as an error list. Loop
suppression is the client protecting the machine from a package that never converges;
the alert belongs on the package, not on the device.

`last_seen_in_session` is empty for items that were only checked. If you want "what did
the last run actually do", filter on a non-empty value.

Nothing in these files is a stable unique identifier for a device. `hostname` in
`environment` is what the client records.

## See also

- [Logging](Logging)
- [Item-Status-Reference](Item-Status-Reference)
- [Troubleshooting](Troubleshooting)
- [Install-Loop-Prevention](Install-Loop-Prevention)
- [How-Cimian-Decides-What-Needs-To-Be-Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Client-Configuration](Client-Configuration)
