# makepkginfo

`makepkginfo` produces pkgsinfo YAML from an installer and prints it to stdout. It changes
nothing in the repo: no file is copied, no catalog is rebuilt. Use it when you want to see
what metadata an installer yields, when you want a starting point to hand-edit, or when you
are scripting pkgsinfo generation and want to control the output yourself. To actually get
an installer into the repo, use [cimiimport](cimiimport).

## Synopsis

```
makepkginfo [options] [<installer>] [--file <path>...]
makepkginfo --new <PkginfoName>
```

There are no subcommands. Unrecognised tokens are a parse error.

The positional argument is the installer path — or, under `--new`, the name of the stub to
create. Three modes follow from what you pass:

- **An installer** — metadata is extracted from it and printed as YAML.
- **Only `-f` paths** — a minimal pkgsinfo built from those files alone.
- **Neither** — prints
  `Usage: makepkginfo [options] /path/to/installer.msi -f path1 -f path2 ...` and exits 1.

## Requirements

`makepkginfo` refuses to run unless `C:\ProgramData\ManagedInstalls\Config.yaml` exists. If
it is missing it prints `Error: Config file not found at ...` and exits 1. Outside `--new`
the file's contents are never used, only its existence — but it must be there.

For `--new`, the config must also carry a repo path, under the key `repo_path`. The error
message when it is absent says to set `'RepoPath'`; the key the parser reads is `repo_path`.

## What it infers, per installer type

The extension decides the extraction path. Everything the tool cannot read is either left
out or filled from a fallback.

### `.msi`

Sets `installer_type: msi` and reads the MSI Property table:

| Property | Becomes |
|---|---|
| `ProductName` | `name` (falls back to `UnknownMSI`) |
| `ProductVersion` | `version` and `installs[].version` |
| `Manufacturer` | `developer` |
| `Comments` | `description` |
| `ProductCode` | `installs[].product_code` |
| `UpgradeCode` | `installs[].upgrade_code` |
| `ARPSYSTEMCOMPONENT` | if `1`, suppresses the `installs` entry entirely |

That last row matters for packages built by [cimipkg](cimipkg) in installer-type mode. Those
wrappers set `ARPSYSTEMCOMPONENT=1`, and their ProductCode identifies the wrapper rather than
the software it installs, so `makepkginfo` deliberately emits **no** `installs` array. You
must write one describing the wrapped application by hand.

A `.msi` that cannot be opened yields `name: UnknownMSI` and nothing else.

Note that `makepkginfo` reads the plain MSI properties only. Unlike `cimiimport`, it does not
decode the `CIMIAN_PKG_BUILD_INFO` property, so for a cimipkg-built MSI the version you get
is the truncated MSI `ProductVersion`, not the full build version.

### `.exe`

Sets `installer_type: exe`. **Only a version is inferred**, from the Win32 version resource,
in this order: the numeric `FileMajorPart.FileMinorPart.FileBuildPart.FilePrivatePart` if any
part is non-zero; else the `FileVersion` string, truncated at the first `(`; else
`ProductVersion`. Nothing else is read — no publisher, no product name, no description. The
`name` is the filename stem.

### `.nupkg`

Sets `installer_type: nupkg` and reads the embedded `.nuspec`: `<id>` becomes `identifier`,
`<title>` (falling back to `<id>`) becomes `name`, plus `<version>`, `<authors>` as
`developer`, and `<description>`. A package with no readable nuspec falls back to the
filename stem with everything else empty.

### Anything else

Sets `installer_type: unknown` and infers nothing. The name is the filename stem and the
version falls back to today's date.

### What it can never infer

No installer type supplies `category`, `display_name`, `catalogs`, `minimum_os_version`,
`maximum_os_version`, `minimum_cimian_version`, `unattended_install`, `unattended_uninstall`,
`OnDemand`, an uninstaller, silent-install arguments, or any script. Supply those on the
command line or add them by hand.

The version fallback chain is `--pkg-version`, then the extracted version, then today's date
as `yyyy.MM.dd`. The name chain is `--name`, then the extracted name, then the filename stem.

## Flags

Most option names use underscores. The exceptions are `--pkg-version`, which is hyphenated,
and `--OnDemand`, which is case-sensitive and must be typed exactly as shown.

