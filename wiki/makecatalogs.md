# makecatalogs

`makecatalogs` reads every pkgsinfo file in the repo and writes the catalog files
that clients download. Nothing you put in a pkgsinfo reaches a client until
`makecatalogs` has run and republished the catalogs. This page covers the flags,
exactly what the tool validates, what it writes, and the failure modes you will
hit in practice.

## Synopsis

```
makecatalogs [--repo_path <path>] [--skip_payload_check] [--hash_check]
             [--silent] [--tolerate_parse_errors] [-V]
```

`makecatalogs` has no subcommands.

## Options

| Option | Aliases | Default | Effect |
|---|---|---|---|
| `--repo_path` | `-repo_path`, `-r` | from config | Path to the repo. |
| `--skip_payload_check` | `-s` | off | Do not check that installer and uninstaller files exist. |
| `--hash_check` | — | off | Also compare each payload's MD5 hash against the pkgsinfo. Slow on a large repo. Size is checked without it. |
| `--silent` | `-q` | off | Suppress the per-item and per-catalog progress lines. Warnings and errors still print. |
| `--tolerate_parse_errors` | — | off | Write catalogs and exit 0 even when some pkgsinfo failed to parse. |
| `-V` | — | — | Print the version and exit. |
| `--help` | — | — | Print usage and exit. |

`-repo_path` with a single dash is a real alias, kept for compatibility with
older tooling. There is no `--version` long form; use `-V`.

## Finding the repo

With `--repo_path` given, that path is used. Without it, `makecatalogs` reads the
`repo_path` key from `%ProgramData%\ManagedInstalls\Config.yaml`. If that file
does not exist, cannot be parsed, or has no `repo_path`, the tool prints an error
and exits 1 — it never guesses a default.

```
makecatalogs --repo_path C:\CimianRepo
```

## What it reads

