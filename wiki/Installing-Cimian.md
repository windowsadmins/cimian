# Installing Cimian

This page covers installing the Cimian client on a Windows endpoint: which artifacts
exist, what the MSI puts on disk, what it registers with the operating system, and how
to install it silently, verify it, and deploy it to a fleet. You need local administrator
rights on the target machine for every command here.

## Artifacts and naming

Each Cimian release publishes per-architecture artifacts. The version is a calendar build
stamp, `yyyy.MM.dd.HHmm`, and the architecture follows it:

| Artifact | Name pattern |
|---|---|
| Windows Installer package | `Cimian-<yyyy.MM.dd.HHmm>-<arch>.msi` |
| Chocolatey package | `CimianTools-<arch>.<yy.M.d.HHmm>.nupkg` |
| Raw binary archive | `Cimian-<yyyy.MM.dd.HHmm>-<arch>.zip` |

`<arch>` is `x64` or `arm64`. The MSI is the supported way to install the client; the zip
exists so repository automation can pick up `cimiimport.exe` and `makecatalogs.exe`
without cracking an installer, and the nupkg is for sites that already run Chocolatey.

Releases published from the source repository are **unsigned**. If your environment
requires signed binaries, sign the MSI yourself before distributing it.

An `.intunewin` package is produced only when the build is invoked with `-IntuneWin`, and
the release workflow does not do that — see [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
for the manual wrapping step.

## Supported platforms

- **Architectures:** x64 and arm64. There is no x86 build and no x86 code path.
- **Windows floor:** Windows 10 1809 (build 17763) is the nominal minimum, which is the
  target platform minimum of the graphical components. Windows Server editions of the
  same generation work for the command-line tools.
- **.NET runtime:** none required. Every binary is published self-contained and
  single-file, so the machine does not need any .NET runtime or SDK installed.

## What the MSI installs

Everything lands in `C:\Program Files\Cimian`. The path is fixed; the installer does not
offer a directory choice, and passing a directory property does not move it.

The payload is the whole published tree: the ten command-line tools
(`managedsoftwareupdate.exe`, `cimiwatcher.exe`, `cimitrigger.exe`, `cimistatus.exe`,
`cimiimport.exe`, `cimipkg.exe`, `makecatalogs.exe`, `makepkginfo.exe`,
`manifestutil.exe`, `repoclean.exe`), the `Managed Software Center.exe` application with
its companion resource files, and a set of support scripts — `install-tasks.ps1`,
`uninstall-tasks.ps1`, `manage-service.ps1`, `verify-installation.ps1` and
`diagnose-cimianwatcher.ps1`.

The client's working data does not live in Program Files. It lives under
`C:\ProgramData\ManagedInstalls` — configuration, cache, catalogs, manifests, icons,
logs, reports and receipts. The MSI does not create or seed that tree; the first run of
`managedsoftwareupdate` does.

## What installation registers

**A Windows service, `CimianWatcher`.** Display name "Cimian Watcher Service", start type
Automatic, running as LocalSystem. It is registered imperatively by `cimiwatcher.exe
install` rather than by an installer service table, and it is configured to restart three
times at 60-second intervals on failure with a 24-hour reset window. It also registers an
Application event log source named `CimianWatcher`. The service polls for the trigger
flag files described in [cimiwatcher](cimiwatcher) every 10 seconds.

**A scheduled task, `Cimian Managed Software Update Hourly`.** It runs
`managedsoftwareupdate.exe --auto` as SYSTEM at highest privilege, first firing five
minutes after registration and repeating every hour indefinitely. It is hidden, wakes the
machine, starts when available, runs on battery, restarts up to three times at 10-minute
intervals, and has a four-hour execution time limit — long, because a first run on a
fresh image can pull many gigabytes.

**A scheduled task, `Cimian Watchdog`.** It runs `managedsoftwareupdate.exe --self-check`
every four hours with a five-minute time limit and writes
`C:\ProgramData\ManagedInstalls\cimian_selfcheck.json` so a reporting system can see
drift. This task is registered by `install-tasks.ps1`, not by the MSI's own post-install
action. If it is missing after an install and you want it, run the shipped script from an
elevated prompt:

```
& "C:\Program Files\Cimian\install-tasks.ps1"
```

**A machine PATH entry.** `C:\Program Files\Cimian` is prepended to the system PATH, so
the tools are on the path for new processes. Existing shells need to be restarted.

**A registry stamp.** `HKLM\SOFTWARE\Cimian` gets two string values, `Version` and
`InstallPath`. The same key is where a DPAPI-protected `AuthHeader` credential is stored
if you use Basic authentication against the repo.

**A Start Menu shortcut.** `Managed Software Center.lnk` under a `Cimian` folder in the
All Users Start Menu.

## Installing silently

Install from an elevated prompt. Pick up the MSI by pattern rather than typing a dated
file name:

```
$msi = (Get-ChildItem .\Cimian-*-x64.msi | Sort-Object Name -Descending | Select-Object -First 1).FullName
msiexec.exe /i "$msi" /qn /norestart /l*v "$env:TEMP\cimian_install.log"
```

`/qn` is silent, `/norestart` prevents the installer rebooting the machine, and the
verbose log is the first thing to read if anything fails. The MSI is per-machine; there
is no per-user install mode.

The client will not run until it has a repository URL. Create
`C:\ProgramData\ManagedInstalls\Config.yaml` before or immediately after installing:

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
ClientIdentifier: WORKSTATION-01
```

Configuration keys are PascalCase. See [Client Configuration](Client-Configuration) for
the full list, and [Configuring Clients With Intune](Configuring-Clients-With-Intune) for
delivering the repository URL and client identifier by policy instead of by file.

## Uninstalling

Covered in full on [Removing Cimian](Removing-Cimian). The short form:

```
msiexec.exe /x "$msi" /qn /norestart
```

## Upgrade behaviour

Installing a newer MSI over an existing installation is a standard major upgrade. Every
Cimian build carries a fresh ProductCode, but the UpgradeCode is stable — it is derived
deterministically from the product identifier — so Windows Installer finds and removes
the previous build before laying down the new one. You do not need to uninstall first,
and you should not pass `REINSTALL=ALL`: on a build whose ProductCode is not already
present that turns the upgrade into a maintenance pass that changes nothing.

During an upgrade the installer stops the `CimianWatcher` service and terminates any
running Cimian processes so the binaries can be replaced, then re-registers the service
and the hourly task. The uninstall action deliberately does not run during the
previous-version removal pass of an upgrade, so the service, task, PATH entry and
registry stamp survive it.

`C:\ProgramData\ManagedInstalls` is untouched by upgrades. Cache, catalogs, receipts and
logs all carry over.

Once a client is installed and pointed at a repository, you generally do not push MSIs to
it again — publish the client as an item in the repository and let it update itself. See
[Updating Cimian](Updating-Cimian).

## Verify the install

The shipped verification script checks the executables, the service, the hourly task, the
PATH entry, the registry stamp, and that `managedsoftwareupdate.exe --version` runs:

```
& "C:\Program Files\Cimian\verify-installation.ps1"
```

To check the pieces by hand, confirm the service is running:

```
Get-Service CimianWatcher
```

Confirm the tasks exist:

```
Get-ScheduledTask -TaskName 'Cimian *'
```

Confirm the client reports its version and its effective configuration:

```
& "C:\Program Files\Cimian\managedsoftwareupdate.exe" --version
& "C:\Program Files\Cimian\managedsoftwareupdate.exe" --show-config
```

Then prove the client can reach the repository and read its manifest without changing
anything on the machine:

```
managedsoftwareupdate --checkonly -vv
```

A successful check writes `C:\ProgramData\ManagedInstalls\InstallInfo.yaml` and returns
0. If it cannot resolve a manifest, start at
[Client Identifier Resolution](Client-Identifier-Resolution).

## Mass deployment

### Intune

Wrap the MSI as a Win32 app and deploy it in device context. The detection rule that
works across builds is a file or registry check, not an MSI product code check — the
ProductCode changes on every build. [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
has the full configuration, including how an MDM triggers an on-demand run.

### Group Policy and configuration management

The MSI installs unattended with no properties, so it works with any mechanism that can
run `msiexec` as SYSTEM.

For Group Policy software installation, publish the MSI to a share the computer accounts
can read and assign it to a computer OU. Because the ProductCode changes each build, GPO
sees each release as a new package; assign the new one and let its major upgrade replace
the old, rather than trying to redeploy the same package entry.

For configuration-management systems that run a command line — SCCM/Configuration
Manager, or any agent that can execute a script as SYSTEM — the install program is:

```
msiexec.exe /i "Cimian.msi" /qn /norestart
```

and the uninstall program is the ProductCode removal shown on
[Removing Cimian](Removing-Cimian). Use a file-existence detection method on
`C:\Program Files\Cimian\cimiwatcher.exe`, or a version comparison against the `Version`
value under `HKLM\SOFTWARE\Cimian`.

### Manual and scripted install

For imaging, a provisioning script, or one-off installs, download the MSI for the
machine's architecture and run the silent install shown above. A minimal end-to-end
script that installs, writes a configuration file and performs a first run:

```powershell
$msi = (Get-ChildItem .\Cimian-*-x64.msi | Sort-Object Name -Descending | Select-Object -First 1).FullName
Start-Process msiexec.exe -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart') -Wait
New-Item -ItemType Directory -Force -Path 'C:\ProgramData\ManagedInstalls' | Out-Null
Set-Content -Path 'C:\ProgramData\ManagedInstalls\Config.yaml' -Value @'
SoftwareRepoURL: https://cimian.example.com/repo
'@
& 'C:\Program Files\Cimian\managedsoftwareupdate.exe' --auto
```

For a machine coming off a fresh image that must converge before anyone logs in, use
bootstrap mode instead of a bare `--auto` run. See
[Bootstrapping With Cimian](Bootstrapping-With-Cimian).

## See also

- [Removing Cimian](Removing-Cimian)
- [Updating Cimian](Updating-Cimian)
- [Bootstrapping With Cimian](Bootstrapping-With-Cimian)
- [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
- [Client Configuration](Client-Configuration)
- [How Cimian Runs](How-Cimian-Runs)
- [cimiwatcher](cimiwatcher)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Troubleshooting](Troubleshooting)
