# Installing Software

This is the routine operation the rest of the wiki exists to support: taking one application
from an installer file on your admin workstation to installed and verified on a managed
machine. It covers the whole spine — package or import, review the pkgsinfo, rebuild the
catalogs, publish, add the item to a manifest, run the client, confirm the result — and then
the three variations you will reach for next: offering an item instead of enforcing it,
targeting a subset of machines, and promoting from a test catalog to production.

[Getting Started](Getting-Started) is the five-minute version that stands the whole system up
on one machine. This page assumes that is already done and that you are adding software to a
working repository.

## Before you start

You need three things in place.

A repository with the authored directories present, and an admin workstation whose
`Config.yaml` points at it as a local path. See
[The Cimian Repository](The-Cimian-Repository).

A way to publish `catalogs\`, `pkgs\`, `manifests\` and `icons\` to whatever serves the repo
over HTTP or HTTPS. If you author directly on the web server, publishing is a no-op; if you
author elsewhere, it is a copy, a sync or a `git push` — see [Cimian With Git](Cimian-With-Git).

At least one managed machine you can run commands on, whose manifest you are willing to change.
`WORKSTATION-01` stands in for it below, and `Example App` for the software.

Every command runs from an elevated PowerShell prompt.

## 1. Get an installer

Most of the time you already have one: a vendor MSI, EXE or MSIX. Skip to step 2.

If what you have is not an installer — a folder of files to place on disk, a set of registry
values, a script that has to run — build one first with [cimipkg](cimipkg). Scaffold a project,
drop your files under `payload\` and your scripts under `scripts\`, describe the package in
`build-info.yaml`, then build:

```powershell
cimipkg --create C:\pkgs\ExampleApp
```

```powershell
cimipkg --skip-import C:\pkgs\ExampleApp
```

The MSI lands in `C:\pkgs\ExampleApp\build\`. `--skip-import` suppresses the post-build prompt
that offers to run `cimiimport` for you, so that you can review the built package before it
enters the repo. Building an MSI rather than shipping a bare script matters for detection: the
client can read an MSI's ProductCode, UpgradeCode and version back out of the registry, so the
item is detectable without you writing an installcheck script.

## 2. Import it into the repository

[cimiimport](cimiimport) extracts what it can from the installer, asks you to confirm the
metadata, writes a pkgsinfo into `pkgsinfo\`, copies the payload into `pkgs\`, and then runs
`makecatalogs`.

```powershell
cimiimport C:\Downloads\ExampleApp-1.2.0.msi
```

It prompts in this order: name, version, developer, description, category, architectures,
catalogs, and the location within `pkgs\`. Press Enter to accept an extracted default. Two
answers deserve thought rather than a keystroke:

**Catalogs.** Put a new package into your test catalog, not into production. If you have not
set up a testing tier yet, [Using Catalogs](Using-Catalogs) describes the development, testing,
production model, and the promotion variation at the end of this page moves the item onward.

**Name.** This is the item's identity everywhere — in catalogs, in manifests, in the receipt
the client keeps. It is matched case-insensitively but it is not a display name, and changing
it later means changing every manifest that references it.

The final prompt, `Import this item? (y/n) [n]:`, defaults to **no**. Answer `y`.

Cancelling at that prompt exits 0 and writes nothing.

## 3. Review the pkgsinfo before it goes anywhere

`cimiimport` produces a starting point, not a finished package. Open what it wrote:

```powershell
Get-ChildItem -Recurse C:\CimianRepo\pkgsinfo -Filter *.yaml | Sort-Object LastWriteTime -Descending | Select-Object -First 1
```

A minimal, complete pkgsinfo for an MSI looks like this:

```yaml
name: ExampleApp
display_name: Example App
version: 1.2.0
description: Example App, the thing this package installs.
category: Utilities
developer: Example Corp
catalogs:
  - Testing
installer:
  location: apps/ExampleApp-1.2.0.msi
  type: msi
installs:
  - type: msi
    product_code: '{0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0}'
