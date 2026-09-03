# Installs Arrays

The `installs` array is Cimian's declarative way of saying what a package leaves behind, so
the client can tell whether the package is present and current without running any of your
code. It is the preferred detection mechanism for anything with a real payload. This page
documents every entry type, every field, the type inference rules, and how conditions inside
one entry override each other.

For where the `installs` array sits relative to the other detection mechanisms, see
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

## How the array is evaluated

If a pkgsinfo carries at least one `installs` entry, the client checks every entry.

- **Any single failing entry short-circuits the whole item** to needs-action. The remaining
  entries are not evidence against that.
- If every entry passes, the item is `installed` with detection method `installs_array` and
  a reason of the form `All N install checks passed`.
- Every failure names the entry by index and identity, for example
  `installs[2] msi product_code={...} upgrade_code={...}`, so a multi-entry array stays
  diagnosable in the session log and in `--loop-status`.

An array is a conjunction. Adding entries makes the check stricter, never more forgiving.
Two entries that describe alternative valid states of the same machine will fight each
other and produce a permanent reinstall.

## Entry types and inference

`type:` is optional. When it is absent, the client infers the type from which identity field
the entry carries:

| Entry declares | Inferred type |
|---|---|
| `identity_name` | `msix` |
| `product_code` or `upgrade_code` | `msi` |
| `path` | `file` |
| none of the above | *(nothing)* |

Inference is only consulted when `type:` is missing; an explicit `type:` is lowercased and
used as given. Note the precedence: an entry with both `identity_name` and `path` and no
`type:` is treated as MSIX, and the path is ignored.

**An entry that has no `type:` and no identity field is an error, not a pass.** It returns
status `error` with needs-action true and reason code `check_failed`. The client will never
treat an unrecognisable entry as satisfied. An entry of an explicit type it does not
recognise falls into the same default and errors the same way.

Recognised types are `file`, `directory`, `msi`, `msix` and `appx`. `appx` is a synonym for
`msix` and behaves identically.

## Field reference

Not every field applies to every type. Fields not listed for a type are ignored by it.

| Field | Types | Purpose |
|---|---|---|
| `type` | all | `file`, `directory`, `msi`, `msix`, `appx`. Optional when inferable |
| `path` | `file`, `directory` | Absolute path to check |
| `md5checksum` | `file` | Expected file hash. The algorithm is inferred from the value's length |
| `version` | `file`, `msi` | Expected minimum version. Falls back to the item's `version` when omitted |
| `product_code` | `msi` | Windows Installer ProductCode GUID, unique per release |
| `upgrade_code` | `msi` | Windows Installer UpgradeCode GUID, stable across releases |
| `display_name` | `msi` | Opt-in Add/Remove Programs display-name fallback for this entry |
| `identity_name` | `msix`, `appx` | Package identity name from the app manifest |
| `key_path` | `msi` | Absolute path to the primary executable, checked in addition to the registry |

## `type: file`

A file entry is evaluated in three stages: presence, then hash, then version.

**Presence.** If the path does not exist the entry fails immediately with reason code
`file_missing`. Whether the failure counts as an update depends on whether the client
already holds a managed-install receipt for the item.

**Hash.** If `md5checksum` is set, the file is hashed and compared. The algorithm is chosen
from the length of the expected string: 32 characters means MD5, 40 means SHA-1, 64 means
SHA-256, and anything else is treated as MD5. A mismatch fails the entry with reason code
`hash_mismatch`. A match records that the hash is verified, which matters for the next
stage. The field is named `md5checksum` for compatibility; put a SHA-256 in it if you have
one.

**Version.** The expected version is the entry's `version`, or the item's `version` when the
entry does not set one. It is compared against the file's version resource.

| Situation | Result |
|---|---|
| catalog newer, hash was verified | passes — the hash is authoritative |
| catalog newer, no hash configured | fails, reason code `version_outdated` |
| installed newer than catalog | passes, and the difference is logged |
| file has no readable version resource, no hash configured | fails, reason code `version_outdated`, with a reason saying the check can never be confirmed |
| file has no readable version resource, hash was verified | passes |

Two consequences worth internalising. First, **hash beats version inside an entry**: a
matching checksum accepts a file whose declared version looks stale, which is what lets a
payload carry an internal version string unrelated to the pkgsinfo version. Second, a file
with no version metadata and no checksum produces an entry that can never pass, so the item
reinstalls every single run. That combination is one of the most common causes of an install
loop.

