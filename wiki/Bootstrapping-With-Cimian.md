# Bootstrapping With Cimian

Bootstrap mode is how you get a freshly imaged machine from "Cimian is installed" to
"every managed item is present" without anybody logging in. It changes how a run behaves —
loop suppression is off and the run is allowed to reboot or log the machine out — and it
turns itself off once a run completes cleanly. This page covers what bootstrap mode is,
how to enter and leave it, exactly what differs from a normal run, and a worked
provisioning sequence.

If you know Munki, the intent is the same as Munki's bootstrap mode. The mechanism is
different: Cimian does not block the logon window and does not loop internally until the
machine converges.

## When to use it

Use bootstrap mode when a machine has to reach its managed state before it is handed to a
user: after imaging, after a wipe-and-reload, or in a provisioning task sequence. In that
window an install loop is not a real loop — it is a first-run install of thirty things in
a row — and a required reboot should just happen.

Do not leave a machine in bootstrap mode as a steady state. Loop suppression is the
protection that stops a mis-authored item reinstalling itself every hour forever, and in
bootstrap mode that protection is off.

## The sentinel file

Bootstrap mode is a file on disk:

```
C:\ProgramData\ManagedInstalls\.cimian.bootstrap
```

While that file exists, every run of `managedsoftwareupdate` is a bootstrap run. The file
content is informational; only its presence matters.

Two other flag files live beside it and are easy to confuse with it:

| File | Role |
|---|---|
| `.cimian.bootstrap` | bootstrap-mode flag, **and** the watcher's on-demand trigger for a run with the status window |
| `.cimian.headless` | the watcher's on-demand trigger for a run with no window |
| `.cimian.selfupdate` | a staged client self-update, see [Updating Cimian](Updating-Cimian) |

That first row is the trap worth reading twice. `.cimian.bootstrap` serves two purposes.
When the `CimianWatcher` service is running, it treats the appearance of that file as a
request to run now, and it **deletes the file before launching**
`managedsoftwareupdate`. So if you create the file by hand on a machine with the service
running, you get one immediate run that is *not* in bootstrap mode, and the flag is gone.
Set bootstrap mode through the client instead, which is described next.

## Entering bootstrap mode

The supported way is the client's own flag:

```
managedsoftwareupdate --set-bootstrap-mode
```

That writes the sentinel file and exits 0 without running anything. Every subsequent run —
the hourly scheduled task, a run you start by hand — is a bootstrap run until the flag
clears.

To run a single session in bootstrap mode without persisting the flag:

```
managedsoftwareupdate --auto --bootstrap
```

`-b` is the short form. This is what you want inside a provisioning script that runs the
client itself: the behaviour applies to that one session and nothing is left on disk for
the watcher to trip over.

Note that nothing in the installer sets bootstrap mode. A machine does not come out of an
MSI install in bootstrap mode; your provisioning step has to ask for it.

## Leaving bootstrap mode

**Automatically.** At the end of a bootstrap session, if every install *and* every
uninstall in that session succeeded, the client deletes the sentinel file. One clean run
ends bootstrap mode. One failed item keeps the machine in bootstrap mode, and the hourly
task will try again in bootstrap mode an hour later.

**Explicitly.** To turn it off regardless of outcome:

```
managedsoftwareupdate --clear-bootstrap-mode
```

Use that when you are done provisioning and something is failing for a reason you have
decided to accept. Leaving the flag set means loop suppression stays off indefinitely.

## What differs from a normal run

A bootstrap run reads the same manifests, the same catalogs and the same pkgsinfo, and
makes the same install decisions. Four things differ.

**Loop suppression is disabled entirely.** [LoopGuard](Install-Loop-Prevention) never
suppresses an item in bootstrap mode and never defers one for a pending restart, because
first-run provisioning has to be allowed to complete. Repeated installs during bootstrap
are not counted against an item.

**A run may reboot the machine.** An item whose `restart_action` requires a restart
triggers `shutdown.exe /r /t 300` — a real reboot with a five-minute grace period and a
notification. In an interactive non-bootstrap, non-`--auto` run the client only logs a
recommendation instead.

