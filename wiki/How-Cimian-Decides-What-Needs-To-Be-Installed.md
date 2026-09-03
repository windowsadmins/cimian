# How Cimian Decides What Needs To Be Installed

Every run, the client takes each item that a manifest asked for, finds its metadata in a
catalog, and answers one question: does this machine need action for this item right now.
This page describes that decision in the exact order it is evaluated, what each gate can
return, and how ties break. Read it before writing detection into a pkgsinfo — most
packages that misbehave in the field have a detection defect, not an installer defect.

## The shape of the decision

The client evaluates a fixed cascade of gates. **The first gate that reaches a definitive
answer wins, and nothing below it runs.** A gate that is not configured is skipped
silently; it does not count as a pass or a failure.

Each answer carries four things that show up in the logs, in `InstallInfo.yaml` and in the
session reports:

- a **status** — `installed`, `pending`, `error` or `unknown`
- **needs action** — whether the client will queue work for this item this run
- **is update** — whether the queued work is an upgrade over an existing install rather
  than a first install
- a **reason** and a machine-readable **reason code**, plus the **detection method** that
  produced it

An exception thrown anywhere in the cascade is not swallowed. The item ends as status
`error` with needs-action true and reason code `check_failed`, so a broken check causes an
install attempt rather than a silent skip.

## Evaluation order

| Order | Gate | Configured by | Definitive when |
|---|---|---|---|
| 0 | Self-update guard | item is the Cimian client itself | running version is at or above the catalog version |
| 1 | On-demand | `OnDemand: true` | always |
| 2 | Install-check script | `installcheck_script` | always (including on failure) |
| 3 | Version script | `version_script` | always |
| 4 | Installs array | `installs:` with at least one entry | always |
| 5 | Registry check | `check.registry.name` | always |
| 6 | File check | `check.file.path` | always |
| 7 | Status script | `check.script` | always |
| 8 | Managed-install receipt | written by the client after a successful install | a receipt exists |
| 9 | Legacy installer product code | `installer.type: msi` with `installer.product_code` or `installer.upgrade_code` | always |
| 10 | Fallback by installer type | nothing | always |

### 0. Self-update guard

When the item being evaluated is the Cimian client itself — recognised by the item name or
by a client payload in `installer.location` — the client compares the catalog version
against the version of the binary that is currently running. If the running binary is at or
above the catalog version, the item is `installed` with detection method `self_update` and
the cascade stops. Otherwise evaluation continues normally, so the client can upgrade
itself through the same machinery as any other package.

### 1. On-demand

An item with `OnDemand: true` is **always** `pending` with needs-action true and reason code
`on_demand`. No detection runs at all.

This gate deliberately precedes `installcheck_script`. An on-demand item's install-check is
never consulted by the status cascade, so you cannot use one to gate an on-demand item. An
on-demand item also never receives a managed-install receipt, so it is never considered
installed on a later run. The way to stop an on-demand item running is to remove it from
the manifest — typically because its own script flipped some external state that a
conditional item keys on.

### 2. Install-check script

`installcheck_script` is a predicate for *needing the install*, and it is **not** inverted:

| Exit code | Meaning | Result |
|---|---|---|
| `0` | install is needed | `pending`, needs action, reason code `installcheck_needed` |
| non-zero | install is not needed | `installed`, reason code `script_confirmed` |
| times out | detection failed | `error`, **needs action false**, reason code `script_error` |
| throws | detection failed | `error`, needs action true, reason code `check_failed` |

The timeout is two minutes and it is a deliberate dead end: a script that hangs never
causes an install. That is safe, but it also means a slow install-check silently freezes an
item at "error" forever. Keep install-check scripts fast and non-interactive.

When this gate says an install is needed, the client decides whether to call it an update
by looking for an existing managed-install receipt for the item.

### 3. Version script

`version_script` reports the currently installed version on standard output. The client
trims the output and treats it as the installed version string.

| Script outcome | Result |
|---|---|
| fails, or prints nothing | `pending`, needs action, reason code `installcheck_needed` |
| prints a version older than the catalog version | `pending`, needs action, is-update, reason code `update_available` |
| prints a version at or above the catalog version | `installed`, reason code `version_match` |

