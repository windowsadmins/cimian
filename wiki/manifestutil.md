# manifestutil

`manifestutil` creates manifest files in the repo and adds or removes items from
their four package sections. It also edits the client's local self-service
manifest. It is a small, flag-driven tool: everything it cannot do — includes,
catalogs, conditional items — you do by editing the manifest YAML directly. This
page covers every option and shows the edits you will actually make.

## Synopsis

```
manifestutil [-l|--list-manifests] [-n|--new-manifest <name>]
             [-m|--manifest <name>] [-a|--add-pkg <package>]
             [-r|--remove-pkg <package>] [-s|--section <section>]
             [--selfservice-request <package>] [--selfservice-remove <package>]
             [-c|--config <path>] [-V]
```

`manifestutil` has no subcommands and no interactive mode. Every invocation is a
single non-interactive action, safe to script; it never prompts and never opens
an editor. Run it with no options and it prints a one-line hint and exits 0.

## Options

| Option | Short | Argument | Default | Effect |
|---|---|---|---|---|
| `--list-manifests` | `-l` | — | — | List the manifest files in the repo. |
| `--new-manifest` | `-n` | name | — | Create an empty manifest with that name. |
| `--manifest` | `-m` | name | — | The manifest to operate on, without the `.yaml` extension. |
| `--add-pkg` | `-a` | package | — | Add an item to `--manifest`. |
| `--remove-pkg` | `-r` | package | — | Remove an item from `--manifest`. |
| `--section` | `-s` | section | `managed_installs` | Which section `--add-pkg` and `--remove-pkg` act on. |
| `--selfservice-request` | — | package | — | Add a package to the local self-service manifest. |
| `--selfservice-remove` | — | package | — | Remove a package from the local self-service manifest. |
| `--config` | `-c` | path | `%ProgramData%\ManagedInstalls\Config.yaml` | Configuration file to read `repo_path` from. |
| `-V` | — | — | — | Print the version and exit. |
| `--help` | — | — | — | Print usage and exit. |

