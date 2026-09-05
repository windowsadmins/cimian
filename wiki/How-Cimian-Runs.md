# How Cimian Runs

Cimian is not a daemon that decides for itself when to work. A scheduled task
runs the client hourly, a Windows service watches for trigger files so that
something else can ask for a run right now, and you can invoke the client
directly. This page covers all three, the run modes they produce, and how to
change the cadence.

## Scheduled tasks

Installing Cimian registers two scheduled tasks. Both run as `SYSTEM` with the
highest privileges, and both are hidden from the default Task Scheduler view.

### Cimian Managed Software Update Hourly

This is the task that actually deploys software.

- Action: `managedsoftwareupdate.exe --auto`, with the Cimian install directory
  as the working directory.
- Trigger: a single trigger five minutes after installation, repeating every hour
  indefinitely.
- Execution time limit: 4 hours. A first run on a freshly imaged machine installs
  the whole manifest, and large packages carry download timeouts of their own, so
  the limit is generous by design.
- Restart on failure: up to 3 times, 10 minutes apart.
- Runs only when a network is available, starts as soon as possible if a
  scheduled start was missed, wakes the machine to run, starts on battery and is
  not stopped when the machine switches to battery.

Hourly triggers cannot stack. The task's multiple-instances policy is the Windows
default, which ignores a new start while the previous one is still running, so a
three-hour first run is not joined by two more.

### Cimian Watchdog

This task verifies the installation rather than deploying anything.

- Action: `managedsoftwareupdate.exe --self-check`.
- Trigger: once ten minutes after installation, repeating every 4 hours.
- Execution time limit: 5 minutes.

`--self-check` confirms that `managedsoftwareupdate.exe`, `cimitrigger.exe` and
`cimiwatcher.exe` are present in the Cimian install directory and writes
`%ProgramData%\ManagedInstalls\cimian_selfcheck.json` describing what it found.
It exits `0` when healthy, `2` when a binary is missing, and `3` when it could
not write the marker or hit an unexpected error. It installs nothing and repairs
nothing — it only reports drift.

The Watchdog task is registered by the MSI. It is best-effort: if registration
fails, the installation still succeeds, so a client can legitimately have the
hourly task and no watchdog.

No other scheduled task is created. When Cimian installs, it first unregisters
every existing task whose name or description contains `Cimian`, and every task
whose name contains `Automatic Software Update`. Any task you create yourself
with a matching name will be removed by the next Cimian upgrade.

## The watcher service and trigger files

`CimianWatcher` is a Windows service, display name `Cimian Watcher Service`,
started automatically and configured to restart three times at one-minute
intervals if it fails. It exists so that something other than the clock can ask
for a run.

The service polls every 10 seconds — it does not use file-system change
notifications — for two flag files in `%ProgramData%\ManagedInstalls`:

| Flag file | Meaning | Arguments used |
|---|---|---|
| `.cimian.bootstrap` | Run with the status window visible | `--auto --show-status -vv` |
| `.cimian.headless` | Run with no window | `--auto --show-status` |

A flag fires when the file is new, or when its last-write time has advanced since
the service last looked. The service deletes the flag file **before** launching
the client; deletion is the acknowledgement signal that Managed Software Center
and `cimitrigger` wait for.

If a flag file contains a line beginning `Args:`, the rest of that line replaces
the default arguments, and the status window is not launched automatically — the
caller is assumed to be managing its own interface.

Runs are serialised. While one triggered run is in flight, a newly written flag
file is left on disk untouched and consumed on the first poll after the run
exits. This matters because `managedsoftwareupdate` holds a global single-instance
mutex: a second concurrent launch would simply print
`Another instance of managedsoftwareupdate is running. Exiting.` and return 1.

The service is also what applies a staged self-update. It checks for one at
service start, on every 10-second poll when the machine is idle, and immediately
after any flag-triggered run finishes. See [Updating Cimian](Updating-Cimian).

`.cimian.bootstrap` does double duty: it is both the GUI trigger file and the
persistent bootstrap-mode flag. Because the service deletes it before launching
the client, a service-triggered run does not see itself as a bootstrap run.

## On-demand runs

`cimitrigger` is the supported way to ask for a run without writing files by
hand. It writes the appropriate flag file, then polls every 500 ms for up to 15
seconds for the service to delete it. If the service never acknowledges,
`cimitrigger` removes its own flag file and falls back to running the client
directly with elevation.

```
cimitrigger gui
```

```
cimitrigger headless
```

To skip the service attempt entirely and elevate directly:

```
cimitrigger --force gui
```

`--force` is a subcommand whose name happens to start with two dashes, and it
takes its mode as a positional argument — `gui` or `headless`. Any other value is
an error.

Managed Software Center triggers runs the same way when a user asks to install or
remove something, passing an `Args:` line so that only the requested items are
processed.

Writing the flag file yourself works and is the pattern used by remediation
scripts run from an MDM:

```powershell
New-Item -Path 'C:\ProgramData\ManagedInstalls\.cimian.headless' -ItemType File -Force
```

