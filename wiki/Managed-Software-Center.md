# Managed Software Center

Managed Software Center (MSC) is Cimian's end-user application: a self-service software
portal where a user browses what an administrator has offered them, requests installs and
removals, watches a run apply, and reads the machine's install history. This page describes
what a user sees and does, and what an administrator has to publish for anything to appear
in it.

MSC never decides policy. Everything it shows comes from files that
`managedsoftwareupdate` writes, and everything it starts is carried out by the client
running as SYSTEM.

## Who can use it, and what rights it needs

MSC runs as the signed-in user with no elevation. Its application manifest requests
`asInvoker`, it does not call for UAC, and a standard user can use every part of it.

It has no privileges of its own, so it does not install anything itself. When a user asks
for work, MSC writes a request file that the `CimianWatcher` service — which does run as
SYSTEM — picks up and acts on. The privileged half of the operation happens in the service,
out of the user's session, so no consent prompt appears.

Two consequences follow. Cimian must be installed and the `CimianWatcher` service must be
running, or nothing a user clicks will ever happen. And the user's account must be able to
write under `C:\ProgramData\ManagedInstalls`, because that is where the self-service
selection file and the trigger file live; Cimian ships no ACL configuration for that
directory, so it relies on the default `%ProgramData%` permissions.

## Launching it

The installer creates a Start Menu shortcut:

```
C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Cimian\Managed Software Center.lnk
```

It points at `Managed Software Center.exe` in the Cimian install directory. That shortcut is
the only entry point. MSC is not started by a scheduled task, is not a service, and is not
launched by `CimianWatcher` — the watcher launches the separate CimianStatus progress window
instead, which is what a user sees during an unattended background run.

MSC takes no command-line arguments of any kind. It also has no single-instance guard, so
launching it twice gives you two windows.

## The window

The shell is a navigation view with a left pane listing **Software**, **Categories**,
**My Items**, **Updates** and **History**. The Updates entry carries a badge with the number
of pending items. The pane header can show a branding image and the footer shows when the
client last checked in.

Keyboard shortcuts:

| Key | Action |
|---|---|
| `Ctrl+R` or `F5` | Refresh |
| `Alt+Left` | Go back |
| `Ctrl+L` | Open the pop-out log window |

Two shell behaviours change the window automatically:

- **Updates-only mode.** When there is nothing optional to browse, the navigation pane is
  hidden entirely and the app pins itself to the Updates page.
- **Persistent-reminder mode.** When updates have been pending longer than
  `aggressive_notification_days` (default 14, set in `preferences.yaml`), the window becomes
  always-on-top and cannot be minimised. It returns to normal once the pending count reaches
  zero.

An administrator can reorder or trim the navigation pane with `sidebar_items` in
`C:\ProgramData\ManagedInstalls\preferences.yaml`. Only `software`, `categories`, `myitems`
and `updates` are accepted — History cannot be included in a custom sidebar, though it stays
reachable in the default list.

`C:\ProgramData\ManagedInstalls\client_resources\branding.yaml` can override the window
title and the app title text, and name an image used as the pane header.

## Software

The catalog browser, and the page MSC opens on.

