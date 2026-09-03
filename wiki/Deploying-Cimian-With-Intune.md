# Deploying Cimian With Intune

This page covers getting the Cimian client onto machines with Microsoft Intune, or any
comparable MDM: producing an `.intunewin` from the released MSI, configuring the Win32
app, detection rules that survive every build, assignment considerations, and how an MDM
asks an already-installed client to run right now. Most of it applies unchanged to any MDM
that can run `msiexec` in device context.

Cimian and the MDM are not competing deployment channels. The MDM's job here is to install
the client, configure it, and occasionally poke it. Everything else the machine needs comes
from the Cimian repository.

## Producing an .intunewin

Releases do not ship an `.intunewin`. The build can produce one, but the release workflow
does not ask it to, so wrapping the MSI is a manual step you perform once per release.

Download the MSI for the architecture you are packaging, put it in a directory on its own,
and run Microsoft's Win32 Content Prep Tool against it:

```powershell
$msi = (Get-ChildItem C:\pkg\cimian-x64\Cimian-*-x64.msi | Sort-Object Name -Descending | Select-Object -First 1)
IntuneWinAppUtil.exe -c C:\pkg\cimian-x64 -s $msi.Name -o C:\pkg\out -q
```

The artifact is named `Cimian-<yyyy.MM.dd.HHmm>-<arch>.msi`, and the datestamp changes
every build, so note the exact name you wrapped — the app's command lines have to repeat it
literally. The tool writes a `.intunewin` into the output directory.

Package x64 and arm64 as two separate apps. There is no combined installer, and there is
no x86 build.

## Win32 app configuration

Create a Win32 app from the `.intunewin` and configure it as follows.

**Install command.** Replace `Cimian-<version>-x64.msi` with the exact file name you
wrapped:

```
msiexec /i "Cimian-<version>-x64.msi" /qn /norestart /l*v "%ProgramData%\cimian_intune_install.log"
```

**Uninstall command.** The ProductCode changes with every build, so a hardcoded GUID in
the uninstall command will be wrong as soon as you ship the next release. Uninstall from
the package instead, again with the real file name:

```
msiexec /x "Cimian-<version>-x64.msi" /qn /norestart
```

**Install behaviour:** System. The MSI installs per-machine into
`C:\Program Files\Cimian` and registers a service and SYSTEM scheduled tasks; a user
context install cannot do any of that.

**Device restart behaviour:** No specific action. `/norestart` is already on the command
line and the install does not require a reboot.

**Return codes:** the defaults are correct — 0 success, 1707 success, 3010 soft reboot,
1618 retry.

**Requirements:** 64-bit for the x64 package, and set the minimum operating system to
Windows 10 1809 or later, which is the client's floor.

## Detection rules that actually work

Every Cimian build carries a fresh ProductCode. **Do not use the MSI detection rule
type** — it keys on the ProductCode, so it stops matching the moment you ship a new build,
and Intune concludes the app is missing on every machine that has it.

Use one of these instead.

**File exists.** The simplest rule that is correct for all builds:

- Path: `C:\Program Files\Cimian`
- File: `cimiwatcher.exe`
- Detection method: File or folder exists

This detects "Cimian is installed" but not "this version is installed", which is usually
what you want — the client updates itself from the repository, so the MDM should stop
caring about the version once the client is on the machine.

**Registry version comparison.** If you do want the MDM to enforce a floor version, use the
stamp the installer writes:

- Key path: `HKEY_LOCAL_MACHINE\SOFTWARE\Cimian`
- Value name: `Version`
- Detection method: Version comparison, greater than or equal to, with the release's
  `yyyy.MM.dd.HHmm` stamp as the value

Be careful with this one. Once the client starts self-updating from the repository, its
version moves ahead of whatever you packaged, which is fine for a "greater than or equal"
rule and wrong for an "equals" rule.

**Custom detection script.** If you write one, remember the polarity. An Intune detection
script exits 0 and writes to standard output when the app **is** present. This is the
opposite of Cimian's own `installcheck_script`, which exits 0 when an install **is
needed**. A script copied from a pkgsinfo into Intune, or the reverse, must have its exit
codes inverted.

A minimal, correct Intune detection script:

