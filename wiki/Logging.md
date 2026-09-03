# Logging

Every run of `managedsoftwareupdate` writes a self-contained session directory plus a
small set of machine-readable reports. This page describes exactly what the client
writes to disk, what goes into each file, how verbosity changes the output, and how
long any of it survives. Read it when you need to find out what a particular run did,
or when you are collecting evidence for a bug report.

For the schemas of the JSON reports, see [Reporting-Data-Contract](Reporting-Data-Contract).

## Where everything lives

The client writes under `%ProgramData%\ManagedInstalls`. The two directories that
matter for logging are `logs` and `reports`:

```
%ProgramData%\ManagedInstalls\
  logs\
    2026-03-04\
      1415\
        install.log
        events.jsonl
        session.json
      1515\
        install.log
        events.jsonl
        session.json
    2026-03-05\
      0902\
        ...
    installs\
    packages\
    selfupdate\
    cimiwatcher.log
  reports\
    run.log
    sessions.json
    events.json
    items.json
    loop_suppressed.json
    state.json
```

`logs` holds the dated session tree and nothing else. Anything that is not a session
goes into a named subdirectory of its own — `installs`, `packages`, `selfupdate` — with
`cimiwatcher.log` as the single exception.

Paths come from `%ProgramData%`, not from a literal `C:\`. A machine with a relocated
`%ProgramData%` moves the whole tree with it.

### Session directories

A session directory is `logs\YYYY-MM-DD\HHMM\` — one directory per day, one
subdirectory per run, named for the local start time to the minute. The session ID
recorded inside the files is `yyyy-MM-dd-HHmm`, matching that path. There are no
seconds anywhere in either.

Two runs starting in the same minute would collide, so the second one gets `_2`
appended to the time directory (and to its session ID), up to `_9`.

Older machines may still carry flat `YYYY-MM-DD-HHmmss` directories at the `logs` root
from a previous layout. Cimian still reads and expires those, but never creates them.

### Directory names are lowercase

`logs`, `reports`, `catalogs`, `icons`, `manifests`, `conditions` and `facts` must be
exactly lowercase on disk. At the start of every run the client checks the real
on-disk casing of each and renames it if it differs, so a machine provisioned before
that convention converges on its own. NTFS treats `Logs` and `logs` as the same
directory, so nothing breaks in the meantime — but a case-sensitive consumer would
otherwise see two names for one thing.

`Cache`, `Receipts` and `SelfUpdateBackup` are deliberately not in that set and keep
their capitalised spellings.

## What a session produces

| File | Location | Behaviour |
|---|---|---|
| `install.log` | session directory | Appended. Human-readable, one line per message. |
| `events.jsonl` | session directory | Appended. One JSON object per line. |
| `session.json` | session directory | Rewritten at start and again at the end of the run. |
| `run.log` | `reports\run.log` | Deleted and recreated at the start of every session. |

There is no `run.log` inside the session directory. `reports\run.log` is a fixed path
for external tooling to tail; it receives the same formatted lines as `install.log`
and is truncated per session rather than accumulating, so it only ever holds the
current or most recent run.

Writes are flushed immediately and guarded by a lock. A failure to write a log line
is swallowed — logging never fails a run.

### install.log and reports\run.log

Both use the same line format: a bracketed local timestamp, a left-padded five
character level, then the message.

```
[2026-03-04 14:15:02] INFO  Cimian managedsoftwareupdate starting (run type: auto)
[2026-03-04 14:15:03] INFO  Client identifier: WORKSTATION-01
[2026-03-04 14:15:04] INFO  Loaded catalog 'Production' (412 items)
[2026-03-04 14:15:09] DEBUG Example App: installs[0] file C:\Program Files\Example App\example.exe not found
[2026-03-04 14:15:09] INFO  Example App 3.2.1 needs install
[2026-03-04 14:15:31] INFO  Downloaded Example-App-3.2.1.msi (48.2 MB in 21s)
[2026-03-04 14:16:12] INFO  Installed Example App 3.2.1
[2026-03-04 14:16:14] WARN  Deferred: Example Utility v2.0.0 (blocking applications running: exampleutil)
[2026-03-04 14:16:20] INFO  Session complete: 1 install, 0 updates, 0 removals, 1 failure
[2026-03-04 14:16:20] ERROR Example Suite 7.1: installation failed (MSI_EXIT=1603)
```

### events.jsonl

One JSON object per line, no indentation, snake_case keys, null fields omitted. This
is the structured record of what the run decided and did. Two event types are
emitted: `status_check` (the outcome of a detection pass over one item) and `install`
(an install, update or removal attempt and its result).

```
{"event_id":"2026-03-04-1415-638761234098765432","session_id":"2026-03-04-1415","timestamp":"2026-03-04T14:15:09.4821337-08:00","level":"DEBUG","event_type":"status_check","package_name":"Example App","package_version":"3.2.1","action":"","status":"pending","message":"installs[0] file C:\\Program Files\\Example App\\example.exe not found","context":{"needs_action":true},"status_reason":"installs[0] file C:\\Program Files\\Example App\\example.exe not found","status_reason_code":"file_missing","detection_method":"installs_array","target_version":"3.2.1"}
{"event_id":"2026-03-04-1415-638761234512345678","session_id":"2026-03-04-1415","timestamp":"2026-03-04T14:16:12.1043882-08:00","level":"INFO","event_type":"install","package_name":"Example App","package_version":"3.2.1","action":"install","status":"completed","message":"Successfully installed Example App 3.2.1","status_reason_code":"install_completed","detection_method":"installs_array","target_version":"3.2.1"}
```

`level` on an `install` event is derived from `status`: `ERROR` when `failed`, `INFO`
when `completed`, `DEBUG` otherwise. `status_check` events are always `DEBUG`, and
carry `needs_action` inside `context`.

### session.json

Written once when the session starts, with `status` set to `running`, and rewritten
at the end with the final status, end time, duration and summary.

```json
{
  "session_id": "2026-03-04-1415",
  "start_time": "2026-03-04T14:15:02.1234567-08:00",
  "end_time": "2026-03-04T14:16:20.7654321-08:00",
  "run_type": "auto",
  "status": "completed",
  "duration_seconds": 78,
  "summary": {
    "total_actions": 2,
    "installs": 1,
    "updates": 0,
    "removals": 0,
    "successes": 1,
    "failures": 1,
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
    "bootstrap": false,
    "check_only": false,
    "install_only": false,
    "auto": true,
    "show_status": false,
    "skip_preflight": false,
    "skip_postflight": false,
    "manifest_target": "",
    "local_manifest": "",
    "client_identifier": "WORKSTATION-01"
  }
}
```

`run_type` is one of `auto`, `manual`, `bootstrap`, `checkonly` or `installonly`. It
is derived from the flags the run was launched with; a run with none of `--auto`,
`--bootstrap`, `--checkonly` or `--installonly` is `manual`.

### Sessions that never finished

A run that is killed mid-session — the process dies, the machine reboots, a scheduled
task is torn down — leaves its `session.json` saying `running` forever, and because
the reports are written at the end of a session, the reports directory keeps
advertising the last run that did finish.

Every new run therefore closes out corpses before it does anything else. It inspects
the 50 most recent session directories, and for any whose `session.json` still says
`running` and whose recorded process ID does not belong to a live process started no
later than that session, it rewrites the file with `status` set to `aborted`, an end
time taken from the last event, and two extra environment fields:

```json
{
  "aborted_reason": "session ended without reaching EndSession while processing Example App",
  "aborted_detected_by": "2026-03-05-0902"
}
```

A session whose process is genuinely still alive is never touched, so a concurrent run
is not clobbered.

## Log levels and verbosity

Five levels appear in the files: `TRACE`, `DEBUG`, `INFO`, `WARN` and `ERROR`.

Verbosity is a counter set by repeating `-v` on the command line, and it controls the
**console only**:

| Flag | Verbosity | Shown on the console |
|---|---|---|
| *(none)* | 0 | Warnings and errors only |
| `-v`, `--verbose` | 1 | Adds info and status messages |
| `-vv` | 2 | Adds per-item detail |
| `-vvv` | 3 | Adds debug output, including item source lines |
| `-vvvv` | 4 | Adds trace output |

Warnings and errors always print regardless of verbosity.

The files are not affected. `install.log` and `reports\run.log` receive every message
at every level on every run, whatever verbosity was requested. Running with `-vvvv`
does not produce a richer log file; it produces a richer terminal.

When stdout or stderr is redirected there is no terminal to read colours from, so each
console line is prefixed with the same `[yyyy-MM-dd HH:mm:ss] LEVEL ` stamp the files
use and the ANSI colouring and box-drawing characters are stripped. A captured
transcript therefore lines up with `reports\run.log`.

The `LogLevel` key in `Config.yaml` is recorded and printed by `--show-config`, but
verbosity is what gates output.

## Logs that are not part of a session

| Path | Contents |
|---|---|
| `logs\installs\` | Verbose `msiexec` and MSIX logs for managed installs, named `<Item>_install.N.log`. The three most recent are kept per item. |
| `logs\selfupdate\` | Verbose MSI logs from Cimian updating itself, named `selfupdate-yyyyMMdd-HHmmss.log`. |
| `logs\packages\<Package>\` | Output from package pre/postinstall and uninstall scripts that ran as MSI custom actions, where there is no console to write to. |
| `logs\cimiwatcher.log` | The watcher service's own rolling log. |

Package script logs are drained into the session log at the start of the next run,
capped at 500 lines per package and phase, and the source file is deleted once it has
been read. A script whose output arrives after the installer exits therefore surfaces
in the *following* session's `install.log`, not the one that ran it.

`cimiwatcher.log` rolls daily and the watcher keeps its own recent files.

## Retention

Retention is a single hard-coded 30-day window over the whole logs tree. **It is not
configurable.** No config key, registry policy or command-line flag changes it.

The sweep runs in the background at the start of every session and covers:

- day directories under `logs` older than 30 days, deleted recursively;
- legacy flat `YYYY-MM-DD-HHmmss` session directories older than 30 days;
- loose files at the `logs` root;
- `logs\selfupdate` and `logs\installs`, file by file;
- `logs\packages`, per package subdirectory, judged by the newest file it contains.

Before the sweep, artefacts left at the logs root by older versions are relocated into
their proper subdirectories, because a file that is rewritten in place never looks old
enough to expire. Files left at the logs root by third-party scripts are deliberately
left alone.

The whole routine is best-effort: a retention failure never fails a run.

Note that the download cache has its own separate retention, governed by
`CacheRetentionDays`, and the JSON reports have their own trimming rules — see
[The-Download-Cache](The-Download-Cache) and
[Reporting-Data-Contract](Reporting-Data-Contract).

## Finding the log for a particular run

The most recent run is always at `reports\run.log`, and it is complete: the file holds
every level regardless of how the run was invoked.

```powershell
Get-Content "$env:ProgramData\ManagedInstalls\reports\run.log" -Tail 100
```

To watch a run as it happens:

```powershell
Get-Content "$env:ProgramData\ManagedInstalls\reports\run.log" -Wait -Tail 20
```

To list the sessions available on the machine, newest first:

```powershell
Get-ChildItem "$env:ProgramData\ManagedInstalls\logs" -Directory -Filter "20*" |
  Sort-Object Name -Descending |
  ForEach-Object { Get-ChildItem $_.FullName -Directory | Sort-Object Name -Descending }
