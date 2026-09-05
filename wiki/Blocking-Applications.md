# Blocking Applications

`blocking_applications` lists processes that must not be running when an item is installed,
updated or removed. It is a deferral, not a negotiation: Cimian never asks the user to quit
anything, never waits, and never closes a process for you. This page covers exactly how a process
is matched, what happens in each run mode, and which names are safe to list.

## What it does

```yaml
name: ExampleVendorSuite
version: 3.2.1
blocking_applications:
- Suite.exe
- SuiteHelper.exe
```

Before any downloads begin, the client snapshots the running process list once and walks the
install, update and removal queues. **Any item with a running blocker is removed from its queue
for the whole run.** The check is then repeated immediately before each individual install and
each individual removal, to catch a process the user started during the session.

That is the entire feature. There is no wait, no retry within the session, no countdown, no
prompt, and no attempt to terminate the process. The item simply does not happen this run and is
reconsidered from scratch next run.

## How a running process is matched

For each entry, the client takes the **file name without its extension**, lowercases it, and
compares it for **exact equality** against the lowercased name of every running process.

This means:

- `Suite.exe`, `Suite`, and `C:\Program Files\Example Vendor\Suite\Suite.exe` are all equivalent —
  only the base name is used, so a path is allowed but ignored.
- The match is **exact**, not a substring. `Suite` does not match a process named `SuiteHelper`,
  and `Suite.Manager.exe` reduces to `Suite.Manager`, which matches only a process actually named
  `Suite.Manager`.
- The match is case-insensitive.
- It is **process name, not window title, not product name, not path**. Two different products
  whose executables are both named `Updater.exe` are indistinguishable.

The process list is machine-wide. **A process running in any session blocks the item for the whole
device** — including a service, a process left behind in another user's disconnected session, and
a process running under a different account. On a multi-session or shared machine this is the
usual reason an item defers indefinitely with nobody apparently using it.

If the process list cannot be read at all, the snapshot comes back empty and nothing is treated as
blocked.

## What happens when a blocker is running

The behaviour is identical in every mode. **`blocking_applications` is applied unconditionally —
there is no exemption for an unattended run, and no exemption in bootstrap mode.**

| Mode | Behaviour |
|---|---|
| Attended / interactive run | Item is dropped from the run and logged. No prompt, no dialog, no offer to quit the app. |
| Unattended (`--auto`) run | Identical. `unattended_install` does not override a blocker. |
| Bootstrap (`--bootstrap`) run | Identical. A blocker during bootstrap silently postpones the item to a later run. |
| Removal | Identical. A running blocker skips the uninstall. |

The log line is:

```
Deferred: ExampleVendorSuite v3.2.1 (blocking applications running: Suite.exe)
```

and the item is recorded with reason code `BlockingApps`. In reports and in Managed Software
Center it shows as **Pending Install**, **Pending Update** or **Pending Removal** — never as
installed, and never as failed. A deferred item is not a failure and does not count against
[install-loop prevention](Install-Loop-Prevention).

This differs from Munki, which can present a "quit these applications" dialog and give the user a
chance to comply. Cimian has no such dialog. If a user keeps the app open, the update keeps
deferring, quietly, forever. Pair a blocker with `force_install_after_date` when the update must
eventually land regardless — see [Force Installs And Deadlines](Force-Installs-And-Deadlines).

## Which process names to list

List the smallest set of processes whose presence would actually break the install or destroy the
user's work:

- The product's **own main executable**, and any helper it spawns that holds a file lock in the
  install directory.
- A tray agent or updater that reopens files under the install path.
- The product's **command-line tool**, if it lives in the install directory — see the console
  failure mode below.

Do not list:

- **`powershell`, `pwsh`, `cmd`, `conhost`, `WindowsTerminal`, `explorer`, `svchost`.** These are
  effectively always running, and an interactive `managedsoftwareupdate` run is itself commonly
  launched from a shell. Listing any of them defers the item permanently on every device.
- Background services you control. Stop them from a `preinstall_script` instead — that is a
  precondition you can actually satisfy, whereas a blocker only ever means "give up".
- Generic names shared across vendors (`Updater.exe`, `Setup.exe`, `Launcher.exe`). Any unrelated
  process with that name blocks your item.

Keep the list short. Every extra name is another way for the item to defer for a reason nobody
will connect to this package.

## The open-console failure mode

A vendor installer that replaces files in its own directory can fail when a **console window has
that directory as its working directory**, or when a shell has one of the product's DLLs or CLI
executables loaded. The symptom is characteristic and misleading: the installer launches, sits for
a few seconds to about twenty, then exits with a generic failure code — commonly `1` from an Inno
Setup installer — with no useful message and nothing in the installer log. Rerunning by hand on a
machine with no console open succeeds, which makes the failure look intermittent.

The trigger is usually a user, a build agent or a scheduled job leaving a shell open inside the
product's directory, or having run the product's CLI in that shell. Windows keeps the directory
handle and the loaded image, and the installer cannot replace them.

**Do not fix this by adding `cmd` or `powershell` to `blocking_applications`.** That defers the
item on every device forever, including the ones with no console anywhere near the product.
Instead:

- List the product's **own** CLI executable as a blocker, so the item defers only when someone is
  actually using it;
- and where the vendor supports it, prefer the installer's own restart-manager or force-close
  switch over guessing.

If the vendor installer cannot cope at all, wrap it: use a `preinstall_script` that verifies the
directory is free and exits non-zero when it is not, so the item fails loudly with a reason
instead of failing with a bare exit code.

## Verifying a blocker list

Check what the device actually sees before trusting a name:

```powershell
Get-Process | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique
```

The values in that list are exactly what Cimian compares against, so an entry in
`blocking_applications` must reduce to one of them. Add `.exe` or not — it makes no difference.

To confirm the deferral is what is holding an item back:

```
managedsoftwareupdate --checkonly -vv
```

A blocked item is absent from the install table and carries a `blocking applications running`
line in the session output.

## See also

- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Force Installs And Deadlines](Force-Installs-And-Deadlines)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Uninstalling Software](Uninstalling-Software)
- [Item Status Reference](Item-Status-Reference)
- [Troubleshooting](Troubleshooting)
