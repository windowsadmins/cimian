# Supported pkgsinfo Keys

This is the complete reference for every key you can put in a pkgsinfo file, grouped by what
it does. It also lists the keys that are accepted without complaint and then have no effect,
which is a longer list than you would expect. Read the second half before assuming a key
works because a tool wrote it for you.

Cimian's YAML reader ignores properties it does not recognise. That is deliberate — it lets
tools add fields without breaking older clients — but it has one consequence you must keep in
mind at all times: **a mistyped key is never an error.** `unattendend_install: true` parses,
publishes into the catalog, and does nothing. `makecatalogs` will not warn you. There is no
schema validation anywhere in the chain. If a key is not behaving, the first thing to check
is the spelling against this page.

Keys are ordinal-alphabetical in a file written by the tools, except that `name`,
`display_name` and `version` come first and `_metadata` comes last. Ordering has no effect
on behaviour.

## Identity

| Key | Type | Default | What it does |
|---|---|---|---|
| `name` | string | — | **Required.** The package's identity. Manifests reference it, catalogs are keyed on it, the install receipt is named after it. Matched case-insensitively everywhere. Changing it creates a new package rather than renaming the existing one. |
| `version` | string | — | **Required.** The other half of the identity, and the value every update decision compares against. Normalised before comparison; an unparseable version compares as equal to everything, so it never triggers an update. See [Version Comparisons](Version-Comparisons). |
| `catalogs` | list of string | `[]` | Which catalogs `makecatalogs` publishes this item into. Consumed only at catalog-generation time; the client never sees it. An item with no `catalogs` still lands in the always-generated `All` catalog. |

`makecatalogs` does not enforce `name` or `version`. A pkgsinfo missing either one parses and
publishes; the failure surfaces later as an item that no manifest can match, or an item that
never updates.

## Display and grouping

None of these affect installation. They exist for Managed Software Center and for your own
navigation.

| Key | Type | Default | What it does |
|---|---|---|---|
| `display_name` | string | unset | Human-readable name shown in Managed Software Center. Falls back to `name`. |
| `description` | string | unset | Description shown in Managed Software Center. Line endings are normalised and runs of three or more blank lines collapsed. An empty string is dropped entirely when a tool rewrites the file — omit the key instead. |
| `category` | string | unset | Grouping label in the GUI. Also used as a subdirectory name in the client's download cache. |
| `developer` | string | unset | Publisher label in the GUI. |
| `icon_name` | string | unset | Icon filename inside `<repo>/icons/`. When unset the client looks for `<name>.png`. See [Product Icons And Screenshots](Product-Icons-And-Screenshots). |

## Targeting and eligibility

These gate whether an item is even considered on a given device.

| Key | Type | Default | What it does |
|---|---|---|---|
| `supported_architectures` | list of string | `[]` (all) | Architecture filter, applied when the catalog is loaded — before any manifest matching, so a filtered-out item behaves as if it does not exist. `amd64` and `x86_64` normalise to `x64`. |
| `minimum_os_version` | string | unset | Minimum Windows version, e.g. `10.0.19045`. Gates the `install`, `update` and `default` actions. |
| `maximum_os_version` | string | unset | Maximum Windows version. Same gating. |
| `minimum_cimian_version` | string | unset | Minimum version of the Cimian agent itself. Same gating. |
| `requires` | list of string | `[]` | Names of packages that must be installed first. Resolved into the run and installed ahead of this item. A failed dependency aborts this item. See [Dependencies And Update Chains](Dependencies-And-Update-Chains). |
| `update_for` | list of string | `[]` | Declares this item to be an update to the named packages. When one of those packages is processed, this item is pulled in and installed after it. A failure here warns but does not fail the parent. |

The OS-version and agent-version gates deliberately do **not** apply to `uninstall`. An item
that has become unsupported can still be removed.

## Installer

The `installer:` block describes the payload and how to run it.