`makecatalogs` walks `<repo>\pkgsinfo` recursively and reads every file with a
`.yaml` extension. Files with any other extension — including `.yml`, `.plist`
and `.json` — are ignored entirely, silently. Subdirectory layout under
`pkgsinfo\` is free: directories are for your convenience and have no meaning to
the tool.

If `<repo>\pkgsinfo` does not exist the run fails with
`pkgsinfo directory not found` and exit 1.

## What it validates

### Parse errors

A pkgsinfo that is not valid YAML, or whose YAML does not fit the pkgsinfo shape
(for example `installs:` given as a string instead of a list), fails to
deserialize. `makecatalogs` prints `WARNING: Error parsing <file>: <message>`,
skips the file, and carries on.

Skipped files are restated at the end of the run, because the original warning
has usually scrolled away, and the run then **fails with exit 1**:

```
WARNING: 2 pkgsinfo skipped (parse errors); those packages are NOT in the catalogs:
```

This is the one condition that fails the run. It exists so a publishing pipeline
cannot report success while shipping catalogs that quietly dropped a package.
`--tolerate_parse_errors` downgrades it: the catalogs are still written without
those packages, the same list is still printed, and the exit code becomes 0.

### Payload checks

Unless `--skip_payload_check` is given, `makecatalogs` builds a list of every file
under `<repo>\pkgs` and checks each item against it:

- `installer.location` must resolve to an existing file under `pkgs\`. If not:
  `<pkgsinfo path> has missing installer => pkgs/<location>`.
- Every entry in `uninstaller:` that has a `location` must likewise exist:
  `<pkgsinfo path> has missing uninstaller => pkgs/<location>`.

Leading slashes are trimmed and backslashes are treated as forward slashes, so
`apps/example/ExampleApp-1.2.3.msi` and `apps\example\ExampleApp-1.2.3.msi` both
work. The comparison is case-insensitive.

Uninstaller entries with no `location` are skipped deliberately — an MSIX or APPX
uninstaller is identified by `identity_name` and has no file in the repo.

### Size and hash checks

Size is checked on every run. For each payload that exists, the file's length is
compared with `installer.size` / `uninstaller[].size`, producing
`installer size mismatch: expected …, actual …`. Reading a file's length is a
stat call, so this costs nothing measurable even on a large repository.

`--hash_check` adds a digest comparison against `installer.hash` /
`uninstaller[].hash`, producing `installer hash mismatch: expected …, actual …`.
This reads every payload in full, so it is slow on a large repository and is off
by default.

Note that the digest `--hash_check` computes is MD5, while `installer.hash` holds
the SHA-256 the client verifies on download. Every hashed item therefore reports
a mismatch, and the flag currently finds nothing real.

### What these checks do *not* do

Payload, size and hash problems are **warnings only**. They are printed after the
catalogs have already been written, and they do not change the exit code. A
catalog containing an item whose installer is missing from the repo is a normal,
successful `makecatalogs` run. Read the output; do not rely on the exit code to
catch a missing payload.

### What is never checked

- **Duplicate `name` + `version`.** Two pkgsinfo files declaring the same name and
  version are both written into the catalog. There is no warning and no
  de-duplication. Which one a client acts on is not defined by `makecatalogs`.
- **Missing required keys.** A pkgsinfo with no `name` or no `version` still
  produces a catalog item, with an empty string in that field.
- **Unknown keys.** See below — they are dropped without a message.
- **Catalog names.** A `catalogs:` entry that no manifest references simply
  creates a catalog file nobody downloads.
- **Icons, categories, `requires` targets, `update_for` targets.** None are
  resolved or verified.

## What it writes

Catalogs go to `<repo>\catalogs\<CatalogName>.yaml`. The directory is created if
it does not exist.

Every item is written into `All.yaml`, which is always produced even for an empty
repo. Each name in an item's `catalogs:` list produces a further catalog file
containing that item. Catalog names are matched case-insensitively, so
`Production` and `production` in different pkgsinfo files land in one file. An
item with no `catalogs:` list appears only in `All.yaml`.

Any `.yaml` file already in `<repo>\catalogs` whose name is not a catalog in this
run is **deleted**, with `WARNING: Removed stale catalog <path>`. Do not keep
hand-written files in that directory.

A catalog file is a single `items:` list of full pkgsinfo records:

```yaml
items:
- name: ExampleApp
  display_name: Example App
  version: 1.2.3
  catalogs:
  - Production
  category: Utilities
  developer: Example Corp
  installs:
  - type: file
    path: C:\Program Files\Example App\ExampleApp.exe
    version: 1.2.3
  unattended_install: true
  unattended_uninstall: false
  installer:
    location: apps/example/ExampleApp-1.2.3.msi
    hash: 0123456789abcdef0123456789abcdef
    type: msi
    size: 48234496
  OnDemand: false
  recurring: false
  loop_fingerprint: 4f2c1d9a7b3e5068
