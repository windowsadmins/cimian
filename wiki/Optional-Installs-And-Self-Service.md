# Optional Installs And Self Service

`optional_installs` is how you offer software to a user without imposing it. An item in that
list appears in [Managed Software Center](Managed-Software-Center) with an Install button and
does nothing until the user asks for it. This page covers the whole path — manifest key,
where the user's choice is recorded, how the client acts on it, how removal works, and the
rule that a user's choice can never override an administrator's.

## Offering an item

`optional_installs` is a manifest key, a list of item names, exactly like `managed_installs`:

```yaml
name: WORKSTATION-01
catalogs:
  - Production
managed_installs:
  - CompanyBaseline
optional_installs:
  - ExampleApp
  - ExampleUtility
```

Each name must resolve in one of the catalogs the manifest brings into scope, or the item
cannot be offered. Optional items may be listed in any manifest in the include tree — a
shared "optional software" manifest included by every device manifest is the usual shape:

```yaml
name: Optional-Software
optional_installs:
  - ExampleApp
  - ExampleUtility
  - ExampleDesignSuite
```

```yaml
name: WORKSTATION-01
catalogs:
  - Production
included_manifests:
  - Optional-Software
managed_installs:
  - CompanyBaseline
```

The pkgsinfo needs nothing special to be offered. It does need enough metadata to look
presentable — `display_name`, `description`, `category`, `developer`, `icon_name` — because
a user is choosing from it rather than receiving it silently.

## What a user's choice does

Every run, the client resolves the manifest tree and then merges the user's own selections
from:

```
C:\ProgramData\ManagedInstalls\SelfServeManifest.yaml
```

That file is written by Managed Software Center when the user clicks Install, Remove or
Cancel. It is a small manifest of its own:

```yaml
name: SelfServeManifest
managed_installs:
  - ExampleApp
managed_uninstalls:
  - ExampleUtility
```

An install request behaves as follows:

- If the name matches an item the server offered as optional, that entry is **promoted in
  place** to an install for this run and every run after it.
- If no manifest in the tree mentions the name at all, an install item is added outright.
- If the name is only under `managed_updates`, the request is left alone as stale state —
  MSC never offers such an item.

Requests persist. `SelfServeManifest.yaml` is not consumed after a successful install; the
entry stays until the user cancels it in **My Items**. That makes a user's install a standing
subscription: once the software is present, ordinary status checks find nothing to do, but if
the item is later removed out from under Cimian it comes back on the next run, and new
versions are picked up like any managed install.

The client writes its view of every optional item into `InstallInfo.yaml` on each check, with
live `installed`, `needs_update` and status values, which is what MSC renders.

## Self-service versus managed install

The install itself is identical — same catalog item, same installer, same run, same logging,
performed by `managedsoftwareupdate` as SYSTEM. What differs is where the intent comes from
and who can withdraw it.

| | Managed install | Self-service install |
|---|---|---|
| Listed under | `managed_installs` | `optional_installs`, then requested by the user |
| Recorded in | The server manifest | `SelfServeManifest.yaml` on the device |
| Visible in MSC | Only while work is outstanding, on Updates | Browseable on Software, with Install/Remove |
| User can decline | No | Yes — it is never installed unless requested |
| User can remove | No | Yes, if the item is removable |
| Enforced when missing | Yes, every run | Yes, while the request stands |

A user's request does not go through a queue or an approval step, and nothing about it is
sent back to the repository. It is a per-device file.

Managed Software Center triggers a targeted run for the item as soon as the user clicks, so
the work usually begins within seconds rather than waiting for the next scheduled check.

## Removal

A user removing a self-service item adds the name to `managed_uninstalls` in
`SelfServeManifest.yaml`. On the next run the optional entry is flipped to an uninstall and
the client removes the software.

Removal only works if the item is actually removable. The Remove button appears when the
catalog item is uninstallable, which means `uninstallable` is not set to `false` **and** the
client has a way to remove it: an `uninstaller` block, an MSI (declared `installer.type: msi`
or an `installs` entry with a product code), an EXE installer that registered its own
uninstall entry, an `uninstall_script`, or an MSIX/APPX `installs` entry with an identity
name. Set `uninstallable: false` in the pkgsinfo to offer an item that the user may install
but must not remove.

