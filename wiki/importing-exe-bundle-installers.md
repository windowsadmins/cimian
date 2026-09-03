# Importing EXE Bundle Installers

Many enterprise Windows installers ship as a single `.exe` that internally chains one or more MSI
payloads plus prerequisites. The most common form is a WiX Burn bundle; InstallShield setup
launchers and NSIS-wrapped MSI installers behave similarly. These are awkward because the bundle
registers several Add/Remove Programs entries — or none — so the obvious detection choice is
usually the wrong one. This page walks through importing one, using a fictional
**Example Vendor Suite 3.2.1** as the worked example.

## The short version

1. Install the bundle once on a scratch machine, then read the Windows uninstall registry to find
   the **main MSI's ProductCode**.
2. Put the `.exe` in the `installer:` block with the vendor's silent switches.
3. Put that ProductCode in `installs:` as a `type: msi` entry. That, not the bundle, is what
   Cimian uses to decide whether the product is present.
4. **Do not** add a `type: file` entry pointing at a component executable unless you can guarantee
   its `FileVersion` matches the bundle version. It usually does not, and the mismatch causes an
   endless reinstall loop.

## 1. Identify the wrapper

WiX Burn bundles embed a `WixBurn` marker near the start of the PE image. Read the first few
kilobytes and look for it:

```powershell
$bytes = [System.IO.File]::ReadAllBytes('C:\staging\ExampleVendorSuite-3.2.1.exe')[0..4095]
if ([System.Text.Encoding]::ASCII.GetString($bytes) -match 'WixBurn|Burn|Bundle') { 'Burn bundle' }
```

Another quick tell: if `/?` opens a graphical window rather than printing help to the console, it
is almost certainly a Burn bundle.

Burn bundles accept a fixed switch set:

| Switch | Purpose |
|---|---|
| `/install` | install (the default action) |
| `/uninstall` | uninstall |
| `/repair` | repair |
| `/quiet` | no UI, no prompts |
| `/passive` | progress UI only |
| `/norestart` | suppress reboot |
| `/log <path>` | write a log |
| `/layout <path>` | extract payloads without installing |

InstallShield and NSIS wrappers use different switches — commonly `/s /v"/qn"` and `/S`
respectively — and you have to determine them per vendor. Cimian does not guess for you at install
time; whatever you put in `installer.switches` is what runs.

## 2. Install it once and harvest the detection key

Snapshot the machine first so you can see what appeared:

```powershell
$dirs = @('C:\Program Files','C:\Program Files (x86)','C:\ProgramData')
$before = foreach ($d in $dirs) { Get-ChildItem $d -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName }
$before | Out-File "$env:TEMP\before.txt"
```

Then run the bundle silently from an elevated console, keeping a log:

```powershell
& 'C:\staging\ExampleVendorSuite-3.2.1.exe' /install /quiet /norestart /log "$env:TEMP\bundle-install.log"
```

A Burn log records the bundle's own registration key and every child MSI it acquires and caches,
which is often the fastest route to the ProductCode. Failing that, read the live registry:

```powershell
$paths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($p in $paths) {
    Get-ChildItem $p -ErrorAction SilentlyContinue | ForEach-Object {
        $r = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($r.DisplayName -like '*Example Vendor*') {
            [pscustomobject]@{
                Key             = $_.PSChildName
                DisplayName     = $r.DisplayName
                DisplayVersion  = $r.DisplayVersion
                UninstallString = $r.UninstallString
            }
        }
    }
}
```

For a bundle this typically returns **two** entries:

| Key | DisplayName | DisplayVersion | What it is |
|---|---|---|---|
| `{6F0A1D3E-...}` | Example Vendor Suite Bundle | 3.2.1 | the Burn wrapper; its uninstall string runs the `.exe` |
| `{9D3F1A77-...}` | Example Vendor Suite | 3.2.1 | the main MSI inside; its uninstall string is `msiexec /I{...}` |

