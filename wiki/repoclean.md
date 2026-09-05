# repoclean

`repoclean` prunes a Cimian repository: it finds superseded versions of packages
and installer files that no pkgsinfo refers to, reports them, and — only when you
ask it to — deletes them. It is a dry run by default. This page covers the
options, exactly what it considers prunable, what it never touches, and how to run
it without losing something you needed.

## Synopsis

```
repoclean --repo-url <path> [--keep <n>] [--show-all] [--auto] [--remove] [-V]
```

`repoclean` has no subcommands.

## Options

| Option | Aliases | Argument | Default | Effect |
|---|---|---|---|---|
| `--repo-url` | `-r` | path | — (required) | Path to the repo. |
| `--keep` | `-k` | integer | `2` | Versions of each package to keep. |
| `--show-all` | `-a` | — | off | List every package, not only those with deletions. |
| `--auto` | `-y` | — | off | With `--remove`, delete without prompting. |
| `--remove` | `--delete` | — | off | Actually delete. Without it, nothing is written. |
| `-V` | — | — | — | Print the version and exit. |
| `--help` | — | — | — | Print usage and exit. |

`--repo-url` is required despite its name — it takes a filesystem path, local or
UNC, not an HTTP URL. `repoclean` never reads
`%ProgramData%\ManagedInstalls\Config.yaml`, so there is no configured default
and no way to omit the path.

`--keep` must be at least 1. A value below that stops the run with
`Error: --keep value must be a positive integer`. `--keep 1` keeps only the
newest version of each package.

There is no `--version` long form; use `-V`.

## Dry run is the default

Without `--remove`, `repoclean` reads the repo, prints everything it would delete,
and exits without writing anything. It ends with:

```
Run with --remove to actually delete these items.
```

With `--remove` and without `--auto`, it asks twice before deleting:

```
Delete pkginfo and pkg items marked as [to be DELETED]? WARNING: This action cannot be undone. [y/N]
Are you sure? This action cannot be undone. [y/N]
```

Each prompt times out after 30 seconds and aborts if nothing is typed, so a
`--remove` run left unattended does nothing rather than deleting. Anything other
than an answer starting with `y` aborts as well.

`--auto` skips both prompts, which is what makes `--remove --auto` suitable for a
scheduled job — and what makes it the only form that can delete files with nobody
watching.

## What it does

1. Reads every file in `<repo>\manifests` and collects the item names they
   reference, including names inside `conditional_items`.
2. Reads every file in `<repo>\pkgsinfo` and records each item's name, version
   and installer location.
3. Lists every file under `<repo>\pkgs`.
4. Groups the pkgsinfo items by package and sorts each group newest version
   first.
5. Marks for deletion everything past the newest `--keep` versions in each group
   that is not otherwise protected, and marks every file in `pkgs\` that no
   pkgsinfo refers to as orphaned.
6. Prints the plan and the space it would recover. If `--remove` was given and
   you confirm, deletes.

Version ordering normalises each version string by replacing everything that is
not a digit or a dot with a dot, then compares the first four numeric components.
Versions that are not basically numeric will not sort the way you expect, so check
the printed order before deleting.

## What it considers prunable

**Superseded pkgsinfo items.** Within a package group, versions past the newest
`--keep` are marked `[to be DELETED]`. The pkgsinfo file itself is deleted, and so
are its installer and uninstaller payloads under `pkgs\` — unless another kept
version references the same payload file, in which case the payload stays.

**Orphaned payloads.** Any file under `<repo>\pkgs` that no pkgsinfo installer
location points at is listed under "The following packages are not referred to by
any pkginfo item" and is deleted on a `--remove` run.

Orphans are not protected by `--keep`. Anything in `pkgs\` that is not currently
referenced goes, regardless of how new it is or how many versions exist. This is
the part of `repoclean` most likely to surprise you — read the orphan list on the
dry run every time.

## What it never touches

- **Versions a manifest names explicitly.** An item written as
  `ExampleApp-1.2.3` in a manifest pins that version; it is shown as
  `(REQUIRED by a manifest)` and kept.
- **Versions another pkgsinfo requires.** A version named in another item's
  `requires` list is shown as `(REQUIRED by another pkginfo item)` and kept.
- **The newest `--keep` versions** of every package, whether or not any manifest
  mentions the package at all. A package no manifest references is annotated
  `[not in any manifests]` but is still pruned only to `--keep` versions, never
  removed entirely.
- **Everything outside `pkgsinfo\` and `pkgs\`.** Manifests, catalogs, icons and
  anything else in the repo are read at most, never modified or deleted.

An unversioned manifest entry — plain `ExampleApp` — protects nothing beyond the
normal `--keep` window, because it does not name a version.

## Sample output

A dry run over a small repo:

```
Repository Cleaner
==================
Repository: C:\CimianRepo
Mode: Dry run (no changes)
Keep versions: 2

