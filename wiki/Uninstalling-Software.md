# Uninstalling Software

Removal in Cimian is weaker and less uniform than installation, and it fails quietly in several
places. This page covers how an item gets queued for removal, everything that decides whether the
client considers it removable at all, how the removal mechanism is chosen, what
`managed_uninstalls` can and cannot undo, and how to diagnose an item that refuses to go away.

## How an item gets queued for removal

Four paths put an item on the removal list:

- **`managed_uninstalls` in a manifest.** The normal, explicit route. See [Manifests](Manifests).
- **A conditional item's `managed_uninstalls`.** Same semantics, evaluated per device.
- **A user's removal request in Managed Software Center**, merged in from the self-serve
  manifest.
- **Automatic removal**, when enabled in client configuration. Anything with a `ManagedInstalls`
  receipt that is no longer named by any manifest, is still present in a loaded catalog, and is
  removable gets queued.
- **Stale-usage removal**, when an item declares `unused_software_removal_info` and
  `unattended_uninstall: true` and the device's usage data shows nobody has run it inside the
  threshold.

All four then go through the same removal machinery.

### Manifest precedence beats intent

Manifest items are deduplicated by name across the whole include tree, and the action ranking is
fixed:

```
install > uninstall > update > default > optional > profile = app
```

**If any manifest in the tree lists the item in `managed_installs`, it will never be uninstalled**,
however many manifests list it in `managed_uninstalls`. This is the single most common reason a
removal appears to do nothing. Remove it from the install list first.

A self-serve removal request is also discarded — logged, not honoured — when the server side
carries a presence-mandating action for that name (`install`, `uninstall`, `default`, `profile` or
`app`). Admin policy always wins over a user's Remove button.

`minimum_os_version` and `maximum_os_version` gate installs and updates but are **deliberately
exempt for uninstalls**, so an item that has become unsupported on a device can still be removed
from it.

## What makes an item removable

Before anything else happens, the client asks whether the item is uninstallable at all. It is
removable when `uninstallable` is not false **and at least one** of the following is true:

- an `uninstaller:` entry is declared;
- `check.registry.name` is set;
- `installer.type` is `exe`;
- `installer.type` is `msi`;
- an `installs[]` entry of type `msi` carries a non-empty `product_code`;
- `uninstall_script` is non-blank;
- an `installs[]` entry of type `msix` or `appx` carries a non-empty `identity_name`.

`uninstallable` defaults to **true**, so most items are removable without saying anything. Setting
`uninstallable: false` **overrides every clause above** — a package with a fully declared
`uninstaller:` block and `uninstallable: false` is not removable, full stop.

Two consequences worth stating plainly:

- **Any MSI is removable with or without a declared ProductCode.** The client can recover the GUID
  from the Windows uninstall registry. You do not have to restate a GUID the system already knows.
- **A script-only package (`nopkg`, `script`, `ps1`) is removable only if it declares
  `uninstall_script`.** Without one there is no mechanism and the item is not removable.
- **An MSIX/APPX `installs[]` entry without `identity_name` does not count.** The client cannot
  synthesize a removal from it.

### The silent drop

When an item is listed in `managed_uninstalls` but is not removable, **it is discarded with no
error, no warning and no status line**. It does not appear as failed; it simply never shows up in
the removal table. If a removal seems to be ignored entirely, check removability first.

Automatic removal is better behaved: it logs `AutoRemove: skipping <name> (not uninstallable)` and
`AutoRemove: skipping <name> (not in catalog)`.

### `check.registry.name` is a trap

`check.registry.name` makes an item pass the removability test, but there is **no removal path
that it enables**. The mechanism selection below only falls back to the registry uninstall entry
when `installer.type` is `exe` or `msi`. An item that is removable *only* because it sets
`check.registry.name` reaches the uninstaller and fails with `No uninstaller defined`. Give such
an item a real `uninstall_script` or an `uninstaller:` block.

## How the removal mechanism is chosen

Resolved in this order; the first match wins.