There is no named pipe, HTTP listener or WMI provider for triggering a run. The
`--show-status` flag opens a local TCP port, but that exists only so the status
window can attach to a run in progress.

## Run modes

Every session records one run type, derived from the flags it was given:

| Run type | Entered by | Behaviour |
|---|---|---|
| `bootstrap` | `--bootstrap`, or the presence of `.cimian.bootstrap` at the moment the client starts | Loop suppression is disabled entirely; restart and logout actions are performed rather than merely recommended. Self-clears the bootstrap flag only if every install and uninstall in the session succeeded. |
| `auto` | `--auto` | The hourly task's mode. Restarts and logouts are performed. Items are deferred when a user is active. |
| `checkonly` | `--checkonly` | Reports what would happen, writes `InstallInfo.yaml`, installs nothing. Postflight does not run. |
| `installonly` | `--installonly` | Installs pending updates without re-checking status. |
| `manual` | none of the above | A plain interactive run. |

Bootstrap mode has no convergence loop. Each invocation is one session; the
repetition that eventually converges a fresh machine comes from the hourly task.
See [Bootstrapping With Cimian](Bootstrapping-With-Cimian).

`managedsoftwareupdate` requires administrative rights. A non-elevated run aborts
with exit code 1.

## What a run does

In order:

1. Start a session, and mark any previous session whose process is no longer
   alive as aborted.
2. Drain package-script output stranded by a previous interrupted run.
3. Check for administrative rights; abort if absent.
4. Create the working directories and normalise their casing.
5. Run the preflight script, then reload configuration and rebuild the repository
   clients, because preflight may have rewritten `SoftwareRepoURL` or
   `ClientIdentifier`. See [Preflight and Postflight Scripts](Preflight-And-Postflight-Scripts).
6. Fetch the primary manifest and everything it includes, and deduplicate items.
7. Load the catalogs.
8. Validate and clean the download cache, and prune entries past their retention
   age.
9. Check the status of every item and bucket it into install, update, uninstall
   or loop-suppressed. See [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).
10. Apply auto-removal and unused-software removal, if enabled.
11. Apply the `--item` filter, if given.
12. Resolve dependencies. A `--checkonly` run stops here, prints its tables,
    writes `InstallInfo.yaml` and exits 0.
13. Defer items blocked by an install window, by a running blocking application,
    or by an active user in `--auto` mode. See
    [Blocking Applications](Blocking-Applications) and
    [Force Installs and Deadlines](Force-Installs-And-Deadlines).
14. Download and install, splitting any Cimian self-update out to be staged
    rather than installed inline.
15. Uninstall.
16. Write the reports, run the postflight script, then end the session.

A session that crashes with an unhandled exception ends without running
postflight.

## How do I make it run right now

The quickest way, from an elevated console on the machine:

```
managedsoftwareupdate --auto -vv
```

To see the status window as a user would:

```
cimitrigger gui
```

To force a run with no window from a remote management tool, write the headless
flag file and let the service pick it up within 10 seconds:

```powershell
New-Item -Path 'C:\ProgramData\ManagedInstalls\.cimian.headless' -ItemType File -Force
```

To see what would happen without changing anything:

```
managedsoftwareupdate --checkonly -vv
```

To act on one item only:

```
managedsoftwareupdate --auto --item ExampleApp
```

`--item` must be a single flag with multiple values — `--item AppOne AppTwo`.
Repeating the flag is an error.

To run the hourly task on demand rather than the executable directly:

```powershell
Start-ScheduledTask -TaskName 'Cimian Managed Software Update Hourly'
```

If a run appears to do nothing and exits immediately, check whether another
instance already holds the single-instance mutex; the client says so on standard
error and returns 1.

## Changing the schedule

The cadence is not configurable from `Config.yaml`. There is no interval key.
Changing it means changing the scheduled task.

To move the managed run to every four hours:

```powershell
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Hours 4)
Set-ScheduledTask -TaskName 'Cimian Managed Software Update Hourly' -Trigger $trigger
```

To stop automatic runs without uninstalling Cimian:

```powershell
Disable-ScheduledTask -TaskName 'Cimian Managed Software Update Hourly'
```

Both changes are undone by the next Cimian installation or upgrade, which
unregisters every task matching `*Cimian*` and re-registers the hourly task with
its built-in schedule. If you need a non-default cadence to survive upgrades,
re-apply it from your management system after each Cimian deployment.

Disabling or stopping the `CimianWatcher` service stops on-demand triggering and
stops staged self-updates from being applied, but does not affect the hourly
task.

## See also

- [Client Configuration](Client-Configuration)
- [Client Identifier Resolution](Client-Identifier-Resolution)
- [Configuring Clients With Intune](Configuring-Clients-With-Intune)
- [Bootstrapping With Cimian](Bootstrapping-With-Cimian)
- [Updating Cimian](Updating-Cimian)
- [managedsoftwareupdate](managedsoftwareupdate)
- [cimitrigger](cimitrigger)
- [cimiwatcher](cimiwatcher)
- [Preflight and Postflight Scripts](Preflight-And-Postflight-Scripts)
- [Logging](Logging)
