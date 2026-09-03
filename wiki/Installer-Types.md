# Installer Types

`installer.type` selects the mechanism the client uses to install a package. This page covers
each type Cimian can install: when to reach for it, the pkgsinfo shape it needs, how detection
usually works for that type, how arguments are passed, and the failure mode you are most
likely to hit.

Detection itself is covered in
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
and [Installs Arrays](Installs-Arrays); the sections below say which detection shape suits
each installer, not how detection works.

## Choosing a type, and what happens if you get it wrong

`installer.type` is the key inside the `installer:` block. There is also a top-level
`installer_type` key that `makepkginfo` writes; it is stripped at catalog generation and
never reaches a device. Only `installer.type` counts.

If you leave `installer.type` blank, the type is inferred from the payload's file extension:
`.msi`, `.exe`, `.nupkg`, `.pkg`, `.ps1`, and `.msix`/`.appx`/`.msixbundle`/`.appxbundle`
(all four mapping to `msix`). Anything else infers to `exe`. With no payload at all, the type
becomes `script`.

If you set `installer.type` to something Cimian does not recognise — including a typo such as
`msii` — it does not error. **It falls through to the EXE installer**, which will try to
execute your payload directly. For an MSI that means launching the `.msi` as if it were a
program, which fails in a way that does not obviously point at the typo. Recognised values
are exactly: `msi`, `exe`, `msix`, `appx`, `powershell`, `ps1`, `nupkg`, `chocolatey`, `pkg`,
`nopkg`, `script`.

Two behaviours are shared across every type. Exit codes `0` and `3010` always count as
success, `3010` additionally noting that a reboot is required; you can add more with
`installer.success_codes`. And a preinstall script that exits non-zero fails the install
before the installer runs, while a postinstall script that exits non-zero only logs a warning.

## MSI

The default choice for anything shipped as a Windows Installer package, including everything
built by `cimipkg`.

```yaml
name: ExampleApp
display_name: Example App
version: 2026.01.15.1200
catalogs:
- Production
installer:
  location: apps/ExampleApp-2026.01.15.1200.msi
  type: msi
  hash: 7d2b1f6c0a4e9385c1d7be22f04a6c19d853e7a0b41c96f8d2350ae7cb914f6d
  size: 184552448
  args:
  - ALLUSERS=1
  - DESKTOPSHORTCUT=0
installs:
- type: msi
  upgrade_code: '{4C8B1E27-5A93-4F60-B7D1-2E9A6C0348FF}'
  version: 2026.01.15.1200
```

**Detection.** An `installs[]` entry of type `msi`. Prefer `upgrade_code` over
`product_code`: an UpgradeCode is stable across versions, whereas a ProductCode changes with
every build for most vendors — and always changes for packages built by `cimipkg`, which
generates a fresh ProductCode each build. A pinned ProductCode on a package you rebuild is a
guaranteed install loop. The client resolves the code in both the 64-bit and 32-bit uninstall
registry views and compares the registered `DisplayVersion` against the catalog version.

Add `key_path` pointing at the product's main executable when you want a second, independent
check. Even after the registry lookup passes, that file must exist and its file version must
be at least the catalog version. This catches an older installer overwriting binaries without
updating its registry entry.

**Arguments.** The client always runs
`msiexec.exe /i "<payload>" /qn /norestart /l*v "<log>"` and appends whatever you put in
`installer.args`. Pass MSI properties as bare `NAME=VALUE` pairs; do not restate `/qn` or
`/norestart`. Verbose logs land under
`C:\ProgramData\ManagedInstalls\logs\installs\<Item>_install.1.log`, with the three most
recent kept.

MSI installs are serialised against each other, and exit code `1618` (another installation is
already in progress) is retried up to three times with a 30- then 60-second backoff.

