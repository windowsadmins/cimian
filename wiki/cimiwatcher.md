# cimiwatcher

`cimiwatcher` is the `CimianWatcher` Windows service. It watches two trigger
files for changes and launches [managedsoftwareupdate](managedsoftwareupdate)
when one appears. Because the service runs as LocalSystem, a run it starts is
already elevated — that is the whole point of it. Anything that can write a file
into `C:\ProgramData\ManagedInstalls` can therefore start a managed software run
without a UAC prompt, which is how Managed Software Center, `cimitrigger` and an
MDM remediation script all do it.

The same executable is both the service and the command-line tool that manages
it. Which one you get depends on how the process was started: launched by the
Service Control Manager it runs the watcher; launched from a shell it parses the
subcommands below.

## Synopsis

```
cimiwatcher install
cimiwatcher remove
cimiwatcher start
cimiwatcher stop
cimiwatcher pause
cimiwatcher continue
cimiwatcher status
cimiwatcher debug
```

## Subcommands

| Subcommand | Effect |
|---|---|
| `install` | Register the `CimianWatcher` service. |
| `remove` | Stop and delete the service, and unregister its event-log source. |
| `start` | Start the service. |
| `stop` | Stop the service. |
| `pause` | Pause the service. |
| `continue` | Resume a paused service. |
| `status` | Print `Service CimianWatcher: <state>`, or `Service CimianWatcher is not installed`. |
| `debug` | Run the watcher in the foreground as a console program, logging to the console as well as the log file. Ctrl+C stops it. |

There are no options. Unrecognised tokens are an error rather than being ignored.
Every subcommand returns 0 on success and 1 on failure, including `status`, which
returns 1 when the service is not installed.

`install`, `remove`, `start`, `stop`, `pause` and `continue` all change service
state and must be run from an elevated prompt. Note that the removal subcommand
is `remove`, not `uninstall`.

## How it installs and registers

`cimiwatcher install` does not use a Windows Installer service table; it shells
out to `sc.exe`. The result is a service with these properties:

| Property | Value |
|---|---|
| Service name | `CimianWatcher` |
| Display name | `Cimian Watcher Service` |
| Description | `Monitors for Cimian bootstrap flag files and triggers managed software updates` |
| Binary path | the `cimiwatcher.exe` that registered it, with the argument `service` |
| Start type | Automatic |
| Account | LocalSystem (no account is passed to `sc create`) |
| Recovery | Restart after 60 seconds, three times, with the failure count resetting after 86400 seconds |

It also registers an Application event-log source named `CimianWatcher`.

If the service already exists, `install` prints
`Service CimianWatcher already exists, skipping installation` and succeeds
without changing anything. To change a registration, `remove` first and then
`install`.

The Cimian installer runs `cimiwatcher install` for you, so you normally only use
these subcommands when repairing an installation.

## Trigger files

The watcher polls for exactly two files, both directly under
`C:\ProgramData\ManagedInstalls`:

| File | Mode |
|---|---|
| `.cimian.bootstrap` | GUI |
| `.cimian.headless` | headless |

The poll interval is **10 seconds**. This is polling, not a filesystem change
notification, so a trigger is picked up within 10 seconds rather than instantly,
and a trigger written while the service is stopped is still found when it starts.

A file fires a run when it is new, or when its last-write time has advanced past
the value seen on the previous poll. Touching an existing trigger file is
therefore enough to fire another run.

Runs are serialised. If a run is already active when a trigger file is seen, the
watcher leaves the file on disk untouched, so the next poll after the current run
finishes picks it up. Nothing is queued and nothing is lost, but two triggers
arriving during one run produce one further run, not two.

When the watcher does consume a trigger, it **deletes the file before launching
the engine**. Callers use that deletion as the acknowledgement that the trigger
was accepted — both [cimitrigger](cimitrigger) and Managed Software Center poll
for it.

### The `Args:` line

The trigger file's contents are normally ignored, and the watcher uses its own
defaults. If any line in the file starts with `Args:` (case-insensitive), the
rest of that line replaces the default arguments entirely, and the watcher does
**not** launch `cimistatus` — a caller that specifies its own arguments is
assumed to be managing its own UI.

This is how Managed Software Center asks for a check-only or install-only run
while reporting progress to its own window. To request a check-only run with
verbose logging, write a file containing:

```
Args: --checkonly --show-status -vv --status-port 19848
```

