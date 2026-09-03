# Troubleshooting

This page is organised by symptom. Each section says what to check, in order, and what
each finding usually means. It assumes you can get an administrative shell on the
affected machine and read `%ProgramData%\ManagedInstalls`.

Two pages underpin everything here: [Logging](Logging) for where the evidence lives,
and [Item-Status-Reference](Item-Status-Reference) for what a status actually means.

## Before anything else

Three commands answer most of "is this machine even managed":

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --version
```

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --show-config
```

```powershell
Get-Content "$env:ProgramData\ManagedInstalls\reports\run.log" -Tail 60
```

`--show-config` prints the repository URL, client identifier, catalog list, cache
location and cache state actually in effect — including any values applied by policy,
which override the configuration file. If those are wrong, stop here and fix them; see
[Client-Configuration](Client-Configuration).

`reports\run.log` is always the most recent run in full, at every log level, regardless
of the verbosity that run was launched with.

## No run is happening at all

The machine's newest session directory is hours or days old, or there are none.

**Check the session record.** If the newest session says `aborted`, runs are starting
and dying; skip to the next section.

```powershell
Get-Content "$env:ProgramData\ManagedInstalls\reports\sessions.json" | ConvertFrom-Json |
  Select-Object -First 5 session_id, run_type, status, end_time
```

**Check the scheduled task.** Cimian's hourly run is a scheduled task named
`Cimian Managed Software Update Hourly`, running as SYSTEM.

```powershell
Get-ScheduledTask -TaskName "Cimian Managed Software Update Hourly" | Get-ScheduledTaskInfo
```

A `LastTaskResult` that is not 0, or a `LastRunTime` far in the past, points at the
task rather than at Cimian. A missing task means the client was installed without its
tasks, or something removed them; reinstalling the client re-registers them. The task
requires a network connection to start, so a machine that is offline at every trigger
never runs.

**Check the watcher service.** `CimianWatcher` runs as SYSTEM, polls for trigger flag
files every ten seconds, and applies staged client updates.

```powershell
Get-Service CimianWatcher
```

```powershell
Get-Content "$env:ProgramData\ManagedInstalls\logs\cimiwatcher.log" -Tail 40
```

A stopped or failed service means on-demand triggers do not work, but it does not by
itself stop the hourly task.

**Check for a stuck instance.** Only one `managedsoftwareupdate` may run at a time; a
second one exits immediately.

```powershell
Get-Process managedsoftwareupdate -ErrorAction SilentlyContinue
```

A process that has been running for hours is stuck on an install or a script. Preflight
and postflight scripts have no timeout at all, so a script that waits for input blocks
the run indefinitely. Kill the process, then look at the tail of the session's
`install.log` to see what it was doing.

**Run it by hand.** If the scheduled infrastructure looks healthy, prove the client
works:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --checkonly -vv
```

This changes nothing on the machine and writes a session of its own.

### Runs start and never finish

A session left at `running` by a killed process is closed out by the next run and
rewritten as `aborted`, with `environment.aborted_reason` naming the item it died on.
That name is the lead.

Common causes: the machine reboots on a schedule in the middle of a long install; the
scheduled task's four-hour execution limit is reached by a very large download; a
preflight script never returns.

An aborted run does not write its reports, so the reports directory still describes the
last run that finished. A machine can look completely healthy in `items.json` and not
have completed a run in days. Always check `sessions.json` first.

## A run happens but nothing installs

**Check whether the run found any items.**

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --checkonly -vvv
```

If the manifest section shows no items, the problem is manifest resolution, not
installation.

**Check which manifest the client asked for.** The client resolves its primary manifest
by trying, in order: the client certificate common name (only when configured), the
`ClientIdentifier` value, the machine name, the machine's BIOS serial number, then
`Orphaned`, then `site_default`. Only a 404 advances that chain — any other error
(authentication failure, TLS failure, a proxy returning 502) stops resolution rather
than falling through to the catch-all. So an authentication problem does not look like
"manifest not found"; it looks like nothing at all.

