# Using Catalogs

A catalog is the list of packages a client is allowed to know about. Manifests name items;
catalogs are where the client looks those names up. This page covers how an item gets into a
catalog, how a client chooses which catalogs to read, what happens when the same item appears
in more than one, and how to run a development / testing / production promotion model.

Catalogs are generated files. You never write one by hand, and nothing you change in a
pkgsinfo reaches a client until you regenerate them.

## What a catalog is

`makecatalogs` scans every `.yaml` under `pkgsinfo\`, groups the items by the catalog names
each one declares, and writes one file per catalog into `catalogs\`. Each file is a mapping
with a single `items:` sequence, and each entry is a complete copy of that package's metadata:

```yaml
items:
- name: ExampleApp
  display_name: Example App
  version: 1.2.0
  installer:
    location: apps/ExampleApp-1.2.0.msi
    type: msi
    hash: 5f2b1c...
  installs:
  - type: msi
    upgrade_code: '{00000000-0000-0000-0000-000000000000}'
```

Because a catalog carries whole item bodies, it is the only thing a client reads about
packages. A client never sees `pkgsinfo\`.

## How an item lands in a catalog

The `catalogs:` key in a pkgsinfo is a list of catalog names, and the item is copied into
every one of them:

```yaml
name: ExampleApp
version: 1.2.0
catalogs:
  - Testing
  - Production
```

That produces an `ExampleApp` entry in both `catalogs\Testing.yaml` and
`catalogs\Production.yaml`. Catalogs come into existence purely by being named — there is no
list of valid catalog names anywhere, and no step that registers one. Name a new catalog in a
pkgsinfo and the next `makecatalogs` run creates the file.

Two details follow from that:

- A catalog with no items left in it is **deleted**. `makecatalogs` removes any
  `catalogs\*.yaml` that the current pkgsinfo set does not produce.
- A typo makes a new catalog rather than an error. `catalogs: [Produciton]` publishes a
  `Produciton.yaml` that no manifest reads, and quietly removes the item from `Production`.

An `All` catalog is always generated and always contains every item, whether or not the
pkgsinfo names any catalogs at all. It exists as a repo-wide index — `cimiimport` reads it to
offer an existing item as a template — and it is a poor choice for a manifest, because it
hands a device the newest version of every package in the repo regardless of promotion state.

Blank and whitespace-only entries in a `catalogs:` list are skipped. Catalog names are grouped
case-insensitively, but the file is written under the first spelling encountered, and clients
request the exact spelling their manifest asks for. Pick one capitalisation and keep to it.

## How a client selects catalogs

A manifest declares which catalogs it uses:

```yaml
name: WORKSTATION-01
catalogs:
  - Production
managed_installs:
  - ExampleApp
```

Catalog lists accumulate as a de-duplicated union across the whole manifest tree. A parent's
`catalogs:` is merged before its `included_manifests` are walked, so a parent's catalogs are
already in scope while the children are read, and a child can only add to the set — it cannot
narrow or replace it. If the entire tree names no catalogs at all, the client falls back to a
single catalog named `Production`.

Every catalog in the accumulated set is downloaded from `{SoftwareRepoURL}/catalogs/<name>.yaml`
and cached at `C:\ProgramData\ManagedInstalls\catalogs\<name>.yaml`. If a download fails for any
reason — 404, auth failure, network error — the client logs a warning and uses the cached copy
from the last successful run. That is deliberate resilience, and it is also why a catalog you
accidentally deleted from the repo can appear to keep working on machines that already have it.

## When the same item is in two catalogs

All the selected catalogs are merged into one flat lookup keyed by the item's `name`, matched
case-insensitively. Two things happen in order:

1. **Architecture filtering.** An item whose `supported_architectures` does not include the
   device's architecture is dropped as the catalog is read, before any comparison.
2. **Highest version wins.** For a name that appears more than once, the client keeps the entry
   with the greatest `version` and discards the rest.

**Catalog order establishes no precedence.** The order in which catalogs are listed in a
manifest is the order they are downloaded, and nothing more. An item at version 2.0 in
`Testing` beats the same item at 1.0 in `Production` for any device that reads both catalogs.
This is the single most important thing to internalise: a device is only on Production
software because Production is the *only* catalog its manifest names, not because Production
outranks anything.

When two catalogs carry the same name at the *same* version, the entry loaded first is kept and
the later one is discarded. Since the load order is just the accumulated manifest order, which
body a device ends up using is not something you can read off the repo. Treat a duplicated
name+version across catalogs as a repo defect, not a configuration technique — see
[Promoting Between Catalogs](Promoting-Between-Catalogs).

Version comparison is not a plain string compare, and it returns "equal" for anything it cannot
parse. See [Version Comparisons](Version-Comparisons).

## A development, testing, production model

The convention is three catalogs and manifests that name exactly one of them:

- **Development** — where a newly imported item lands. Read only by the machine that imported
  it, or by a handful of admin workstations.
- **Testing** — a pilot group. Small, real, and representative of the fleet.
- **Production** — everything else.

`makepkginfo` defaults `--catalogs` to `Development`, and `cimiimport` defaults it to the
`DefaultCatalog` value in the admin `Config.yaml`, so an import starts in Development unless
you say otherwise.

### Worked example

Import a new version. It is written with `catalogs: [Development]` and, after
`makecatalogs` runs, exists only in `catalogs\Development.yaml`.

```powershell
cimiimport C:\Downloads\ExampleApp-1.2.0.msi --repo_path C:\CimianRepo
```

Your own workstation's manifest names Development, so it picks the item up on its next run and
you can confirm the install actually works:

```yaml
name: ADMIN-WORKSTATION-01
catalogs:
  - Development