| Flag | Alias | Argument | Default | Effect |
|---|---|---|---|---|
| `<installer>` | — | path | none | Installer to read. Under `--new`, the stub name instead. |
| `--file` | `-f` | path, repeatable | none | Add a file to the `installs` array. |
| `--name` | — | string | extracted | Override the item name. |
| `--displayname` | — | string | none | Set `display_name`. Ignored in files-only mode. |
| `--pkg-version` | — | string | extracted | Override the version. |
| `--description` | — | string | extracted | Set `description`. |
| `--developer` | — | string | extracted | Set `developer`. |
| `--category` | — | string | none | Set `category`. |
| `--catalogs` | — | list | `Development` | Comma-separated catalog names. |
| `--identifier` | — | string | none | **Parsed and never used.** The emitted `identifier` comes only from a nuspec `<id>`. |
| `--minimum_os_version` | — | version | none | Set `minimum_os_version`. |
| `--maximum_os_version` | — | version | none | Set `maximum_os_version`. |
| `--minimum_cimian_version` | — | version | none | Set `minimum_cimian_version`. |
| `--unattended_install` | — | no | off | Emit `unattended_install: true`. |
| `--unattended_uninstall` | — | no | off | Emit `unattended_uninstall: true`. Ignored in files-only mode. |
| `--OnDemand` | — | no | off | Emit `OnDemand: true`. See [On Demand Items](On-Demand-Items). |
| `--uninstaller` | — | path | none | Emit a one-element `uninstaller:` list. Type inferred from the extension: `.msi`, `.exe`, `.ps1`; anything else omits the type. Ignored in files-only mode. |
| `--installcheck_script` | — | path | none | File contents become `installcheck_script`. |
| `--uninstallcheck_script` | — | path | none | File contents become `uninstallcheck_script`. |
| `--preinstall_script` | — | path | none | File contents become `preinstall_script`. |
| `--postinstall_script` | — | path | none | File contents become `postinstall_script`. |
| `--preuninstall_script` | — | path | none | File contents become `preuninstall_script`. |
| `--postuninstall_script` | — | path | none | File contents become `postuninstall_script`. |
| `--unused_removal_days` | — | integer | none | Emit `unused_software_removal_info.removal_days`. |
| `--unused_path` | — | path, repeatable | none | Emit `unused_software_removal_info.paths` entries. |
| `--unused_minimum_history_days` | — | integer | none | Emit `unused_software_removal_info.minimum_history_days`. |
| `--new` | — | no | off | Write a pkgsinfo stub into the repo and exit. |

All six `*_script` options and `--uninstaller` are silently ignored in files-only mode.

The `--unused_path` help text says it defaults to the `.exe` entries in `installs`. It does
not — if you pass no `--unused_path`, the `paths` key is simply omitted.

Exit code is `0` on success, `1` on a missing config, a missing `--new` name, an
unconfigured repo path, no input at all, a missing installer file, or any exception.

## Output

Normal operation writes the YAML to **stdout only**. There is no output-file option;
redirect it yourself.

Top-level keys are emitted as `name`, `display_name`, `version`, then everything else in
ordinal sort order — which puts the capitalised `OnDemand` immediately after `version` —
with `_metadata` last if present. Keys whose value is null, an empty list or an empty string
are dropped, and the boolean flags are omitted when false, so `makepkginfo` never writes
`unattended_install: false`. Scripts are emitted as literal `|` blocks.

`installs` entries use a lowercase-hex **MD5** in `md5checksum`, while `installer.hash` is a
lowercase-hex **SHA-256**. `installer.size` is the file size in KB, integer-divided.
`installer.location` is the installer's filename only, with no directory — you must correct
it to the repo-relative path before the pkgsinfo is usable.

A typical MSI produces:

```yaml
name: Example App
version: 4.2.1
catalogs:
- Development
description: Example App runtime files
developer: Example Vendor
installer:
  type: msi
  size: 4821
  location: ExampleApp-4.2.1.msi
  hash: 3b1f...
installer_type: msi
installs:
- type: msi
  version: 4.2.1
  product_code: '{2C4A9E71-0000-0000-0000-000000000000}'
  upgrade_code: '{6D18B0C4-0000-0000-0000-000000000000}'
```

A typical EXE produces an `installs` entry pointing at the installer itself, which is almost
never what you want:

```yaml
name: ExampleAppSetup
version: 4.2.1
catalogs:
- Development
installer:
  type: exe
  size: 91204
  location: ExampleAppSetup.exe
  hash: c07d...
installer_type: exe
installs:
- type: file
  path: C:\Downloads\ExampleAppSetup.exe
  md5checksum: 9a11...
  version: 4.2.1
```

## The installs array

Two sources feed it.

From the installer itself, one entry: `type: msi` with `version`, `product_code` and
`upgrade_code` for an MSI; `type: file` with `path`, `md5checksum` and `version` for an EXE
or nupkg; `type: file` with `path` and `md5checksum` only for an unknown extension; and
nothing at all for an MSI with `ARPSYSTEMCOMPONENT=1`.

