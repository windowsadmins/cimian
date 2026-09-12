# Manifests

A manifest is the per-client list of what should be installed, updated, removed or
offered on a device. This page is the complete key reference for a manifest file,
explains how included manifests are resolved and how competing entries for the same
item are settled, and ends with a recommended repository layout you can copy.

Manifests are served from `<SoftwareRepoURL>/manifests/<name>.yaml` and cached on the
device under `%ProgramData%\ManagedInstalls\manifests\`. Which manifest a given device
asks for first is a separate subject — see
[Client-Identifier-Resolution](Client-Identifier-Resolution).

## What a manifest is

A manifest names packages; it does not describe them. The description of a package —
its version, installer, detection checks and scripts — lives in a
[pkgsinfo](Introduction-To-pkgsinfo-Files) file and reaches the client through a
[catalog](Using-Catalogs). A manifest only says which names apply to this device and
what should happen to them.

Every name in a manifest must resolve to an item in one of the catalogs the manifest
tree brings into scope. A name with no catalog match produces no action.

A minimal manifest:

```yaml
name: WS-0001
catalogs:
  - Production
managed_installs:
  - ExampleApp
  - ExampleSecurityAgent
```

## Key reference

These are the keys the client parses. Anything else in the file is silently ignored —
a misspelled key is not an error, it simply has no effect.

| Key | Type | Default | Behaviour |
|---|---|---|---|
| `name` | string | `""` | Parsed but unused. Item provenance is tracked by the file name, not this field. Keep it in step with the file name for readability only. |
| `catalogs` | list of strings | `[]` | Catalog names to search. Accumulated as a de-duplicated union across the whole manifest tree, and merged **before** the manifest's includes are walked, so a parent's catalogs are in scope for its children. If the whole tree contributes none, the client falls back to the single catalog `Production`. |
| `included_manifests` | list of strings | `[]` | Other manifests to process as part of this one. See [Included manifests](#included-manifests). |
| `managed_installs` | list of strings | `[]` | Install and keep installed. The item is re-enforced every run. |
| `managed_uninstalls` | list of strings | `[]` | Remove, and keep removed. Only acts on items that are actually removable. |
| `managed_updates` | list of strings | `[]` | Patch if present. Never installs an item that is absent — it only upgrades one that is already there. |
| `optional_installs` | list of strings | `[]` | Offer in Managed Software Center. Nothing is installed until a user asks for it. |
| `default_installs` | list of strings | `[]` | Install once, if not already installed. After the first successful install the item is not re-enforced and drops off every list — a user may remove it and it will not come back. |
| `featured_items` | list of strings | `[]` | Presentational only. Collected across the whole manifest tree, de-duplicated case-insensitively, and surfaced to Managed Software Center as the featured set. It queues nothing; an item must also appear in `optional_installs` (or another list) to be actionable. |
| `conditional_items` | list of mappings | `[]` | Item lists that apply only when a condition matches. See [Conditional-Items](Conditional-Items). |
| `managed_profiles` | list of strings | `[]` | Recorded and reported as externally managed. **The client performs no action on these** — they are logged as skipped external items. |
| `managed_apps` | list of strings | `[]` | Same as `managed_profiles`: recorded, reported, never acted on. |

Item lists are lists of package names, matched case-insensitively against the catalog.

A note on tooling: `manifestutil` understands only `name`, `catalogs`,
`included_manifests`, `managed_installs`, `managed_uninstalls`, `managed_updates` and
`optional_installs`. Round-tripping a manifest through it drops `conditional_items`,
`featured_items`, `default_installs`, `managed_profiles` and `managed_apps`. Edit
manifests that use those keys by hand or with your own tooling.

## Included manifests

`included_manifests` is how a manifest composes others. Each entry is a manifest name
relative to the `manifests/` directory of the repo, with any `.yaml` suffix stripped and
backslashes normalised to forward slashes. `roles/design-lab`, `roles/design-lab.yaml`
and `roles\design-lab` all fetch `<repo>/manifests/roles/design-lab.yaml`.

Nesting is unbounded and depth-first: an included manifest may include further
manifests, to any depth.

Two behaviours are worth knowing:

- **Cycles terminate.** A manifest that has already been processed in this run is not
  processed again, so a circular include resolves instead of looping. A manifest that
  failed earlier in the run keeps that failure when it is referenced again — it is not
  reported as successful just because something else included it.
- **A missing include is loud.** A 404 on an `included_manifests` entry is logged as a
  warning, unlike the quiet probing done while resolving the primary manifest. Includes
  are asserted by the admin, so a missing one is a repository error.

Catalogs are merged before includes are walked. That means an included manifest does
not need to restate the catalogs of its parent, and a child adding a catalog widens the
set for everything processed afterwards.

## Action precedence

The same package name can appear in many places: several manifests in the include tree,
a conditional block, and a user's self-service choices. Every occurrence is collapsed to
one action per name, keyed case-insensitively.

Highest rank wins, and the result does **not** depend on the order the manifests were
read:

```
install > uninstall > update > default > optional > profile = app
```

If two occurrences share the same rank, the higher version wins; failing that, the first
occurrence's position in the list is kept. Listing an item in `managed_installs` in one
manifest and `optional_installs` in another therefore always yields an install — you
cannot make an item optional by adding it to `optional_installs` further down the tree.

Because precedence is order-independent, an include tree cannot be used to "override"
a parent with a weaker action. To stop enforcing an item on a subset of machines, remove
it from the shared manifest rather than trying to demote it in a child.

### Self-service can never undo an admin action

A user's choices in Managed Software Center are merged last, from the device-local
self-service manifest. The merge respects admin intent:

- A user request for an item the server does not mention adds an install.
- A user request for an item the server lists as `optional_installs` promotes that entry
  to an install in place.
- A user request to remove an item whose server action mandates presence — `install`,
  `uninstall`, `default`, `profile` or `app` — is logged and ignored.

`optional` and `update` are deliberately not presence-mandating. That is what makes the
common pairing of `optional_installs` plus `managed_updates` work: the admin keeps the
item patched when it is present, and the user stays the authority on whether it is
present at all.

## Recommended layout

Keep per-machine manifests thin. A machine manifest should ideally contain nothing but
its own name and a list of includes; everything substantive lives in a shared manifest
that many machines include. That way a change to a role is one edit, not one edit per
device, and reading a machine manifest tells you what the machine *is* rather than what
it has.

A layout that scales:

```
manifests/
  site_default.yaml
  Orphaned.yaml
  roles/
    base.yaml
    workstation.yaml
    design-lab.yaml
  WS-0001.yaml
  LAB-0007.yaml
  STUDIO-0003.yaml
