# Dependencies And Update Chains

Cimian has two relationship keys between packages: `requires`, which pulls a dependency in before
an item, and `update_for`, which attaches a patch to a base product. This page covers how each is
resolved, the order things actually happen in, how cycles behave, and the name-parsing trap that
makes some dependencies silently unresolvable.

## `supersedes` is not implemented

**`supersedes` is a [cimipkg](cimipkg) `build-info.yaml` key, not a pkgsinfo key.** In cimipkg it
is a list of legacy MSI UpgradeCode GUIDs used at MSI build time. The client has no `supersedes`
field on its catalog item, and there is no supersession resolution anywhere in
`managedsoftwareupdate`.

Putting `supersedes:` in a pkgsinfo does nothing. It does not retire an old package, it does not
suppress an older version, and it does not chain upgrades. Unknown keys are tolerated silently, so
you get no warning — the key is simply dropped on its way to the device. **Do not rely on it for
any pkgsinfo behaviour.** To retire a title, move it into `managed_uninstalls` as described in
[Uninstalling Software](Uninstalling-Software).

## `requires`

```yaml
name: ExampleVendorSuite
version: 3.2.1
requires:
- ExampleVendorRuntime
- ExampleVendorFonts
```

`requires` names other packages that must be installed before this one. Names are matched
case-insensitively against the catalog.

A dependency is only installed if **its own status check says it needs to be**. Being named in
`requires` does not force a reinstall; the client runs the dependency's normal detection (its
`installs[]` array, `installcheck_script` or receipt) and skips it when it is already satisfied.

### Version suffixes are parsed off, and mostly ignored

An entry may carry a version suffix in either `Name-1.2.3` or `Name--1.2.3` form. The client
splits it and, at the closure stage, **discards the version entirely** — resolution is by name.
At install time a declared version is compared only as an exact string against another entry's
suffix, and any mismatch in shape is resolved in favour of "satisfied". Treat `requires` as
name-only. If you need a minimum version of a dependency, express it in that dependency's own
detection, not in the requirer's `requires` line.

### The name-splitting trap

The split looks for the **last hyphen followed by a digit**. A package whose real name ends that
way is silently mangled:

| `requires` entry | Parsed as |
|---|---|
| `ExampleRuntime` | name `ExampleRuntime` |
| `ExampleRuntime-2.0` | name `ExampleRuntime`, version `2.0` |
| `ExampleSuite-3` | name `ExampleSuite`, version `3` |
| `Office-365` | name `Office`, version `365` |

The last two are almost certainly not what the author meant. A package literally named
`ExampleSuite-3` can never be resolved through `requires`, because the client will look for
`ExampleSuite`. **Do not put a hyphen followed by a digit in a package `name`** if it will ever be
a dependency.

### A missing dependency behaves differently in each phase

- During classification, a `requires` name that is not in any loaded catalog is **skipped
  silently**.
- During installation, a `requires` name that is not in the catalog is a **hard failure**: the
  parent logs `Required dependency not found in catalog: <name>` and is not installed.

So a typo in `requires` produces no warning during a `--checkonly` run and a failed install during
a real one.

## `update_for`

```yaml
name: ExampleVendorSuitePatch
version: 3.2.1.4
update_for:
- ExampleVendorSuite
```

`update_for` declares that this item is an update **to** the named items. The relationship is
inverted relative to `requires`: the patch names the base product, and the base product knows
nothing about it.

Whenever the base product is installed or updated, every catalog item declaring `update_for` on it
is pulled in, status-checked and installed after the base product succeeds. A patch that fails is
a warning and does **not** fail the base product.

`update_for` is resolved by scanning the whole catalog, so the patch does not need to appear in
any manifest. That is the point: put the base product in the manifest and let the patch follow.

## Resolution order

Two separate mechanisms are involved, and they run at different times.

### 1. Classification-phase closure

Once per run, after status checking and before any deferral filtering, the client expands a
dependency closure. It seeds from **every manifest item whose action is `install` or `update`** —
manifest intent, not current install state — and walks outward in **both** directions:

- forward along `requires` (an item to its declared dependencies);
- backward along `update_for` (an item to the patches that declare it).

Each name is visited once. Names not present in the catalog are skipped. The seeds themselves are
excluded from the result. A closure member that the manifest already claims with a conflicting
explicit action — `uninstall`, `profile` or `app` — is skipped, because explicit removal and MDM
intent beat a transitive install. Everything else is status-checked and, if it needs action, added
to the install queue with its source recorded as `dependency`.

