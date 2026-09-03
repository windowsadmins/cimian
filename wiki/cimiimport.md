# cimiimport

`cimiimport` takes an installer on disk and puts it into the repo: it reads what metadata it
can out of the file, asks you to confirm or correct the rest, copies the installer into
`pkgs/`, writes a matching pkgsinfo into `pkgsinfo/`, and rebuilds the catalogs. It is the
normal way to add a new item to a Cimian repository. If you only want the metadata and not
the import, use [makepkginfo](makepkginfo) instead.

## Synopsis

```
cimiimport [<installerPath>] [--installs-array <path>...] [--repo_path <path>] [--arch <arch>]
           [--uninstaller <path>] [--minimum_os_version <v>] [--maximum_os_version <v>]
           [--minimum_cimian_version <v>]
           [--preinstall-script <p>] [--postinstall-script <p>]
           [--preuninstall-script <p>] [--postuninstall-script <p>]
           [--install-check-script <p>] [--uninstall-check-script <p>]
           [--config] [--config-auto] [--nointeractive] [--emit-installs]
           [--extract-icon] [--icon <path>]
```

There are no subcommands. If you omit the installer path, `cimiimport` prompts for it and
exits 1 if you give it nothing.

## Configuration

`cimiimport` reads `%ProgramData%\ManagedInstalls\Config.yaml`. Run the interactive setup
once before your first import:

```
cimiimport --config
```

It asks for the repo path (required), a cloud provider, a default catalog, a default
architecture, and whether to open the pkgsinfo in an editor after import. `--config-auto`
writes the same file from defaults with no prompts. If you pass both, `--config` wins.

The repo path can also be given per-run with `--repo_path`, which overrides the configured
value for that invocation only.

## What it does, end to end

1. Loads the configuration, applying `--arch` and `--repo_path` overrides.
2. If the repo path is inside a git working tree, prints `Git repository detected, pulling
   latest changes...` and runs `git pull` with interactive credential prompts disabled and
   a 120-second timeout. A failed or timed-out pull is a warning; the import continues.
3. Extracts metadata from the installer (see below).
4. Runs `makecatalogs` silently, then looks in `catalogs\All.yaml` for an existing item with
   the same name. If one exists, it offers to use it as a template, which pre-fills the
   metadata fields and carries over keys such as `requires`, `update_for` and
   `blocking_applications` that cannot be derived from an installer.
5. Prompts you through the metadata fields.
6. Reads any script files given on the command line into the pkgsinfo.
7. Computes the installer's SHA-256 hash and size.
8. Asks where in the repo the item should live.
9. Builds the `installs` array.
10. Prints a summary and asks you to confirm. **The confirmation defaults to no.**
11. Copies the installer into `pkgs\`, writes the pkgsinfo into `pkgsinfo\`, and opens it in
    an editor if configured to.
12. Runs `makecatalogs` again.

## The interactive workflow

Prompts appear in this order. Pressing Enter accepts the value shown in brackets.

If an item with the same name already exists in `catalogs\All.yaml`, `cimiimport` prints its
name, version and description and asks:

```
Use existing item as a template? [Y/n]:
```

This one defaults to **yes** — a blank answer or `y` accepts the template.

Then the metadata fields, each rendered as `<Label> [<default>]: `:

| Prompt | Default |
|---|---|
| `Name [<default>]: ` | the extracted identifier, or the literal `package` when nothing was extracted |
| `Version [<default>]: ` | the extracted version, or `1.0.0` |
| `Developer [<default>]: ` | extracted, often empty |
| `Description [<default>]: ` | extracted, often empty |
| `Category [<default>]: ` | empty unless it came from a template — nothing is ever extracted from an installer |
| `Architecture(s) [<default>]: ` | the architectures detected from the filename, else the configured default (`x64,arm64`) |
| `Catalogs [<default>]: ` | the template's catalogs, else the configured default catalog (`Development`) |
| `Location in repo [<default>]: ` | the computed subdirectory, else `\mgmt` |

The architecture answer is split on commas, semicolons, spaces or tabs and lower-cased; the
first entry becomes the item's primary architecture and the whole list becomes
`supported_architectures`.

The location answer is a repo-relative subdirectory. A leading backslash is added if you
omit it and a trailing one is trimmed. It must not be a UNC or drive-qualified path; either
is rejected.

Finally, a summary and the confirmation:

```

Pkginfo details:
     Name: ExampleApp
     Display Name: ExampleApp
     Version: 2026.09.03
     Description: Example App runtime files
     Category: Utilities
     Developer: Example Vendor
     Architectures: x64
     Catalogs: Development
     Installer Type: msi

