# Importing .exe Bundle Installers (WiX Burn, InstallShield, NSIS)

Most enterprise Windows installers ship as a single `.exe` that internally chains one or more `.msi` payloads plus prerequisite installers (VC++ Redist, .NET, etc.). The most common form is a **WiX Burn bundle**, but the same pattern applies to InstallShield setup launchers and NSIS-wrapped MSI installers.

This guide walks through the right way to write a pkgsinfo for one — using the **Trotec Ruby Bundle 2.11.1.64391** import as a worked example — and covers the install-loop pitfall that bit us in production.

## TL;DR

1. **Install the bundle once on your workstation**, then mine the Windows uninstall registry to find the main MSI's `ProductCode`.
2. Put the `.exe` bundle in `installer:` with the silent switches (`/install /quiet /norestart` for Burn; vendor-specific for other wrappers).
3. Put the main MSI's `ProductCode` in `installs:` with `type: msi` — this is what Cimian uses to decide "is it installed?"
4. **Do not** add a `type: file` installs entry pointing at a component .exe unless you can guarantee its `FileVersion` matches the bundle version. They usually don't — and the mismatch causes an infinite reinstall loop.

## Step-by-step: Trotec Ruby case study

### 1. Identify the wrapper type

```powershell
# Quick WiX Burn detection — Burn bundles embed the literal "WixBurn" marker in the PE
$bytes = [System.IO.File]::ReadAllBytes("C:\path\to\installer.exe")[0..4095]
if ([System.Text.Encoding]::ASCII.GetString($bytes) -match "WixBurn|Burn|Bundle") {
    "Burn bundle"
}
```

Burn bundles always accept this silent switch set:

| Switch | Purpose |
|---|---|
| `/install` | install (default action) |
| `/uninstall` | uninstall |
| `/repair` | repair |
| `/quiet` | no UI, no prompts |
| `/passive` | progress UI only |
| `/norestart` | suppress reboot |
| `/log <path>` | write log |
| `/layout <path>` | extract payloads only (no install) |

If `/?` opens a GUI (rather than printing help to a console), you are almost certainly looking at a Burn bundle.

### 2. Install it once locally to harvest the detection key

```powershell
# Snapshot first so you can diff afterwards
$dirs = @('C:\Program Files','C:\Program Files (x86)','C:\ProgramData')
$snap = foreach ($d in $dirs) { Get-ChildItem $d -Directory -EA 0 | Select -Expand FullName }
$snap | Out-File "$env:TEMP\before.txt"

# Silent install with a Burn log for forensics
sudo "C:\path\to\installer.exe" /install /quiet /norestart /log "$env:TEMP\install.log"
```

When the install finishes, look at the Burn log — it records the bundle ID and every child MSI's ProductCode:

```
i370: Session begin, registration key: SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{dfd070dc-23b6-4e0d-a413-5cbc33cb40ac}
i305: Verified acquired payload: Trotec.Ruby.Main.Installer.msi at path: ...,
       moving to: C:\ProgramData\Package Cache\{05D3C784-482A-4324-A386-10001F5FC6E9}v2.11.1.64391\Trotec.Ruby.Main.Installer.msi
```

Or query the live registry once it's installed:

```powershell
$paths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($p in $paths) {
    Get-ChildItem $p -EA 0 | ForEach-Object {
        $r = Get-ItemProperty $_.PSPath -EA 0
        if ($r.DisplayName -match 'YourAppName') {
            "Key: $($_.PSChildName)"
            "  DisplayName    : $($r.DisplayName)"
            "  DisplayVersion : $($r.DisplayVersion)"
            "  UninstallString: $($r.UninstallString)"
        }
    }
}
```

For Trotec Ruby, this revealed **two** uninstall entries:

| Key | DisplayName | DisplayVersion | What it is |
|---|---|---|---|
| `{dfd070dc-23b6-4e0d-a413-5cbc33cb40ac}` | Trotec Ruby Bundle | 2.11.1.64391 | The Burn bundle wrapper (uninstall calls the .exe) |
| `{05D3C784-482A-4324-A386-10001F5FC6E9}` | Trotec Ruby | 2.11.1.64391 | The main MSI inside (uninstall calls `msiexec /I{guid}`) |