```powershell
if (Test-Path 'C:\Program Files\Cimian\cimiwatcher.exe') {
    Write-Output 'Installed'
    exit 0
}
exit 1
```

## Assignment considerations

**Assign to devices, not users.** The client is machine-scoped: one service, one set of
SYSTEM tasks, one repository configuration.

**Required, not available.** The client is infrastructure, not something a user chooses
from a portal.

**Do not chain supersedence across releases.** The MSI's UpgradeCode is stable and its
major upgrade removes the previous build on its own, so a straightforward "required"
assignment of the new package upgrades the old one. Better still, stop pushing new client
MSIs from the MDM entirely after the first install and let the client update itself from
the repository — see [Updating Cimian](Updating-Cimian).

**Deliver configuration separately from the app.** The client needs a repository URL before
it can do anything. You can lay down `C:\ProgramData\ManagedInstalls\Config.yaml` from a
script, or deliver the settings by policy — the client reads `SoftwareRepoURL`,
`ClientIdentifier`, `InstallerTimeout` and `CacheRetentionDays` from
`HKLM\SOFTWARE\Policies\Cimian`, and policy wins over the configuration file. That is the
whole policy surface; other configuration keys are file-only. See
[Configuring Clients With Intune](Configuring-Clients-With-Intune) and
[Client Configuration](Client-Configuration).

**Give the first run room.** After install, the client's first session on a fresh machine
can run for a long time and download a great deal. If you are provisioning rather than
retrofitting, use bootstrap mode rather than expecting the MDM to manage that —
[Bootstrapping With Cimian](Bootstrapping-With-Cimian).

## Triggering a run from the MDM

There is no API, named pipe or HTTP endpoint for asking Cimian to run. The trigger is a
file, watched by the `CimianWatcher` service on a 10-second poll:

| File | Effect |
|---|---|
| `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` | run with the status window shown |
| `C:\ProgramData\ManagedInstalls\.cimian.headless` | run with no window |

The service deletes the flag before launching `managedsoftwareupdate`, so its
disappearance is the acknowledgement that the run started. Only one triggered run happens
at a time; if a run is already in flight the flag is left in place and fires on a later
poll. The file's content is not required, but a line beginning `Args:` replaces the
default arguments for that run.

Any MDM mechanism that can run a script as SYSTEM can create that file. In Intune, the
usual vehicle is a remediation (proactive remediation) with a detection script that always
reports non-compliant and a remediation script that writes the flag.

Detection script — always ask for the remediation to run:

```powershell
exit 1
```

Remediation script — request a headless run:

```powershell
New-Item -ItemType Directory -Force -Path 'C:\ProgramData\ManagedInstalls' | Out-Null
Set-Content -Path 'C:\ProgramData\ManagedInstalls\.cimian.headless' -Value "Triggered by MDM at $(Get-Date -Format o)"
exit 0
```

Schedule that on the cadence you want an out-of-band run. Do not schedule it aggressively:
the hourly scheduled task already runs the client every hour, and a trigger that fires more
often than sessions can finish just leaves flags on disk.

If you would rather not write the file yourself, the shipped `cimitrigger` tool does the
same thing and falls back to direct elevation if the service does not acknowledge within
15 seconds:

```
& "C:\Program Files\Cimian\cimitrigger.exe" headless
```

## What an MDM cannot do here

- It cannot read Cimian's run results directly. Session outcomes are written to
  `C:\ProgramData\ManagedInstalls\reports\`; surfacing them is a reporting exercise. See
  [Reporting Data Contract](Reporting-Data-Contract).
- It cannot pass configuration through MSI properties. The install directory is fixed and
  the repository URL is not an installer property.
- It cannot cancel a run in progress. There is no stop file; cancellation is an
  in-process operation from the graphical interface.

## See also

- [Installing Cimian](Installing-Cimian)
- [Removing Cimian](Removing-Cimian)
- [Updating Cimian](Updating-Cimian)
- [Bootstrapping With Cimian](Bootstrapping-With-Cimian)
- [Configuring Clients With Intune](Configuring-Clients-With-Intune)
- [Client Configuration](Client-Configuration)
- [cimiwatcher](cimiwatcher)
- [cimitrigger](cimitrigger)
- [Reporting Data Contract](Reporting-Data-Contract)
