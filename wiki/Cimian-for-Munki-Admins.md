# Cimian for Munki Admins

Cimian is modelled on [Munki](https://github.com/munki/munki), so most of what you know
transfers. This page maps Munki concepts, tools, paths and pkginfo keys onto their Cimian
equivalents, then sets out — in detail — where Cimian deliberately behaves differently and
where it simply does not do what Munki does. Read the divergences before you port a repo:
several of them are silent, and a Munki habit applied to Cimian can produce a package that
looks correct and never works.

## Rosetta stone

| Munki | Cimian |
|---|---|
| `munkiimport` | [cimiimport](cimiimport) |
| `makepkginfo` | [makepkginfo](makepkginfo) |
| `makecatalogs` | [makecatalogs](makecatalogs) |
| `manifestutil` | [manifestutil](manifestutil) |
| `managedsoftwareupdate` | [managedsoftwareupdate](managedsoftwareupdate) |
| `repoclean` | [repoclean](repoclean) |
| `munki-pkg` | [cimipkg](cimipkg) |
| `iconimporter` | no equivalent — see [Product Icons And Screenshots](Product-Icons-And-Screenshots) |
| Managed Software Center | [Managed Software Center](Managed-Software-Center) |
| launchd agents and daemons | the `CimianWatcher` service plus two scheduled tasks — see [How Cimian Runs](How-Cimian-Runs) |
| `/Library/Managed Installs/` | `C:\ProgramData\ManagedInstalls\` |
| `/Library/Preferences/ManagedInstalls.plist` | `C:\ProgramData\ManagedInstalls\Config.yaml` |
| `SoftwareRepoURL` | `SoftwareRepoURL` |
| `ClientIdentifier` | `ClientIdentifier` |
| `/Library/Managed Installs/manifests/`, `catalogs/`, `Cache/` | the same names under `C:\ProgramData\ManagedInstalls\` |
| `ManagedInstallReport.plist` | `InstallInfo.yaml` plus `reports\*.json` — see [Reporting Data Contract](Reporting-Data-Contract) |
| `/Library/Managed Installs/Logs/ManagedSoftwareUpdate.log` | `logs\YYYY-MM-DD\HHmm\install.log` |
| conditions scripts in `/usr/local/munki/conditions/` | `C:\ProgramData\ManagedInstalls\conditions\` |
| `SelfServeManifest` | `C:\ProgramData\ManagedInstalls\SelfServeManifest.yaml` |
| `.com.googlecode.munki.checkandinstallatstartup` | `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` |
| `.pkg`, `.dmg`, `.app` | `.msi`, `.exe`, `.nupkg`, `.msix`/`.appx`, `.ps1` |
| bash scripts in pkginfo | PowerShell scripts in pkgsinfo |

## The repository

The tree is the one you know: `pkgsinfo/`, `pkgs/`, `catalogs/`, `manifests/`, `icons/`.
Metadata files are YAML rather than plists, and only the `.yaml` extension is scanned — a
file named `ExampleApp.yml` is invisible to `makecatalogs` and its package silently does not
exist.

Serving is the same idea and simpler: plain HTTP or HTTPS GETs at fixed paths, no server
component. Cimian supports HTTP Basic, a bearer token, and mutual TLS. It does **not**
support proxies, extra or custom request headers, Windows Integrated authentication, or any
storage-provider signed-URL scheme, and it rejects every URL scheme except `http` and
`https` — there is no `file://` or UNC repo. See
[The Cimian Repository](The-Cimian-Repository) and
[Securing The Repository](Securing-The-Repository).

There is no repository plugin system. Munki's pluggable repo backends have no counterpart;
the repo is a directory that something serves over HTTP.

## Manifests

`catalogs`, `included_manifests`, `managed_installs`, `managed_updates`,
`managed_uninstalls`, `optional_installs`, `default_installs`, `featured_items` and
`conditional_items` all exist and mean what they mean in Munki. Includes nest to any depth,
cycles terminate, and a missing include is logged as a warning.

Two differences matter.

**Precedence between duplicate entries is by action rank, not by position.** Every
occurrence of a name across the whole tree collapses to one action, and the ranking is
`install > uninstall > update > default > optional > profile = app`, independent of the
order the manifests were read. You cannot demote an item in a child manifest.

**`default_installs` is install-once.** After the first successful install the item is not
re-enforced and drops off every list, so a user may remove it and it will not return.

Full reference: [Manifests](Manifests).

## pkgsinfo keys that transfer

These behave as you expect: `name`, `display_name`, `version`, `description`, `category`,
`developer`, `icon_name`, `catalogs`, `requires`, `update_for`, `supported_architectures`,
`minimum_os_version`, `maximum_os_version`, `blocking_applications`, `unattended_install`,
`unattended_uninstall`, `force_install_after_date`, `precache`, `installs`,
`preinstall_script`, `postinstall_script`, `preuninstall_script`, `postuninstall_script`,
`installcheck_script`, `restart_action`, and `OnDemand`.

`installcheck_script` keeps Munki's polarity: **exit 0 means the install is needed**, a
non-zero exit means it is not. Note that this is the inverse of an Intune Win32 detection
script, so a script reused from there needs its polarity flipped.

`OnDemand` is spelled in that exact PascalCase. It is the one key in the schema that is not
snake_case, and the spelling is load-bearing — `on_demand:` is not recognised.

Cimian adds keys Munki has no equivalent for: `minimum_cimian_version` (gate on the agent's
own version), `install_window` (a per-item time-of-day and weekday window),
`installer_timeout` (per-item, in **seconds**), `recurring` (re-run every session without
`OnDemand`'s never-installed semantics), `version_script` (stdout is taken as the installed
version), and `unused_software_removal_info`. See
[Supported pkgsinfo Keys](Supported-pkgsinfo-Keys).

## Where the payload and the hash live

Cimian has no `installer_item_location`, `installer_item_hash` or `installer_item_size`.
The installer is described by an `installer:` block:

```yaml
installer:
  location: apps/ExampleApp-1.2.0.msi
  type: msi
  hash: 0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0
  size: 41943040
```

`location` is relative to `pkgs/`, or an absolute `http(s)://` URL, in which case `pkgs/` is
bypassed. `hash` is **SHA-256** and is verified after every download and again before a
cached copy is reused.

`uninstaller` is a **list**, not a scalar, each entry carrying `type`, `command`,
`product_code`, `identity_name` and argument fields.

## Detection: `installs`, not receipts

There are no `receipts`. Detection is the `installs` array plus, for MSI packages, the
Windows Installer registration.

Entry types are exactly `file`, `directory`, `msi`, `msix` and `appx`. Munki's `plist`,
`application` and `bundle` types do not exist. An `msi` entry identifies the product by
`product_code` or `upgrade_code`; `msix`/`appx` by `identity_name`. An entry with no `type`
is inferred from whichever identity field it carries, and an entry with neither a type nor
any identity field is a hard error that marks the item as needing action — it does not
silently pass.

The decision cascade is longer than Munki's and the order is worth learning:
`OnDemand` → `installcheck_script` → `version_script` → `installs[]` → a `check:` block →
the `HKLM\SOFTWARE\ManagedInstalls\<Name>` receipt written by Cimian itself. See
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
and [Installs Arrays](Installs-Arrays).

## Removal

There is no `uninstall_method` and no `uninstaller_item_location`. Whether an item can be
removed is derived: an item is uninstallable unless `uninstallable: false`, and only when it
offers at least one removal mechanism — a non-empty `uninstaller` list, an `uninstall_script`,
an MSI or EXE installer type, an `installs` MSI entry with a `product_code`, an `installs`
MSIX entry with an `identity_name`, or a `check.registry.name`. Any MSI is removable whether
or not a ProductCode is declared, because removal can fall back to the registry
`UninstallString`. See [Uninstalling Software](Uninstalling-Software).

## Conditions

Conditional items exist and are evaluated once per run against a fact set, but **the
expression language is not NSPredicate**. It is a small purpose-built grammar:

```yaml
conditional_items:
  - condition: machine_type == "laptop" AND os_vers_major >= 11
    managed_installs:
      - ExampleBatteryTool
```

Operators are `==`, `!=`, `CONTAINS`, `DOES_NOT_CONTAIN`, `BEGINSWITH`, `ENDSWITH`, `LIKE`,
`IN`, the numeric comparisons, and the connectives `AND`, `OR`, `NOT` and `ANY`. Several
NSPredicate habits break here:

- `LIKE` is not a glob. `*` characters are deleted from the pattern and the result is a
  plain case-insensitive substring match, so `'Design*'`, `'*Design'` and `'*Design*'` are
  all identical.
- `IN` with a bracketed list does not work. `domain IN ["A", "B"]` parses as
  `domain IN "A"` and the remaining elements are discarded silently. The working form is one
  comma-separated quoted string: `domain IN "A,B"`.
- Any value containing a space, `.`, `-`, `\` or `*` must be quoted, or the tokenizer splits
  it and only the first fragment is compared.
- A malformed condition never fails the run. It evaluates to `false` and the block simply
  never matches.
- An unknown fact name resolves to null, which compares equal to `""`. There is no
  undefined-fact error.

Fact names are Cimian's own — `hostname`, `arch`, `os_version`, `os_vers_major`, `domain`,
`machine_type`, `machine_model`, `joined_type`, `catalogs`, and a set of hardware facts.
There is no `serial_number` fact and no `free_disk_space` fact. Admin-supplied conditions
work the way Munki's do: executables in `C:\ProgramData\ManagedInstalls\conditions\` whose
stdout lines are parsed as `key=value`, except that they are PowerShell, batch or EXE, each
gets 30 seconds, and a non-zero exit discards all of that script's output.

See [Conditional Items](Conditional-Items) and
[Conditional Facts Reference](Conditional-Facts-Reference).

## Where Cimian deliberately differs

**Catalog precedence is by version, not by catalog order.** In Munki, the order of
`catalogs:` in a manifest is the precedence order and the first catalog containing an item
wins. Cimian merges every catalog into one name-keyed map and keeps the **highest version**
regardless of which catalog it came from. Listing `Testing` before `Production` buys you
nothing; a higher version in `Testing` reaches every device whose manifest names both. This
is the single most consequential difference for anyone porting a promotion workflow. See
[Using Catalogs](Using-Catalogs) and [Promoting Between Catalogs](Promoting-Between-Catalogs).

**Configuration is YAML with PascalCase keys**, not a plist:

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
ClientIdentifier: WORKSTATION-01
```

Unknown keys are ignored silently, so a snake_case key is accepted and does nothing. MDM
policy can override exactly four values — `SoftwareRepoURL`, `ClientIdentifier`,
`InstallerTimeout` and `CacheRetentionDays` — under `HKLM\SOFTWARE\Policies\Cimian`. See
[Client Configuration](Client-Configuration).

**Logs are per-session directories, not one growing text file.** Each run writes
`logs\YYYY-MM-DD\HHmm\` containing `install.log`, `events.jsonl` and `session.json`, plus
aggregate JSON under `reports\`. See [Logging](Logging).

**The manifest fallback chain advances only on 404.** Cimian tries the client certificate
CN, then `ClientIdentifier`, then the machine name, then the BIOS serial, then `Orphaned`,
then `site_default`. Any non-404 failure — 401, 403, 500, a TLS error — aborts resolution
instead of falling through, deliberately, so that a server error cannot silently move a
device onto a catch-all manifest. A server that answers missing files with a login page or a
`403` breaks the chain. See [Client Identifier Resolution](Client-Identifier-Resolution).

**LoopGuard has no Munki counterpart.** Cimian detects packages that install successfully
and immediately report themselves as needing installation again, and suppresses them for a
window that escalates on repetition. It also probes for convergence straight after a first
install rather than waiting for a pattern to build. This changes what a broken package looks
like: instead of reinstalling forever, it stops and appears in
`reports\loop_suppressed.json`. See [Install Loop Prevention](Install-Loop-Prevention).

**A run is triggered by a flag file.** Writing `.cimian.bootstrap` or `.cimian.headless`
into `C:\ProgramData\ManagedInstalls\` causes the `CimianWatcher` service to start a run
within about ten seconds. Any MDM that can write a file can trigger a run. See
[How Cimian Runs](How-Cimian-Runs) and [cimitrigger](cimitrigger).

**Packaging is a first-class part of the toolchain.** [cimipkg](cimipkg) builds an MSI
directly from a project directory, with no WiX project and no external toolchain, and can
also emit a Chocolatey `.nupkg` or an `.intunewin`.

**Apple Software Update integration has no analogue**, and neither does anything that
depends on it. Windows Update is not managed by Cimian.

## Where Cimian is behind

These are real gaps, not stylistic differences. Each one is something a Munki admin will
reasonably expect and not get.

**`uninstallcheck_script` is accepted and never read.** `makepkginfo` has a flag for it,
`cimiimport` writes it, and `makecatalogs` carries it into the catalog — but the client has
no property for it and nothing in the update engine reads it. A removal that should be
skipped because the software is already gone is not skipped by this key; removal is governed
only by `uninstallable` and the manifest's `managed_uninstalls`. Do not port these scripts
expecting them to run.

**`installable_condition` does not exist.** There is no per-package condition key at all.
Conditions live only in a manifest's `conditional_items`.

**`supersedes` is not implemented in the client.** There is no supersession resolution
anywhere in the update engine. (`supersedes` in a `cimipkg` `build-info.yaml` is an
unrelated MSI packaging key.)

**Nested `conditional_items` are silently dropped.** The deployed manifest model has exactly
five keys per conditional block — `condition` plus the four item lists — and a nested
`conditional_items:` inside one is discarded when the file is parsed. Only the outer level
applies. Flatten nested logic into combined `AND` expressions.

**`managed_profiles` and `managed_apps` do nothing.** Names listed there are recorded and
reported as externally managed, and no action of any kind is taken on them. See
[Managed Profiles And Managed Apps](Managed-Profiles-And-Managed-Apps).

**`makecatalogs` validates almost nothing.** A pkgsinfo with no `name` or no `version`
parses and is published. Duplicate name-and-version pairs within one catalog are not
detected. `installs` entries, architecture strings, `restart_action` spellings and version
strings are not checked. Deserialization is the only schema gate, and because unknown keys
are ignored rather than rejected, a misspelled key is not an error — it is simply dropped.
The corollary is that a key is only real if it survives the authoring tool, `makecatalogs`
and the client. `installer.arguments`, `identifier` and the top-level `installer_type:`
scalar all fail that test today.

**There is no icon importer.** Icons are copied into `icons/` by hand, or extracted by
`cimiimport --extract-icon`, which is experimental, off by default, and supports only
`.exe`, `.msi`, `.msix` and `.appx`.

**`makecatalogs --hash_check` computes MD5** while the client verifies `installer.hash` as
SHA-256. Since `cimiimport` writes a SHA-256, `--hash_check` reports a mismatch for
essentially every payload in a normal repo. It is not a usable integrity check.

**Postflight is weaker than Munki's.** It does not run at all in a `--checkonly` session or
after an unhandled exception, a non-zero exit is logged as a warning and changes nothing,
and there is no timeout on either preflight or postflight — a hanging script hangs the run
indefinitely. See [Preflight And Postflight Scripts](Preflight-And-Postflight-Scripts).

**`managedsoftwareupdate --quiet` is parsed and never read.** It suppresses nothing.

**`manifestutil` has a narrow model.** It knows only `name`, `catalogs`,
`included_manifests` and the four package sections. Round-tripping a manifest through it
drops `conditional_items`, `featured_items`, `default_installs`, `managed_profiles` and
`managed_apps`.

**Managed Software Center is not fully wired.** `cimian://` deep links are implemented in
the application but the protocol is not registered by any installer, so they do not work in
a normal install. Toast notifications are shown, but the handlers for their action buttons
are empty — clicking one does nothing. A custom `sidebar_items` list cannot include the
History page.

**There is no proxy support.** The client sets no proxy of its own, and no proxy
configuration key is read.

## Porting a repo: the short version

1. Convert pkginfo plists to YAML, dropping `receipts`, `uninstall_method`,
   `installer_item_location`, `installer_item_hash`, `installer_item_size` and
   `installable_condition`, and moving the payload details into the `installer:` block.
2. Replace macOS installer payloads with Windows ones and set `installer.type`.
3. Rewrite `installs` entries to `file`, `directory`, `msi`, `msix` or `appx`.
4. Translate scripts from bash to PowerShell. Exit-code contracts are unchanged.
5. Rewrite conditions from NSPredicate into Cimian's grammar, and re-check every use of
   `LIKE` and `IN`.
6. Rebuild catalogs and confirm each key you rely on actually appears in the generated
   catalog before you trust it.

Work through [Demonstration Setup](Demonstration-Setup) once on a throwaway repo before
porting anything real, then use [Installing Software](Installing-Software) as the routine
workflow.

## See also

- [Demonstration Setup](Demonstration-Setup)
- [Installing Software](Installing-Software)
- [The Cimian Repository](The-Cimian-Repository)
- [Manifests](Manifests)
- [Using Catalogs](Using-Catalogs)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Conditional Items](Conditional-Items)
- [Client Configuration](Client-Configuration)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Glossary](Glossary)