The **main MSI's ProductCode** is what you want — it represents the actual application install state, not the bundle wrapper. If the user uninstalls just the app via Add/Remove Programs, the MSI entry vanishes but the bundle entry might linger; tracking the MSI gives you accurate state.

### 3. Write the pkginfo

```yaml
name: TrotecRuby
display_name: Trotec Ruby
version: 2.11.1.64391
catalogs:
- Production
category: Modeling
description: |
  Trotec Ruby is the design and production software for Trotec laser engraving and cutting systems.
developer: Trotec Laser GmbH
installer:
  type: exe
  location: /apps/modeling/TrotecRuby-x64-2.11.1.64391.exe
  hash: df1b792a...
  size: 1121089
  switches:
  - /install
  - /quiet
  - /norestart
installs:
- type: msi
  product_code: '{05D3C784-482A-4324-A386-10001F5FC6E9}'
  version: 2.11.1.64391
minimum_os_version: 10.0.19041
supported_architectures:
- x64
unattended_install: true
unattended_uninstall: true
```

Two separate concerns:

| Block | Purpose |
|---|---|
| `installer.type: exe` + switches | **How to install** — runs the Burn bundle .exe |
| `installs: type: msi product_code: …` | **How to detect installed** — Cimian queries Windows Installer for that GUID |

### 4. Verify against `StatusService`

The detection path is `packages/CimianTools/cli/managedsoftwareupdate/Services/StatusService.cs`:

- `CheckStatus` reaches **Priority 2** (`installs` array present) and calls `CheckInstallsArray`.
- For the `type: msi` entry, line 525 calls `CheckMsiWithUpgradeCode(productCode, upgradeCode, catalogVersion, name)`.
- If the MSI is not registered → returns `Reason="MSI product not installed", ReasonCode=ProductCodeMissing, NeedsAction=true` → Cimian schedules the install.
- If the MSI is registered at matching version → returns `Status=installed, NeedsAction=false` → skip.

If `CheckMsiWithUpgradeCode` returns "not found", Cimian also tries a **display_name fallback** (line 530-551) — it walks Add/Remove Programs looking for a `DisplayName` matching `display_name` from the pkginfo. This catches auto-updating apps (Chrome, etc.) whose ProductCode rotates per version.

### 5. UpgradeCode is optional

Some MSIs declare an `UpgradeCode`, some don't. The Trotec main MSI does not (the Burn bundle handles upgrades centrally). Omit `upgrade_code:` if you cannot find it — the ProductCode alone is sufficient for detection.

To find the UpgradeCode when one exists:

```powershell
Get-CimInstance Win32_Product -Filter "IdentifyingNumber='{05D3C784-482A-4324-A386-10001F5FC6E9}'" |
    Select-Object Name, Version, IdentifyingNumber, Vendor, PackageCode
# Win32_Product does not expose UpgradeCode directly — use registry reverse-lookup
# under HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\<packed-guid> with packed product code as a value name.
```

## The pitfall: do not use `type: file` for bundle components

It is tempting to add a second installs entry like this for "belt and suspenders":

```yaml
installs:
- type: msi
  product_code: '{05D3C784-...}'
  version: 2.11.1.64391
- type: file
  path: 'C:\Program Files (x86)\Trotec\Ruby\Manager\Ruby.Manager.exe'  # DO NOT DO THIS
```

**This causes an infinite reinstall loop.**

Why: `Ruby.Manager.exe` is one of ~12 component executables built independently inside the Trotec product. Its `FileVersion` (from the PE header) is `2.11.0.63389` — the internal component version — **not** the bundle version `2.11.1.64391` declared by the marketing release.

Trace through `StatusService.cs:447-471`:

1. `expectedVersion = item.Version` → `2.11.1.64391`
2. `GetFileVersion(Ruby.Manager.exe)` → `2.11.0.63389`
3. `CompareVersions("2.11.1.64391", "2.11.0.63389")` → `1` (expected > installed)
4. No `md5checksum` → `hashVerificationPassed = false`
5. Returns `NeedsAction=true, Reason="Version outdated: 2.11.0.63389 -> 2.11.1.64391"`