**Common failure mode.** Detection that never converges. An MSI package with no `installs[]`
array and no other detection has nothing to report installed state from, so it reinstalls on
every run until loop suppression stops it. The other frequent cause is a ProductCode pinned to
a build that has since been re-cut. If a package installs successfully and immediately reports
that it needs installing again, the detection contract is wrong, not the installer.

## EXE

For vendor installers that ship as a self-extracting or bundled executable — Inno Setup, NSIS,
InstallShield, and vendor bootstrappers.

```yaml
name: ExampleTool
display_name: Example Tool
version: 5.4.1
catalogs:
- Production
installer:
  location: tools/ExampleTool-5.4.1.exe
  type: exe
  args:
  - /VERYSILENT
  - /SUPPRESSMSGBOXES
  - /NORESTART
installs:
- type: file
  path: C:\Program Files\Example Vendor\Example Tool\ExampleTool.exe
  version: 5.4.1
```

**Detection.** Usually a `file` entry pointing at the installed executable, with `version`
set. The client reads the file's file-version metadata and compares it. Beware that many
vendors ship an executable whose file version does not match the product version they
advertise — check the actual `FileVersion` on a test install before pinning it. An alternative
is `check.registry.name`, matching a substring of the uninstall-registry `DisplayName`; note
that without `check.registry.version` a registry hit means "installed" at any version.

**Arguments.** Whatever you put in `installer.args` (plus `switches`, `flags` and
`subcommand`, if you use them) is passed verbatim to the executable. **If you provide no
arguments at all, the client appends all six of `/S /silent /quiet /SILENT /VERYSILENT /qn`
together.** That shotgun works for many installers and is actively harmful for some, which
treat an unrecognised switch as a fatal argument error or, worse, as a positional path. Always
state the correct silent switch for the specific installer rather than relying on the default.

**Common failure mode.** The installer opens a UI and blocks. Because the process never exits,
the item burns the whole `installer_timeout` (900 seconds by default) and is then killed. If
an EXE package hangs for exactly the timeout on every run, the silent switch is wrong. Some
Inno installers also refuse to run silently while a console window belonging to the target app
is open — declare those processes in `blocking_applications`, see
[Blocking Applications](Blocking-Applications).

For unpacking and importing a vendor bundle, see
[Importing EXE Bundle Installers](Importing-EXE-Bundle-Installers).

## MSIX and APPX

For modern packaged apps. `msix` and `appx` are aliases for the same handler, and
`.msixbundle` / `.appxbundle` payloads infer to it too.

```yaml
name: ExamplePackagedApp
display_name: Example Packaged App
version: 3.1.0.0
catalogs:
- Production
installer:
  location: apps/ExamplePackagedApp-3.1.0.0.msix
  type: msix
installs:
- type: msix
  identity_name: ExampleVendor.ExamplePackagedApp
  version: 3.1.0.0
uninstaller:
- type: msix
  identity_name: ExampleVendor.ExamplePackagedApp
```

**Detection.** An `installs[]` entry of type `msix` or `appx` carrying `identity_name` — the
`Identity/@Name` value from the package's `AppxManifest.xml`, not the display name. The client
queries both the per-user store and the provisioned (all-users) store, takes the highest
version found, and compares. An entry of this type **without** `identity_name` is a hard
detection error, not a silent pass.

**Arguments.** There are none. The client runs its own PowerShell sequence: it discovers
existing per-user and provisioned installs, refuses a downgrade against the catalog version,
removes an older per-user copy, and then provisions the package for all users with
`-SkipLicense`. `installer.args` is not used on this path.

**Common failure mode.** The install succeeds and the app does not appear for the person
currently signed in. Provisioning applies at next sign-in, so an already-logged-in user sees
nothing until they log out and back in. The second common problem is a wrong
`identity_name` — it must be the identity, and getting it wrong means detection never
succeeds and the package reinstalls every run.

Removal needs `identity_name` too, on either an `uninstaller[]` entry or an `installs[]`
entry. An MSIX package with neither is not removable at all.

## PowerShell script payload

