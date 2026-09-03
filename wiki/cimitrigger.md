# cimitrigger

`cimitrigger` starts a [managedsoftwareupdate](managedsoftwareupdate) run on
demand. It prefers to ask the [cimiwatcher](cimiwatcher) service to do the work,
because the service already runs as SYSTEM and can therefore start an elevated
run without a UAC prompt. If the service does not answer, `cimitrigger` falls
back to elevating a run itself.

This page covers the commands, how the handoff to the service works, and what to
check when a trigger produces no run.

## Synopsis

```
cimitrigger gui
cimitrigger headless
cimitrigger debug
cimitrigger --force gui
cimitrigger --force headless
```

## Commands

| Command | Argument | Effect |
|---|---|---|
| `gui` | none | Ensure a status window is visible in the console session, then trigger a run through the service, falling back to direct elevation. |
| `headless` | none | Trigger a run through the service with no UI, falling back to direct elevation. |
| `debug` | none | Run the built-in diagnostics. Changes nothing. |
| `--force` | `gui` or `headless`, required | Skip the service entirely and elevate a run directly. |

### The `--force` command takes a positional argument

`--force` is spelled like an option but is a command, and the mode is a
positional argument rather than the option's value. It must be written as two
tokens:

```
cimitrigger --force gui
```

`cimitrigger --force=gui` and a bare `cimitrigger --force` are both errors, and
`--force` cannot be combined with the `gui` or `headless` commands — it replaces
them. The only accepted values are `gui` and `headless`; anything else is
rejected with `Invalid mode: <value> (must be 'gui' or 'headless')`.

### There is no `--direct` option

Older material documents `cimitrigger --direct gui` and
`cimitrigger --direct headless`. No such option exists in Cimian. The equivalent
is `--force`, as above.

## How a trigger reaches the engine

`cimitrigger` does not start `managedsoftwareupdate` itself on the normal path.
It writes a trigger file that the `CimianWatcher` service is polling for:

| Command | Trigger file |
|---|---|
| `gui` | `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` |
| `headless` | `C:\ProgramData\ManagedInstalls\.cimian.headless` |

The file body is three lines — a timestamp, the mode (`GUI` or `headless`), and
`Triggered by: cimitrigger CLI`. It carries no arguments, so the service uses its
own defaults for that mode; see [cimiwatcher](cimiwatcher) for what those are.

Having written the file, `cimitrigger` polls every 500 ms for up to **15
seconds** for the file to disappear. The service deletes the file before it
launches the engine, so deletion is the acknowledgement. If the file is still
there after 15 seconds, `cimitrigger` deletes it itself and elevates a run
directly instead — the same thing `--force` does immediately.

`gui` mode adds three steps around that exchange. If a `managedsoftwareupdate`
process is already running, `cimitrigger` reports that the status window will
attach to it and returns success without triggering anything. After the service
acknowledges, it waits two seconds and checks where the status window landed: a
copy that ended up in Session 0 cannot be seen by anyone, so it is killed and the
run is redone through direct elevation. Otherwise the window is brought forward
in the user's session.

Note that `.cimian.bootstrap` has a second, unrelated meaning: its presence also
puts the *next* run into bootstrap mode. Because the watcher deletes the file
before launching the engine, a run triggered this way does not see itself as a
bootstrap run. See [Bootstrapping With Cimian](Bootstrapping-With-Cimian).

## Relationship to cimiwatcher

`cimitrigger` is a client of the service and does nothing the service could not
be asked to do another way. Writing either trigger file with any tool has exactly
the same effect — which is how an MDM triggers a run, since it can deliver a
remediation script but cannot easily run an interactive command. `cimitrigger`
adds the acknowledgement wait, the session-0 correction, and the direct-elevation
fallback.

If the service is not installed or not running, `cimitrigger gui` and
`cimitrigger headless` still work: they spend 15 seconds waiting, then fall back.
`cimitrigger --force` skips that wait.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | The run was triggered, or a run was already in progress. `debug` always returns 0. |
| 1 | `gui`, `headless` or `--force` could not start a run. `--force` also prints `Error running forced update: <error>` to standard error. |