Every run, every hour, forever — until **LoopGuard** (see [install-loop-prevention.md](install-loop-prevention.md)) suppresses it 6-24 hours later.

**Fixes ranked best to worst:**

1. **Use MSI ProductCode alone.** The MSI's `DisplayVersion` in the registry matches the bundle version, so version comparison works correctly. Drop the file entry.
2. **Provide an exact `md5checksum`** alongside the file path. When hash matches, `StatusService.cs:456` accepts the install regardless of file version. But hashes are fragile — they break on minor patches.
3. **Pin the file entry's `version:` to the actual `FileVersion`** the component reports (`2.11.0.63389` in our case). Works but makes the pkginfo lie about what version the install actually is.

Just use MSI ProductCode. It is right.

## Decision tree: which installs type to use

```
Is the install registered in Windows Installer (has an MSI ProductCode)?
├── Yes → type: msi, product_code: '{GUID}', version: <catalog_version>
│         Add display_name: at the top of the pkginfo too — Cimian uses it
│         as a fallback when ProductCode rotation makes the GUID unreliable.
│
└── No  → Is there a single, stable file whose FileVersion matches the catalog version exactly?
          ├── Yes → type: file, path: '<full path>', version: <catalog_version>
          ├── No, but you can fingerprint it → type: file with md5checksum
          └── No → type: registry (custom HKLM check) OR write an installcheck_script
```

When in doubt, **prefer `type: msi` over `type: file`**. MSI ProductCodes are authoritative and rotate cleanly across versions; file versions are at the mercy of however the vendor's build pipeline stamps them.

## Mirror to all repos

After importing, the pkginfo and rebuilt catalog must reach every client. The Cimian repo lives in three places:

| Location | Purpose | Sync method |
|---|---|---|
| local repo (`deployment/`) | Source of truth for editing | git push |
| Mars (`admin@mars.its.ecuad.ca:/Users/Shared/Cimian/deployment/`) | On-campus HTTP origin (`http://mars.its.ecuad.ca/deployment`) | `scp` after `makecatalogs` |
| Azure blob (`cimiancloudstorage/repo/deployment/`) | Off-campus origin via FrontDoor | `az storage blob upload` |

A typical post-import sync:

```powershell
sudo makecatalogs
scp deployment/pkgsinfo/apps/modeling/MyApp-*.yaml admin@mars.its.ecuad.ca:/Users/Shared/Cimian/deployment/pkgsinfo/apps/modeling/
scp deployment/catalogs/Production.yaml deployment/catalogs/All.yaml admin@mars.its.ecuad.ca:/Users/Shared/Cimian/deployment/catalogs/
az storage blob upload --account-name cimiancloudstorage --container-name repo `
  --name "deployment/pkgs/apps/modeling/MyApp-1.0.exe" `
  --file "deployment/pkgs/apps/modeling/MyApp-1.0.exe" --auth-mode login --overwrite
az storage blob upload --account-name cimiancloudstorage --container-name repo `
  --name "deployment/pkgsinfo/apps/modeling/MyApp-1.0.yaml" `
  --file "deployment/pkgsinfo/apps/modeling/MyApp-1.0.yaml" --auth-mode login --overwrite
az storage blob upload --account-name cimiancloudstorage --container-name repo `
  --name "deployment/catalogs/Production.yaml" `
  --file "deployment/catalogs/Production.yaml" --auth-mode login --overwrite
```

The pipeline (`pipelines/cimian-bootstrap-mgmt.yml`) is taking over these sync responsibilities — see `docs/bootstrap-mgmt-pipeline-migration.md`.

## Related docs

- [How Cimian decides what needs to be installed](how-cimian-decides-what-needs-to-be-installed.md) — full priority chain
- [Install loop prevention](install-loop-prevention.md) — LoopGuard backoff thresholds
- [MSI ProductID handling](MSI-ProductID-Fix.md) — keeping ProductCode stable across builds
- [Status classification implementation](status-classification-implementation.md) — what each `ReasonCode` means