**Use the main MSI's ProductCode.** It represents the application's actual install state. If a
user removes the app through Add/Remove Programs, the MSI entry disappears while the bundle entry
can linger, so tracking the MSI gives you accurate state and tracking the bundle does not.

Some bundles register nothing at all, or register only the wrapper. Section 6 covers that case.

## 3. Write the pkgsinfo

```yaml
name: ExampleVendorSuite
display_name: Example Vendor Suite
version: 3.2.1
catalogs:
- Production
category: Design
description: |
  Example Vendor Suite is the authoring and production application for Example Vendor hardware.
developer: Example Vendor Ltd
installer:
  type: exe
  location: apps/example-vendor/ExampleVendorSuite-3.2.1.exe
  switches:
  - install
  - quiet
  - norestart
installs:
- type: msi
  product_code: '{9D3F1A77-4C22-4B0E-B6D1-3E7F2A9C1140}'
  version: 3.2.1
minimum_os_version: 10.0.19041
supported_architectures:
- x64
unattended_install: true
unattended_uninstall: true
```

`installer.switches` are written Windows-style; a leading `/` is added if you omit it, so `install`
and `/install` are equivalent. `installer.location` is relative to `pkgs/` in the repository.

The two blocks answer two different questions, and conflating them is the root of most bundle
problems:

| Block | Answers |
|---|---|
| `installer.type: exe` plus switches | **how to install** — run the bundle |
| `installs:` with `type: msi` and `product_code` | **how to tell whether it is installed** — query Windows Installer for that GUID |

If you import with [cimiimport](cimiimport), it fills in `installer.hash` and `installer.size` for
you and can emit a starting `installs` array; review that array before publishing, because for a
bundle it will often propose file entries you do not want.

## 4. How detection then behaves

With an `installs` array present, the client evaluates that array and nothing below it in the
detection cascade. For the `msi` entry it looks up the ProductCode in both the 64-bit and 32-bit
views of the Windows uninstall registry and reads `DisplayVersion`.

- Not registered → the item needs installing.
- Registered at a version older than the pkgsinfo `version` → the item needs updating.
- Registered at the pkgsinfo version or newer → nothing to do.

**Any one failing entry short-circuits the whole item to "needs action".** That is why a spurious
extra entry is so damaging — see section 5.

Two narrow display-name fallbacks exist for products whose Windows Installer registration is
unreliable:

- When an `installs` entry declares **neither** `product_code` nor `upgrade_code`, the client
  searches Add/Remove Programs by the item's `display_name`, falling back to `name`.
- When the **entry itself** carries a `display_name`, an Add/Remove Programs hit on that name
  counts as installed. This is opt-in per entry and exists for wrapper MSIs that drop their
  Windows Installer registration after an in-app self-update.

Neither fallback applies when the entry declares a ProductCode or UpgradeCode. **Declared codes are
authoritative** and fuzzy matching is deliberately disabled in their presence, so a stale GUID
fails rather than being papered over by a name match. Name matching is also one-directional — a
registry `DisplayName` that contains your search string counts, but not the reverse — so
`Example Vendor Suite Reader` will never be mistaken for `Example Vendor Suite`.

## 5. Do not use `type: file` for bundle components

It is tempting to add a second entry for belt and braces:

```yaml
installs:
- type: msi
  product_code: '{9D3F1A77-4C22-4B0E-B6D1-3E7F2A9C1140}'
  version: 3.2.1
- type: file
  path: 'C:\Program Files\Example Vendor\Suite\SuiteManager.exe'
  version: 3.2.1
```

**This causes an endless reinstall loop.** Component executables inside a bundle are built
independently and stamped with their own internal `FileVersion`, which is almost never the
marketing version on the bundle. If `SuiteManager.exe` reports `3.2.0.9910`, the check compares
the pkgsinfo version `3.2.1` against it, finds the catalog newer, and reports the item as outdated
— every run, forever, on a machine where the product is perfectly current. [Install loop
prevention](Install-Loop-Prevention) will eventually suppress it, but suppression is a gag, not a
fix, and the item shows as broken until you correct the pkgsinfo.