```

`roles/base.yaml` carries what every managed device gets, and owns the catalog list so
nothing further down has to repeat it:

```yaml
name: base
catalogs:
  - Production
managed_installs:
  - ExampleSecurityAgent
  - ExampleInventoryAgent
managed_updates:
  - ExampleBrowser
```

`roles/workstation.yaml` builds on it and adds the self-service catalogue for staff
machines:

```yaml
name: workstation
included_manifests:
  - roles/base
managed_installs:
  - ExampleOfficeSuite
optional_installs:
  - ExampleDiagramTool
  - ExampleArchiveUtility
featured_items:
  - ExampleDiagramTool
```

`roles/design-lab.yaml` is a sibling role for shared teaching machines. It includes the
same base, adds its own titles, and uses a conditional to keep a large driver package off
machines that cannot use it:

```yaml
name: design-lab
included_manifests:
  - roles/base
managed_installs:
  - ExampleImageEditor
  - ExampleVectorEditor
default_installs:
  - ExampleFontManager
conditional_items:
  - condition: ram_total_gb >= 32 AND ANY gpu_vendors CONTAINS "NVIDIA"
    managed_installs:
      - ExampleRenderPlugin
```

A per-machine manifest is then two lines of intent:

```yaml
name: LAB-0007
included_manifests:
  - roles/design-lab
```

And a machine that is a workstation with one extra title:

```yaml
name: WS-0001
included_manifests:
  - roles/workstation
managed_installs:
  - ExampleStatisticsPackage
```

`site_default.yaml` is the last-resort manifest for a device whose own manifest does not
exist. Keep it conservative — it is what an unrecognised device gets:

```yaml
name: site_default
included_manifests:
  - roles/base
```

Two habits keep this layout healthy. Put catalogs in exactly one place — the base
manifest — so no device can end up with a catalog set nobody intended. And never let a
per-machine manifest grow item lists that two or more machines share; the moment a second
machine needs the same list, it is a role.

## See also

- [Client-Identifier-Resolution](Client-Identifier-Resolution)
- [Conditional-Items](Conditional-Items)
- [Conditional-Facts-Reference](Conditional-Facts-Reference)
- [Using-Catalogs](Using-Catalogs)
- [The-Cimian-Repository](The-Cimian-Repository)
- [manifestutil](manifestutil)
- [Optional-Installs-And-Self-Service](Optional-Installs-And-Self-Service)
- [Featured-Items](Featured-Items)
- [Item-Status-Reference](Item-Status-Reference)
