# Scripts In pkgsinfo

A pkgsinfo can carry PowerShell that runs at defined points around detection, installation and
removal. This page documents every script hook the client executes, how each one is invoked, how
its exit code is read, and which hooks have no timeout. Read it before writing an
`installcheck_script` — its exit-code polarity is the inverse of an Intune detection script, and
that single fact causes more install loops than anything else in Cimian.

## The hooks

| Key | Runs | Exit code meaning |
|---|---|---|
| `installcheck_script` | During status checking, before anything is downloaded | **0 = install needed**; non-zero = nothing to do |
| `version_script` | During status checking, only when there is no `installcheck_script` | Exit code ignored except as failure; **stdout is the installed version** |
| `check.script` | During status checking, after `installs[]`, `check.registry` and `check.file` | **0 = already installed**; non-zero = install needed |
| `preinstall_script` | Immediately before the installer runs | Non-zero **fails the install** |
| `install_script` | *Is* the install, for `installer.type: nopkg` and `script` | Non-zero fails the install |
| `postinstall_script` | After a successful install, before the receipt is written | Non-zero logs a warning only |
| `preuninstall_script` | Immediately before removal | Non-zero **aborts the removal** |
| `uninstall_script` | *Is* the removal, when no `uninstaller:` block is declared | Non-zero fails the removal |
| `postuninstall_script` | After a **successful** removal | Non-zero logs a warning only |
| `uninstallcheck_script` | Never | — |

Every one of these is an **inline PowerShell string**, not a path to a file. There is no key that
points the client at a script on disk; the body travels inside the pkgsinfo and into the catalog.

`uninstallcheck_script` is accepted by `makepkginfo` and `cimiimport`, is written into the
pkgsinfo, and is carried through into the catalog by `makecatalogs` — but the client has no
property for it and nothing in `managedsoftwareupdate` ever reads it. **Setting it has no effect
whatsoever.** Removal is decided by [Uninstalling Software](Uninstalling-Software), not by a check
script.

## How a script is invoked

Inline scripts run as an external process:

```
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command <script body>
```

**PowerShell is always invoked with `-ExecutionPolicy Bypass`.** You never need to set a machine
execution policy for Cimian, and a `Restricted` or `AllSigned` policy on the device does not stop
a pkgsinfo script from running. `-NoProfile` is also always passed, so nothing from a user or
system PowerShell profile is in scope.

The interpreter is chosen in this order, first hit wins:

1. `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` (Windows PowerShell 5.1)
2. `C:\Program Files\PowerShell\7\pwsh.exe`
3. `C:\Program Files\PowerShell\pwsh.exe`
4. the first `pwsh.exe` found on `PATH`

In practice this means **Windows PowerShell 5.1 on every normal Windows device**. Do not write
PowerShell 7-only syntax (ternaries, `??`, `-Parallel`) in a pkgsinfo script.

If no interpreter is found at all, the hook fails with `Neither pwsh.exe nor powershell.exe was
found`.

### Working directory and environment

The script inherits the working directory and the full environment of `managedsoftwareupdate`
itself. **No working directory is set for inline scripts and no Cimian-specific environment
variables are injected.** There is no variable telling the script which package it belongs to,
which version is being installed, or where the downloaded payload is. Use absolute paths for
everything; never assume `$PWD`, and never use a relative path.

`managedsoftwareupdate` requires administrator and aborts otherwise, so a script always runs
elevated. When the client runs from the watcher service or a scheduled task it runs as
**SYSTEM** — there is no logged-in user's profile, no `HKCU` for that user, and no mapped drives.
Anything that must land in a user context has to be written by a mechanism that runs in the user's
session, not by a pkgsinfo script.

### stdout and stderr

Both streams are captured. stdout is collected first, then stderr is appended to it, and the
combined text is what the client logs and reports. Inline scripts are **not** streamed to the
console line by line as they run — the output appears once the script exits. (Only the
[preflight and postflight scripts](Preflight-And-Postflight-Scripts), which are files rather than
inline strings, stream live.)

No output encoding is set on the child process, so non-ASCII characters in script output can be
mis-decoded in the logs. Keep script output plain ASCII.

A `postinstall_script` can emit a line containing `CIMIAN-WARNING: <message>` on either stream.
The client extracts the message, marks the item's outcome as a warning, and **still counts the
install as successful**. It also suppresses the loop-convergence probe for that install and
records nothing against [install-loop prevention](Install-Loop-Prevention) — a self-reported
warning is a soft outcome, not a failure. The marker is matched anywhere on a line, so
`Write-Error "CIMIAN-WARNING: needs follow-up"` works.