Using repository: C:\CimianRepo
Analyzing manifest files...
Analyzing pkginfo files...
Analyzing installer items...
name: ExampleApp
versions:
    2.1.0
    2.0.4
    1.9.8 (pkgsinfo\apps\ExampleApp-1.9.8.yaml) [to be DELETED]
    1.9.1 (pkgsinfo\apps\ExampleApp-1.9.1.yaml) [to be DELETED]

name: ExampleUtility
[not in any manifests]
versions:
    3.0.0
    2.7.2
    2.7.0 (pkgsinfo\utils\ExampleUtility-2.7.0.yaml) [to be DELETED]

The following packages are not referred to by any pkginfo item:
	apps\example\ExampleApp-1.8.0.msi

Total pkginfo items:     128
Item variants:           47
pkginfo items to delete: 3
pkgs to delete:          3
pkginfo space savings:   14.6 KB
pkg space savings:       2.4 GB
                         (Unknown additional pkg space savings from 1 orphaned pkgs)

Run with --remove to actually delete these items.
```

Only packages with something to delete are shown; `--show-all` adds the rest.
"Item variants" is the number of package groups, which is usually lower than the
number of pkgsinfo files.

A `--remove` run prints one line per file:

```
Removing pkgsinfo\apps\ExampleApp-1.9.8.yaml
Removing pkgs\apps\example\ExampleApp-1.9.8.msi
```

Failures to delete an individual file are printed as
`Error removing <path>: <message>` and the run continues.

## Running it safely

Start with a plain dry run and read all of it, particularly the orphan list:

```
repoclean --repo-url C:\CimianRepo
```

Widen the retention window if the default is tighter than your rollback policy,
and confirm the plan again:

```
repoclean --repo-url C:\CimianRepo --keep 4
```

When the plan is what you want, repeat it exactly with `--remove` added, and
answer both prompts:

```
repoclean --repo-url C:\CimianRepo --keep 4 --remove
```

Then rebuild the catalogs, because `repoclean` does not:

```
makecatalogs --repo_path C:\CimianRepo
```

Until `makecatalogs` runs, the published catalogs still advertise packages whose
pkgsinfo and payloads have been deleted, and clients that try to install them fail
on a missing download.

Two habits are worth keeping. Take a backup or a version-control commit of the
repo before the first `--remove` on it — deletions are immediate and there is no
undo. And keep `--keep` at or above the number of versions any manifest might pin,
since only explicitly versioned manifest entries are detected as pinned.

## Exit codes

`repoclean` exits 0 in almost every case, including a completed dry run, an
aborted confirmation prompt, and a repo path that does not exist. It exits 1 only
when `--repo-url` is missing entirely, or when an exception escapes the run.

Do not use the exit code to decide whether cleanup happened. A run against a
mistyped path prints `Error: Repository path does not exist: <path>` and still
exits 0.

## Limitations

- **It does not rebuild catalogs.** After a `--remove` run it prints
  `Rebuilding catalogs at <path>...` followed by
  `Catalog rebuild would be performed here...`. No catalog is written. Run
  [makecatalogs](makecatalogs) yourself.
- **Parsed pkgsinfo fields are not all used.** The analyzer reads the
  uninstaller payload path, `requires` and `update_for`, but the cleanup pass
  never consults them. Consequences:
  - Uninstaller payloads are not registered as referenced, so a standalone
    uninstaller file in `pkgs\` is reported as orphaned and is deleted on a
    `--remove` run.
  - The `(REQUIRED by another pkginfo item)` protection does not apply; a
    dependency is protected only by the `--keep` window or by an explicitly
    versioned manifest entry.
  - Grouping is by package name alone. Two items with the same `name` but
    different `catalogs` or `supported_architectures` share one `--keep` window.
- **Orphan matching compares path strings literally.** The `installer.location`
  in a pkgsinfo is compared with the path as listed on disk without normalising
  separators, so a repo whose pkgsinfo files use forward slashes can have its
  payloads reported as orphaned. If the dry run shows an implausible number of
  orphans, stop; do not pass `--remove`.
- A pkgsinfo missing `name` or `version` is skipped, so its payload counts as
  orphaned.
- Exit codes do not reflect analysis failures.

## See also

- [The Cimian Repository](The-Cimian-Repository)
- [makecatalogs](makecatalogs)
- [Manifests](Manifests)
- [Using Catalogs](Using-Catalogs)
- [Cimian With Git](Cimian-With-Git)
- [Command Line Tools](Command-Line-Tools)