Ranked fixes:

1. **Use the MSI ProductCode alone.** The registry `DisplayVersion` matches the bundle version, so
   the comparison is correct. Drop the file entry. This is the right answer.
2. **Provide a hash** on the file entry. When the hash matches, it is authoritative and the version
   mismatch becomes informational. But hashes break on every patch, so this trades one maintenance
   burden for another.
3. **Pin the file entry's `version:` to the component's real `FileVersion`.** It works, but the
   pkgsinfo now carries a version that does not describe what was shipped, and it has to be
   re-derived on every release.

## 6. When the bundle registers nothing usable

Some wrappers leave no MSI registration at all, or leave only a wrapper entry whose
`DisplayVersion` never changes. In that case, in order of preference:

- **A stable file whose `FileVersion` genuinely tracks the release.** Check it on two consecutive
  releases before trusting it.

  ```yaml
  installs:
  - type: file
    path: 'C:\Program Files\Example Vendor\Suite\Suite.exe'
    version: 3.2.1
  ```

- **A file plus a hash**, when the file has no usable version metadata. A file with neither a
  version nor a hash can never be confirmed and the item reports as outdated forever.
- **An `installcheck_script`**, when the truth lives somewhere only a script can read. Remember
  that exit 0 means "install needed" — see [Scripts In pkgsinfo](Scripts-In-pkgsinfo).

If the product does declare an UpgradeCode, it is worth adding: an UpgradeCode is stable across
versions where a ProductCode usually is not. Look it up under
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes`, whose subkey names are
packed GUIDs carrying the ProductCodes as value names. `Win32_Product` does not expose it, and
querying `Win32_Product` at all is best avoided — it reconfigures every installed MSI as a side
effect.

## Choosing an installs entry type

```
Is the product registered with Windows Installer (does it have an MSI ProductCode)?
├── Yes -> type: msi with product_code (add upgrade_code when one exists)
└── No  -> Is there one stable file whose FileVersion tracks the release?
           ├── Yes -> type: file with path and version
           ├── No, but it can be fingerprinted -> type: file with path and a hash
           └── No -> installcheck_script
```

When in doubt, prefer `type: msi` over `type: file`. ProductCodes are authoritative and rotate
cleanly across versions; file versions are at the mercy of however the vendor's build pipeline
stamps them.

## 7. Removal

An item with `installer.type: exe` is removable by default: with no `uninstaller:` block declared,
the client resolves the product's own uninstaller from the Windows uninstall registry, preferring
`QuietUninstallString`, and infers a silent switch when the string is not already quiet. For a
Burn bundle that means it invokes the bundle's own uninstall path.

The inferred switch is a guess. When you know the vendor's switches, declare them:

```yaml
uninstaller:
- type: exe
  command: 'C:\ProgramData\Package Cache\{6F0A1D3E-1B44-4E7C-9C0A-2D5E8F3B1A66}\ExampleVendorSuite-3.2.1.exe'
  switches:
  - uninstall
  - quiet
  - norestart
```

Note that a Burn bundle's cached uninstaller path contains the bundle's own version, so it changes
on every release. See [Uninstalling Software](Uninstalling-Software) for the full mechanism.

## 8. Publishing

Rebuild the catalogs with [makecatalogs](makecatalogs) and publish the pkgsinfo, the payload and
the rebuilt catalogs to the repository your clients read. How that publication happens depends on
how your repository is hosted and served — see [The Cimian Repository](The-Cimian-Repository).

## See also

- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Installs Arrays](Installs-Arrays)
- [Installer Types](Installer-Types)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Uninstalling Software](Uninstalling-Software)
- [Install Loop Prevention](Install-Loop-Prevention)
- [cimiimport](cimiimport)
- [The Cimian Repository](The-Cimian-Repository)