For a package whose payload *is* a `.ps1` file — the script is downloaded and executed as a
file. Use `powershell` or its alias `ps1`.

```yaml
name: ExampleConfiguration
display_name: Example Configuration
version: 2026.01.15.1200
catalogs:
- Production
installer:
  location: scripts/ExampleConfiguration-2026.01.15.1200.ps1
  type: powershell
installcheck_script: |
  $marker = 'C:\ProgramData\Example Vendor\configured.txt'
  if (Test-Path $marker) { exit 1 } else { exit 0 }
uninstall_script: |
  Remove-Item 'C:\ProgramData\Example Vendor\configured.txt' -Force -ErrorAction SilentlyContinue
```

**Detection.** There is nothing in the filesystem or registry that a script payload
inherently creates, so you must declare detection yourself. `installcheck_script` is the usual
choice. Note its convention: **exit 0 means an install is needed**, non-zero means it is not.
That is the opposite of `check.script`, where exit 0 means installed. Getting the polarity
backwards produces a package that either never installs or installs forever.

An alternative is `version_script`, whose trimmed stdout is taken as the installed version and
compared against the catalog version.

**Arguments.** None. The file is executed with
`powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File <path>`. `installer.args` is
not passed. If your script needs inputs, embed them or read them from a configuration file the
script itself knows about.

**Common failure mode.** No detection declared. Without `installcheck_script`,
`version_script`, an `installs[]` array or a `check` block, a `powershell` package falls
through to the receipt-based fallback and reinstalls on a schedule that depends on the
receipt, not on whether the work is actually done. Second most common: a script that returns
zero after failing internally, so the install registers as successful and the item reports
installed while nothing happened.

Note also that pre-install and post-install scripts run with **no timeout**. A script that
waits on user input hangs the session indefinitely.

## nopkg and script — metadata-only items

For a package with no payload at all, whose entire behaviour is an inline
`install_script`. `nopkg` and `script` are aliases for the same handler.

```yaml
name: ExampleMaintenanceTask
display_name: Example Maintenance Task
version: 2026.01.15.1200
catalogs:
- Production
installer:
  type: nopkg
install_script: |
  $stateDir = 'C:\ProgramData\Example Vendor'
  New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
  Set-Content -Path (Join-Path $stateDir 'maintenance.txt') -Value (Get-Date -Format o)
uninstall_script: |
  Remove-Item 'C:\ProgramData\Example Vendor\maintenance.txt' -Force -ErrorAction SilentlyContinue
installs:
- type: file
  path: C:\ProgramData\Example Vendor\maintenance.txt
recurring: true
```

**Detection.** Whatever you declare. If you declare nothing, a `nopkg` or `script` item falls
into a special branch of the detection fallback and is **assumed installed**. That is the
opposite of what happens to an untyped or MSI/EXE item with no detection, which falls through
to "not installed". So a `nopkg` item with no detection runs once, gets a receipt, and never
runs again — which is fine when that is what you want, and a silent no-op when it is not.

Declare `installs[]`, `installcheck_script` or a `check` block whenever the script's effect
can be undone by anything outside Cimian.

**Arguments.** None; `installer.location`, `installer.args` and the rest are unused. If
`install_script` is empty, the client warns and reports the item as successfully installed.

**Common failure mode.** Silence. A `nopkg` item with an empty or misspelled `install_script`
reports success having done nothing, and with no detection declared it will never be looked at
again. Confirm a state change on the device rather than trusting the item's reported status.

Removal for these items is `uninstall_script`, and nothing else. It is also the only thing
that makes a `nopkg` package removable at all — without it, `managed_uninstalls` cannot
remove the item.

For an item that must genuinely run every session, see the on-demand section below or set
`recurring: true`, which exempts an idempotent maintenance item from install-loop suppression
while still tracking it normally.

## .nupkg and Chocolatey

For packages in NuGet form. `nupkg` attempts Cimian's own package installer first and falls
back to Chocolatey; `chocolatey` goes straight to Chocolatey.