| Key | Type | Default | What it does |
|---|---|---|---|
| `installer.location` | string | `""` | Payload path relative to `<repo>/pkgs/`. An absolute `http://` or `https://` URL is also accepted and used as-is. |
| `installer.type` | string | inferred | Which installation mechanism to use. Recognised: `msi`, `exe`, `msix`, `appx`, `powershell`, `ps1`, `nupkg`, `chocolatey`, `pkg`, `nopkg`, `script`. **Anything unrecognised falls through to the EXE installer.** When blank, the type is inferred from the payload's file extension; with no payload at all it becomes `script`. See [Installer Types](Installer-Types). |
| `installer.hash` | string | unset | Expected digest of the payload. The client computes **SHA-256** and refuses a download that does not match, retrying like any other download failure. |
| `installer.size` | integer | unset | Payload size in bytes. Used for the pre-download size sanity check and the disk-space estimate. |
| `installer.args` | list of string | `[]` | Arguments passed to the installer verbatim. This is the key you want. |
| `installer.switches` | list of string | `[]` | Windows-style arguments; a leading `/` is added if you omit it. |
| `installer.flags` | list of string | `[]` | Unix-style arguments; `-` is prefixed for a single character, `--` otherwise, if you omit it. |
| `installer.subcommand` | string | unset | Emitted before everything else on the composed command line. |
| `installer.success_codes` | list of integer | unset | Additional process exit codes to treat as success. `0` and `3010` are always successful; MSI removal additionally accepts `1605` and `1614`. |
| `installer.product_code` | string | unset | MSI ProductCode. A legacy shape — put the identity in `installs[]` instead. Only consulted when `installer.type` is `msi` and no `installs[]` entry resolved. |
| `installer.upgrade_code` | string | unset | MSI UpgradeCode, same legacy status. |
| `installer.temp_dir` | string | unset | A short extraction directory, to avoid hitting the Windows path-length limit when a bundle unpacks deeply. |

The composed command line is `subcommand`, then normalised `switches`, then normalised
`flags`, then `args`. In practice, use `args` alone and write the arguments exactly as the
installer expects them.

There is a real inconsistency around `installer.hash` worth knowing: the authoring tools
write SHA-256 and the client verifies SHA-256, but `makecatalogs --hash_check` computes
**MD5** when it compares the field against the payload on disk. The result is that
`--hash_check` reports a hash mismatch warning for every item with a hash. The warning does
not fail the run and does not affect what clients do.

## Detection

Detection decides whether the package is already installed. The client evaluates the
mechanisms in a fixed order and the first one that reaches a definite answer wins; nothing
below it runs. Full detail is in
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

| Key | Type | Default | What it does |
|---|---|---|---|
| `installs` | list of entry | `[]` | The canonical detection mechanism. Each entry is one thing that must be true. **Any single failing entry marks the whole package as needing action.** See [Installs Arrays](Installs-Arrays). |
| `check.registry.name` | string | unset | Substring matched against `DisplayName` in the uninstall registry, in both the 64-bit and 32-bit views. |
| `check.registry.version` | string | unset | Only when set does a registry hit also compare the registered `DisplayVersion` against the catalog version. Without it, a registry hit means "installed" regardless of version. |
| `check.registry.path` | string | unset | An alternate registry path to scan instead of the standard uninstall keys. |
| `check.registry.value` | string | unset | Registry value name to read. |
| `check.file.path` | string | unset | A file whose existence means installed. |
| `check.file.version` | string | unset | When set, the file's file-version metadata is also compared. |
| `check.file.hash` | string | unset | When set, the file's **SHA-256** digest is verified. |
| `check.script` | string | unset | Inline PowerShell whose exit code decides installed state. **Exit 0 means installed.** |
| `installcheck_script` | string | unset | Inline PowerShell evaluated before everything else except `OnDemand`. **Exit 0 means an install is needed** — the opposite convention to `check.script`. A timeout (default two minutes) yields a detection error and explicitly does *not* install. |
| `version_script` | string | unset | Inline PowerShell whose trimmed stdout is taken as the installed version. Empty output or a failure means "install needed". Runs with no timeout. |

`installs` entries accept these fields:

| Field | Type | What it does |
|---|---|---|
| `type` | string | `file`, `directory`, `msi`, `msix` or `appx`. Case-insensitive. |
| `path` | string | Absolute path. Used by `file` and `directory`. |
| `md5checksum` | string | Expected digest of the file. Despite the name, the algorithm is picked by digest length: 32 characters MD5, 40 SHA-1, 64 SHA-256. A hash match is authoritative — it overrides a version mismatch. |
| `version` | string | Expected version. Falls back to the package's `version` when omitted. |
| `product_code` | string | MSI ProductCode. Per-version identity. |
| `upgrade_code` | string | MSI UpgradeCode. Stable across versions, so usually the better choice. |
| `display_name` | string | Opt-in fallback: an uninstall-registry `DisplayName` hit counts as installed. For wrapper MSIs that drop their Windows Installer registration after self-updating. |
| `identity_name` | string | The MSIX/APPX package identity name. Required for `msix` and `appx` entries. |
| `key_path` | string | Absolute path to the product's main executable. When set on an `msi` entry, that file must also exist and its file version must be at least the catalog version, even after the registry check passes. Catches an older installer overwriting binaries without touching its registry entry. |

If you omit `type`, it is inferred: `identity_name` set means `msix`; `product_code` or
`upgrade_code` set means `msi`; `path` set means `file`. An entry with neither a type nor any
identity field is a **hard error** — the item's status becomes `error` and it is flagged as
needing action. It does not silently pass.

There is no `plist`, `application` or `bundle` entry type. Those are Munki concepts.

## Scripts

Every script key holds inline PowerShell as a string, not a path to a file. Use a YAML block
scalar (`|`) so the body survives round-tripping. See
[Scripts In pkgsinfo](Scripts-In-pkgsinfo).

| Key | Type | Default | What it does |
|---|---|---|---|
| `preinstall_script` | string | unset | Runs before the installer. A non-zero exit **fails the install** and the installer never runs. |
| `postinstall_script` | string | unset | Runs after a successful install. A non-zero exit is logged as a warning and does **not** fail the install. Writing a line `CIMIAN-WARNING: <message>` to stdout reports a Warning outcome for the item without failing it, and suppresses the post-install convergence probe. |
| `install_script` | string | unset | The body executed for `installer.type: nopkg` and `script`. A `nopkg` item with no `install_script` warns and reports success. |
| `uninstall_script` | string | unset | Executed to remove the package when no `uninstaller[]` entry is declared. For a script-only package this is the entire removal story — it is also what makes such a package removable at all. |
| `preuninstall_script` | string | unset | Runs before removal. |
| `postuninstall_script` | string | unset | Runs after removal. |

Scripts run through `powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command`.
Pre-install, post-install, pre-uninstall and post-uninstall scripts have **no timeout** at
all. `installcheck_script` has a two-minute default; `version_script` and `check.script` have
none.

## Removal

| Key | Type | Default | What it does |
|---|---|---|---|
| `uninstallable` | bool | `true` | Master opt-out. Setting it to `false` makes the package unremovable regardless of every other key. |
| `uninstaller` | list of entry | `[]` | Explicit removal instructions. **This is a list, not a single mapping.** Only the first entry is dispatched on. |
| `uninstaller[].type` | string | — | `msi`, `exe`, `powershell`, `ps1`, `msix`, `appx`. Anything else is treated as `msi`. |
| `uninstaller[].product_code` | string | — | Required by the `msi` uninstaller. |
| `uninstaller[].command` | string | — | Required by the `exe` and `powershell` uninstallers. |
| `uninstaller[].identity_name` | string | — | Package identity for `msix`/`appx` removal. |
| `uninstaller[].location` | string | — | Path relative to `pkgs/`, when removal needs its own payload. `makecatalogs` warns if the file is missing. |
| `uninstaller[].args` / `.switches` / `.flags` / `.subcommand` | list / string | — | Arguments, composed exactly as on the installer side. |
| `unused_software_removal_info.removal_days` | integer | unset | Remove the package when no tracked executable has been used for this many days. Zero or negative disables it. |
| `unused_software_removal_info.paths` | list of string | unset | Absolute executable paths whose usage gates removal. When empty, falls back to `.exe` entries in `installs[]`. |
| `unused_software_removal_info.minimum_history_days` | integer | unset | Minimum days of usage history that must exist on the device before removal may act. Null defers to the client default. |