unattended_install: true
unattended_uninstall: true
```

Four things are worth checking every time.

**The `installs:` array is the detection.** It is what decides whether the package is already
present, and it is the single most common place a package goes wrong: an install that works but
a detection that never matches makes the item reinstall on every run. Read
[Installs Arrays](Installs-Arrays) and
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
before you accept an auto-generated array for anything non-trivial.

**`unattended_install`.** It defaults to `false`, and a `false` item is deferred during an
`--auto` run while a user is signed in — which is when nearly all fleet runs happen. Unless the
installer genuinely needs an interactive desktop, set it to `true`. The same applies to
`unattended_uninstall` for removals.

**`blocking_applications`.** If the installer fails or corrupts an in-use application when its
process is running, list the process names. The item is then deferred rather than attempted
while any of them is running. See [Blocking Applications](Blocking-Applications).

**`catalogs:`.** Confirm the spelling matches the catalog your test manifest actually names.
Catalog names are bucketed case-insensitively when the catalogs are built but requested by the
client exactly as spelled in the manifest.

The full key list, including the keys that are accepted and then silently ignored, is on
[Supported pkgsinfo Keys](Supported-pkgsinfo-Keys).

## 4. Rebuild the catalogs

`cimiimport` already ran `makecatalogs` once. Any hand edit to a pkgsinfo needs it again.
**Nothing you change in `pkgsinfo\` reaches a device until the catalogs are regenerated.**

```powershell
makecatalogs --repo_path C:\CimianRepo
```

Check the exit code. A non-zero exit means at least one pkgsinfo in the repository failed to
parse; the catalogs on disk have already been written and are incomplete, so fix the parse
error and rerun rather than publishing what is there.

Then confirm your item is actually in the catalog you expect:

```powershell
Select-String -Path C:\CimianRepo\catalogs\Testing.yaml -Pattern '^- name: ExampleApp$'
```

Nothing returned means the catalog name in the pkgsinfo does not match the file you are
searching. See [makecatalogs](makecatalogs) for what the tool validates, warns about and
rejects.

## 5. Publish

Copy or sync `catalogs\`, `pkgs\`, `manifests\` and `icons\` to the server. `pkgsinfo\` is
authoring material and is never fetched by a client — there is no harm in publishing it, and no
benefit either.

Confirm the two things the client will ask for are actually reachable at the URLs it will use,
because a server that answers `404` for a file that exists on disk is a common and very
confusing failure:

```powershell
Invoke-WebRequest https://cimian.example.com/repo/catalogs/Testing.yaml -UseBasicParsing | Select-Object StatusCode
```

```powershell
Invoke-WebRequest -Method Head https://cimian.example.com/repo/pkgs/apps/ExampleApp-1.2.0.msi -UseBasicParsing | Select-Object StatusCode
```

If the payload request fails while the file is plainly on disk, the usual cause is a missing
MIME mapping for the extension. [The Cimian Repository](The-Cimian-Repository) lists everything
the serving layer has to get right.

## 6. Add the item to a manifest

A catalog makes an item available. A manifest is what asks for it. Add the name to
`managed_installs` in the manifest for the machine you are testing on:

```yaml
name: WORKSTATION-01
catalogs:
  - Testing
managed_installs:
  - ExampleApp
```

[manifestutil](manifestutil) does the same edit without opening the file:

```powershell
manifestutil --manifest WORKSTATION-01 --add-pkg ExampleApp --section managed_installs
```

`manifestutil` understands only a subset of the manifest keys — it knows nothing of
`conditional_items`, `featured_items`, `default_installs`, `managed_profiles` or
`managed_apps` — and drops the rest when it rewrites the file. On a manifest that uses any of
those, edit by hand.

Publish the changed manifest. [Manifests](Manifests) covers the include tree, action precedence
and the layout to grow into before your manifests multiply.

## 7. Trigger a run

Check first. A check-only run resolves the manifest, downloads the catalogs, evaluates every
item and prints what it would do, without downloading a payload or installing anything:

```powershell
managedsoftwareupdate --checkonly -vv
```

Read the output for three things: which manifest resolved (a warning here means the client fell
through to a fallback or catch-all name — see
[Client Identifier Resolution](Client-Identifier-Resolution)), that the catalog loaded, and that
`ExampleApp` is listed as pending. `Item not found in catalog: ExampleApp` means the client's
catalog does not contain it — the rebuild, the publish, or the download of the catalog itself is
where to look.

Then install. Without a mode flag this is a manual, foreground run:

```powershell
managedsoftwareupdate -vv
```

To act on only the item you are testing and leave everything else alone:

```powershell
managedsoftwareupdate -vv --item ExampleApp
```

On a machine you are not sitting at, ask the `CimianWatcher` service to run instead of invoking
the client directly, which keeps the run in the service's own elevated context:

```powershell
cimitrigger headless
```

See [managedsoftwareupdate](managedsoftwareupdate), [How Cimian Runs](How-Cimian-Runs) and
[cimitrigger](cimitrigger). Nothing further is needed to keep the fleet current after this: the
hourly scheduled task installed with the client repeats the run on its own.

## 8. Verify the result

The client re-runs the item's own detection immediately after installing it, so a successful run
has already proved convergence once. Confirm it independently.

The per-item record, which is also what a reporting system collects:

```powershell
Get-Content C:\ProgramData\ManagedInstalls\reports\items.json
```

The state file Managed Software Center reads:

```powershell
Get-Content C:\ProgramData\ManagedInstalls\InstallInfo.yaml
```

Then check again. The item should report as installed, and nothing should be pending:

```powershell
managedsoftwareupdate --checkonly
```

**If it is still pending, the install worked and the detection is wrong.** That is the single
most common authoring defect. Left alone it becomes an install loop, which the client will
eventually suppress on its own — see
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
and [Install Loop Prevention](Install-Loop-Prevention). Fix the `installs:` array, rebuild the
catalogs and publish; editing the pkgsinfo is also what releases an existing suppression across
the fleet.

For a failed install rather than a failed detection, the session directory under
`C:\ProgramData\ManagedInstalls\logs\` has the transcript and the installer's own log. See
[Logging](Logging) and [Troubleshooting](Troubleshooting).

## Variation: offer it instead of enforcing it

Move the name from `managed_installs` to `optional_installs` and the item stops being installed
automatically. It appears in Managed Software Center for the user to install, and to remove
again, on their own:

```yaml
name: WORKSTATION-01
catalogs:
  - Testing