```yaml
name: ExamplePackage
display_name: Example Package
version: 4.2.0
catalogs:
- Production
installer:
  location: apps/ExamplePackage-4.2.0.nupkg
  type: nupkg
installs:
- type: file
  path: C:\Program Files\Example Vendor\Example Package\ExamplePackage.exe
  version: 4.2.0
```

**Detection.** Nothing about the NuGet format itself is detectable, so use whatever the
package actually puts on disk — usually a `file` entry, or an `msi` entry if the package
wraps an MSI.

**Arguments.** Not taken from `installer.args` on the Chocolatey path. Chocolatey is invoked
as `choco install <name> --yes --no-progress --force --version=<version>` with the download
directory as the source. Note the implication: the Chocolatey package id must equal the
pkgsinfo `name` exactly, or the install fails to find anything.

**Common failure mode.** Chocolatey is not installed on the device, in which case the fallback
path reports "Chocolatey is not installed" and the item fails on every run. The `nupkg` type
depends on either Cimian's package installer being present or Chocolatey being present; on a
device with neither, nothing installs.

## File payloads

Cimian has **no `copy` or `file` installer type.** There is no mechanism that takes a
directory of files in the repo and lays it down on a device directly.

The supported way to ship files is to build an MSI with `cimipkg`: put the files in the
project's `payload/` directory, set `install_location` in `build-info.yaml` to the
destination, and import the resulting `.msi` as an ordinary MSI package. Everything in the
[MSI section](#msi) then applies — detection, arguments, removal and all.

A legacy `.pkg` format also exists, installed by Cimian's own package installer, and is
recognised as `installer.type: pkg`. It is on the way out and should not be used for anything
new. Build MSIs.

**Common failure mode** for file payloads is detection: a directory of loose files often has
no versioned executable to point an `installs[]` entry at. Point the entry at a specific file
you control and give it a `md5checksum`, which is authoritative — a hash match overrides a
version mismatch — or use `installcheck_script`.

## On-demand items

`OnDemand` is not an `installer.type`; it is a behaviour flag that can be set on a package of
any type. It is included here because it is the answer to "how do I make something run every
session", which is a question people bring to this page.

```yaml
name: ExampleEnrollmentHelper
display_name: Example Enrollment Helper
version: 2026.01.15.1200
catalogs:
- Production
installer:
  type: nopkg
OnDemand: true
install_script: |
  $stateDir = 'C:\ProgramData\Example Vendor'
  New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
  Add-Content -Path (Join-Path $stateDir 'enrollment.log') -Value (Get-Date -Format o)
```

**The key is `OnDemand`, in PascalCase.** This is deliberate. Writing `on_demand:` is a
silently ignored key and the item behaves as an ordinary package.

An on-demand item is never considered installed, never gets an install receipt, and is
attempted every session for as long as it appears in the manifest. `OnDemand` is evaluated
before every other detection mechanism, so an `installcheck_script` on an on-demand item is
never consulted. It is also exempt from install-loop suppression and from the post-install
convergence probe, which is why on-demand items do not show up as loops.

**When to use it.** Provisioning and enrolment work that must keep running until its own
script flips some external state and an administrator removes the item from the manifest.

**Common failure mode.** Using it as a substitute for correct detection. An on-demand item
runs forever by design; there is no mechanism that will ever stop it except removing it from
the manifest. If what you actually want is an idempotent maintenance item that still reports
installed state, use `recurring: true` instead. See
[On Demand Items](On-Demand-Items).

## See also

- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Installs Arrays](Installs-Arrays)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Importing EXE Bundle Installers](Importing-EXE-Bundle-Installers)
- [Uninstalling Software](Uninstalling-Software)
- [Blocking Applications](Blocking-Applications)
- [On Demand Items](On-Demand-Items)
- [Install Loop Prevention](Install-Loop-Prevention)
- [cimipkg](cimipkg)
- [cimiimport](cimiimport)
