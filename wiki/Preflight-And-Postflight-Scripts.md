# Preflight And Postflight Scripts

Cimian can run one PowerShell script immediately before a session does any work, and one
immediately after the session's reports are on disk. They are the two supported hooks for
adjusting client configuration before a run and for handing a run's results to something
else afterwards. This page covers where the scripts live, exactly when each runs, what a
non-zero exit does, where the output goes, and the limits you need to design around.

## Where the scripts live

The client looks for each script in two places and uses the first one that exists:

| Order | Preflight | Postflight |
|---|---|---|
| 1 | `%ProgramFiles%\Cimian\preflight.ps1` | `%ProgramFiles%\Cimian\postflight.ps1` |
| 2 | `%ProgramData%\ManagedInstalls\sbin\preflight.ps1` | `%ProgramData%\ManagedInstalls\sbin\postflight.ps1` |

If neither path exists the hook is a no-op and the run continues normally. There is no
`preflight.d` directory and no support for more than one script per hook — if you need
several steps, call them from the one script.

The install directory is checked first, so a script shipped inside the Cimian package wins
over one dropped into `sbin`. Deploy your own script to `%ProgramData%\ManagedInstalls\sbin`
unless you deliberately want it replaced by the next client upgrade.

## How the script is invoked

Cimian runs the script as an external process:

```
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File <script path>
```

Windows PowerShell 5.1 at `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` is
preferred. If it is missing, Cimian falls back to PowerShell 7 (`pwsh.exe`) and then to
whatever `powershell` resolves to on `PATH`. Write for 5.1 unless you know your fleet.

The working directory is set to the directory holding the script, and `TERM` is set to
`xterm-256color` so ANSI colour in your output survives.

The scheduled task that drives the hourly run executes as `SYSTEM`, so both scripts normally
run as `SYSTEM` with no user profile, no mapped drives, and no interactive desktop.

### No arguments are passed

**Cimian passes the script no arguments at all.** In particular there is no run-type
argument: a script cannot tell from its parameters whether the session was `auto`,
`manual`, `bootstrap`, `checkonly` or `installonly`. Any `param()` block you write receives
nothing.

The run type is recorded in the session metadata rather than handed to the hook. A postflight
script that needs it can read the most recent entry from the session report:

```powershell
$sessions = Get-Content 'C:\ProgramData\ManagedInstalls\reports\sessions.json' -Raw |
    ConvertFrom-Json
$sessions | Sort-Object start_time -Descending | Select-Object -First 1 -ExpandProperty run_type
```

There is no equivalent for preflight — at preflight time the session has started but the
reports have not been regenerated, so the newest entry in `sessions.json` still describes an
earlier run.

## When each script runs

Preflight runs early, after the client has checked it is elevated and created its
directories, and **before** manifests are retrieved:

1. Session log opened, stale sessions reaped
2. Elevation check
3. Directories created, local manifests and catalogs cleared
4. **Preflight**
5. Configuration reloaded and the manifest, catalog, download and installer services rebuilt
6. Manifest retrieval
7. Catalog loading
8. Cache validation and retention sweep
9. Status checking, dependency resolution
10. Deferral filters, downloads, installs, uninstalls

Step 5 is the point of the hook. After preflight returns, Cimian re-reads
`%ProgramData%\ManagedInstalls\Config.yaml` and reconstructs the services that depend on it,
so a preflight script that rewrites `SoftwareRepoURL` or `ClientIdentifier` affects the
session it is running in. Anything the script changes before this point takes effect; the
config as it stood when the process started is discarded.

Postflight runs at the very end, after the session's reports have been written and before
the session is closed:

```
CollectSessionItems
WriteInstallInfo
reports regenerated (sessions.json, events.json, items.json, loop_suppressed.json)
POSTFLIGHT
session ended
```

That ordering is deliberate. The reports exist on disk by the time postflight starts, so a
script whose job is to hand this session's results to a reporting client reads this session's
results, not the previous one.

### When postflight does not run

- **`--checkonly` never runs postflight.** A check-only session prints its tables, writes
  `InstallInfo.yaml`, ends the session and returns. The postflight hook is not on that path.
- **An unhandled exception never runs postflight.** If the run fails with an unexpected
  error, the session is ended directly as `failed` and the hook is skipped.
- **A preflight abort never runs postflight.** See below.
- `--no-postflight`, or `NoPostflight: true` in `Config.yaml`, skips it.

If your reporting depends on postflight, be aware that the machines you most want data from —
the ones whose runs are crashing — are the ones that will not send it.

## Exit codes

### Preflight

A non-zero exit is handled according to `PreflightFailureAction` in `Config.yaml`, default
`continue`:

| `PreflightFailureAction` | Behaviour on non-zero exit |
|---|---|
| `continue` (default) | Logs a warning, runs the rest of the session |
| `warn` | Logs a warning, runs the rest of the session |
| `abort` | Logs an error and ends the session as `failed` — no manifests, no installs, and no postflight |
| any other value | Treated as `continue` |

`warn` and `continue` differ only in the wording of the log line.

### Postflight

A non-zero exit **logs a warning and nothing else.** It does not fail the session, does not
change the process exit code, and does not stop the session being recorded as completed.