```

Fields absent from the pkgsinfo are omitted, except for the boolean fields
`unattended_install`, `unattended_uninstall`, `OnDemand` and `recurring`, which
are always written, and `name` and `version`, which are always written even if
empty.

### loop_fingerprint

`makecatalogs` stamps every item with `loop_fingerprint`, a hash of that item's
whole serialized catalog entry. The client stores the fingerprint of the item it
last installed and clears that package's loop suppression as soon as it sees a
different one. Publishing a corrected pkgsinfo is therefore what releases a
package that install-loop protection has suppressed, fleet-wide, with no
per-machine action.

Because the hash covers the entire item, any edit that reaches the catalog —
including a description-only change — clears suppression. See
[Install Loop Prevention](Install-Loop-Prevention).

## Unknown and newer keys are dropped

`makecatalogs` deserializes each pkgsinfo into a fixed set of fields and
re-serializes that model into the catalog. Any key it does not know is discarded
silently: no warning, no entry in the catalog, and therefore **no effect on any
client**. The `_metadata` block that some tools write into pkgsinfo is dropped the
same way.

This matters because a pkgsinfo key that a newer client understands does nothing
at all until `makecatalogs` also knows how to carry it. If you add a key and
clients ignore it, open `<repo>\catalogs\All.yaml` and check whether the key is
actually there before assuming a client bug.

The keys carried into the catalog today are:

`name`, `display_name`, `identifier`, `version`, `description`, `catalogs`,
`category`, `developer`, `icon_name`, `requires`, `update_for`, `installs`,
`blocking_applications`, `supported_architectures`, `unattended_install`,
`unattended_uninstall`, `unused_software_removal_info`, `minimum_os_version`,
`maximum_os_version`, `minimum_cimian_version`, `installer`, `uninstaller`,
`preinstall_script`, `postinstall_script`, `preuninstall_script`,
`postuninstall_script`, `installcheck_script`, `uninstallcheck_script`,
`uninstallable`, `install_script`, `uninstall_script`, `version_script`,
`restart_action`, `installer_timeout`, `force_install_after_date`, `precache`,
`check`, `install_window`, `OnDemand`, `recurring`.

Within `installer:` and each `uninstaller:` entry: `location`, `hash`, `type`,
`size`, `switches`, `flags`, `success_codes`, `subcommand`, `arguments`, `args`,
`temp_dir`, `product_code`, `upgrade_code`, `identity_name`.

Within each `installs:` entry: `type`, `path`, `md5checksum`, `version`,
`product_code`, `upgrade_code`, `display_name`, `identity_name`.

Key names are matched exactly as written, including `OnDemand`, which is
capitalised where every other key is lower case with underscores.

See [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys) for what each key means to
a client.

## When to run it

Run `makecatalogs` after any change to the repo's metadata:

- adding, editing or deleting a pkgsinfo by hand
- changing a package's `catalogs:` list to promote it between catalogs
- deleting a pkgsinfo and its payload

You do not need to run it after [cimiimport](cimiimport), which runs
`makecatalogs` itself at the end of a successful import. You do need to run it
after [repoclean](repoclean), which does not.

If your repo is served from a static file host or CDN, publishing the changed
files under `<repo>\catalogs` is a separate step; `makecatalogs` only writes to
the local path you gave it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Catalogs written. Payload, size and hash warnings may still have been printed. |
| 0 | Catalogs written with some pkgsinfo skipped, when `--tolerate_parse_errors` is set. |
| 1 | One or more pkgsinfo failed to parse and `--tolerate_parse_errors` was not set. |
| 1 | No repo path could be resolved, `pkgsinfo\` is missing, or the catalogs could not be written. |

## Failure modes

**Exit 1, "N pkgsinfo skipped (parse errors)".** Fix the listed files. Until you
do, those packages are absent from every catalog, so clients that were installing
them stop seeing them. This is the intended behaviour, not a spurious failure —
resist adding `--tolerate_parse_errors` to a pipeline permanently.

**`Error: No repo_path found in config or via --repo_path`.** You ran the tool on
a machine with no `%ProgramData%\ManagedInstalls\Config.yaml`, or with one that
does not set `repo_path`. Pass `--repo_path`.

**`has missing installer => pkgs/…`.** The pkgsinfo names a payload that is not in
`pkgs\`. Clients will download-fail on that item. Common causes are a payload that
was never copied into the repo, a payload deleted by a cleanup pass, and a
`location` typo.

**A catalog disappeared.** The last item referencing that catalog name lost it
from its `catalogs:` list, so the file was removed as stale. Any manifest still
naming that catalog now resolves nothing.

**The change did not reach clients.** Either `makecatalogs` was not run, the
catalogs were not published to the served repo, or the key was dropped as unknown.
Check `<repo>\catalogs\All.yaml` first — it is the ground truth for what
`makecatalogs` produced.

**A new item is in `All.yaml` but not in `Production.yaml`.** The pkgsinfo has no
`catalogs:` entry for `Production`. Membership comes from the pkgsinfo, never from
where the file sits on disk.

## Limitations

- Only `.yaml` files under `pkgsinfo\` are read. A pkgsinfo saved as `.yml` is
  ignored with no message.
- Duplicate `name` + `version` pairs are never detected.
- Payload, hash and size failures do not affect the exit code, so an automated
  run passes with missing installers.
- Unknown keys are dropped without a warning.
- `--hash_check` reads every payload in the repo in full; on a large repo
  this reads the whole `pkgs\` tree.
- There is no `--version` long option, only `-V`.

## See also

- [Using Catalogs](Using-Catalogs)
- [The Cimian Repository](The-Cimian-Repository)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Promoting Between Catalogs](Promoting-Between-Catalogs)
- [cimiimport](cimiimport)
- [repoclean](repoclean)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Command Line Tools](Command-Line-Tools)