### Timeouts

| Hook | Timeout |
|---|---|
| `installcheck_script` | **2 minutes**, fixed |
| `version_script` | **none** |
| `check.script` | **none** |
| `preinstall_script` | **none** |
| `install_script` | **none** |
| `postinstall_script` | **none** |
| `preuninstall_script` | **none** |
| `uninstall_script` | **none** |
| `postuninstall_script` | **none** |

Only `installcheck_script` is bounded. Every other hook can run forever and will hold the whole
session open while it does. `installer_timeout` (per item, in seconds) bounds the **installer
process**, not any of these scripts, so it will not rescue a hung `postinstall_script`.

A script that waits on input, opens a dialog, or calls `Read-Host` will hang the run. Nothing is
attached to stdin, so an interactive prompt blocks indefinitely.

When `installcheck_script` exceeds its 2 minutes, its process tree is killed and the item's status
becomes `error` with **`NeedsAction` false** — a timed-out installcheck never causes an install.
The item reports a detection error for that run and is retried next session. This is deliberate:
a flaky detection script must not be able to trigger repeated installs.

## Exit-code semantics, precisely

### `installcheck_script` — a predicate, not a detection rule

```
exit 0        -> install is needed        (item becomes pending, and is queued)
exit non-zero -> install is not needed    (item is reported installed)
```

**This is the inverse of an Intune detection script**, where exit 0 plus output means "detected,
do not install". If you paste an Intune detection script into `installcheck_script` unchanged, you
get exactly the wrong answer: the app is reinstalled on every run when it is present, and skipped
when it is absent.

The result also distinguishes a first install from an update: when the script exits 0 and the item
already has a `ManagedInstalls` receipt, the action is reported as an update rather than a new
install.

An exception thrown while trying to run the script at all (as distinct from a timeout) sets the
status to `error` **with `NeedsAction` true**, so the item is queued. Only the timeout path
declines to act.

`installcheck_script` is consulted at priority 1 in the detection cascade — after the `on_demand`
short-circuit and before `installs[]`. An `OnDemand: true` item never has its installcheck
consulted at all. See
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

### `version_script`

Runs only if there is no `installcheck_script`. Its **trimmed stdout is taken as the installed
version**, and compared against the pkgsinfo `version` using the normal version comparison rules
(see [Version Comparisons](Version-Comparisons)).

- Script fails, or stdout is empty → item is treated as **not installed** and queued.
- Catalog version is newer than the reported version → queued as an update.
- Otherwise → installed.

The script must print the version and nothing else. Any banner, warning or progress text becomes
part of the "version" string, and an unparseable version compares as equal, so the update never
happens.

### `check.script`

```
exit 0        -> already installed
exit non-zero -> install is needed
```

Note that this is the opposite polarity to `installcheck_script`, and both live in the same
cascade. `check.script` is only reached when there is no `installcheck_script`, no
`version_script`, no `installs[]` entries, no `check.registry.name` and no `check.file.path`.
Prefer `installs[]` or `installcheck_script`; `check.script` exists mainly for pkgsinfos carried
over from older repositories.

### `preinstall_script` and `preuninstall_script`

A non-zero exit is fatal to that operation. The installer never runs, the item is reported failed
with `Preinstall script failed: <output>` (or `Preuninstall script failed: …`), and no receipt is
written. Use this for a genuine precondition — stopping a service, releasing a lock — and make it
exit 0 when the precondition was already satisfied.

### `postinstall_script` and `postuninstall_script`

A non-zero exit is logged as a warning and **does not fail the operation**. The install still
counts as successful and the receipt is still written. If a postinstall step is genuinely required
for the product to work, do not rely on its exit code to fail the run — verify the result in the
`installs[]` array or the `installcheck_script` instead, so the item is re-queued when the step
did not take.

`postuninstall_script` only runs when the removal itself succeeded. A failed removal returns
before the hook.

### `install_script` and `uninstall_script`

For `installer.type: nopkg` or `script` there is no payload; `install_script` is the whole
install, and a non-zero exit fails it. An empty `install_script` on such an item logs a warning
and reports success.

`uninstall_script` is the removal mechanism for script-only packages, and its presence is one of
the things that makes an item removable at all. See
[Uninstalling Software](Uninstalling-Software).