At the top is a hero banner. If `C:\ProgramData\ManagedInstalls\branding\` contains files
matching `branding*.png`, `branding*.jpg` or `branding*.jpeg`, the first three (in name
order) cross-fade on a timer with clickable indicator dots; with no such files the banner is
a plain gradient. Nothing in Cimian copies those images down from the repo — put them on the
machine yourself, with your management tooling.

Beside the banner are two tiles: **Featured**, which filters the list to featured items, and
**Updates**, which jumps to the Updates page.

Below that is a row of category filter chips built from the categories actually present, a
search box, an optional **Featured** grid, and the main "All apps" grid. Each tile shows the
item's icon, name, and an action button: **Install**, **Remove**, or **Cancel** for a request
not yet carried out. Clicking a tile opens the item's detail page.

The list contains optional installs only. Items an administrator mandates are not
browseable here — a user cannot install or remove them from this page.

## Categories

A read-only grid of category cards, each with a name and item count. Selecting one takes you
to the Software page filtered to that category. There are no buttons on this page.

## My Items

Everything the user has personally chosen: install requests and removal requests, read back
from the self-service selection file rather than from the catalog. Each row shows the
display name, `version • category` and a status label such as "Installed", "Install
pending", "Will be installed", "Removal pending" or "Installing…".

Each row offers **Remove** or **Cancel**. Cancel withdraws a request that has not been acted
on yet. A footer **Process All** button appears when there are pending actions and starts a
single run covering them.

An install request stays listed after the install finishes — it is a standing subscription,
not a one-shot job — and the badge then reads "Installed" rather than staying pending.

## Updates

The page a user is sent to when work is waiting, and the only page with live progress.

Header actions:

| Control | What it does |
|---|---|
| **Check Again** | Starts a check-only run |
| **Install Now** | Starts an install-only run |
| **Stop** | Asks the running client to stop |
| **View Log** | Opens the log viewer in a flyout, with a pop-out button |

The body shows, each section hidden when empty: a progress overlay with message, percentage,
detail line and a Cancel button; a banner warning when a restart will be needed; **Pending
Installs**; **Available Updates** (installed version to new version); **Pending Removals**;
and **Problem Items** with their error text. Rows in the first three sections carry a live
per-item stage badge — pending, downloading, downloaded, installing, installed, removing,
removed or failed — streamed from the running client, with the failure reason (for example
an installer exit code) shown alongside.

Items with a deadline show the deadline and how many days they have been pending.

When there is nothing to do the page reads "Your software is up to date." and reminds the
user that managed software also installs in the background, with those runs listed under
History.

## History

A read-only list of every software run on the machine — date, run type, summary, the
packages involved, and duration — read from the client's session reports. It includes
automatic background runs the user never saw, which is the point of the page: an item can be
installed and updated without ever appearing under Updates.

## Item detail

Opened by clicking any tile or row. It shows a **Back** button, the icon, display name,
developer, a status badge, and the buttons that apply to the item's current state —
**Install**, **Remove**, **Cancel**. There is a deadline warning when the item has a forced
install date, plus panels for dependency information and for why an item is unavailable.

Below the description is an information grid: available update, category, installed version,
download size, developer, and whether a restart is required.

The page also has "What's New" and Screenshots panels, but the client does not currently
publish release notes or screenshots for an item, so both stay hidden. See
[Product Icons And Screenshots](Product-Icons-And-Screenshots).

## The log viewer

Available from the Updates page or with `Ctrl+L`. It reads the newest session log under
`C:\ProgramData\ManagedInstalls\logs` — sessions are laid out as
`logs\YYYY-MM-DD\HHMM\install.log` — and tails it live, switching automatically to a newer
session when one starts. A status line names the session and file currently shown.

Its toolbar has **Show Debug**, **Auto-scroll**, **Copy**, **Refresh**, **Clear** and
**Pop out**. Popping out opens a single standalone window titled "Managed Software Update
Log".

## Theme

MSC follows the Windows app theme; light and dark are picked up from the system setting.
There is no theme control inside the application. In dark mode the page background and
default layer colour are overridden with a slightly softer charcoal than the WinUI default.

## How it talks to the client

MSC never runs `managedsoftwareupdate` itself. Two channels connect them.

**Starting work.** MSC writes `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` containing
the argument line it wants run. The `CimianWatcher` service polls for that file about every
ten seconds, deletes it, and launches `managedsoftwareupdate` elevated with those arguments.
Deletion of the file is MSC's acknowledgement that the run has been picked up; if nothing
consumes it within thirty seconds the request times out. Concurrent clicks are merged into
one run rather than racing a second engine instance, so clicking Install on three items in
quick succession produces a single run covering all three.

**Watching it.** Every argument line MSC writes ends with `--status-port 19848`. MSC listens
on loopback TCP port 19848 and the client connects back to it, streaming newline-delimited
JSON status, detail, percentage and per-item stage messages. That connection is how the
Updates page shows live progress. It is two-way in one respect only: the **Stop** button
sends a stop command back down it. Port 19847 belongs to the separate CimianStatus window
used at the login screen, which is why the two do not collide when a machine is locked.

MSC also watches `InstallInfo.yaml` for changes, so its lists refresh as soon as a run
rewrites them.

## Notifications

MSC raises Windows toast notifications for updates available, install complete, install
failed, restart required and logout required. The buttons on those toasts do nothing today —
the activation handlers are empty — so treat toasts as notice-only and tell users to open
MSC from the Start Menu.

## The administrator's view

MSC renders `C:\ProgramData\ManagedInstalls\InstallInfo.yaml`, which
`managedsoftwareupdate` writes at the end of every check. Nothing appears in MSC that the
client did not put there, so anything missing from the UI is a repository or manifest
problem, not a GUI problem.

For an item to be **browseable** — visible on Software, Categories and Item detail, with an
Install button — it must be in a manifest's `optional_installs` and resolvable in one of the
catalogs that manifest brings into scope:

```yaml
name: WORKSTATION-01
catalogs:
  - Production