```

To open the log for a run you know the time of — say the 14:15 run on 4 March 2026:

```powershell
Get-Content "$env:ProgramData\ManagedInstalls\logs\2026-03-04\1415\install.log"
```

To find every session that touched one item:

```powershell
Select-String -Path "$env:ProgramData\ManagedInstalls\logs\*\*\install.log" -Pattern "Example App"
```

To read what a run decided about one item, without the surrounding noise:

```powershell
Get-Content "$env:ProgramData\ManagedInstalls\logs\2026-03-04\1415\events.jsonl" |
  ConvertFrom-Json |
  Where-Object package_name -eq "Example App" |
  Select-Object timestamp, event_type, status, status_reason_code, message
```

To find runs that did not complete:

```powershell
Get-ChildItem "$env:ProgramData\ManagedInstalls\logs" -Recurse -Filter session.json |
  ForEach-Object { Get-Content $_.FullName | ConvertFrom-Json } |
  Where-Object status -ne "completed" |
  Select-Object session_id, run_type, status
```

## What to collect when reporting a problem

Collect the whole session directory for the affected run, plus the reports directory
and the client version. That is enough to reconstruct what the run saw, what it
decided, and why.

Cimian's version:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --version
```

The effective configuration, which shows the repo URL, client identifier, catalogs and
cache state actually in use:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --show-config
```

A zip of the evidence:

```powershell
Compress-Archive -Path "$env:ProgramData\ManagedInstalls\logs","$env:ProgramData\ManagedInstalls\reports" -DestinationPath "$env:TEMP\cimian-logs.zip"
```

For an installer failure, add the matching installer log from `logs\installs\`; for a
Cimian update that did not take, add `logs\selfupdate\`; for a machine where no run is
happening at all, add `logs\cimiwatcher.log`.

If you can reproduce the problem, a fresh check-only run at high verbosity gives the
cleanest transcript, and writes a session directory of its own without changing
anything on the machine:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --checkonly -vvv
```

Before attaching anything to a public report, remember that logs carry the machine
name, the signed-in user, your repository URL, and the full list of software the
machine is managing.

## See also

- [Reporting-Data-Contract](Reporting-Data-Contract)
- [Item-Status-Reference](Item-Status-Reference)
- [Troubleshooting](Troubleshooting)
- [Client-Configuration](Client-Configuration)
- [Install-Loop-Prevention](Install-Loop-Prevention)
- [managedsoftwareupdate](managedsoftwareupdate)