`--section` accepts exactly `managed_installs`, `managed_uninstalls`,
`managed_updates` and `optional_installs`. Matching is case-insensitive; anything
else is an error and exits 1. There is no `included_manifests` section here — see
[Including another manifest](#including-another-manifest).

There is no `--version` long form; use `-V`.

## How it finds the repo

`manifestutil` reads the `repo_path` key from a configuration file and works in
`<repo_path>\manifests`. The file defaults to
`%ProgramData%\ManagedInstalls\Config.yaml` and can be overridden per invocation
with `--config`.

That means the tool normally runs on a machine whose Cimian configuration already
points at the repo. To run it elsewhere — an admin workstation with a mounted
repo, for instance — write a small YAML file containing just the repo path and
pass it with `--config`:

```yaml
repo_path: C:\CimianRepo
```

```
manifestutil --config C:\CimianRepo\adminconfig.yaml --list-manifests
```

If the configuration file does not exist, the run fails with
`Error: Config file not found: <path>` and exit 1. If it exists but sets no
`repo_path`, it fails with `Error: repo_path not configured in config file`.

The two `--selfservice-*` options are the exception: they are handled before the
configuration is read, so they work on a machine with no `repo_path` at all.

## Listing manifests

```
manifestutil --list-manifests
```

This prints `Available manifests:` followed by the file names, with the `.yaml`
extension, sorted. Only `<repo_path>\manifests` itself is listed — manifests
filed into subdirectories do not appear.

If the directory does not exist you get `Error: Manifest directory not found:
<path>` and exit 1.

## Creating a manifest

```
manifestutil --new-manifest WORKSTATION-01
```

This writes `<repo_path>\manifests\WORKSTATION-01.yaml` and prints
`New manifest created: <path>`. The new file contains only its name:

```yaml
name: WORKSTATION-01
```

If a manifest with that name already exists the run fails with
`Error: Manifest 'WORKSTATION-01' already exists` and exit 1 — an existing
manifest is never overwritten. The name is used verbatim as the file name, so it
must be valid as one.

## Adding a managed install

`--add-pkg` needs `--manifest`; on its own it does nothing. The default section is
`managed_installs`, so this adds `ExampleApp` to that list:

```
manifestutil --manifest WORKSTATION-01 --add-pkg ExampleApp
```

The tool prints `Added ExampleApp to managed_installs in WORKSTATION-01` and the
file becomes:

```yaml
name: WORKSTATION-01
managed_installs:
- ExampleApp
```

Adding an item that is already in that section is a no-op — the comparison is
case-insensitive, and the file is rewritten unchanged. `manifestutil` does not
check that the item exists in any catalog, so a typo is accepted silently and
only shows up as an unresolved item on a client.

The same form with `--section` targets the other lists:

```
manifestutil --manifest WORKSTATION-01 --add-pkg LegacyApp --section managed_uninstalls
```

```
manifestutil --manifest WORKSTATION-01 --add-pkg ExampleApp --section managed_updates
```

## Adding an optional install

Optional installs are the items a user can choose in Managed Software Center:

```
manifestutil --manifest WORKSTATION-01 --add-pkg ExampleApp --section optional_installs
```

Short form:

```
manifestutil -m WORKSTATION-01 -a ExampleApp -s optional_installs
```

## Removing an item

```
manifestutil --manifest WORKSTATION-01 --remove-pkg ExampleApp --section optional_installs
```

If the item was there it is removed, the file is saved, and the tool prints
`Removed ExampleApp from optional_installs in WORKSTATION-01`. If it was not, the
tool prints `Package ExampleApp was not in optional_installs in WORKSTATION-01`
and still **exits 0** — a removal that changed nothing is not an error, which
matters if you are checking exit codes in a script.

Removing an item from `managed_installs` stops Cimian keeping it up to date; it
does not uninstall it. To uninstall, add the item to `managed_uninstalls` — see
[Uninstalling Software](Uninstalling-Software).

If the target manifest file does not exist, both `--add-pkg` and `--remove-pkg`
fail with `Error: Manifest file not found: <path>` and exit 1.

## Including another manifest

`manifestutil` cannot edit `included_manifests`. Add the key by hand:

```yaml
name: WORKSTATION-01
included_manifests:
- site-default
- lab-shared
catalogs:
- Production
managed_installs:
- ExampleApp
```

Included manifest names are written without the `.yaml` extension, and any
backslashes in them are normalised to forward slashes when the file is saved —
including by `manifestutil` itself, the next time it edits that manifest.

`catalogs:` is likewise not editable through the tool. Both keys survive
`manifestutil` edits untouched, so it is safe to hand-edit a manifest and keep
using the tool on its package sections afterwards. See [Manifests](Manifests) for
what these keys mean and how inclusion resolves.

## Listing what a manifest resolves to

`manifestutil` has no resolve or preview command. It reads and writes one file at
a time and never follows `included_manifests`, so `--list-manifests` tells you
which manifests exist, not what any of them produces.

To see the resolved set for a client, run a check-only session on that client and
read the result:

```
managedsoftwareupdate --checkonly
```

That walks the client's manifest and everything it includes, resolves items
against the catalogs, and writes the outcome to
`%ProgramData%\ManagedInstalls\InstallInfo.yaml`. To resolve a specific manifest
rather than the one the client identifier selects:

```
managedsoftwareupdate --checkonly --manifest WORKSTATION-01
```

See [managedsoftwareupdate](managedsoftwareupdate) and
[Client Identifier Resolution](Client-Identifier-Resolution).

## Self-service requests

`--selfservice-request` and `--selfservice-remove` do not touch the repo. They
edit `%ProgramData%\ManagedInstalls\SelfServeManifest.yaml` on the machine you run
them on — the same file Managed Software Center writes when a user installs or
removes an optional item.

```
manifestutil --selfservice-request ExampleApp
```

The package is appended to that file's `managed_installs` list and the tool
prints `Package will be processed on next 'managedsoftwareupdate' run.` If it was
already listed you get `Package 'ExampleApp' is already in self-service manifest`
and exit 0.

```
manifestutil --selfservice-remove ExampleApp
```

Removal takes the package out of both the `managed_installs` and
`optional_installs` lists of the self-service manifest. Removing something that
was never there prints `was not in self-service manifest` and exits 0.

Writing under `%ProgramData%\ManagedInstalls` normally requires an elevated shell.
Requesting a package here does not make it installable: it still has to be offered
to that client as an optional install by a manifest. See
[Optional Installs And Self Service](Optional-Installs-And-Self-Service).

## Order of evaluation

When several options are given, `manifestutil` acts on the first one that
applies and ignores the rest:

1. `-V`
2. `--selfservice-request`
3. `--selfservice-remove`
4. `--list-manifests`
5. `--new-manifest`
6. `--manifest` with `--add-pkg`
7. `--manifest` with `--remove-pkg`

So `--new-manifest X --add-pkg Y` creates the manifest and does **not** add the
package. Use two invocations:

```
manifestutil --new-manifest WORKSTATION-01
```

```
manifestutil --manifest WORKSTATION-01 --add-pkg ExampleApp
```

## Exit codes

| Code | Meaning |
|---|---|
| 0 | The action succeeded, including "already present" and "was not there". |
| 1 | The configuration file is missing, or sets no `repo_path`. |
| 1 | `--section` is not one of the four valid names. |
| 1 | `<repo_path>\manifests` does not exist. |
| 1 | `--new-manifest` names a manifest that already exists. |
| 1 | The manifest named by `--manifest` does not exist. |

## Limitations

- No interactive mode. Every action is one command.
- Only the four package sections are editable. `included_manifests`, `catalogs`,
  `conditional_items` and any other key must be hand-edited.
- Items are never validated against catalogs, so misspelled package names are
  accepted.
- `--list-manifests` does not recurse into subdirectories of `manifests\`.
- There is no command to show a manifest's contents or its resolved item set.
- Nothing is written to the repo's version control; commit the changed manifest
  yourself. See [Cimian With Git](Cimian-With-Git).

## See also

- [Manifests](Manifests)
- [Conditional Items](Conditional-Items)
- [Optional Installs And Self Service](Optional-Installs-And-Self-Service)
- [Client Identifier Resolution](Client-Identifier-Resolution)
- [The Cimian Repository](The-Cimian-Repository)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Command Line Tools](Command-Line-Tools)