Checking a file with an explicit checksum:

```yaml
name: ExampleApp
display_name: Example App
version: 4.2.1.0
catalogs:
  - Production
installer:
  location: /apps/ExampleApp-4.2.1.0.exe
  type: exe
  hash: 8f2c0f7d2a1e4b6c9d0e3f5a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708
  switches:
    - /VERYSILENT
    - /SUPPRESSMSGBOXES
    - /NORESTART
installs:
  - type: file
    path: C:\Program Files\Example App\ExampleApp.exe
    md5checksum: 4f2a9c81b3d5e7f60a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f6071
```

Checking a file by version only, letting the entry inherit the item's version:

```yaml
name: ExampleApp
display_name: Example App
version: 4.2.1.0
catalogs:
  - Production
installer:
  location: /apps/ExampleApp-4.2.1.0.exe
  type: exe
  switches:
    - /S
installs:
  - type: file
    path: C:\Program Files\Example App\ExampleApp.exe
```

## `type: directory`

A directory entry checks existence and nothing else. Missing fails with reason code
`directory_missing`; present passes. There is no version check and no content check, so a
directory entry can only ever prove that a first install happened.

```yaml
name: ExampleAssets
display_name: Example Assets
version: 2.0.0
catalogs:
  - Production
installer:
  location: /apps/ExampleAssets-2.0.0.msi
  type: msi
installs:
  - type: directory
    path: C:\ProgramData\Example App\Assets
```

Because a directory entry cannot detect an upgrade, pair it with something that can — an
`msi` entry, or a `file` entry on a versioned binary inside it.

## `type: msi`

An MSI entry resolves the product in the Windows Installer registration and compares its
version. Both the 64-bit and 32-bit registry views are searched at every step.

**Resolution order.**

1. **`product_code`.** Looked up directly in the uninstall hive, reading `DisplayVersion`.
2. **`upgrade_code`.** Resolved through the Windows Installer upgrade-code registration to
   the product codes registered against it, then matched back to an uninstall entry. This is
   what makes detection survive a product that changes its ProductCode on every release.
3. **Display-name fallback, only in two narrow cases.** If the entry declares **neither** a
   product code nor an upgrade code, the client searches Add/Remove Programs by the item's
   `display_name`, falling back to its `name`. Separately, if the **entry itself** carries
   `display_name`, a matching Add/Remove Programs entry counts as installed even when the
   declared codes missed — an opt-in for wrapper MSIs that drop their Windows Installer
   registration after updating themselves.

Display-name matching is exact first, then substring, and only in the direction where the
registered name contains the search term. The reverse is rejected, so a product named
`Example App Patch` does not adopt the version of `Example App`.

When declared codes are present, name matching is deliberately off. Codes are authoritative.

**Version reporting.** The version the entry compares against is normally the MSI
`DisplayVersion`, except that the client prefers its own managed-install receipt when the
receipt compares as greater or equal. This keeps long build-timestamp versions intact for
packages Cimian built, while still reporting the live registry version for products that
update themselves outside Cimian.

**Outcomes.**

| Situation | Result |
|---|---|
| product not found by any enabled method | fails, reason code `product_code_missing` |
| found, registered version older than the catalog version | fails, reason code `version_outdated` |
| found, registered version at or above the catalog version | passes, unless `key_path` says otherwise |

**`key_path`.** An optional absolute path to the primary executable the MSI installs. It is
checked **after** the registry check has already passed, and it can overturn it: the file
must exist, or the entry fails with `file_missing`; and its file version must be at or above
the catalog version, or the entry fails with `version_outdated` and detection method `file`.
This catches the case where another installer lays older binaries over a current MSI without
touching its Add/Remove Programs entry. `key_path` beats a passing registry check.

An MSI whose product code changes each release, pinned by upgrade code and hardened with
`key_path`:

```yaml
name: ExampleApp
display_name: Example App
version: 4.2.1.0
catalogs:
  - Production
installer:
  location: /apps/ExampleApp-4.2.1.0.msi
  type: msi
  hash: 8f2c0f7d2a1e4b6c9d0e3f5a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708
installs:
  - type: msi
    upgrade_code: "{6A1B2C3D-4E5F-6789-ABCD-EF0123456789}"
    version: 4.2.1.0
    key_path: C:\Program Files\Example App\ExampleApp.exe
uninstallable: true
```

A fixed-identity MSI pinned by both codes:

```yaml
name: ExampleTool
display_name: Example Tool
version: 1.4.0
catalogs:
  - Production
installer:
  location: /apps/ExampleTool-1.4.0.msi
  type: msi
installs:
  - type: msi
    product_code: "{11112222-3333-4444-5555-666677778888}"
    upgrade_code: "{99990000-AAAA-BBBB-CCCC-DDDDEEEEFFFF}"
    version: 1.4.0
uninstallable: true
```

A wrapper MSI that deregisters itself after an in-app update:

```yaml
name: ExampleBrowser
display_name: Example Browser
version: 130.0.1
catalogs:
  - Production
installer:
  location: /apps/ExampleBrowser-130.0.1.msi
  type: msi
installs:
  - type: msi
    upgrade_code: "{ABCDEF01-2345-6789-ABCD-EF0123456789}"
    display_name: Example Browser
    version: 130.0.1
uninstallable: true
```

Quote GUIDs in YAML. An unquoted `{...}` is a YAML flow mapping and will not parse as a
string.

## `type: msix` and `type: appx`

An MSIX entry **requires `identity_name`** — the `Name` attribute from the package's
identity in its app manifest. An entry of this type without `identity_name` is an error with
reason code `check_failed`, not a failed check.

Detection queries both places a modern package can live, in one pass: per-user installs
across all users, and the provisioned (device-wide) package store. All versions found in
either store are sorted and the highest is compared against the catalog version.

| Situation | Result |
|---|---|
| not found in either store | fails, reason code `not_installed` |
| found, highest version older than the catalog version | fails, reason code `version_outdated` |
| found, highest version at or above the catalog version | passes |

A missing MSIX reports `not_installed` rather than `product_code_missing`; that reason code
is MSI-specific.

`identity_name` is also what makes the package removable, since it is how the client
synthesises an uninstaller. An MSIX package with no `identity_name` anywhere in its
`installs` array cannot be uninstalled by Cimian. See
[Uninstalling Software](Uninstalling-Software).

```yaml
name: ExampleStoreApp
display_name: Example Store App
version: 3.1.0.0
catalogs:
  - Production
installer:
  location: /apps/ExampleStoreApp-3.1.0.0.msix
  type: msix
installs:
  - type: msix
    identity_name: Example.StoreApp
    version: 3.1.0.0
uninstallable: true
```

## Choosing what to check

The entry you write is a claim that this exact thing changes when the package is installed
or upgraded, and does not change otherwise. Most detection defects are a violation of one
half of that claim.

**Check the thing the payload actually replaces.** For an MSI, that is the Windows Installer
registration, ideally by upgrade code. For a file payload, that is a binary the installer
overwrites on every release and whose version resource the vendor increments.

**Do not check a launcher stub.** Many products install a small `.exe` at the path a person
would call "the app" which only starts the real program. Vendors rarely re-stamp these, so
the stub's file version stays at something like `1.0.0.0` for years while the product moves
through many releases. An `installs` entry pointing at one either passes forever — hiding
every upgrade — or fails forever, because the stub's version can never reach the catalog's.
Both failure modes look like a working check right up until the first upgrade.

**Do not check a shortcut.** A `.lnk` has no meaningful version resource, and it is
per-user, roams, gets deleted by the user, and is recreated by things other than the
installer. A shortcut in an `installs` array is a permanent loop waiting to happen: it fails
the moment a user tidies their desktop, and the reinstall does not necessarily put it back.

**Do not check an uninstaller.** The `unins###.exe` an installer leaves behind is versioned
by the installer framework, not by the product.

**Do not check a file the application writes at runtime.** Logs, caches, user configuration
and licence files change without any install and are absent on a fresh profile.

**Prefer one strong entry to several weak ones.** Because the array is a conjunction, each
additional entry is another chance to produce a false failure. One `msi` entry with an
upgrade code and a `key_path` is stronger and quieter than five file entries.

**If the version is uncheckable, use a checksum.** A binary with no version resource can
still be pinned exactly with `md5checksum`. A file entry with neither can never pass.

**Remember what a change costs.** Editing the `installs` array changes the item's catalog
fingerprint, which clears any standing loop suppression for the package fleet-wide on the
next run. That is usually what you want after fixing detection. See
[Install Loop Prevention](Install-Loop-Prevention).

## See also

- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Version Comparisons](Version-Comparisons)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Installer Types](Installer-Types)
- [Uninstalling Software](Uninstalling-Software)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Troubleshooting](Troubleshooting)
