# cimistatus

`cimistatus` is the progress window Cimian shows while
[managedsoftwareupdate](managedsoftwareupdate) is working. It is a small desktop
window with a status line, a progress bar and a collapsible log tail, and it is
launched for you by [cimiwatcher](cimiwatcher) or [cimitrigger](cimitrigger)
rather than run by hand.

Read the first section before anything else: the Cimian source tree contains two
different programs that both produce an assembly named `cimistatus`, and only one
of them ships.

## Two programs, one name

| | Where it comes from | Ships? |
|---|---|---|
| **CimianStatus** — a desktop progress window | `gui/CimianStatus` | **Yes.** This is `C:\Program Files\Cimian\cimistatus.exe`. |
| **cimistatus CLI** — a console status reporter | `cli/cimistatus` | No. It is part of the solution and compiles, but the release build does not produce it and no installer contains it. |

Both projects declare the assembly name `cimistatus`, but the build only maps the
tool name `cimistatus` to the desktop project, and that is the binary the
installers place in `C:\Program Files\Cimian` and that `cimiwatcher` and
`cimitrigger` look for. On a managed client, `cimistatus.exe` is the window.

The console project exists in the source tree and offers `service`, `logs`,
`config` and `diag` subcommands that summarise service state, log directories and
configuration. Because it is not built or shipped, do not write tooling that
depends on it. To get the same information from a client, use
`managedsoftwareupdate --show-config`, `sc query CimianWatcher`, and the session
logs under `C:\ProgramData\ManagedInstalls\logs`.

Everything below describes the shipped desktop program.

## What it does

`cimistatus` is a display for a run that something else started. It does not
check for updates, does not decide anything, and does not install anything. It
opens a loopback TCP listener and renders whatever `managedsoftwareupdate` sends
it: a status line, a detail line, a percentage, and log output.

It is started in two situations:

- **By `cimiwatcher`**, after the watcher consumes a GUI-mode trigger file
  (`.cimian.bootstrap`) that carried no `Args:` line. The watcher runs
  `cimistatus.exe` from the same directory as `managedsoftwareupdate.exe`, with
  no arguments.
- **By `cimitrigger gui`**, which additionally makes sure the window ends up in
  the interactive session and kills a copy that landed in Session 0, where nobody
  could see it.

There is no Start Menu shortcut for `cimistatus`, no scheduled task, no service
registration and no protocol handler. It is not something a user launches. The
user-facing application is [Managed Software Center](Managed-Software-Center).

## Command-line arguments

`cimistatus` accepts no Cimian-specific arguments. The argument array is handed
straight to the .NET generic host, so standard host switches such as
`--environment` are consumed there and anything else is ignored without error.
Passing `cimistatus logs` or `cimistatus --open` to the shipped binary does not
produce console output; it opens the window.

## Interactive and background modes

The mode is chosen by identity, not by an argument. If the process is running as
`SYSTEM`, or `USERPROFILE` is empty, `cimistatus` runs as a headless background
service: it starts the status listener and shows no window at all. Otherwise it
runs interactively and shows the window.

That is why a copy started in the wrong session is useless rather than merely
misplaced — in an interactive session it draws a window, and in Session 0 it
silently becomes a listener with no UI.

A session-scoped single-instance mutex prevents a second interactive copy. A
duplicate launch restores and foregrounds the existing window and exits.

## The window

One window, no tabs, 600 by 550 pixels, draggable by clicking anywhere on it.

| Element | Behaviour |
|---|---|
| Heading | "Managed Software Update" with the product icon. |
| Status line | The engine's current status message. |
| Progress bar | Driven by the percentage the engine reports; indeterminate when no percentage is available. |
| **Log** button | Expands and collapses a read-only log pane, animating the window between 450 and 700 pixels tall. The pane holds the last 500 lines and auto-scrolls. |
| **Copy Log** | Copies the visible log to the clipboard; the label reads "Copied!" for a second. |
| **×** | Closes the window — see the warning below. |

### The close button kills the run

The close button does not just close the window. Its handler kills every running
`managedsoftwareupdate` process, waiting up to five seconds for each, and then
closes. Closing the progress window therefore aborts the software run in
progress, part-way through whatever it was doing. Leave the window open until the
run finishes.

## IPC

`cimistatus` is the **server** and `managedsoftwareupdate` is the client. The
listener is a TCP socket bound to the loopback address only; it is not reachable
from the network.

| Port | Listener |
|---|---|
| 19847 | `cimistatus` |
| 19848 | [Managed Software Center](Managed-Software-Center) |

Two ports exist so that a locked machine showing `cimistatus` at the login window
and a user session running Managed Software Center do not collide. The engine
defaults to 19847 and Managed Software Center passes `--status-port 19848` for
itself.

The wire format is newline-delimited UTF-8 JSON, one message per line, with the
fields `Type`, `Data`, `Percent` and `Error`. `cimistatus` handles the message
types `statusmessage`, `detailmessage`, `percentprogress`, `displaylog` and
`quit`. It does **not** understand `itemStatus`, the per-item lifecycle message
that Managed Software Center uses, so per-item state is never shown here.

`quit` does not close the window. It pins the progress bar to 100% and leaves the
window for the user to dismiss.

There is no named pipe. Loopback TCP is the only channel.

## Files it reads and writes

| Path | Direction |
|---|---|
| `C:\ProgramData\ManagedInstalls\LastRunTime.txt` | read and write |
| `C:\ProgramData\ManagedInstalls\logs\` | read; opened in Explorer |
| `C:\ProgramData\ManagedInstalls\logs\<session>\install.log` | read, tailed |
| `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` | written when it needs to request a run, and deleted again if unconsumed |
| `C:\ProgramData\ManagedInstalls\.cimian.headless` | written when it needs to request a headless run |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` → `AppsUseLightTheme` | read, at startup and when the user changes theme |

There is no preferences file for `cimistatus`. In background mode it logs to the
Windows Event Log.

Its live log display prefers to attach to a `managedsoftwareupdate` process that
is already running; failing that it tails the newest session log file.

## Limitations

- **The window has no button that starts a run.** There is no "Check for Updates"
  or "Run Now" control in the shipped UI. `cimistatus` is a display for a run
  that something else triggered. To start a run, use
  [cimitrigger](cimitrigger) or Managed Software Center.
- **Closing the window kills the run.** See above.
- The window shows overall progress only. Per-item status is not displayed.
- It exposes no configuration: no port option, no log path option, no way to
  suppress it other than supplying an `Args:` line in the trigger file, which
  makes `cimiwatcher` skip launching it.
- It sets no exit code of its own; a normal close exits 0.

## See also

- [cimiwatcher](cimiwatcher)
- [cimitrigger](cimitrigger)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Managed Software Center](Managed-Software-Center)
- [Logging](Logging)
- [Troubleshooting](Troubleshooting)