Import this item? (y/n) [n]:
```

**Only a literal `y` proceeds.** A blank answer, an `n`, or anything else cancels, prints
`Import canceled.` and exits **0** — a cancel is not an error, so a script that only checks
the exit code cannot tell an import from an abandoned one.

Note also that the summary shows nine fields and nothing else. The `installs` array, the
hash, the repo location, the scripts and the OS version constraints are not shown before you
confirm. Read the written pkgsinfo afterwards.

## Non-interactive use

`--nointeractive` replaces every prompt with its default: the template is always accepted,
the metadata is used unedited, the default repo subdirectory is taken, and the import is
always confirmed. It also suppresses the post-import editor launch.

One consequence to plan for: the fallback that fills empty catalogs with the configured
default catalog lives in the interactive prompt, so under `--nointeractive` an item whose
metadata yielded no catalogs is written with none, and no catalog will contain it. Pass the
metadata you need explicitly, or import interactively.

`--emit-installs` is the other non-interactive mode. It extracts metadata, builds the
`installs` array, prints it as YAML to stdout and exits. It does not pull, does not import,
does not copy anything, and does not run `makecatalogs`. Status messages go to stderr so
stdout stays parseable.

```
cimiimport C:\Downloads\ExampleApp-2026.09.03.msi --emit-installs
```

## Flags

| Flag | Alias | Argument | Effect |
|---|---|---|---|
| `<installerPath>` | — | path | The installer to import. Prompted for if omitted. |
| `--installs-array` | `-i` | path, repeatable | Add an explicit path to the `installs` array. Overrides all automatic generation. |
| `--repo_path` | — | path | Override the configured repo path for this run. |
| `--arch` | — | list | Override the architectures, e.g. `x64,arm64`. |
| `--uninstaller` | — | path | Import an uninstaller alongside the installer. |
| `--minimum_os_version` | — | version | e.g. `10.0.19041`. |
| `--maximum_os_version` | — | version | e.g. `11.0.22000`. |
| `--minimum_cimian_version` | — | version | Minimum Cimian client version. |
| `--preinstall-script` | — | path | File contents become `preinstall_script`. |
| `--postinstall-script` | — | path | File contents become `postinstall_script`. |
| `--preuninstall-script` | — | path | File contents become `preuninstall_script`. |
| `--postuninstall-script` | — | path | File contents become `postuninstall_script`. |
| `--install-check-script` | — | path | File contents become `installcheck_script`. |
| `--uninstall-check-script` | — | path | File contents become `uninstallcheck_script`. |
| `--config` | — | no | Run the interactive configuration and exit. |
| `--config-auto` | — | no | Write the configuration from defaults and exit. |
| `--nointeractive` | — | no | Accept every default without prompting. |
| `--emit-installs` | — | no | Print the generated `installs` array to stdout and exit. |
| `--extract-icon` | — | no | Attempt to extract a product icon. Experimental, off by default. |
| `--icon` | — | path | Where to write the extracted icon. Only meaningful with `--extract-icon`. |
| `--skip-icon` | — | no | Deprecated no-op. Prints a warning and is otherwise ignored. |

The hyphenation is inconsistent and is not a typo in this page: script options use hyphens
(`--preinstall-script`), while version options and `--repo_path` use underscores.

`--extract-icon` supports `.exe`, `.msi`, `.msix` and `.appx`, and produces a 256×256 PNG at
`<repo>\icons\<name>.png`, recorded as `icon_name`. Other extensions yield nothing, and a
failure is a warning that does not abort the import. See
[Product Icons And Screenshots](Product-Icons-And-Screenshots).

Exit codes are `0` on success **and on a user cancel**, and `1` when the installer path is
missing or blank, or on any exception during import.

## Metadata extracted per installer type

Before anything else, `cimiimport` looks for an architecture token in the filename —
`arm64`/`aarch64`, `x64`/`amd64`/`x86_64`/`x86-64`, or `x86`/`win32`/`i386`/`i686` — and if
it finds one, that wins over every other source. Otherwise it uses the configured default.

| Type | Extracted | From |
|---|---|---|
| `.msi` | name, version, developer, ProductCode, UpgradeCode | the MSI Property table: `ProductName`, `ProductVersion`, `Manufacturer`, `ProductCode`, `UpgradeCode` |
| `.exe` | version, developer, description | the Win32 version resource: `FileVersion` then `ProductVersion`, `CompanyName`, `FileDescription`. The **name is always the filename stem** — an EXE never supplies its own name. |
| `.nupkg` | id, name, version, developer, description | the embedded `.nuspec`: `<id>`, `<title>`, `<version>`, `<authors>`, `<description>`. A dotted id is trimmed to its last segment. |
| `.msix`, `.appx`, `.msixbundle`, `.appxbundle` | identity name, version, architecture, display name, publisher, description | `AppxManifest.xml`, or `AppxBundleManifest.xml` for a bundle |
| anything else | nothing | name is the filename stem, version defaults to `1.0.0` |

An MSI built by [cimipkg](cimipkg) carries its own `build-info.yaml` in the
`CIMIAN_PKG_BUILD_INFO` property. When present, its `product` values override the plain MSI
properties, and `CIMIAN_PKG_FULL_VERSION` replaces the truncated MSI `ProductVersion` — this
is how a date-based version survives the round trip.

For a third-party MSI with no `build-info.yaml`, `cimiimport` additionally walks the MSI's
file table and adds up to three `type: file` entries for the largest installed `.exe` files
that carry a version resource.

Nothing is ever extracted for `category`, `requires`, `update_for`,
`blocking_applications` or `catalogs`. `unattended_install` and `unattended_uninstall`
default to `true`.

## The installs array

`cimiimport` picks the first rule that applies:

1. Any `-i` paths you supplied. Each existing file becomes a `type: file` entry with its
   path and an MD5; `.exe` files also get a version. A path that does not exist is skipped
   with a message.
2. `.exe` installer: a single guessed entry at
   `C:\Program Files\<name>\<name>.exe` with the package version. **Verify this** — it is a
   guess and is wrong more often than not.
3. MSIX with an identity name: one `type: msix` entry.
4. A cimipkg installer-type wrapper: if `key_path` was set in the wrapper's `build-info.yaml`,
   one `type: file` entry for that path; otherwise an **empty** array, with a message saying
   the array must describe the wrapped application. The wrapper's own ProductCode identifies
   the wrapper, not the software, so it is deliberately not used.
5. `.msi` with a ProductCode or UpgradeCode: one `type: msi` entry carrying
   `product_code`, `upgrade_code`, and a `display_name` derived from the product name with
   any trailing version token stripped. **No `version` is emitted** — the MSI ProductVersion
   is truncated and cannot be compared reliably.
6. Anything else: an empty array, and the `installs` key is omitted entirely.

An empty `installs` array is a real outcome, not a failure. It means Cimian falls back to its
receipt for detection, which for an installer-type wrapper means the wrapper's presence
rather than the application's. Fill the array in by hand when that matters. See
[Installs Arrays](Installs-Arrays) and
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

## Where things are written

Given repo path `C:\CimianRepo`, an item named `Example-App`, architecture `x64`, version
`2026.09.03`, and a location answer of `\mgmt`:

| Artifact | Path |
|---|---|
| Installer | `C:\CimianRepo\pkgs\mgmt\Example-App-x64-2026.09.03.msi` |
| pkgsinfo | `C:\CimianRepo\pkgsinfo\mgmt\Example-App-x64-2026.09.03.yaml` |
| `installer.location` in the pkgsinfo | `/mgmt/Example-App-x64-2026.09.03.msi` |
| Icon, with `--extract-icon` | `C:\CimianRepo\icons\Example-App.png` |

The name is sanitised first: spaces become hyphens and anything outside
`A-Z a-z 0-9 - _ .` becomes a hyphen. The architecture segment is `-<arch>-` only when
exactly one architecture is supported; a multi-architecture item gets a single hyphen and no
architecture in the filename.

An uninstaller passed with `--uninstaller` is handled differently: it is copied to
`C:\CimianRepo\pkgs\` at the repo root, under its original filename, with no subdirectory and
no rename.

Both the installer copy and the pkgsinfo write **overwrite silently**. Importing the same
name, architecture and version twice replaces what was there, with no prompt and no version
check. An existing pkgsinfo's `_metadata:` block is preserved across the rewrite; everything
else is regenerated.

## What it does not do

- **It does not commit or push.** It pulls, but the copied installer and the new pkgsinfo are
  left uncommitted in the working tree. Committing them is your job. See
  [Cimian With Git](Cimian-With-Git).
- **It does not upload to cloud storage.** The cloud provider and bucket in `Config.yaml` are
  written by `--config` and never read during an import.
- It does not edit manifests. Assign the new item yourself — see [Manifests](Manifests).
- It does not check that the catalogs you named exist.
- It does not detect a duplicate or downgrade version.
- **`--subfolder` does not exist.** Older material documents a
  `cimiimport <installer> --subfolder apps\productivity` form; there is no such option and
  passing it is a parse error. The repo subdirectory comes from the
  `Location in repo [<default>]: ` prompt, or from its default under `--nointeractive`.

It *does* run `makecatalogs` — twice, in fact: once silently before looking for a template,
and once after a successful import. You do not need to run [makecatalogs](makecatalogs)
yourself afterwards.

## Examples

Configure once:

```
cimiimport --config
```

Import an MSI interactively:

```
cimiimport C:\Downloads\ExampleApp-2026.09.03.msi
```

Import an EXE with an explicit architecture and repo, overriding the guessed installs array:

```
cimiimport C:\Downloads\ExampleAppSetup.exe --arch x64 --repo_path C:\CimianRepo -i "C:\Program Files\Example App\ExampleApp.exe"
```

Attach scripts at import time:

```
cimiimport C:\Downloads\ExampleApp.msi --postinstall-script .\postinstall.ps1 --install-check-script .\installcheck.ps1
```

Constrain the OS and import without prompts:

```
cimiimport C:\Downloads\ExampleApp.msi --minimum_os_version 10.0.19041 --nointeractive
```

Inspect what the installs array would be, without touching the repo:

```
cimiimport C:\Downloads\ExampleApp.msi --emit-installs
```

## See also

- [cimipkg](cimipkg)
- [makepkginfo](makepkginfo)
- [makecatalogs](makecatalogs)
- [Command Line Tools](Command-Line-Tools)
- [Installing Software](Installing-Software)
- [Installs Arrays](Installs-Arrays)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [The Cimian Repository](The-Cimian-Repository)
