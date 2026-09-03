# Updating Cimian

Cimian updates itself the same way it updates anything else: you publish a new client
package into the repository, put it in a manifest, and the client picks it up on a normal
run. Because a process cannot replace its own running binary, a client update is *staged*
rather than installed inline, and applied a moment later by the `CimianWatcher` service.
This page covers what triggers a self-update, how one is recognised, the files involved,
what happens to a run in flight, rollback, and how to diagnose a client that is stuck on an
old version.

## Delivering a client update

Import the client MSI into the repository like any other installer, give it a pkgsinfo,
put it in a catalog, and add it to the manifests of the machines that should get it. See
[Installing Software](Installing-Software) and
[Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files). There is no separate
update channel, no update server and no phone-home: if the item is not in a machine's
manifest, that machine never updates.

Because the item is ordinary repository content, catalog promotion applies. Put the new
client in a testing catalog, assign it to a few machines, and promote it once it is proven.
See [Promoting Between Catalogs](Promoting-Between-Catalogs).

## How the client recognises itself

An item is treated as a Cimian self-update when either of these is true.

**The item name matches.** Compared case-insensitively, the name is exactly `cimian` or
`cimiantools`, optionally with one of the suffixes `-msi`, `-nupkg`, `-tools`, `.msi` or
`.nupkg`. So `cimian`, `CimianTools`, `cimian-msi` and `cimiantools-nupkg` all match;
`cimian-client` and `cimian2` do not.

**The installer location matches.** The pkgsinfo's `installer.location` contains
`/cimian-` or `/cimiantools-`, or contains `/cimian.` and ends in `.msi` or `.nupkg`.

Name your client item to fall inside that rule. An item carrying the client MSI under a
name the rule does not match is treated as an ordinary package, and the run will try to
install it inline over the running binaries.

## What triggers a self-update

A self-update is triggered by a normal run — the hourly scheduled task, a
watcher-triggered run, or a run you start by hand. There is no separate schedule.

When the run reaches status checking, a matching item is compared by version first: the
running agent version is read from the installed binary, and if the catalog version is
less than or equal to it the item is reported installed with reason code
`SelfUpdateCurrent` and nothing happens. This is what stops a client downgrading itself
onto an older catalog entry. Only a strictly newer catalog version proceeds.

If it proceeds, the run splits the self-update out of the install queue. It downloads the
package to the cache as usual, then **schedules** it instead of installing it, and logs
`CIMIAN SELF-UPDATE DETECTED`. The rest of the session's regular items install normally in
the same run.

## The flag file

Scheduling means writing this file:

```
C:\ProgramData\ManagedInstalls\.cimian.selfupdate
```

It is a plain key/value listing with the item name, version, installer type, the full path
of the downloaded installer in the cache, and the time it was scheduled. Its presence is
what "an update is pending" means; nothing else records the intent.

Two other paths take part:

| Path | Role |
|---|---|
| `C:\ProgramData\ManagedInstalls\SelfUpdateBackup\` | copy of the current install directory's files, taken immediately before the installer runs |
| `C:\ProgramData\ManagedInstalls\logs\selfupdate\selfupdate-<yyyyMMdd-HHmmss>.log` | verbose Windows Installer log for each attempt |

## How a staged update is applied

`CimianWatcher` applies it. The service checks for a pending self-update when it starts,
and again on every 10-second poll, but only when the machine is idle with respect to
Cimian: it defers while it is running a triggered update itself, and it defers while any
`managedsoftwareupdate` process exists at all — including one started by the hourly task
or by you at a prompt. Applying an update mid-session would replace
`managedsoftwareupdate.exe` under a running install and truncate the session.

When it is idle, the service:

1. Copies the files in `C:\Program Files\Cimian` to `SelfUpdateBackup`.
2. Deletes the flag file — before the installer runs, so a failure cannot become an
   endless retry loop on every restart.
3. Launches the installer as a fully detached process and exits, so the service is not
   holding its own binary open while it is replaced.
4. Windows restarts the service afterwards under its automatic recovery configuration, and
   the new build takes over.

For an MSI, the command is a quiet install with a verbose log. `REINSTALLMODE=vamus
REINSTALL=ALL` is appended **only** when that exact ProductCode is already installed —
that is, on a repair. Every build carries a fresh ProductCode, so on a genuine upgrade the
plain install runs and the major upgrade removes the previous build. Passing repair
properties to an unknown ProductCode produces a maintenance pass that reports success and
changes nothing, which is exactly how clients get stuck for weeks.

For `pkg` and `nupkg` types the update is handed to the sbin installer at
`C:\Program Files\sbin\installer.exe`; if that binary is absent the self-update fails.
Anything other than `msi`, `pkg` or `nupkg` is refused as an unsupported installer type.

You can also apply a pending update on demand by restarting the service:

```
managedsoftwareupdate --restart-service
```

## What happens to a run in flight

Nothing is interrupted. A self-update is staged, not installed, so the session that
discovered it continues and finishes normally, installing every other pending item. The
watcher will not apply the staged update while that session — or any other
`managedsoftwareupdate` process — is alive; it takes the next idle poll.

A session that is killed or truncated mid-run leaves the flag file in place, and the
watcher applies the update once the process is gone.

## Rollback

Rollback is limited and worth understanding precisely.

The backup taken before the installer runs is a copy of the **files directly in**
`C:\Program Files\Cimian` — not subdirectories.

On the path where the client performs the update itself and waits for the installer
(`--perform-selfupdate`), a failed install triggers a rollback that copies those backed-up
files back over the install directory, and the update is re-scheduled so it retries on the
next service restart.

On the path the watcher actually uses in normal operation, the installer is detached and
the calling process exits immediately, so there is **no failure detection and no automatic
rollback**. If that installer fails, the machine keeps whatever the failed install left
behind, and the flag has already been cleared, so nothing retries on its own. The next
routine run will see the catalog version is still newer than the running version and stage
the update again — that re-detection is the real recovery mechanism, not rollback.

A leftover `SelfUpdateBackup` directory is not evidence of a problem: on the detached path
the success cleanup never runs, so the service removes the stale backup the next time it
starts with no update pending.

To recover a broken client by hand, install the MSI directly as described on
[Installing Cimian](Installing-Cimian).

## Operator commands

Show whether an update is pending, with the item, version, installer type and schedule
time:

```
managedsoftwareupdate --selfupdate-status
```

The same information in a shorter form:

```
managedsoftwareupdate --check-selfupdate
```

Discard a pending update — the flag file is deleted and nothing is installed. A later run
will re-detect and re-stage it if the catalog is still newer:

```
managedsoftwareupdate --clear-selfupdate
```

Apply a pending update now by restarting the watcher service:

```
managedsoftwareupdate --restart-service
```

Check what version is actually running:

```
managedsoftwareupdate --version
```

`--perform-selfupdate` also exists. It performs the update in the calling process rather
than detaching it, which is not how the service applies one; it is intended for internal
use and for recovery, not routine operation.

## Why a client is stuck on an old version

Work through these in order on the affected machine.

**Is an update even pending?** Run `managedsoftwareupdate --selfupdate-status`. If it says
nothing is pending, the client never decided it needed one, and the problem is upstream —
continue with the next two checks.

**Does the machine's manifest include the client item?** Run
`managedsoftwareupdate --checkonly -vv` and look for the item in the plan. If it is
absent, the item is not in the manifest or not in a catalog the machine reads. See
[Manifests](Manifests) and [Using Catalogs](Using-Catalogs).

**Does the item's name match the self-update rule?** If the item installs as an ordinary
package rather than being staged, the name and installer location do not match the rule
above. Rename the item to `cimian` or `cimiantools`, or one of the accepted suffixed
forms.

**Is the catalog version actually newer?** The comparison is against the running binary's
version. Client versions are calendar stamps, and a package whose version does not parse
as a version compares equal, which means no update. Compare
`managedsoftwareupdate --version` against the catalog entry, and read
[Version Comparisons](Version-Comparisons) if the two look like they should order
differently than they do.

**Is an update pending but never applied?** That is the watcher's job, so check it:

```
Get-Service CimianWatcher
```

If it is stopped or missing, nothing will apply the staged update. Reinstall or restart
it. If it is running, check whether it is permanently deferring because a
`managedsoftwareupdate` process never exits:

```
Get-Process managedsoftwareupdate -ErrorAction SilentlyContinue
```

A hung run — most often a package script with no timeout — blocks self-updates
indefinitely. Read `C:\ProgramData\ManagedInstalls\logs\cimiwatcher.log` for the
deferral messages.

**Did the installer run and fail?** Read the newest file under
`C:\ProgramData\ManagedInstalls\logs\selfupdate\`. That is the Windows Installer verbose
log for the attempt. A log that reports success while the version does not change is the
`REINSTALL=ALL` failure mode described above; the fix is a plain install of the MSI.

**Is the item loop-suppressed?** Self-updates are staged before installation, so ordinary
suppression rarely applies, but it costs nothing to check:

```
managedsoftwareupdate --loop-status
```

## See also

- [Installing Cimian](Installing-Cimian)
- [Removing Cimian](Removing-Cimian)
- [Bootstrapping With Cimian](Bootstrapping-With-Cimian)
- [cimiwatcher](cimiwatcher)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Version Comparisons](Version-Comparisons)
- [Promoting Between Catalogs](Promoting-Between-Catalogs)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Troubleshooting](Troubleshooting)
