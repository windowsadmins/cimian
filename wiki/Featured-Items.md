# Featured Items

Featuring promotes an item to the top of the Software page in
[Managed Software Center](Managed-Software-Center). It is presentation only: featuring an item
never installs it, never queues it, and never changes what the client does.

## How an item becomes featured

Featuring is driven by one key, and it is a **manifest** key:

```yaml
name: Optional-Software
optional_installs:
  - ExampleApp
  - ExampleUtility
  - ExampleDesignSuite
featured_items:
  - ExampleApp
```

There is no `featured` key in pkgsinfo. Setting `featured: true` on a package does nothing —
none of the tools that write or read a pkgsinfo have such a property, so it is silently
dropped somewhere between authoring and the catalog and never reaches a device. If you have
inherited pkgsinfo files carrying it, they are inert; move the names into a manifest's
`featured_items`.

`featured_items` is collected from **every** manifest the client walks in a run, not just the
primary one, and de-duplicated case-insensitively. So a shared manifest that is included by
many device manifests can carry the featured list for a whole fleet:

```yaml
name: Featured-This-Term
featured_items:
  - ExampleApp
  - ExampleDesignSuite
```

```yaml
name: WORKSTATION-01
catalogs:
  - Production
included_manifests:
  - Optional-Software
  - Featured-This-Term
managed_installs:
  - CompanyBaseline
```

The accumulated list is written into `InstallInfo.yaml` on each check, and MSC reads it from
there.

## What changes in the UI

A featured item appears twice on the Software page: once in a **Featured** grid above the
main list, and once in the "All apps" grid below it. The Featured grid is hidden entirely
when nothing is featured. The **Featured** promo tile beside the hero banner filters the page
to the featured set.

Tiles in the Featured grid are ordinary item tiles — same icon, name and Install/Remove
button. Nothing about the item's status, ordering elsewhere, or install behaviour changes.

## The rule that catches people out

MSC matches each name in `featured_items` against the items it can browse, and the browseable
list is built from `optional_installs` only. A name that is not offered as an optional install
on that device matches nothing and is silently skipped.

So featuring a mandated item does nothing visible: `managed_installs` items are not
browseable, so there is no tile to promote. To feature an item, list it in both places:

```yaml
name: Optional-Software
optional_installs:
  - ExampleApp
featured_items:
  - ExampleApp
```

The same applies to a name with a typo, a name that does not resolve in any catalog the
manifest brings into scope, and a name that is only reachable through a conditional that did
not match. In every case the entry is simply absent from the Featured grid, with no error.

## Using it well

Feature a small number of items. The Featured grid sits above everything else on the page and
loses its meaning at a dozen entries.

Keep the featured list in its own manifest, included where you want it. Because
`featured_items` accumulates across the whole include tree, a device inherits the union of
every list it touches, which makes a single dedicated manifest much easier to reason about
than featured lists scattered through department manifests.

Feature things that are new, seasonal, or being rolled out — the point is to answer "what
should I look at this term" for a user who does not know what to search for.

Do not use featuring to signal urgency. It carries no deadline and no notification; an item
that genuinely must be installed belongs in `managed_installs`, optionally with
`force_install_after_date`.

Verify from the client rather than from the manifest. After a check, `InstallInfo.yaml`
carries the resolved `featured_items` list for that device:

```
managedsoftwareupdate --checkonly
```

## Limitations

- Featuring works only for items offered as `optional_installs` on that device.
- A pkgsinfo-level `featured` key does not exist and is ignored.
- The list has no ordering guarantee that is worth relying on and no per-item weighting.
- `manifestutil` does not know the `featured_items` key: round-tripping a manifest through it
  drops the list.
- Featuring has no effect anywhere except the Software page — not on Categories, My Items,
  Updates, or notifications.

## See also

- [Managed Software Center](Managed-Software-Center)
- [Optional Installs And Self Service](Optional-Installs-And-Self-Service)
- [Manifests](Manifests)
- [Product Icons And Screenshots](Product-Icons-And-Screenshots)
- [Force Installs And Deadlines](Force-Installs-And-Deadlines)