Comparison uses the ordering described in [Version Comparisons](Version-Comparisons),
including the rule that an unparseable version compares equal — a version script that
prints something that is not a version makes the item permanently current.

The version script has no timeout.

### 4. Installs array

If the pkgsinfo carries any `installs` entries, every entry is checked. **Any single
failing entry short-circuits the whole item to needs-action.** If all entries pass, the item
is `installed` with detection method `installs_array` and a reason of the form
`All N install checks passed`.

Each entry type has its own rules, and an entry that declares neither a type nor any
identity field is an **error**, not a pass. See [Installs Arrays](Installs-Arrays) for the
full per-type reference.

### 5. Registry check

`check.registry.name` scans the Add/Remove Programs uninstall hive in both the 64-bit and
32-bit registry views for a subkey whose `DisplayName` **contains** the given name,
case-insensitively. Set `check.registry.path` to scan a different key.

| Outcome | Result |
|---|---|
| no matching display name | `pending`, needs action, reason code `registry_missing`, not an update |
| match, `check.registry.version` not set | `installed`, reason code `registry_match` |
| match, `check.registry.version` set, `DisplayVersion` older than the catalog version | `pending`, needs action, is-update, reason code `update_available` |
| match, `check.registry.version` set, `DisplayVersion` at or above the catalog version | `installed`, reason code `registry_match` |

The version comparison only happens when `check.registry.version` is set. A registry check
with just a name is a presence check and will never notice an outdated install.

Substring matching is a footgun in both directions: a name of `Example` matches
`Example App Patch`, and a name of `Example App 2026` matches nothing once the vendor
renames the product.

### 6. File check

`check.file.path` is a presence check, optionally strengthened.

| Outcome | Result |
|---|---|
| path missing | `pending`, needs action, reason code `file_missing`, not an update |
| exists, no further fields | `installed`, reason code `file_match` |
| exists, `check.file.version` set and the file's version resource is older | `pending`, needs action, reason code `version_outdated` |
| exists, `check.file.hash` set and the SHA-256 does not match | `pending`, needs action, reason code `hash_mismatch` |
| exists and all configured sub-checks pass | `installed`, reason code `file_match` |

`check.file.hash` is SHA-256 only. This is different from the `installs` array, where the
hash algorithm is inferred from the length of the expected value.

### 7. Status script

`check.script` is a predicate for *being installed*, and it is inverted relative to
`installcheck_script`:

| Exit code | Meaning | Result |
|---|---|---|
| `0` | already installed | `installed`, reason code `script_confirmed` |
| non-zero | not installed | `pending`, needs action, reason code `not_installed` |

Both script conventions live in the same cascade, with opposite polarity. Naming the wrong
key is the single most common way to invert a package's behaviour. If in doubt, use
`installcheck_script` and remember: exit 0 means *do the install*.

### 8. Managed-install receipt

If nothing above was configured, the client looks up its own receipt for the item under
`HKLM\SOFTWARE\ManagedInstalls\<Name>`, reading the `version` value it wrote after the last
successful install.

| Outcome | Result |
|---|---|
| receipt version older than the catalog version | `pending`, needs action, is-update, reason code `update_available`, detection method `managed_installs` |
| receipt version at or above the catalog version | `installed`, reason code `version_match` |
| no receipt | fall through |

This is the correct and sufficient mechanism for payload types that leave nothing
identifiable on disk. An item with no checks at all installs on its first run, gets a
receipt, and tracks catalog versions from then on.

The receipt is the client's own record. It says the client ran an installer successfully.
It does not say the software is still present.

### 9. Legacy installer product code

For pkgsinfo written before MSI identity moved into the `installs` array: when
`installer.type` is `msi` and the `installer` block carries `product_code` or
`upgrade_code`, the client resolves it against the uninstall hive.

| Outcome | Result |
|---|---|
| not registered | `pending`, needs action, reason code `product_code_missing` |
| registered but older | `pending`, needs action, reason code `version_outdated` |
| registered and current | `installed`, reason code `product_code_match` |

Prefer an `installs` entry of type `msi` for new packages. It supports `key_path` and
per-entry display-name fallback, which this path does not.

### 10. Fallback

