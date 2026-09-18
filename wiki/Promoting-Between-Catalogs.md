# Promoting Between Catalogs

Promotion is how a package moves from a pilot group to the fleet: you add a catalog name to
the item's pkgsinfo, regenerate the catalogs, and publish. This page is the task — the edit,
the rebuild, how to confirm a client actually picked it up, how to back out, and the failure
modes that make a promotion look done when it is not.

Read [Using Catalogs](Using-Catalogs) first if you have not; this page assumes you know that
catalog order carries no precedence and that the highest version wins across every catalog a
device reads.

## Find the item

Promotion is a per-pkgsinfo-file operation, and a package usually has one file per version.
Locate the exact file for the version you intend to promote:

```powershell
Get-ChildItem -Path C:\CimianRepo\pkgsinfo -Recurse -Filter *.yaml | Select-String -Pattern '^name: ExampleApp$' -List
```

If more than one file matches, check the `version:` in each and be deliberate about which one
you are promoting. Promoting two versions of the same package into the same catalog is the
first pitfall below.

## Make the edit

Add the target catalog to the item's `catalogs:` list. Leave the earlier ones in place — a
pilot machine that drops off Testing simply stops getting updates through it.

```yaml
name: ExampleApp
version: 1.2.0
catalogs:
  - Development
  - Testing
  - Production
```

Nothing else changes. Do not bump the version, do not touch `installer.location`, and do not
re-import the installer. Promotion is a metadata change to a package that has already been
built and verified.

## Rebuild and publish

```powershell
makecatalogs --repo_path C:\CimianRepo
```

Confirm the item is now in the target catalog before publishing anything:

```powershell
Select-String -Path C:\CimianRepo\catalogs\Production.yaml -Pattern '^- name: ExampleApp$'
```

If that returns nothing, the promotion did not happen — check the spelling of the catalog name
in the pkgsinfo, and check that `makecatalogs` exited 0. A non-zero exit means at least one
pkgsinfo in the repo failed to parse and the catalogs on disk are incomplete; fix the parse
error and rerun rather than publishing.

Then publish `catalogs\` to the served repo by whatever mechanism you use. Only `catalogs\`
changes in a promotion — the payload is already in `pkgs\` and is untouched.

## Verify a client picks it up

On a machine whose manifest names the target catalog, run a check-only session with verbosity
on. This resolves the manifest, downloads the catalogs and evaluates every item without
installing anything.

```powershell
managedsoftwareupdate --checkonly -vv
```

The item should appear in the run's output with a status. To confirm the client really fetched
a fresh catalog rather than falling back to its cached copy, look at the cached file directly —
it is overwritten on every successful download:

```powershell
Select-String -Path C:\ProgramData\ManagedInstalls\catalogs\Production.yaml -Pattern '^- name: ExampleApp$'
```

`Item not found in catalog: ExampleApp` in the run output means the client's catalog does not
contain it: either the rebuild did not include it, the publish did not reach the server, or the
catalog download failed and the client silently used its cached copy from before your change.
A failed catalog download is logged as a warning followed by `Falling back to local cache`.

Once satisfied, let the fleet pick it up on its own schedule.

## Rolling back

Remove the catalog name and regenerate:

```yaml
catalogs:
  - Development
  - Testing
```

```powershell
makecatalogs --repo_path C:\CimianRepo
```

That stops further deployment. **It does not undo installs that already happened.** Cimian does
not downgrade: a device whose installed version is greater than or equal to the catalog version
reports the item as installed and takes no action. Removing an item from a catalog only makes
the device stop hearing about it. To actually reverse an install you need either a higher
version that supersedes the bad one, or a `managed_uninstalls` entry — see
[Uninstalling Software](Uninstalling-Software).

There is one trap specific to rollback. If the item you remove was the *only* item in that
catalog, `makecatalogs` deletes the catalog file, and clients whose manifests still name it get
a 404 and fall back to the copy they cached on their last successful run — which still contains
the item you just pulled. Keep at least one item in every catalog a manifest names, or remove
the catalog from the manifests at the same time.

## Pitfalls

### The same name and version in two catalogs

`makecatalogs` does not check for duplicates. If two different pkgsinfo files declare the same
`name` at the same `version` and land in catalogs the same device reads, the client keeps
whichever it loaded first and discards the other — and load order is just the order catalogs
accumulated through the manifest tree, which is not something you can read off the repo.

The practical damage is that promotion state becomes unreadable. You cannot tell from
`catalogs\Production.yaml` which body a device is running, and promoting or rolling back one of
the two files may change nothing at all. Before promoting, confirm the version you are
promoting is unique in the repo:

```powershell
Get-ChildItem -Path C:\CimianRepo\pkgsinfo -Recurse -Filter *.yaml | Select-String -Pattern '^version: 1\.2\.0$' -List
```

Fix duplicates by deleting the redundant pkgsinfo file, not by editing catalogs.

### A rewrite that drops fields

Any tool that rewrites a pkgsinfo re-serializes it from its own model. Keys the tool does not
know about are dropped when the file is read, and never written back. Empty-string values are
dropped on rewrite too, so `description: ""` does not survive.

This bites hardest on packages that were imported and then hand-edited: re-importing over them,
or running them through a tool with a narrower model, can silently strip the hand-added keys.
`manifestutil` has the same problem on the manifest side — its model knows nothing of
`conditional_items`, `featured_items`, `default_installs`, `managed_profiles` or
`managed_apps`, so round-tripping a manifest through it drops them.

Losing an `installs` array is the worst case: with no install-verification entries, the item's
installed state falls back to a recorded version, and the package can report itself installed
forever without ever being verified against anything on disk. Diff the pkgsinfo before you
publish, every time a tool has touched it.

### A key the catalog generator does not carry through

A pkgsinfo key is only real if it survives all three legs of the chain: the tool that writes
it, `makecatalogs`, and the client. `makecatalogs` has a fixed set of properties; a key it does
not declare is dropped at catalog generation and never reaches a device at all, no matter how
correctly it is spelled in the pkgsinfo. A key it does carry but the client has no property for
is written into the catalog and then ignored.

Neither failure produces an error anywhere. The pkgsinfo looks right, the catalog builds
cleanly, and the behaviour you asked for never happens. Two examples that ship today: the
top-level `installer_type:` scalar is stripped by `makecatalogs` and never reaches a client —
the real key is `installer.type` — and `uninstallcheck_script`, `identifier` and
`installer.arguments` are carried into the catalog but the client has no property for any of
them and never reads them. Use `installer.args`, not `installer.arguments`.

When you use a key you have not used before, verify it end to end after rebuilding:

```powershell
Select-String -Path C:\CimianRepo\catalogs\Production.yaml -Pattern 'installer_timeout'
```

If the key is not in the catalog, it will never reach a client. If it is in the catalog,
confirm the client's behaviour changes — the catalog carrying a key is not proof the client
reads it. [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys) records which keys are accepted
but ignored.

## See also

- [Using Catalogs](Using-Catalogs)
- [The Cimian Repository](The-Cimian-Repository)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Version Comparisons](Version-Comparisons)
- [Installs Arrays](Installs-Arrays)
- [Uninstalling Software](Uninstalling-Software)
- [Installing Software](Installing-Software)
- [makecatalogs](makecatalogs)
- [Cimian With Git](Cimian-With-Git)