The verbose output names each URL it tries. Compare it against what your repository
actually serves.

**Check the catalogs.** Items come from catalogs, not manifests. A manifest can name an
item that no loaded catalog offers, and the item is then simply absent.

```powershell
Get-ChildItem "$env:ProgramData\ManagedInstalls\catalogs"
```

If a catalog file is missing or stale, the client fell back to a cached copy after a
download failure — which is deliberate, so that a repository outage does not
unmanage the fleet, but it does mean you may be looking at yesterday's catalog. A
catalog that was never regenerated after a pkgsinfo was added will not contain the
item at all.

Note that catalog precedence is highest version wins, not list order. Listing a catalog
first does not pin an older version.

**Check whether everything was deferred.** Items removed from the run by an install
window, a blocking application, or an active user report as `Pending` with a reason
code, and the run legitimately does nothing. See
[Item-Status-Reference](Item-Status-Reference).

**Check for loop suppression.**

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --loop-status
```

## An item reinstalls every run

This is the most common real fault, and it is almost never the installer. It is the
item's own detection criteria disagreeing with what the installer puts on disk: Cimian
checks, concludes the item is missing, installs it, checks again next run, concludes it
is missing again.

**Find what the check keeps finding.** The loop status output names it directly, and so
does `reports\loop_suppressed.json` in its `trigger` field.

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --loop-status
```

The output distinguishes a cache hit from a cache miss, which separates "this is a
download problem" from "this is a detection problem". A cache hit with repeated
installs is always a detection problem.

**Match the trigger to the defect.**

| Trigger reason code | Usually means |
|---|---|
| `file_missing` | A path in the `installs` array does not exist after installation. The installer puts the file somewhere else, or the path has a typo, or it is a per-user path being checked from a SYSTEM context |
| `version_outdated` | The file's own version metadata does not match the catalog version. Common with installers whose executable version differs from the product version |
| `product_code_missing` | The declared MSI product code is not what the installer registers. A build that regenerates its product code every time can never match a pinned one |
| `installcheck_needed` | The `installcheck_script` always exits 0. Remember the convention: exit 0 means "install needed" |
| `hash_mismatch` | The file on disk is not the file the pkgsinfo describes |

An `installs` entry that verifies a shortcut file, a per-user path, or anything a
postinstall script creates outside the installer's own transaction is a permanent loop
waiting to happen — if the postinstall step is ever skipped, the item can never be
confirmed.

**Fix the pkgsinfo, then clear the suppression.** Changing the item's install behaviour
in the repository clears suppression automatically, because the client fingerprints the
catalog entry. To clear it by hand:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --clear-loop "Example App"
```

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --clear-loop all
```

Items marked `on_demand` or `recurring` reinstall every run by design and are exempt
from loop protection. Seeing one repeatedly is not a fault.

See [Install-Loop-Prevention](Install-Loop-Prevention) and
[Installs-Arrays](Installs-Arrays).

## An item reports Installed but is not present

**Check what verified it.** Read the item's `detection_method` and
`status_reason_code`.

```powershell
Get-Content "$env:ProgramData\ManagedInstalls\reports\items.json" | ConvertFrom-Json |
  Where-Object item_name -eq "Example App" |
  Select-Object current_status, installed_version, detection_method, status_reason_code, status_reason
```

`status_reason_code: no_checks` is the answer most of the time. An item whose installer
type is `nopkg`, `script` or empty, and which declares no `installs` array, no
`installcheck_script` and no `check` block, is assumed installed. Nothing was verified.
The remedy is to give the item something to check.

`detection_method: managed_installs` means the answer came from Cimian's own receipt of
having installed it, not from looking at the machine. That receipt survives the
software being removed by other means.

`detection_method: registry` with a display-name match can attach to the wrong product
when the name is a prefix of another product's name.

