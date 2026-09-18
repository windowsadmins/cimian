# Removing Cimian

This page covers uninstalling the Cimian client from a Windows endpoint: the uninstall
command, what the uninstaller removes, what it deliberately leaves on disk, and how to
remove the remainder when you want the machine returned to a clean state. Every command
here needs an elevated prompt.

Removing the client does **not** remove the software Cimian installed. Applications
deployed through Cimian stay installed and become unmanaged. If you want them gone, remove
them through Cimian first with `managed_uninstalls` in the machine's manifest, let a run
complete, and only then remove the client.

## Uninstalling

If you still have the MSI that installed the current build, use it:

```powershell
$msi = (Get-ChildItem .\Cimian-*-x64.msi | Sort-Object Name -Descending | Select-Object -First 1).FullName
msiexec.exe /x "$msi" /qn /norestart
```

Usually you do not. Each build has a different ProductCode, so look it up from the
uninstall registry instead of guessing:

```powershell
$key = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
    Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -like 'Cimian*' } |
    Select-Object -First 1
msiexec.exe /x $key.PSChildName /qn /norestart /l*v "$env:TEMP\cimian_uninstall.log"
```

The uninstall runs a custom action that does the teardown described below. That action is
conditioned to fire only on a standalone uninstall — it does not run during the
previous-version removal pass of an upgrade, which is why an upgrade keeps the service,
the task and the PATH entry.

## What the uninstaller removes

- The scheduled task `Cimian Managed Software Update Hourly`.
- The `CimianWatcher` service: stopped, then removed via `cimiwatcher.exe uninstall`,
  falling back to `sc.exe delete` if the binary is already gone.
- Any running Cimian processes, including `Managed Software Center.exe` in a user
  session, so file removal is not blocked.
- The Start Menu folder `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Cimian`.
- The `C:\Program Files\Cimian` entry from the machine PATH.
- The registry key `HKLM\SOFTWARE\Cimian`, including the `AuthHeader` credential if one
  was stored there.
- Everything the MSI installed under `C:\Program Files\Cimian`.

Each of those steps is written to continue on failure so a partial problem cannot block
removal. Read the verbose log if you need to know which step warned.

## What the uninstaller leaves behind

**The scheduled task `Cimian Watchdog`.** The uninstall action removes only the hourly
task. If the watchdog task was registered, it survives the uninstall and will keep firing
against a missing binary. Remove it explicitly:

```
Unregister-ScheduledTask -TaskName 'Cimian Watchdog' -Confirm:$false
```

The shipped `uninstall-tasks.ps1` removes both tasks by name, but it is inside
`C:\Program Files\Cimian`, which the uninstall deletes — so run it *before* uninstalling
if you want to use it, or remove the task by hand afterwards as above.

**All client state under `C:\ProgramData\ManagedInstalls`.** Nothing in this tree is
touched. That includes:

| Path | Contents |
|---|---|
| `Config.yaml` | repository URL, client identifier, authentication settings |
| `Cache\` | downloaded installer payloads, which can be many gigabytes |
| `catalogs\`, `manifests\`, `icons\` | the last fetched repository content |
| `logs\` | session logs, `cimiwatcher.log`, self-update logs |
| `reports\` | `state.json`, `sessions.json`, `events.json`, `items.json`, `loop_suppressed.json` |
| `Receipts\` | per-item install receipts |
| `sbin\` | `preflight.ps1` and `postflight.ps1` if you deployed them |
| `conditions\`, `facts\` | conditional-item inputs |
| `SelfUpdateBackup\` | a copy of the previous client binaries, if a self-update was in flight |
| `InstallInfo.yaml`, `SelfServeManifest.yaml` | last run's plan, and user self-service choices |
| `.cimian.bootstrap`, `.cimian.headless`, `.cimian.selfupdate` | flag files, if any are pending |
| `cimian_selfcheck.json` | the watchdog's drift marker |

**Managed-install receipts in the registry.** `HKLM\SOFTWARE\ManagedInstalls` records the
installed version of each managed item and is not removed.

**Policy configuration.** `HKLM\SOFTWARE\Policies\Cimian`, if you deliver the repository
URL or client identifier by MDM policy, belongs to the policy channel and is not removed
by the uninstall. Retire the policy in your MDM, or it will be reapplied.

This is deliberate for upgrades and reinstalls — a machine that gets the client back keeps
its cache, its receipts and its history. It does mean an uninstall alone does not reclaim
the disk that the download cache is using.

## Removing the remainder deliberately

To reclaim cache space before uninstalling, while the tools are still present:

```
managedsoftwareupdate --clean-cache
```

To remove the whole state tree after uninstalling:

```
Remove-Item -Path 'C:\ProgramData\ManagedInstalls' -Recurse -Force
```

To remove the managed-install receipts, which makes every previously deployed item look
uninstalled to a future client:

```
Remove-Item -Path 'HKLM:\SOFTWARE\ManagedInstalls' -Recurse -Force
```

If you intend to reinstall the client and want it to pick up where it left off, remove
only the cache and leave the rest.

## If the service or tasks are stranded

An interrupted uninstall can leave the service registered against a deleted binary. The
service will fail to start and Windows will retry it on the configured recovery schedule.
Remove it directly:

```
sc.exe stop CimianWatcher
sc.exe delete CimianWatcher
```

If `sc.exe delete` reports the service is marked for deletion and it persists across a
reboot, the registration key can be removed directly:

```
Remove-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\CimianWatcher' -Recurse -Force
```

Check for any leftover tasks under the `Cimian` names before declaring the machine clean:

```
Get-ScheduledTask -TaskName 'Cimian *'
```

## See also

- [Installing Cimian](Installing-Cimian)
- [Updating Cimian](Updating-Cimian)
- [Uninstalling Software](Uninstalling-Software)
- [The Download Cache](The-Download-Cache)
- [cimiwatcher](cimiwatcher)
- [Troubleshooting](Troubleshooting)