Only the first token match matters: the text after `Args:` is passed to
`managedsoftwareupdate` as its whole command line, so it must be a valid argument
string in its own right. There is no validation, and a malformed line produces a
parse error from the engine, visible only in the logs.

## GUI and headless behaviour

| | `.cimian.bootstrap` (GUI) | `.cimian.headless` |
|---|---|---|
| Default arguments | `--auto --show-status -vv` | `--auto --show-status` |
| Console window | shown | suppressed |
| `cimistatus` launched | yes, unless an `Args:` line was present | no |

In GUI mode with no `Args:` line, the watcher starts `cimistatus.exe` from the
same directory as `managedsoftwareupdate.exe` so the user has a progress window.
See [cimistatus](cimistatus) for what that window does, and for the caveat about
which session it lands in.

## Self-updates

The watcher applies staged Cimian self-updates. It checks once at service start
and again on every poll, and defers while a triggered run holds the slot or while
any `managedsoftwareupdate` process exists — so an update to Cimian itself is
never applied on top of a running session. It checks once more after a run it
started finishes. See [Updating Cimian](Updating-Cimian).

## Logging

The service writes to `C:\ProgramData\ManagedInstalls\logs\cimiwatcher.log`,
rolling daily and keeping seven files. The minimum level is Information; framework
noise is suppressed to Warning.

Running as a service it also writes to the Windows Application event log under
the source `CimianWatcher`. In `debug` mode it writes to the console instead of
the event log. To read the event-log side:

```
Get-WinEvent -LogName Application | Where-Object { $_.ProviderName -eq 'CimianWatcher' }
```

## Triggering a run from an MDM

An MDM cannot usually run an interactive command against a client, but it can
deliver a script. Since the watcher's entire interface is a file, a one-line
remediation script is enough. As an Intune remediation, use a detection script
that always exits 1 and a remediation script that writes the trigger file:

```powershell
New-Item -Path 'C:\ProgramData\ManagedInstalls\.cimian.bootstrap' -ItemType File -Force
```

Use `.cimian.headless` instead if no user should see a window. To control the
run's arguments, write the `Args:` line as the file's content:

```powershell
Set-Content -Path 'C:\ProgramData\ManagedInstalls\.cimian.headless' -Value 'Args: --auto -vv'
```

The remediation script runs as SYSTEM, which can write to that directory. The
run itself starts within 10 seconds. See
[Deploying Cimian With Intune](Deploying-Cimian-With-Intune).

## Troubleshooting a watcher that does not react

**Confirm the service is running.**

```
cimiwatcher status
```

If it reports that the service is not installed, register it:

```
cimiwatcher install
```

```
cimiwatcher start
```

**Watch it work in the foreground.** `cimiwatcher debug` runs the same watcher
code as a console program with console logging. Stop the service first, or the
two will compete for the same trigger files:

```
cimiwatcher stop
```

```
cimiwatcher debug
```

Then write a trigger file from another window and watch the console.

**The trigger file stays on disk.** The watcher deletes a trigger file it
accepts. A file that persists for more than a minute means either the service is
not running, or a `managedsoftwareupdate` run is already active and the watcher
is deliberately holding the trigger back. Check for a running engine process
before concluding the service is broken.

**The file disappears but nothing installs.** The trigger worked; the engine ran
and decided there was nothing to do, or failed. Look at the session logs rather
than the watcher log — see [Logging](Logging).

**Runs fire repeatedly.** Anything that rewrites the trigger file advances its
last-write time and fires another run. A scheduled task or configuration profile
that recreates the file on a schedule will trigger a run every time.

## Notes

- The service has no configuration of its own: no interval setting, no path
  override, no argument defaults you can change. The poll interval and the
  default argument strings are fixed.
- The `service` subcommand exists but is hidden and internal; the Service Control
  Manager passes it. Do not use it directly.
- The watcher does not check whether the arguments in an `Args:` line are valid,
  and it does not report the engine's exit code anywhere except its own log.

## See also

- [cimitrigger](cimitrigger)
- [managedsoftwareupdate](managedsoftwareupdate)
- [cimistatus](cimistatus)
- [Installing Cimian](Installing-Cimian)
- [Bootstrapping With Cimian](Bootstrapping-With-Cimian)
- [Updating Cimian](Updating-Cimian)
- [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
- [How Cimian Runs](How-Cimian-Runs)
- [Logging](Logging)