**Check whether the install was actually completed.** A run that was truncated
mid-install never runs the item's postinstall step, so anything that step was
responsible for — a shortcut, a licence file, a configuration key — is absent even
though the installer itself succeeded. Look for an `aborted` session around the time
the item was last installed.

## A download fails

**Look at the error.** The session log names the URL, the retry attempts and the final
reason.

| Message | Meaning | What to do |
|---|---|---|
| `Hash mismatch: expected X, got Y` | What the repository served is not what the pkgsinfo declares | The payload was replaced without the pkgsinfo being updated, or a proxy returned an error page. Re-import the package |
| `Incomplete download` | The bytes received did not match the declared content length | A truncating proxy or an interrupted connection |
| Stalled download | Throughput stayed below the minimum for two minutes | Network. The partial file is kept and the next run resumes from where it stopped |
| HTTP 401 or 403 | The repository rejected the client | Check the authentication settings printed by `--show-config`, and check them against how the repository is actually secured. See [Securing-The-Repository](Securing-The-Repository) |
| HTTP 404 | The `installer.location` in the pkgsinfo does not exist on the repository | Compare the built URL in the log against what is served |

Cimian retries a download five times with increasing backoff, resumes partial downloads
where the server supports ranges, and raises its own timeout for large payloads. A
package that fails all five attempts has a real problem, not a transient one.

**Check the cache.**

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --cache-status
```

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --validate-cache
```

A corrupt cached payload is deleted and re-downloaded automatically. To start from
nothing:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --clean-cache
```

That deletes every cached payload, not just expired ones, and the next run re-downloads
everything the machine needs. On a machine with large packages, expect a long run.

Note that a full disk shows up as a deferral with reason code `disk_space` rather than
as a download failure: Cimian wants twice the installer's size free before it will
proceed.

See [The-Download-Cache](The-Download-Cache).

## An installer fails with a non-zero exit

**Read the exit code.** For an MSI the error text carries `MSI_EXIT=<code>`, extracted
diagnostics from the verbose log, and the log's path.

```powershell
Get-ChildItem "$env:ProgramData\ManagedInstalls\logs\installs" | Sort-Object LastWriteTime -Descending | Select-Object -First 5
```

The three most recent verbose logs are kept per item.

| Code | Meaning | What to do |
|---|---|---|
| `0` | Success | — |
| `3010` | Success, reboot required | Cimian records this as a success and notes the restart |
| `1618` | Another installation is in progress | Cimian retries this automatically, three times with backoff. Persistent 1618 means something else on the machine holds the installer mutex — Windows Update, a competing management agent |
| `1603` | Generic MSI failure | Read the verbose log. Frequently a pending reboot, a permissions problem on a target path, or a repair of a package whose source is no longer valid |
| `1605` / `1614` | Product not installed / already uninstalled | Treated as success on an uninstall |
| Other | Product-specific | Look it up for that installer |

If an item's installer legitimately returns a non-zero code on success, declare it in
the item's `success_codes` rather than treating the failure as noise.

**Check for a timeout.** An install that reports "Installation timed out after N
minutes" was killed, along with its whole process tree. The per-item limit comes from
`installer_timeout` on the item, falling back to the client-wide default of 900
seconds. Large installers need their own value.

**Check for blocking applications.** An installer that exits almost immediately with a
generic failure is often refusing to run while its own application is open. Declare
those in `blocking_applications` so Cimian defers instead of failing. See
[Blocking-Applications](Blocking-Applications).

**Check the package's own scripts.** Pre and postinstall scripts packaged into an MSI
run as custom actions with no console. Their output is written to a sidecar file and
drained into the *next* session's log, capped at 500 lines per phase. If an install
fails for no visible reason, the explanation may be in the following run's
`install.log`, or under `logs\packages\`.

## The client is stuck on an old version

Cimian cannot replace its own running binaries, so an update to the client is staged
rather than installed inline.

**Check whether an update is staged.**

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --selfupdate-status
```