A package is removable if `uninstallable` is not `false` **and** at least one of: an
`uninstaller[]` entry exists; `check.registry.name` is set; `installer.type` is `exe` or
`msi`; an `installs[]` MSI entry carries a `product_code`; `uninstall_script` is non-blank;
or an `installs[]` MSIX/APPX entry carries a non-blank `identity_name`.

Any MSI is removable with or without a declared ProductCode — removal falls back to reading
the `UninstallString` out of the registry. An MSIX entry **without** `identity_name` is not
removable, because there is nothing to synthesise an uninstaller from.

`unused_software_removal_info` additionally requires `unattended_uninstall: true`. Without
it the feature does nothing. See [Uninstalling Software](Uninstalling-Software).

## Behaviour and scheduling

| Key | Type | Default | What it does |
|---|---|---|---|
| `blocking_applications` | list of string | `[]` | Process names (not paths, no extension needed). If any is running, the item is **deferred for the whole run**, not retried later in the session. Applies to installs, updates and removals, in every run mode, regardless of whether a user is signed in. See [Blocking Applications](Blocking-Applications). |
| `unattended_install` | bool | `false` | When `false`, the item is deferred during an `--auto` run while a user is active. Has no effect on an interactive or idle run. |
| `unattended_uninstall` | bool | `false` | The same, for removal. Also a hard prerequisite for `unused_software_removal_info`. |
| `restart_action` | string | unset | Recognised values, **case-sensitive**: `RequireRestart`, `RecommendRestart` (both trigger a reboot), `RequireLogout` (triggers a logout), and `RecommendLogout` (no action of its own). All four mark the item as user-interrupting, which defers it in an `--auto` run with an active user. A reboot in auto or bootstrap mode is a `shutdown /r` with a 300-second grace period. |
| `install_window.start` | string | unset | Start of the permitted install window, e.g. `22:00`. Inclusive. |
| `install_window.end` | string | unset | End of the window, e.g. `05:00`. Exclusive. A start later than the end means an overnight window and is supported. |
| `install_window.weekdays` | list of string | unset | `Mon` `Tue` `Wed` `Thu` `Fri` `Sat` `Sun`, case-insensitive. For the after-midnight half of an overnight window, *yesterday's* abbreviation is the one matched. |
| `force_install_after_date` | datetime | unset | Once this moment has passed, the item installs regardless of `install_window`, and an item that only appears in `optional_installs` is force-installed. See [Force Installs And Deadlines](Force-Installs-And-Deadlines). |
| `installer_timeout` | integer | unset | Per-item override of the fleet installer timeout, **in seconds**. Must be greater than zero to take effect. The fleet default is 900 seconds. On timeout the process tree is killed. Note that a stale comment in the source describes this field as minutes; the engine treats it as seconds. |
| `precache` | bool | `false` | Download the payload proactively, even for an optional item nobody has requested. |
| `OnDemand` | bool | `false` | **This key is PascalCase, deliberately.** The item is never considered installed, never gets an install receipt, and runs every session. It also bypasses install-loop suppression and skips the convergence probe. It takes precedence over `installcheck_script`, which is therefore never consulted for an OnDemand item. Writing `on_demand:` in snake_case does nothing at all. See [On Demand Items](On-Demand-Items). |
| `recurring` | bool | `false` | Exempts an idempotent maintenance item from install-loop suppression **without** OnDemand's never-installed, no-receipt semantics. The item still tracks normally. |

`install_window` fails open: an unparseable `start` or `end` means no restriction at all,
rather than a blocked item.

## Machine-written keys