Exit code 0 means the run *started*, not that it succeeded. The outcome of the
run itself is in the session logs.

## Diagnostics

`cimitrigger debug` runs seven checks and prints a summary. It is the fastest way
to find out which link in the chain is broken:

1. Whether the current process has administrative privileges.
2. Whether the `CimianWatcher` service exists and is running.
3. Whether `C:\ProgramData\ManagedInstalls` exists and is writable.
4. Whether `managedsoftwareupdate.exe`, `cimiwatcher.exe` and `cimistatus.exe`
   are present.
5. Whether a test trigger file can be created and read back.
6. Whether the service consumes that test file within 30 seconds.
7. The current user, domain, machine name and OS version.

The test trigger file is cleaned up afterwards if the service did not consume it.

## When nothing happens

A trigger that produces no run has a small number of possible causes. Work
through them in this order.

**The service is not running.** Check it, and start it if it is stopped:

```
sc query CimianWatcher
```

```
sc start CimianWatcher
```

If the service does not exist at all, the Cimian installation is incomplete —
see [Installing Cimian](Installing-Cimian).

**The trigger file cannot be written.** `cimitrigger` must be able to create a
file in `C:\ProgramData\ManagedInstalls`. Run it from an elevated prompt. If the
file cannot be written, no trigger is ever delivered.

**A run is already in progress.** Only one `managedsoftwareupdate` may run at a
time, and the service serialises its own triggers. If a run is active, the
service leaves the trigger file on disk and consumes it when the current run
finishes — so the trigger is not lost, but `cimitrigger` will time out after 15
seconds and fall back to direct elevation, which then fails its own
single-instance check. Wait for the first run to end.

**The trigger file is still on disk.** If
`C:\ProgramData\ManagedInstalls\.cimian.bootstrap` or `.cimian.headless` is still
present a minute after a trigger, the service is not polling. Check the service
log at `C:\ProgramData\ManagedInstalls\logs\cimiwatcher.log` and the Windows
Application event log:

```
Get-WinEvent -LogName Application | Where-Object { $_.ProviderName -eq 'CimianWatcher' }
```

**The run starts but no window appears.** A status window launched into Session 0
is invisible to the logged-in user. `cimitrigger gui` detects and corrects this,
but a run triggered by writing the flag file directly does not. Use
`cimitrigger gui` rather than writing `.cimian.bootstrap` by hand when a user
needs to see progress.

**Everything checks out and the run still does nothing.** The trigger worked and
the engine decided there was nothing to do. Confirm with a check-only run, which
prints the pending tables:

```
managedsoftwareupdate -vv --checkonly
```

As a last resort, bypass `cimitrigger` altogether and elevate the engine
yourself:

```
PowerShell -Command "Start-Process -FilePath 'C:\Program Files\Cimian\managedsoftwareupdate.exe' -ArgumentList '--auto','--show-status','-vv' -Verb RunAs"
```

## Notes

- `cimitrigger` has no options of its own — no verbosity flag, no timeout
  override, no way to pass arguments through to `managedsoftwareupdate`. To
  control the engine's arguments, write the trigger file yourself with an
  `Args:` line; see [cimiwatcher](cimiwatcher).
- The 15-second acknowledgement window is fixed and cannot be configured. It is
  longer than the service's 10-second poll interval, so a healthy service always
  answers in time.
- `debug` writes a real trigger file to the real path. On a machine with a
  working service, running `cimitrigger debug` will start an update.

## See also

- [cimiwatcher](cimiwatcher)
- [managedsoftwareupdate](managedsoftwareupdate)
- [cimistatus](cimistatus)
- [How Cimian Runs](How-Cimian-Runs)
- [Bootstrapping With Cimian](Bootstrapping-With-Cimian)
- [Troubleshooting](Troubleshooting)