A staged update leaves a marker file at
`%ProgramData%\ManagedInstalls\.cimian.selfupdate` recording the item, version,
installer type, staged payload path and the time it was scheduled.

**Check whether the watcher can apply it.** The watcher service applies a staged update
at service start, immediately after any trigger-driven run finishes, and on its ten
second poll whenever the machine is idle — meaning no `managedsoftwareupdate` process
is running, including one the watcher did not launch. A machine where a run is always
in progress, or where the watcher service is stopped, never reaches the idle window.

```powershell
Get-Service CimianWatcher
```

**Read the update's own log.**

```powershell
Get-ChildItem "$env:ProgramData\ManagedInstalls\logs\selfupdate" | Sort-Object LastWriteTime -Descending | Select-Object -First 3
```

A verbose MSI log that says the installation completed successfully while the version
did not change means the installer ran a maintenance pass rather than an upgrade. The
client guards against this, but a hand-run `msiexec` with repair properties on a
package whose product code is not already registered reproduces it exactly. Install the
MSI plainly and let the upgrade remove the previous build.

**Recover from a stuck marker.** The marker is cleared before the installer launches,
so a crashed installer leaves the machine un-updated with nothing pending, and the next
run re-detects the package. To clear it by hand:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --clear-selfupdate
```

To restart the watcher without performing an install:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --restart-service
```

Only `msi`, `pkg` and `nupkg` payloads can update the client. Anything else is refused.

See [Updating-Cimian](Updating-Cimian).

## Permission and elevation failures

`managedsoftwareupdate` requires administrative rights. Without them it prints
`Administrative access required.` and exits 1 before doing any work. Everything below
is about how it gets those rights.

**Confirm the shell is elevated.**

```powershell
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

### Prefer the service path over interactive elevation

Cimian's normal execution paths already run as SYSTEM: the hourly scheduled task, the
watchdog task, and the `CimianWatcher` service. None of them prompts, and none of them
depends on how the machine is joined. Anything that has to elevate a user's session is
the fragile path.

Trigger a run through the service rather than launching the client directly:

```powershell
& "$env:ProgramFiles\Cimian\cimitrigger.exe" headless
```

```powershell
& "$env:ProgramFiles\Cimian\cimitrigger.exe" gui
```

Both first try the service. If the service is unavailable they fall back to direct
elevation. To skip the service attempt and elevate directly:

```powershell
& "$env:ProgramFiles\Cimian\cimitrigger.exe" --force headless
```

`cimitrigger debug` runs its own diagnostics on the trigger path.

### Domain-joined machines behave differently from Entra-joined ones

Interactive elevation depends on the token the signed-in user holds and on the User
Account Control policy in force. On an Entra-joined device, standard shell elevation
generally works. On an on-premises domain-joined device, the token type and the domain's
UAC policy can prevent an interactive elevation that succeeds elsewhere, and the visible
symptom is not an error dialog: the client starts unelevated, prints
`Administrative access required.`, and exits 1. Every install then appears to "fail"
for no reason.

On a hybrid-joined device the behaviour follows how the device was actually joined,
which is not always what the fleet inventory says.

Diagnose the elevation itself rather than the installs:

```powershell
whoami /groups | Select-String "S-1-5-32-544"
```

```powershell
Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" | Select-Object EnableLUA, ConsentPromptBehaviorAdmin, FilteredAdministratorToken
```

Confirm the service really is configured to run as SYSTEM:

```powershell
Get-CimInstance Win32_Service -Filter "Name='CimianWatcher'" | Select-Object Name, State, StartMode, StartName
```

`StartName` should be `LocalSystem`.

The durable fix on a domain-joined estate is not a UAC policy change; it is to route
every trigger through the service or a SYSTEM scheduled task, so that no interactive
elevation is needed at all.

To reproduce a run as SYSTEM without touching the shipped tasks:

```powershell
schtasks /Create /TN "CimianDiagnosticRun" /TR "\"C:\Program Files\Cimian\managedsoftwareupdate.exe\" --checkonly -vv" /SC ONCE /ST 23:59 /RU SYSTEM /F
```

```powershell
schtasks /Run /TN "CimianDiagnosticRun"
```

```powershell
schtasks /Delete /TN "CimianDiagnosticRun" /F
```

### Permission failures inside an install

An elevated run can still fail on individual items. A registry key owned by an MDM
policy will reject a write from SYSTEM even where the access control list appears to
grant full control, and the package's own script may report success regardless. A
per-user path written from a SYSTEM context lands in the wrong profile. Neither is a
Cimian elevation problem; both surface as an item that installs happily and never
verifies.

## Tracing which manifest contributed an item

When an item appears on a machine you did not expect it on — or fails to appear on one
you did — the question is which manifest declared it, and in which section.

Cimian records, for every item, the manifest it came from and how it was referenced.
Those records are emitted as debug lines, so they need verbosity 3:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --checkonly -vvv
```