From each `-f` path, one entry. The path is made absolute, and a `%USERPROFILE%` prefix is
folded back to the literal `%USERPROFILE%` token. A path that does not exist, or names a
directory, prints `Skipping -f path: '<path>'` and is dropped. A version is read only for
`.exe` files.

When `-f` is combined with an installer, the per-file versions are stripped from the emitted
YAML — the entries keep their paths and checksums but not their versions.

For the `installs` entry taken from the installer, the `path` is the installer path exactly
as you typed it. That is the file you are installing *from*, not a file that ends up on the
target machine, so for an EXE or nupkg it is essentially always wrong and must be replaced.

## Creating a stub

`--new` is the one mode that writes a file. It ignores every other option.

```
makepkginfo --new ExampleApp
```

That writes `<repo_path>\pkgsinfo\ExampleApp.yaml`, creating `pkgsinfo\` if needed, and
prints `New pkgsinfo created: ...`. The content is:

```yaml
name: ExampleApp
version: 2026.09.03
catalogs:
- Testing
unattended_install: true
```

Two things to watch: `.yaml` is appended unconditionally, so `--new ExampleApp.yaml` gives
you `ExampleApp.yaml.yaml`; and an existing file at that path is **overwritten with no
prompt**.

## How it differs from cimiimport

| | `makepkginfo` | `cimiimport` |
|---|---|---|
| Output | YAML on stdout | files written into the repo |
| Copies the installer into `pkgs\` | no | yes |
| Writes into `pkgsinfo\` | only with `--new` | yes |
| Runs `makecatalogs` | no | yes, before and after |
| Runs `git pull` | no | yes, when the repo is a git tree |
| Prompts | never | yes, unless `--nointeractive` |
| Reads a cimipkg `build-info.yaml` from the MSI | no | yes |
| Extracts icons | no | with `--extract-icon` |
| `installer.location` | filename only | repo-relative path |
| Filename convention | you choose | `<name>-<arch>-<version>` |

Put simply: `makepkginfo` is for producing metadata you intend to inspect or edit;
`cimiimport` is for getting an item into the repo.

## Worked example: generate, then hand-edit

Generate the metadata for an EXE installer and save it where you want it:

```
makepkginfo --name ExampleApp --displayname "Example App" --developer "Example Vendor" --category Utilities --catalogs Testing --unattended_install C:\Downloads\ExampleAppSetup.exe > C:\CimianRepo\pkgsinfo\mgmt\ExampleApp-4.2.1.yaml
```

The generated file needs three corrections before it is usable. First, `installer.location`
is the bare filename, so fix it to the repo-relative path the client will download. Second,
the `installs` entry points at the installer rather than at the installed application.
Third, an EXE installer needs its silent switches. The edited result:

```yaml
name: ExampleApp
display_name: Example App
version: 4.2.1
catalogs:
- Testing
category: Utilities
developer: Example Vendor
installer:
  type: exe
  size: 91204
  location: /mgmt/ExampleApp-x64-4.2.1.exe
  hash: c07d...
  arguments:
  - /S
  - /norestart
installer_type: exe
installs:
- type: file
  path: C:\Program Files\Example App\ExampleApp.exe
  version: 4.2.1
unattended_install: true
unattended_uninstall: true
```

Then copy the installer into `C:\CimianRepo\pkgs\mgmt\ExampleApp-x64-4.2.1.exe` and rebuild
the catalogs with [makecatalogs](makecatalogs).

## Limitations

- `--identifier` is accepted and has no effect.
- `installer.location` is a bare filename and is wrong for every real repo layout.
- The `installs` entry derived from an EXE or nupkg installer points at the installer, not at
  anything that will exist on a client.
- `--new` overwrites an existing pkgsinfo silently, and appends `.yaml` unconditionally.
- The stub written by `--new` uses the `Testing` catalog, not the `--catalogs` default of
  `Development`.
- A failure to stat or hash the installer is swallowed, producing a pkgsinfo with no
  `installer:` block at all rather than an error.
- Files-only mode silently drops `--displayname`, `--uninstaller`, `--unattended_uninstall`,
  all six script options and all three `--unused_*` options.
- `Config.yaml` must exist even though nothing outside `--new` reads it.

## See also

- [cimiimport](cimiimport)
- [cimipkg](cimipkg)
- [makecatalogs](makecatalogs)
- [Command Line Tools](Command-Line-Tools)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Installs Arrays](Installs-Arrays)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Installer Types](Installer-Types)