Nothing above produced an answer and there is no receipt. The outcome now depends on the
installer type, and the split is deliberate:

| `installer.type` | Result |
|---|---|
| empty, `nopkg`, `script` | **`installed`**, reason code `no_checks` — a script-only item with no detection is assumed done |
| `msi`, `exe`, `pkg`, `nupkg`, `copy`, anything else | **`pending`**, needs action, reason code `not_installed` |

A script-only item with no checks that you expect to run every session will therefore run
once and never again. Give it `installcheck_script`, `recurring: true`, or `OnDemand: true`
depending on what you actually want — see [On-Demand Items](On-Demand-Items).

## Decision table

| You configured | Gate that answers | `pending` when | `installed` when |
|---|---|---|---|
| `OnDemand: true` | 1 | always | never |
| `installcheck_script` | 2 | exit 0 | exit non-zero |
| `version_script` | 3 | reported version older, or no output | reported version current |
| `installs` array | 4 | any entry fails | every entry passes |
| `check.registry.name` | 5 | no display-name match, or version older | match, and version current or unchecked |
| `check.file.path` | 6 | missing, hash mismatch, or version older | present and sub-checks pass |
| `check.script` | 7 | exit non-zero | exit 0 |
| nothing, MSI/EXE/pkg payload | 8 then 10 | receipt older, or no receipt | receipt current |
| nothing, script payload | 8 then 10 | receipt older | receipt current, or no receipt at all |

## How ties break

- Within an `installs` array, **hash beats version**. A file entry whose checksum matches
  is accepted even when its version resource looks older.
- Within an MSI `installs` entry, **declared codes beat name matching**. Display-name
  fallback only runs when the entry declares neither a product code nor an upgrade code, or
  when the entry opts in with its own `display_name`.
- Within an MSI `installs` entry, **`key_path` beats a passing registry check**. Even after
  the product resolves in Add/Remove Programs at a current version, a declared `key_path`
  file must exist and be at or above the catalog version.
- Across catalogs, **highest version wins, not catalog order**. If two catalogs both carry
  the item, the client uses the higher version. See [Using Catalogs](Using-Catalogs).
- Between versions, `compare(catalog, installed) > 0` means update; anything else means no
  update. An unparseable version on either side compares equal, so it means no update.

## Worked example 1: an MSI that self-updates

`Example App` ships as an MSI and also updates itself in the background, so its product
code changes without Cimian's involvement. The upgrade code is stable, and the packager
pins the main binary as well.

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

Machine state on `WORKSTATION-01`: the product is registered in Add/Remove Programs with
`DisplayVersion` `4.3.0.0`, and `ExampleApp.exe` reports file version `4.3.0.0`.

The cascade reaches gate 4. The single entry is type `msi`. The upgrade code resolves to a
registered product; the reported version `4.3.0.0` is newer than the catalog's `4.2.1.0`,
so no update is required. `key_path` then runs anyway: the file exists and its version is at
or above the catalog version, so it passes too. Every entry passed.

**Outcome: `installed`, detection method `installs_array`, no action.** The self-updated
product is correctly left alone, and the catalog is not forced to chase it.

Now change one thing: an unrelated installer overwrites `ExampleApp.exe` with a 4.1 build
while leaving the Add/Remove Programs entry untouched. The registry half still passes, but
`key_path` now finds file version `4.1.x` below the catalog's `4.2.1.0`. The entry fails,
the item short-circuits, and the outcome is `pending` with reason code `version_outdated`
and detection method `file`. That is exactly what `key_path` is for.

## Worked example 2: a configuration script that should run every session

A packager writes a `nopkg` item to enforce a setting and expects it to run hourly.

```yaml
name: ExampleSettings
display_name: Example Settings
version: 1.0.0
catalogs:
  - Production
installer:
  type: nopkg
install_script: |
  Set-ItemProperty -Path 'HKLM:\SOFTWARE\Example\App' -Name 'Mode' -Value 'Managed'
  exit 0
unattended_install: true
```

Machine state: the item installed successfully last week.

Gates 1 through 7 are not configured. Gate 8 finds the receipt at version `1.0.0`, equal to
the catalog. **Outcome: `installed`, no action.** The script has not run since.