Each item produces one line naming the manifest and the section:

```
[2026-03-04 14:15:06] DEBUG Setting item source item: Example App sourceManifest: WORKSTATION-01 sourceType: managed_installs
```

To pull just those lines out of a run:

```powershell
Select-String -Path "$env:ProgramData\ManagedInstalls\reports\run.log" -Pattern "Setting item source"
```

The section names you will see are `managed_installs`, `managed_updates`,
`managed_uninstalls`, `optional_installs` and `default_installs`, plus the conditional
forms `conditional_managed_installs`, `conditional_managed_updates`,
`conditional_managed_uninstalls` and `conditional_optional_installs`. A conditional
form tells you the item came from a `conditional_items` block whose predicate evaluated
true on this machine, which is usually the answer to "why does this one machine have
it".

Items that no manifest declared, and which are present only because another item
requires them, are marked as coming from `dependency` rather than from a manifest.
They also appear in the log as an explicit line when they are added:

```
[2026-03-04 14:15:07] INFO  Added dependencies: Example Runtime, Example Codec
```

The source record is always populated; verbosity only controls whether you see it.

A few interactions worth knowing when the source does not explain the outcome:

- Included manifests are resolved recursively, and a circular include terminates rather
  than looping. An item can therefore come from a manifest two or three levels below
  the one assigned to the machine.
- When the same item is declared more than once, one action wins: `install` beats
  `uninstall`, which beats `update`, which beats `default`, which beats `optional`,
  which beats the MDM profile and app actions. Within an equal action the newer version
  wins. So an item you removed from one manifest may still be installed because another
  manifest asks for it more strongly.
- A user's self-service choice can add an item or promote an optional one, but never
  overrides an admin action. A self-service request for an item the admin has already
  mandated is logged and ignored.

See [Manifests](Manifests) and [Conditional-Items](Conditional-Items).

## Collecting evidence

If you cannot resolve it on the machine, collect the whole logs and reports tree plus
the client version, as described in [Logging](Logging). Remember that those files carry
the machine name, the signed-in user, your repository URL and the full list of software
the machine manages — review before sharing.

## See also

- [Logging](Logging)
- [Item-Status-Reference](Item-Status-Reference)
- [Reporting-Data-Contract](Reporting-Data-Contract)
- [Install-Loop-Prevention](Install-Loop-Prevention)
- [How-Cimian-Decides-What-Needs-To-Be-Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Client-Configuration](Client-Configuration)
- [How-Cimian-Runs](How-Cimian-Runs)
- [Manifests](Manifests)
- [The-Download-Cache](The-Download-Cache)
- [Updating-Cimian](Updating-Cimian)
- [cimitrigger](cimitrigger)
- [cimiwatcher](cimiwatcher)