managed_installs:
  - CompanyBaseline
optional_installs:
  - ExampleApp
```

The pkgsinfo supplies everything the tiles and detail page display. The keys MSC actually
uses are:

| pkgsinfo key | Where it shows up |
|---|---|
| `name` | Identity; also the default icon filename |
| `display_name` | The title on every tile and row |
| `version` | Version shown, and the update comparison |
| `description` | Item detail body text |
| `category` | Category chips, the Categories page, the detail grid |
| `developer` | Detail grid |
| `icon_name` | Which file to use from the icons directory |
| `installer.size` | "Download size" in the detail grid |
| `restart_action` | The restart warning and detail-grid row |
| `uninstallable` and the removal mechanism | Whether a Remove button appears |
| `force_install_after_date` | Deadline text and days-pending |

A worked pkgsinfo for a self-service item:

```yaml
name: ExampleApp
display_name: Example App
version: 3.2.1
description: A short paragraph shown on the item's detail page in Managed Software Center.
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
```

Items in `managed_installs`, `managed_updates` or `default_installs` are not browseable.
They appear in MSC only while there is work outstanding on them — on the Updates page as a
pending install or available update — and in History afterwards. That is deliberate: a user
must not be able to opt out of mandated software.

`featured_items` in a manifest promotes an item on the Software page; see
[Featured Items](Featured-Items). Icons come from the repository's `icons/` directory and are
mirrored down automatically; see
[Product Icons And Screenshots](Product-Icons-And-Screenshots).

## Limitations

- **`cimian://` deep links do not work.** The URI handling is written and the protocol is
  declared in a packaged app manifest, but MSC ships unpackaged and nothing registers the
  scheme, so no `cimian://` link resolves on an installed machine.
- **Toast action buttons do nothing.** Clicking one produces no navigation and no install.
- **No screenshots and no release notes.** The panels exist, but the client never publishes
  the data that fills them.
- **Pre-install, pre-upgrade and pre-uninstall alert dialogs, blocking-application warnings
  and dependency lists are read from `InstallInfo.yaml` but never written into it**, so they
  do not appear.
- **History cannot be added to a custom `sidebar_items` list.**
- **No single-instance guard** — MSC can be launched more than once.
- MSC does no work when the `CimianWatcher` service is stopped: requests are written and
  never consumed.

## See also

- [Optional Installs And Self Service](Optional-Installs-And-Self-Service)
- [Featured Items](Featured-Items)
- [Product Icons And Screenshots](Product-Icons-And-Screenshots)
- [Manifests](Manifests)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Item Status Reference](Item-Status-Reference)
- [cimiwatcher](cimiwatcher)
- [Client Configuration](Client-Configuration)