Had there been no receipt at all, gate 10 would have returned `installed` as well, because
the installer type is `nopkg`. Either way the script does not run.

Two correct fixes, depending on intent. To enforce the setting every session, make the
check reflect the real state:

```yaml
name: ExampleSettings
display_name: Example Settings
version: 1.0.0
catalogs:
  - Production
installer:
  type: nopkg
installcheck_script: |
  $v = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Example\App' -Name 'Mode' -ErrorAction SilentlyContinue).Mode
  if ($v -eq 'Managed') { exit 1 }
  exit 0
install_script: |
  Set-ItemProperty -Path 'HKLM:\SOFTWARE\Example\App' -Name 'Mode' -Value 'Managed'
  exit 0
unattended_install: true
recurring: true
```

That runs only when the setting has drifted. To run unconditionally every session — an
idempotent maintenance action with nothing to detect — use `recurring: true` with an
install-check that always exits 0, and accept that the item will show as pending every run.
Do not reach for `OnDemand: true` for maintenance; on-demand also suppresses the receipt and
is meant for transient provisioning actions that eventually leave the manifest.

## Choosing a detection mechanism

Work down this list and stop at the first one that fits.

1. **`installs` array.** First choice for anything with a real payload. It is declarative,
   it is what the packaging tools emit, it reports the specific entry that failed, and it
   participates in the loop fingerprint so a fix propagates. Use `type: msi` with an
   `upgrade_code` for MSI products, `type: file` with a checksum for file payloads.
2. **Nothing at all.** For an installer type with a payload, the managed-install receipt in
   gate 8 handles first install and version tracking correctly on its own. This is the right
   answer for payloads that leave nothing distinctive on disk. It costs you the ability to
   notice that a user uninstalled the software.
3. **`check.registry` or `check.file`.** Single-condition checks for a product you can
   identify by one display name or one path. Simpler than an `installs` array, weaker: no
   per-entry reporting, no `key_path`, no MSI upgrade-code resolution.
4. **`version_script`.** When the installed version is knowable but lives somewhere no
   declarative check can read — a config file, a vendor API, a command's output. Print the
   version and nothing else.
5. **`installcheck_script`.** Last, not first. It is the most powerful and the least
   inspectable: it hides everything below it in the cascade, it is the easiest thing to get
   inverted, and it is where install loops come from. Reach for it when the condition is
   genuinely procedural.

## When an item reports Installed forever

An item that never installs, or that installs once and then reports `Installed` while the
software is plainly absent, almost always has a **detection** problem rather than an
install problem. Check these in order:

- **A check that cannot fail.** A `check.registry.name` with no `version`, or a
  `check.file.path` with no `version` or `hash`, passes forever after the first install and
  will never notice an upgrade or a manual removal.
- **An unparseable version on either side.** Versions that do not normalise to numbers
  compare equal, and equal means no update. A leading `v`, or a first segment that is not a
  number, makes the whole string unparseable. See
  [Version Comparisons](Version-Comparisons).
- **An inverted script.** `check.script` exit 0 means installed; `installcheck_script`
  exit 0 means install needed. Swapping them produces an item that is permanently current.
- **A checked path that is not the payload.** A launcher stub, a shortcut, or a bootstrapper
  that keeps its own version forever will pass every version check while the real
  application drifts. See [Installs Arrays](Installs-Arrays).
- **A timed-out install-check.** A hung install-check returns `error` with needs-action
  false, so the item is never queued. Look for reason code `script_error`.
- **A stale receipt with no other check.** Gate 8 believes its own record. If the software
  was removed outside Cimian, the receipt still says the current version is installed.
- **A truncated session.** If a run is interrupted after the installer succeeds but before
  post-install work completes, the receipt is written while the machine is not actually in
  the intended state.

The reverse failure — an item that installs successfully every single run — is the same
class of defect seen from the other side, and is handled by
[Install Loop Prevention](Install-Loop-Prevention).

## See also

- [Installs Arrays](Installs-Arrays)
- [Version Comparisons](Version-Comparisons)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Item Status Reference](Item-Status-Reference)
- [On-Demand Items](On-Demand-Items)
- [Troubleshooting](Troubleshooting)
