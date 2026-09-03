# Configuring Clients With Intune

This page covers configuring an already-installed Cimian client from an MDM. It
applies to Microsoft Intune and, with different tooling, to any management system
that can write registry values and deliver files. For installing the client
itself, see [Deploying Cimian With Intune](Deploying-Cimian-With-Intune).

## What policy can and cannot deliver

Read this first, because it decides your whole approach.

Cimian reads exactly **four** settings from a registry policy key. Everything
else in [Client Configuration](Client-Configuration) — catalogs, authentication,
loop-guard tuning, script behaviour, auto-removal, client certificates — can only
be set in `Config.yaml`, and `Config.yaml` can only reach a device as a file. It
has no MDM surface at all.

So a typical deployment uses both mechanisms: a configuration file delivered as a
package for the full settings, and, where you want a fleet-wide setting to
override whatever is in that file, the policy key.

Cimian does not read `HKLM\SOFTWARE\Cimian\Config`. Older documentation
describing a broad configuration surface at that key, delivered over
`./Device/Vendor/MSFT/Policy/Config/Software/Cimian/Config/...`, describes
something that has never existed in the client.

## The policy key

```
HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Cimian
```

| Value name | Type | Applied when | Overrides |
|---|---|---|---|
| `SoftwareRepoURL` | `REG_SZ` | Non-blank; whitespace is trimmed | `SoftwareRepoURL` |
| `ClientIdentifier` | `REG_SZ` | Non-blank; whitespace is trimmed | `ClientIdentifier` |
| `InstallerTimeout` | `REG_DWORD`, or `REG_SZ` holding an integer | Value is 60 or greater | `InstallerTimeout` |
| `CacheRetentionDays` | `REG_DWORD`, or `REG_SZ` holding an integer | Value is 0 or greater | `CacheRetentionDays` |

Both numeric values accept a DWORD or a numeric string, because a Policy CSP
delivers ADMX decimal elements either way depending on the profile type.

Any other value you write under this key is ignored. There is no policy override
for `Catalogs`, for any authentication setting, or for anything else.

The key is read on every configuration load, so the override applies to every run
including runs where `Config.yaml` is missing or unparseable, and it always wins
over the file. Removing a value from the key restores whatever the file says at
the next run.

## Delivering the policy values

**No ADMX template ships with Cimian.** The client reads the registry key
directly and does not care how the values got there, but nothing in the
repository defines the policy for you, so an ADMX-backed profile requires an ADMX
and ADML you author and ingest yourself. There is no Intune settings-catalog
entry for Cimian either — the settings catalog only surfaces policies Microsoft
or an ingested ADMX has defined.

That leaves three practical routes.

### Author and ingest an ADMX

Write an ADMX/ADML pair whose policy elements write the four values above under
`SOFTWARE\Policies\Cimian`, ingest it with the Policy CSP's `ADMXInstall` node in
a custom OMA-URI profile, and then configure the resulting policies. This is the
route the client was designed around, and it gives you a real Intune policy with
proper reporting. Authoring the ADMX is your work; the payload it must produce is
the table above and nothing else.

A custom OMA-URI under `./Device/Vendor/MSFT/Policy/Config/...` only addresses
policies that already exist in the tenant, whether built in or ingested. Pointing
one at a Cimian path without first ingesting an ADMX that defines it does not
write the registry value.

### Deploy the values with a script

The simplest route, and the one that needs no ADMX. Run this as a platform script
or a remediation in SYSTEM context:

```powershell
$key = 'HKLM:\SOFTWARE\Policies\Cimian'
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name 'SoftwareRepoURL' -Value 'https://cimian.example.com/repo' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name 'ClientIdentifier' -Value 'lab-standard' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name 'InstallerTimeout' -Value 3600 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name 'CacheRetentionDays' -Value 14 -PropertyType DWord -Force | Out-Null
```

Values written this way are not true policy: nothing removes them when the
assignment is removed. If you need them retractable, pair the script with an
explicit removal script.

To remove them:

```powershell
Remove-Item -Path 'HKLM:\SOFTWARE\Policies\Cimian' -Recurse -Force -ErrorAction SilentlyContinue
```

### Include them in the package that delivers the configuration file

If you are already shipping a configuration package, its install script can write
the registry values at the same time. This keeps one artefact per configuration
rather than two assignments that can drift apart.

## Delivering a configuration file

Everything not in the four policy values has to arrive as
`%ProgramData%\ManagedInstalls\Config.yaml`. Build a package that writes that
file and deploy it as a Win32 app.

The file must use PascalCase keys. Unrecognised keys are silently discarded, so a
casing mistake in a fleet-wide package produces no error anywhere and every
client falls back to defaults.

Author the file you want to ship:

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
ClientIdentifier: lab-standard
Catalogs:
  - Production
CacheRetentionDays: 14
InstallerTimeout: 3600
AutoRemove: true
```

Put it beside an install script in a source folder, and have the script place it:

```powershell
$dest = Join-Path $env:ProgramData 'ManagedInstalls'
New-Item -Path $dest -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'Config.yaml') -Destination (Join-Path $dest 'Config.yaml') -Force
```

Wrap the folder with `IntuneWinAppUtil.exe` and upload it as a Win32 app with
install command `powershell.exe -ExecutionPolicy Bypass -File .\install.ps1`,
uninstall command of your choosing, and install behaviour **System**.

For a detection rule, either check that the file exists at
`C:\ProgramData\ManagedInstalls\Config.yaml`, or, so that an edited file is
re-deployed, use a detection script that compares its content:

```powershell
$path = 'C:\ProgramData\ManagedInstalls\Config.yaml'
if ((Test-Path $path) -and ((Get-Content $path -Raw) -match 'ClientIdentifier:\s*lab-standard')) {
    Write-Output 'Detected'
    exit 0
}
exit 1
```

An Intune detection script signals "present" with exit code 0. Cimian's own
`installcheck_script` uses the opposite convention — exit 0 means the item needs
installing. Do not reuse a script across the two systems without flipping the
polarity. See [Scripts in pkgsinfo](Scripts-In-pkgsinfo).

You can also deploy the configuration file through Cimian itself once a client is
bootstrapped, as an ordinary package in a manifest. That is circular for a first
install but convenient for later changes.

## Triggering a run from the MDM

Cimian's routine cadence is a scheduled task, not an MDM push — see
[How Cimian Runs](How-Cimian-Runs). To force a run after a configuration change,
have a remediation script write the headless trigger file; the watcher service
picks it up within 10 seconds:

```powershell
New-Item -Path 'C:\ProgramData\ManagedInstalls\.cimian.headless' -ItemType File -Force
```

Because this is a detection-and-remediation pattern, the paired detection script
must report non-compliance every time it runs if you want the remediation to fire
on every cycle.

## Verifying what a client is actually using

Ask the client, rather than reading the file. `--show-config` prints the values in
force after `Config.yaml` has been read and the policy override applied, so it is
the only reliable way to see whether policy is winning:

```
managedsoftwareupdate --show-config
```

It does not print `CacheRetentionDays`. For that value, plus the cache path,
size and oldest entry:

```
managedsoftwareupdate --cache-status
```

To see the raw policy values, independently of whether the client agrees:

```powershell
Get-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Cimian' -ErrorAction SilentlyContinue
```

If `--show-config` reports something you did not set, work through it in this
order: the policy key wins over the file; a blank `CachePath`, `CatalogsPath` or
`ManifestsPath` resets to its default; a misspelled or wrongly-cased key in the
file is discarded silently; and an unparseable file falls back to defaults after
printing a single line about the failure. The client's session log records that
line — see [Logging](Logging).

## See also

- [Client Configuration](Client-Configuration)
- [Client Identifier Resolution](Client-Identifier-Resolution)
- [How Cimian Runs](How-Cimian-Runs)
- [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
- [Securing The Repository](Securing-The-Repository)
- [Scripts in pkgsinfo](Scripts-In-pkgsinfo)
- [Troubleshooting](Troubleshooting)