`PostflightFailureAction` is a recognised key in `Config.yaml` and is printed by
`managedsoftwareupdate --show-config`, but **the engine never reads it.** Setting it to
`abort` has no effect whatsoever. Treat postflight as best-effort.

## Output

Both scripts have their standard output and standard error captured and streamed to the
console line by line while they run, so a script's progress is visible in an interactive run
and in whatever captures the scheduled task's output.

That live stream is *not* written into the session log. The captured output reaches
`install.log` and `reports\run.log` only when the script exits non-zero, as part of the
warning or error text — a successful hook leaves no record of what it printed. If you need a
durable trace of what a hook did, have the script write its own log file.

## There is no timeout

Neither hook has a timeout of any kind. The client waits for the process to exit, with no
deadline, no cancellation and no kill. **A preflight script that hangs hangs the entire
run** — no manifests are fetched, nothing installs, and the session stays open until the
process is killed by something else.

The hourly scheduled task carries a four-hour execution time limit, so a hung hook on the
scheduled path is eventually terminated by Task Scheduler rather than by Cimian, and the
session is left to be marked `aborted` by the next run. A hook invoked any other way can hang
indefinitely.

Build the timeout into the script. Anything that talks to the network needs its own bound:

```powershell
$job = Start-Job { Invoke-RestMethod -Uri 'https://cimian.example.com/preflight' }
if (Wait-Job $job -Timeout 60) { Receive-Job $job } else { Stop-Job $job }
Remove-Job $job -Force
exit 0
```

## Skipping and running the hooks by hand

| Flag | Effect |
|---|---|
| `--no-preflight` | Skip preflight for this run |
| `--no-postflight` | Skip postflight for this run |
| `--preflight-only` | Run only the preflight script and exit; 0 on success, 1 on failure |
| `--postflight-only` | Run only the postflight script and exit; 0 on success, 1 on failure |

`NoPreflight` and `NoPostflight` in `Config.yaml` do the same as the two skip flags,
permanently.

The `--preflight-only` and `--postflight-only` paths bypass the update engine completely:
no session is started, no reports are written, and the script's captured output is printed
after it finishes. They are the right way to test a script without triggering a run.

Self-service runs launched from Managed Software Center pass `--no-preflight`, so a preflight
script does not fire when a user clicks Install on an optional item.

## Use case: adjusting client configuration before a run

The classic preflight job is deciding, per machine and per run, which repository and which
manifest this client should use — then writing that into `Config.yaml` so the session picks
it up at step 5.

```powershell
$configPath = 'C:\ProgramData\ManagedInstalls\Config.yaml'
$config = Get-Content $configPath -Raw

$site = if ((Get-CimInstance Win32_ComputerSystem).Domain -like '*.contoso.example') {
    'contoso'
} else {
    'default'
}

$config = $config -replace '(?m)^ClientIdentifier:.*$', "ClientIdentifier: $site-baseline"
Set-Content -Path $configPath -Value $config -Encoding UTF8
exit 0
```

Only the keys the client actually reads are worth writing. `Config.yaml` keys are PascalCase
— see [Client Configuration](Client-Configuration).

Preflight is also where per-run housekeeping belongs: releasing a licence server lock,
dismounting something an installer will conflict with, or checking that a required service is
running before the session tries to install into it.

## Use case: reporting after a run

The classic postflight job is shipping this session's results somewhere. The reports are on
disk before the hook starts, so the script can read them directly:

```powershell
$reports = 'C:\ProgramData\ManagedInstalls\reports'
$payload = @{
    hostname = $env:COMPUTERNAME
    session  = (Get-Content "$reports\sessions.json" -Raw | ConvertFrom-Json |
                Sort-Object start_time -Descending | Select-Object -First 1)
    items    = (Get-Content "$reports\items.json" -Raw | ConvertFrom-Json)
} | ConvertTo-Json -Depth 8

Invoke-RestMethod -Uri 'https://reporting.example.com/ingest' -Method Post `
    -Body $payload -ContentType 'application/json' -TimeoutSec 60
exit 0
```

`items.json` is absent when a session had no managed items to report, so guard the read.
The full set of report files and their schemas is in
[Reporting Data Contract](Reporting-Data-Contract).

Do not put a retry loop with no bound in a postflight script. There is no timeout, and a
reporting endpoint that is down will otherwise pin one `managedsoftwareupdate` process per
hour until the machine is rebooted.

## Limitations to design around

- No arguments, so no run type, no item list, no session id on the command line.
- No timeout on either hook.
- Successful output is not persisted to the session log.
- Postflight is skipped in check-only mode, after a crash, and after a preflight abort.
- `PostflightFailureAction` is accepted and ignored.
- Only one script per hook, and the install-directory copy shadows the `sbin` copy.
- Both run as `SYSTEM` on the scheduled path, with no access to a user's session.

## See also

- [Client Configuration](Client-Configuration)
- [How Cimian Runs](How-Cimian-Runs)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Logging](Logging)
- [Reporting Data Contract](Reporting-Data-Contract)
- [Troubleshooting](Troubleshooting)