**1. A declared `uninstaller:` entry.** `uninstaller` is a **list**; only the first entry is used.
Dispatch is on its `type`:

| `uninstaller[0].type` | What runs |
|---|---|
| `msi` | `msiexec.exe /x <product_code> /qn /norestart` plus the entry's switches, flags and args |
| `exe` | the entry's `command`, with its composed arguments |
| `powershell` / `ps1` | the entry's `command`, executed as PowerShell script text |
| `msix` / `appx` | provisioned and per-user package removal by identity |
| anything else | falls through to the `msi` handler |

An `msi` uninstaller entry with no `product_code` fails. An `exe` entry with no `command` fails.

**2. `uninstall_script`**, when no `uninstaller:` block is declared. Run as inline PowerShell; a
non-zero exit fails the removal. See [Scripts In pkgsinfo](Scripts-In-pkgsinfo).

**3. A synthesized uninstaller**, in this order:

- an MSI ProductCode taken from an `installs[]` entry of type `msi`, falling back to the legacy
  `installer.product_code`;
- otherwise an MSIX/APPX identity taken from an `installs[]` entry of that type;
- otherwise, **only when `installer.type` is `exe` or `msi`**, the product's own uninstaller from
  the Windows uninstall registry.

The registry fallback resolves the Add/Remove Programs entry by `display_name` then `name`, exact
match first and then substring, across both the 64-bit and 32-bit views. It prefers
`QuietUninstallString` over `UninstallString`. An `msiexec` string is normalised — `/I` is
rewritten to `/X` and `/qn /norestart` is forced. For a non-quiet string it appends the switches
from `uninstaller[0]` if present, otherwise it **guesses**: an uninstaller named `unins###.exe`
(Inno Setup) gets `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, and anything else gets `/S` on the
assumption that it is NSIS. That guess is wrong for some vendors and there is no way to tell in
advance; declare an `uninstaller:` block when you know the switches.

MSI removal treats exit code **1605** (unknown product) and **1614** (product already uninstalled)
as success — the product is already gone, which is the desired end state.

## What runs around the removal

1. `preuninstall_script`, if present. A non-zero exit aborts the removal.
2. Dependents first: any catalog item whose `requires` names this item is removed before it,
   recursively, and if a dependent fails to remove, the parent removal is abandoned.
3. `blocking_applications` are rechecked immediately before the removal. A running blocker skips
   it for this run. See [Blocking Applications](Blocking-Applications).
4. The removal itself.
5. `postuninstall_script`, **only if the removal succeeded**. A non-zero exit is a warning only.
6. The item's `ManagedInstalls` receipt is deleted.
7. If the removal came from a user request, the name is dropped from the self-serve manifest so it
   is not re-queued every run.

`restart_action` applies to removals as well as installs. In an `--auto` run with an active user,
an item is only removed when `unattended_uninstall: true` **and** its `restart_action` would not
reboot or log the user out; otherwise it is deferred and reported as Pending Removal.

## What `managed_uninstalls` can and cannot undo

**It can:** run the product's own uninstaller, `msiexec /x` an MSI, remove a provisioned MSIX for
the device and per-user copies, run your `uninstall_script`, and delete Cimian's own receipt so
the item stops being reported as installed.

**It cannot:**

- Undo anything a `postinstall_script` did. Registry values, scheduled tasks, firewall rules,
  files written outside the installer's own payload, service registrations and per-user
  configuration all survive unless you write a `postuninstall_script` or `uninstall_script` that
  removes them. Vendor uninstallers routinely leave configuration behind by design.
- Reverse a `default_installs` entry meaningfully — a default install is a one-shot, so removing
  it is honoured, but nothing prevents a later re-install if the item moves back onto an install
  list.
- Beat an `install` action elsewhere in the manifest tree. See the precedence ladder above.
- Undo something installed by a different mechanism unless that mechanism left an Add/Remove
  Programs entry the registry fallback can find, and the item's `installer.type` is `exe` or
  `msi`.
- Remove an item that is no longer in any catalog the device loads. See below.
- Guarantee that removal has *happened*. The outcome is the uninstaller's exit code, not a
  re-check. There is no post-removal verification pass.

## When the original payload is gone

Removals **never download the installer payload**. The client drives them from the ProductCode,
the ARP entry, the MSIX identity or your script, all of which live on the device. Deleting an old
installer from `pkgs/` therefore does not break removal.

What does break removal is deleting the **pkgsinfo**. The item is looked up in the loaded catalog
by name; if it is not there, the removal logs `Item not found in catalog: <name>` and fails. To
retire a title, keep its pkgsinfo published in a catalog the device loads and move the name into
`managed_uninstalls`. Only remove the pkgsinfo once every device has reported the removal.

An `uninstaller:` entry of type `exe` whose `command` points at a path under the product's own
install directory is also fragile: if a partial or corrupted install left that file missing, the
removal fails with nothing to fall back on.

## Diagnostics: "the item will not uninstall"

Work down this list.

**1. Is anything else claiming it?** Search the whole manifest include tree for the name. An
`install` in any manifest beats an `uninstall` in every manifest. Also check conditional items —
a condition that matched today may not have matched when you last looked.

```
managedsoftwareupdate --checkonly -vv
```

The removal table lists what is actually queued for removal. If the name is not there, the
decision was made before removal ever started.

**2. Is it removable?** Read the pkgsinfo against the clause list above. `uninstallable: false`,
a `nopkg` item with no `uninstall_script`, or an MSIX `installs[]` entry with no `identity_name`
all produce a silent drop with no message at all.

**3. Is a blocker running?** Look for `Deferred: <name> ... (blocking applications running: ...)`.
A process in **any** session, including another user's disconnected session, counts.

**4. Is it an `--auto` deferral?** In an automatic run with an active user, a removal needs
`unattended_uninstall: true` and a `restart_action` that would not interrupt. The item reports
Pending Removal rather than failing.

**5. What did the uninstaller actually say?** Run the removal alone and read the output:

```
managedsoftwareupdate --item ExampleVendorSuite -vv
```

`No uninstaller defined` means mechanism selection found nothing — most often the
`check.registry.name` trap, or an `msi`/`exe` type that was expected but is actually `nopkg`.

**6. Does the ARP entry exist and is it findable?** The registry fallback matches on
`display_name` then `name`, exact then substring, so a pkgsinfo `name` of `ExampleVendorSuite`
will not match a DisplayName of `Example Vendor Suite`. Set `display_name` to the string that
actually appears in Add/Remove Programs.

```powershell
$paths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($p in $paths) {
    Get-ChildItem $p -ErrorAction SilentlyContinue | ForEach-Object {
        $r = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($r.DisplayName -like '*Example Vendor*') {
            [pscustomobject]@{
                Key             = $_.PSChildName
                DisplayName     = $r.DisplayName
                DisplayVersion  = $r.DisplayVersion
                UninstallString = $r.UninstallString
                QuietUninstall  = $r.QuietUninstallString
            }
        }
    }
}
```

**7. Is the guessed silent switch wrong?** If the uninstaller launches and the removal hangs or
returns an odd exit code, the inferred `/S` or `/VERYSILENT` is probably not what this vendor
wants. Declare the switches explicitly:

```yaml
uninstaller:
- type: exe
  command: 'C:\Program Files\Example Vendor\Suite\uninstall.exe'
  switches:
  - quiet
  - norestart
```

**8. Did it come back?** An item that removes cleanly and reappears next run is being re-queued by
an install action, by `default_installs`, or by another item's `requires`. See
[Dependencies And Update Chains](Dependencies-And-Update-Chains).

## See also

- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Manifests](Manifests)
- [Blocking Applications](Blocking-Applications)
- [Dependencies And Update Chains](Dependencies-And-Update-Chains)
- [Optional Installs And Self Service](Optional-Installs-And-Self-Service)
- [Troubleshooting](Troubleshooting)
- [Item Status Reference](Item-Status-Reference)