**A run may log the user out.** An item that requires a logout triggers an immediate
`shutdown.exe /l`. Again, outside bootstrap and `--auto` this is only a recommendation.

**The session is tagged.** The session's run type is recorded as `bootstrap` in the
session logs and reports, so you can tell provisioning runs apart from routine ones when
reading [Logging](Logging) or the reporting data.

What does **not** differ, despite the name: there is no convergence loop. A bootstrap run
is one session. It checks status once, installs what it found, and exits. If item B only
becomes installable after item A's reboot, item B gets installed on the *next* run — which
is the hourly scheduled task five minutes to an hour later, or a run you trigger yourself.
Nothing in the client repeats until nothing is pending.

There is also no logon blocking. Bootstrap mode does not hold the logon screen, does not
change the run interval, and does not gate the graphical interface.

## Combining with the watcher for provisioning

`CimianWatcher` is what lets an external system ask for a run immediately rather than
waiting up to an hour for the scheduled task. It polls every 10 seconds for
`.cimian.bootstrap` and `.cimian.headless`, deletes the flag it finds, and launches
`managedsoftwareupdate --auto --show-status -vv` for the first or
`managedsoftwareupdate --auto --show-status` for the second. Only one triggered run happens
at a time; if a run is already in flight the flag is left in place and re-fires on a later
poll.

Because the watcher consumes the flag, the durable way to combine the two is: set
bootstrap mode with the client, then use the flag file only as a "run now" nudge. The
persistent mode comes from `--set-bootstrap-mode`; the immediacy comes from the trigger.

The flag file may carry an `Args:` line whose remainder replaces the default arguments,
which is how a caller supplying its own interface suppresses the status window. That is
covered on [cimiwatcher](cimiwatcher) and
[Deploying Cimian With Intune](Deploying-Cimian-With-Intune).

## A worked provisioning sequence

This runs as SYSTEM at the end of an image or task sequence, after networking is up.

Install the client and point it at the repository:

```powershell
$msi = (Get-ChildItem .\Cimian-*-x64.msi | Sort-Object Name -Descending | Select-Object -First 1).FullName
Start-Process msiexec.exe -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart') -Wait
New-Item -ItemType Directory -Force -Path 'C:\ProgramData\ManagedInstalls' | Out-Null
Set-Content -Path 'C:\ProgramData\ManagedInstalls\Config.yaml' -Value @'
SoftwareRepoURL: https://cimian.example.com/repo
'@
```

Turn on bootstrap mode so that every run until convergence gets bootstrap behaviour,
including the ones after a reboot:

```
& 'C:\Program Files\Cimian\managedsoftwareupdate.exe' --set-bootstrap-mode
```

Run the first session immediately rather than waiting for the hourly task. Run it in the
foreground so the task sequence can see it finish:

```
& 'C:\Program Files\Cimian\managedsoftwareupdate.exe' --auto -vv
```

That session installs what it can. If an item requires a restart, the machine reboots on a
five-minute timer; the hourly task picks the work back up afterwards, still in bootstrap
mode, and continues. When a session finishes with no failed installs or uninstalls, the
sentinel file is deleted and the machine drops into normal operation on its own.

To watch progress from another session while this is running:

```
managedsoftwareupdate --checkonly
```

And to confirm the machine has left bootstrap mode:

```
Test-Path 'C:\ProgramData\ManagedInstalls\.cimian.bootstrap'
```

`False` means bootstrap completed cleanly. `True` after several hourly runs means an item
is failing every time — read that machine's session log, fix the item, and either wait for
the next run or clear the flag if you are accepting the failure.

## See also

- [Installing Cimian](Installing-Cimian)
- [Updating Cimian](Updating-Cimian)
- [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
- [How Cimian Runs](How-Cimian-Runs)
- [Install Loop Prevention](Install-Loop-Prevention)
- [cimiwatcher](cimiwatcher)
- [cimitrigger](cimitrigger)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Logging](Logging)