Unlike install requests, removal requests **are** consumed. After a successful removal the
client drops the name from `SelfServeManifest.yaml` so it is not re-queued every run. A
failed removal is kept and retried on the next run. The item stays on the Software page,
back in its "not installed" state, so the user can install it again.

## Precedence: administrator intent always wins

The merge classifies server-side actions as *presence-mandating* — `managed_installs`,
`managed_uninstalls`, `default_installs`, `managed_profiles`, `managed_apps` — because each
of those states that the item must be present or must be absent.

- A user removal request for an item any manifest mandates is **logged and ignored**. The run
  prints which action and which manifest blocked it.
- A user install request for an item already mandated is left alone; the managed install
  governs it.
- `optional_installs` and `managed_updates` are deliberately *not* presence-mandating.
  `optional_installs` merely offers the item, and `managed_updates` only means "patch it if
  it is present", so for the common pairing of the two the user remains the authority on
  whether the software is there at all.

Matching is done across every entry for the name, not just the first, so an item that is
optional in one manifest and managed in another is governed by the managed entry.

This is the pairing to use when you want a user to opt in but want the version kept current
once they have:

```yaml
name: Optional-Software
optional_installs:
  - ExampleApp
managed_updates:
  - ExampleApp
```

The user chooses whether to have Example App. Once installed, updates are applied
automatically, and the user can still remove it.

## Turning self-service off

Set `SkipSelfService: true` in `C:\ProgramData\ManagedInstalls\Config.yaml` and the client
skips the merge entirely — `SelfServeManifest.yaml` is never read and never cleaned up. Users
can still click in MSC and their choices are still recorded, but nothing acts on them.
`optional_installs` items continue to be listed in `InstallInfo.yaml`, so they stay
browseable. If you want the items gone from the UI, remove them from the manifest instead.

Confirm the effective setting with:

```
managedsoftwareupdate --show-config
```

## Worked example

The repository holds a pkgsinfo for a removable MSI:

```yaml
name: ExampleApp
display_name: Example App
version: 3.2.1
description: Example App is offered to anyone who wants it and updates itself once installed.
category: Productivity
developer: Example Software Ltd
icon_name: ExampleApp.png
catalogs:
  - Production
installer:
  location: apps/ExampleApp-3.2.1.msi
  type: msi
  size: 84213760
installs:
  - type: msi
    product_code: "{2C6B1A3E-9E1B-4C4E-9F0F-6C1E8D2A77B1}"
unattended_install: true
unattended_uninstall: true
uninstallable: true
```

The device manifest offers it and keeps it patched:

```yaml
name: WORKSTATION-01
catalogs:
  - Production
managed_installs:
  - CompanyBaseline
optional_installs:
  - ExampleApp
managed_updates:
  - ExampleApp
```

The user clicks Install in MSC. The device's `SelfServeManifest.yaml` becomes:

```yaml
name: SelfServeManifest
managed_installs:
  - ExampleApp
```

The next run promotes the optional entry to an install, downloads and installs 3.2.1, and
writes the result to `InstallInfo.yaml`. Later, the user clicks Remove; the name moves to
`managed_uninstalls`, the following run uninstalls the MSI and clears the entry, and Example
App is back on the Software page as an offer.

If an administrator later moves `ExampleApp` into `managed_installs`, any standing removal
request for it is ignored from that point on and the software is reinstalled.

## Limitations

- Self-service choices are per device, stored in a local file. Reimaging a machine loses
  them; nothing syncs them to the repository or to a user's other machines.
- There is no approval workflow and no licence-count enforcement. Anything you list in
  `optional_installs` is available to every user of every device that manifest reaches.
- An install request is never cleared automatically, so a user who installs an item once
  keeps receiving its updates until they cancel the request in **My Items**.
- `manifestutil` understands `optional_installs`, but round-tripping a manifest through it
  drops `featured_items`, `default_installs`, `conditional_items`, `managed_profiles` and
  `managed_apps`.

## See also

- [Managed Software Center](Managed-Software-Center)
- [Featured Items](Featured-Items)
- [Manifests](Manifests)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Uninstalling Software](Uninstalling-Software)
- [Client Configuration](Client-Configuration)
- [Item Status Reference](Item-Status-Reference)