optional_installs:
  - ExampleApp
```

An optional item is chosen from a list rather than delivered silently, so its metadata is now
user-visible and worth filling in properly — `display_name`, `description`, `category`,
`developer` and an icon. Pairing `optional_installs` with `managed_updates` is the usual shape:
the user decides whether the item is present, and Cimian patches it if it is.

Optional items are not downloaded until somebody asks for one, unless the pkgsinfo sets
`precache: true`. See [Optional Installs And Self Service](Optional-Installs-And-Self-Service),
[Managed Software Center](Managed-Software-Center) and
[Product Icons And Screenshots](Product-Icons-And-Screenshots).

## Variation: target a subset of machines

When only some machines should get the item, a conditional block inside the manifest applies its
lists only where the condition matches. Conditions are evaluated against facts collected from
the device on every run:

```yaml
name: Site-Default
catalogs:
  - Production
conditional_items:
  - condition: machine_type == "laptop"
    managed_installs:
      - ExampleApp
```

Anything on the right-hand side that contains a space, a dot, a hyphen or a backslash must be
quoted, or the tokenizer splits it and only the first fragment is compared. A condition that
fails to parse never matches and never fails the run, so a malformed condition looks exactly
like a machine that does not qualify.

The complete grammar, the operator list with its real behaviour, and the available facts are on
[Conditional Items](Conditional-Items) and
[Conditional Facts Reference](Conditional-Facts-Reference). Two limitations to know before you
lean on this: `IN` with a bracketed list silently matches only the first element, and nested
`conditional_items` are dropped without warning.

The alternative to a conditional is a separate manifest included by the machines that need it,
which is easier to read once the rules get complicated. [Manifests](Manifests) compares the two.

## Variation: promote from testing to production

Once the item has proved itself on your test machines, add the production catalog to its
`catalogs:` list. Leave the earlier entries in place:

```yaml
name: ExampleApp
version: 1.2.0
catalogs:
  - Testing
  - Production
```

```powershell
makecatalogs --repo_path C:\CimianRepo
```

Publish `catalogs\` and the fleet picks the item up on its next scheduled run. Nothing else
changes — do not bump the version, do not touch `installer.location`, and do not re-import the
installer. Promotion is a metadata change to a package that has already been built and verified.

Removing the catalog name again stops further deployment but **does not undo installs that
already happened**; Cimian does not downgrade. [Promoting Between Catalogs](Promoting-Between-Catalogs)
covers the verification, the rollback, and the failure modes that make a promotion look done when
it is not.

## See also

- [Getting Started](Getting-Started)
- [cimiimport](cimiimport)
- [cimipkg](cimipkg)
- [makecatalogs](makecatalogs)
- [manifestutil](manifestutil)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Installs Arrays](Installs-Arrays)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Manifests](Manifests)
- [Using Catalogs](Using-Catalogs)
- [Promoting Between Catalogs](Promoting-Between-Catalogs)
- [Optional Installs And Self Service](Optional-Installs-And-Self-Service)
- [Conditional Items](Conditional-Items)
- [Uninstalling Software](Uninstalling-Software)
- [Troubleshooting](Troubleshooting)