## Package-embedded scripts are a different thing

Scripts placed in a `scripts/` directory and built into an MSI by [cimipkg](cimipkg) are **not**
pkgsinfo hooks. They run as Windows Installer custom actions inside `msiexec`, in a context where
their console output is invisible. cimipkg writes them to a sidecar log file, and the client
drains that log into the session log after every install and every uninstall, capped at 500 lines
per package per phase.

Both mechanisms can be present on the same item. The order is: pkgsinfo `preinstall_script` →
installer (including any custom-action scripts inside it) → pkgsinfo `postinstall_script`.

## YAML mechanics

Scripts are multi-line strings and must use a literal block scalar:

```yaml
installcheck_script: |
  $ErrorActionPreference = 'Stop'
  exit 1
```

Use `|`, never the folded `>` form — folding joins lines and destroys PowerShell. Cimian's own
serializer forces `|` on any string containing a newline when it rewrites a pkgsinfo, but a
hand-written `>` will have already broken the script before that happens.

Because the whole serialized item is hashed into the catalog's loop fingerprint, **editing any
script releases that package's standing loop suppression fleet-wide** on the next run after the
catalog is rebuilt. That is the intended way to ship a fix for a looping package.

## Writing a reliable installcheck script

Five rules cover almost every failure:

1. **Exit 0 means install.** Write the check so the healthy, fully-installed state exits non-zero.
2. **Always exit explicitly, on every path.** A PowerShell script that runs off the end exits 0 —
   which Cimian reads as "install needed". A script with no `exit` statement reinstalls the
   package every single run, forever.
3. **Trap your own errors.** With `$ErrorActionPreference = 'Stop'`, an unhandled terminating error
   also produces a non-zero exit, which reads as "installed" — the opposite of what you want when
   the check could not be performed. Wrap the risky part and decide the answer yourself.
4. **The next run must be able to see the change.** After a successful install, the same script
   has to return non-zero. If it cannot — because the thing it tests is only written later, or by
   a user-context process — the package loops.
5. **Keep it under two minutes.** No network calls, no `Get-WmiObject` sweeps, no
   `Win32_Product` (which reconfigures every MSI on the box and is slow enough to trip the
   timeout on its own).

A version-gated check for a product installed to a known path:

```yaml
name: ExampleVendorSuite
display_name: Example Vendor Suite
version: 3.2.1
installcheck_script: |
  $ErrorActionPreference = 'Stop'
  $exe = 'C:\Program Files\Example Vendor\Suite\Suite.exe'
  $target = [version]'3.2.1'
  if (-not (Test-Path -LiteralPath $exe)) { exit 0 }
  try {
      $found = [version](Get-Item -LiteralPath $exe).VersionInfo.FileVersion
  } catch {
      exit 0
  }
  if ($found -lt $target) { exit 0 }
  exit 1
```

Every path exits explicitly. A missing file, an unreadable file and an old version all exit 0
(install); only a present, readable, current file exits 1.

A registry-based check for a product with no useful file version:

```yaml
installcheck_script: |
  $ErrorActionPreference = 'Stop'
  $key = 'HKLM:\SOFTWARE\Example Vendor\Suite'
  try {
      $value = (Get-ItemProperty -LiteralPath $key -Name 'ConfiguredVersion' -ErrorAction Stop).ConfiguredVersion
  } catch {
      exit 0
  }
  if ($value -eq '3.2.1') { exit 1 }
  exit 0
```

If your check can be expressed as "this file, this product code, or this MSIX identity is present
at this version", **do not write a script at all** — use an [installs array](Installs-Arrays). It
is faster, it cannot hang, it produces a far better failure message, and it cannot get the
polarity backwards.

## What happens when a check script is wrong

A package whose `installcheck_script` still says "install needed" immediately after a successful
install is caught on the **first** install, not after several sessions. The client re-runs the
status check right after installing; if the item still needs action and no restart is pending, it
logs a looping-install warning and pauses that package for 24 hours by default. See
[Install Loop Prevention](Install-Loop-Prevention) for the thresholds and for
`managedsoftwareupdate --clear-loop`.

## See also

- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Installs Arrays](Installs-Arrays)
- [Uninstalling Software](Uninstalling-Software)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Preflight And Postflight Scripts](Preflight-And-Postflight-Scripts)
- [Installer Types](Installer-Types)