managed_installs:
  - ExampleApp
```

When it installs cleanly, add Testing to the pkgsinfo:

```yaml
catalogs:
  - Development
  - Testing
```

Regenerate, and the pilot machines — whose manifests name Testing — get it on their next run:

```powershell
makecatalogs --repo_path C:\CimianRepo
```

After the pilot holds, add Production the same way. Keeping Development and Testing in the list
alongside Production is normal and harmless; it means the pilot machines stay on the same
version as everyone else instead of falling behind. What matters is that the name is present
in the catalog a device reads.

Because the highest version wins across every catalog a device reads, a pilot machine whose
manifest names both Testing and Production always ends up on whichever version is newer. That
is usually what you want, and it is the reason a pilot group cannot be held back on an older
version simply by leaving it out of Production.

## Catalogs are generated — rebuild them

Nothing you write in `pkgsinfo\` has any effect until `makecatalogs` has run and the new
`catalogs\` files are published.

```powershell
makecatalogs --repo_path C:\CimianRepo
```

`cimiimport` runs `makecatalogs` for you at the end of a successful import. Every other kind of
change — editing a pkgsinfo by hand, changing a `catalogs:` list, deleting a pkgsinfo file — is
yours to rebuild.

The symptom of forgetting is that nothing happens. Clients keep reading the last catalogs you
published, so:

- An edit to an existing item has no effect at all. The client reports the item as installed
  and up to date against the old version, and the logs show no error, because from the client's
  point of view nothing changed.
- A brand-new item that a manifest already lists fails with `Item not found in catalog:
  ExampleApp` in the run log. The manifest entry is real; the catalog has never heard of it.
- A deleted pkgsinfo stays deployed.

There is no staleness check and no warning. If a change did not take, regenerating the catalogs
is the first thing to try.

Regeneration also restamps each item's `loop_fingerprint`, which changes whenever the item's
own content changes. Clients treat a changed fingerprint as a reason to clear that package's
loop suppression, so republishing a genuinely edited pkgsinfo is what releases an item that
install-loop protection has been holding back. See
[Install Loop Prevention](Install-Loop-Prevention).

## What makecatalogs checks

A pkgsinfo that fails to parse fails the whole run: `makecatalogs` reports the offending files
and exits 1, so an automated publish cannot mistake a partial build for a good one. Note that
the catalogs are written *before* that check, so a failed run leaves partial catalogs on disk —
the exit code is a guard, not a rollback. `--tolerate_parse_errors` skips the bad files and
exits 0 instead.

Missing payloads are warnings, not failures: `<file> has missing installer => pkgs/<location>`.
`--skip_payload_check` suppresses the check entirely, and `--hash_check` extends it to compare
recorded sizes and hashes.

`makecatalogs` does **not** validate anything else. A pkgsinfo with no `name`, no `version`, a
misspelled key, a malformed `installs` entry or a nonsense architecture string parses fine and
is published. Deserialization is the only schema gate, and unknown keys are silently dropped
rather than rejected.

## See also

- [Promoting Between Catalogs](Promoting-Between-Catalogs)
- [The Cimian Repository](The-Cimian-Repository)
- [Manifests](Manifests)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Version Comparisons](Version-Comparisons)
- [makecatalogs](makecatalogs)
- [cimiimport](cimiimport)
- [Cimian With Git](Cimian-With-Git)