| Key | Type | What it does |
|---|---|---|
| `loop_fingerprint` | string | Stamped by `makecatalogs` over the entire serialised item. **Never hand-author it.** The client clears a package's install-loop suppression whenever it sees a different value, which makes any pkgsinfo edit — including a description-only edit — the fleet-wide lever for releasing a suppression. |
| `_metadata` | mapping | A free-form trailing dictionary that authoring and promotion tooling uses for its own bookkeeping. Round-trips untouched; the client ignores it. |

## Keys that are accepted but have no effect

Every key below can be written into a pkgsinfo without error. Some are emitted by Cimian's
own authoring tools. None of them change what a client does.

| Key | What actually happens |
|---|---|
| `uninstallcheck_script` | Written by `cimiimport` and `makepkginfo` (which even exposes `--uninstall-check-script` as a flag) and carried into the catalog by `makecatalogs`. The client has no property for it and nothing in the update engine reads it. Removal is governed entirely by whether the package is removable and by `managed_uninstalls`. |
| `identifier` | Written by the authoring tools and carried into the catalog. The client has no such property, so it is dropped on load. Package identity is `name`, and only `name`. |
| `installer.arguments` | Accepted by every authoring tool and by `makecatalogs`, but the client's installer model has no `arguments` property. **Your arguments are silently discarded.** Use `installer.args`. |
| `installer.identity_name` | Accepted at the `installer:` level and carried into the catalog, but the client reads `identity_name` only from `uninstaller[]` and `installs[]` entries. Setting it on `installer:` does nothing. |
| `installer_type` (top-level scalar) | Written by `makepkginfo` as a top-level key. `makecatalogs` has no such property, so it is stripped at catalog generation and never reaches a device. The real key is `installer.type`. |
| `receipts` | Not part of the Cimian schema. It is parsed only by `repoclean`, which reads foreign Munki-shaped pkgsinfo for repo-cleanup purposes. Never read by a client. |
| `uninstall_method` | Same: a Munki key that only `repoclean` looks at. Removal mechanism is chosen from `uninstaller[]`, `uninstall_script` and `installer.type` — there is no method field. |
| `installer_item_location` | A Munki key. Not part of the Cimian schema; only `repoclean` parses it. Use `installer.location`. |
| `installer_item_hash` | Same. Use `installer.hash`. |
| `installer_item_size` | Same. Use `installer.size`. |
| `uninstaller_item_location` | Same. Use `uninstaller[].location`. |
| `uninstaller_path` | Was declared by `makepkginfo` and never by anything else, so setting it never did anything. It has been removed, and a regression test asserts it is not written. |
| `supersedes` | Not a pkgsinfo key. It exists in `cimipkg`'s `build-info.yaml`, where it lists legacy MSI UpgradeCodes to remove at build time. The client has no supersession resolution of any kind. |
| `featured` | Does not exist as a per-package key. Featuring is a manifest concern — `featured_items`. See [Featured Items](Featured-Items). |
| `notes` | Not a pkgsinfo key. It appears only on the client-side generated `InstallInfo.yaml` state file. |
| `url` | Only exists on an internal reporting model that no client code deserialises. Use `installer.location`, which accepts an absolute URL. |
| `installer_args` / `uninstaller_args` | Same internal model. Use `installer.args` and `uninstaller[].args`. |
| `requires_elevation` | Same internal model. `managedsoftwareupdate` always runs elevated; there is nothing to request. |
| `restart_required` | Same internal model. Use `restart_action`. |
| `metadata` (unprefixed) | Same internal model. The real free-form field is `_metadata`. |

And once more, because it is the failure mode you will actually hit: **a key that is simply
misspelled behaves exactly like every entry in this table.** No error, no warning, no effect.

## See also

- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Installer Types](Installer-Types)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Installs Arrays](Installs-Arrays)
- [Version Comparisons](Version-Comparisons)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Uninstalling Software](Uninstalling-Software)
- [Blocking Applications](Blocking-Applications)
- [Dependencies And Update Chains](Dependencies-And-Update-Chains)
- [Force Installs And Deadlines](Force-Installs-And-Deadlines)
- [On Demand Items](On-Demand-Items)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Manifests](Manifests)
- [makecatalogs](makecatalogs)