This is what makes a dependency appear in `--checkonly` output even though no manifest names it.

### 2. Install-time recursive walk

During the actual install of each item, a second, recursive algorithm runs:

1. Architecture, OS-version and agent-version eligibility gates. A skip here is not an error.
2. **`requires` first.** Missing dependencies are downloaded and installed by a recursive call to
   the same routine. **A failed dependency aborts the parent** — the parent is not installed.
3. The item itself: blocking-application recheck, payload presence check, install, then the
   convergence probe.
4. **`update_for` after.** Each catalog item declaring `update_for` on this one is status-checked,
   downloaded and installed the same way. **A failed patch is a warning only** and does not fail
   the parent.

So the ordering guarantee is: **dependencies before the item; updates after the item succeeds.**

There is no ordering guarantee *between* two entries in the same `requires` list beyond the order
you wrote them in, and none at all between unrelated items.

### Removal order is the mirror image

When an item is removed, every catalog item whose `requires` names it is removed **first**,
recursively. If a dependent fails to remove, the parent removal is abandoned. See
[Uninstalling Software](Uninstalling-Software).

## Cycles

Cycle handling is structural rather than special-cased. The classification closure keeps a visited
set seeded with the seed names and enqueues a node only when it has not been seen, so a `requires`
cycle terminates rather than looping. No error is reported; the graph is simply walked once.

The install-time recursive walk has **no visited-set guard at its recursion point**. A live cycle
reaching that path would recurse without bound. In practice the closure that feeds it is bounded
and no test exercises a cycle through the install path, but the guard is not there. Do not create
mutual `requires` relationships.

## Worked examples

### A runtime shared by several products

The runtime is published as an ordinary package and is not named in any manifest:

```yaml
name: ExampleVendorRuntime
display_name: Example Vendor Runtime
version: 8.4.0
catalogs:
- Production
installer:
  type: msi
  location: apps/example-vendor/ExampleVendorRuntime-8.4.0.msi
installs:
- type: msi
  product_code: '{2C1B4E90-3F5A-4E01-9E1B-8A1C0D5F7A22}'
  version: 8.4.0
unattended_install: true
```

Each consumer declares it:

```yaml
name: ExampleVendorSuite
display_name: Example Vendor Suite
version: 3.2.1
catalogs:
- Production
requires:
- ExampleVendorRuntime
installer:
  type: msi
  location: apps/example-vendor/ExampleVendorSuite-3.2.1.msi
installs:
- type: msi
  product_code: '{9D3F1A77-4C22-4B0E-B6D1-3E7F2A9C1140}'
  version: 3.2.1
unattended_install: true
```

The manifest lists only `ExampleVendorSuite`. On a device without the runtime, the closure adds
`ExampleVendorRuntime`, and the install-time walk installs it first. On a device that already has
it, its status check reports no action and the install is skipped.

### A patch that follows its base product

```yaml
name: ExampleVendorSuiteHotfix
display_name: Example Vendor Suite Hotfix
version: 3.2.1.4
catalogs:
- Production
update_for:
- ExampleVendorSuite
installer:
  type: exe
  location: apps/example-vendor/ExampleVendorSuiteHotfix-3.2.1.4.exe
  switches:
  - quiet
  - norestart
installs:
- type: file
  path: 'C:\Program Files\Example Vendor\Suite\Suite.exe'
  version: 3.2.1.4
unattended_install: true
```

The manifest still lists only `ExampleVendorSuite`. Whenever the suite is installed or updated,
the hotfix is evaluated and applied afterwards. If the hotfix fails, the suite install still
counts as successful and the hotfix is retried next run.

### What not to write

```yaml
requires:
- ExampleVendorRuntime-8.4.0
```

The version is parsed off and discarded during closure building, so this behaves as
`ExampleVendorRuntime` while looking like a version pin. Write the bare name and put the version
requirement in the runtime's own detection.

```yaml
supersedes:
- '{A1B2C3D4-0000-0000-0000-000000000000}'
```

Ignored entirely by the client. See the top of this page.

## See also

- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Manifests](Manifests)
- [Uninstalling Software](Uninstalling-Software)
- [Using Catalogs](Using-Catalogs)
- [Installer Types](Installer-Types)
- [cimipkg](cimipkg)
