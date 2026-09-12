# Overview

Cimian manages software on Windows endpoints from a repository of YAML metadata and
installer payloads published over HTTP. This page explains the parts — the repository,
catalogs, manifests, pkgsinfo files, the client and the self-service application — and
follows one run from manifest fetch to report. Read it before anything else; the rest of
the wiki assumes this vocabulary.

If you have run Munki, the model is the one you know. The layout and most of the concepts
carry over; the file format is YAML and the installer types are Windows ones. See
[Cimian for Munki Admins](Cimian-for-Munki-Admins) for the differences in one place.

## The model

Cimian is **declarative and pull-based**. You do not tell a machine to install something.
You publish a statement of what that machine should have, and the machine reads it on a
schedule and makes itself match.

Two files carry that statement:

- A **pkgsinfo** file describes one version of one package: where its installer lives, how
  to run it, and — most importantly — how to tell whether it is already installed.
- A **manifest** names the packages that apply to one device, and what should happen to
  them: install and keep installed, remove, patch if present, or offer to the user.

Every hour, the client fetches the manifest for its own machine, looks up each named item
in a catalog, checks the machine's actual state, and acts only on the difference. Nothing
is queued centrally, no job is dispatched, and no server tracks what a device has done.

Three things follow from that, and they are the reason to pick this model:

**The repository is the source of truth, and it is text.** Everything an admin authors is
a YAML file in a directory tree, so the whole software estate can live in version control
with a history, review and rollback. See [Cimian With Git](Cimian-With-Git).

**A machine that was offline is not behind.** There is no missed push to retry. A device
that has been off for a month converges on its next run, because the run reads current
intent rather than replaying a queue of past commands.

**State is checked, not assumed.** Each run re-verifies every managed item against the
machine, so software removed by hand, rolled back, or broken by an out-of-band update is
reinstalled without anybody noticing it had gone.

The cost is honest: convergence is on a schedule rather than instant (though a run can be
triggered on demand), the client polls whether or not anything changed, and correctness
depends entirely on the detection you write into each pkgsinfo. A package whose detection
is wrong either loops forever or reports itself installed while absent — see
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

This suits fleets that want their software estate in version control, reviewed as code,
and applied uniformly to machines that are not always reachable. It suits a
one-off "install this on that machine now" workflow much less well.

## The pieces

### The repository

Five directories served by any static web server. There is no Cimian server component and
no API; the client makes plain `GET` requests at fixed paths.

```
C:\CimianRepo\
  catalogs\
  icons\
  manifests\
  pkgs\
  pkgsinfo\
```

`pkgsinfo\` and `pkgs\` are what you author and import; `manifests\` is what you assign;
`catalogs\` is generated. Clients never read `pkgsinfo\` at all. Full detail in
[The Cimian Repository](The-Cimian-Repository).

### pkgsinfo

One `.yaml` file per package version, holding identity, the installer to run, and the
detection that decides whether the machine already has it:

```yaml
name: ExampleApp
display_name: Example App
version: 1.2.0
catalogs:
  - Production
installer:
  location: apps/ExampleApp-1.2.0.msi
  type: msi
installs:
  - type: msi
    upgrade_code: '{00000000-0000-0000-0000-000000000000}'
```

See [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files) and
[Supported pkgsinfo Keys](Supported-pkgsinfo-Keys).

### Catalogs

A catalog is a generated aggregate: `makecatalogs` scans every pkgsinfo, copies each item
into the catalogs its `catalogs:` key names, and writes one file per catalog name. The
client downloads catalogs, not pkgsinfo, so **nothing you change in a pkgsinfo reaches a
device until you regenerate catalogs**.

Catalogs are also the promotion mechanism: move an item from `Development` to `Testing` to
`Production` by editing one list. See [Using Catalogs](Using-Catalogs) and
[Promoting Between Catalogs](Promoting-Between-Catalogs).

### Manifests

One `.yaml` file per device or role, listing item names under `managed_installs`,
`managed_uninstalls`, `managed_updates`, `optional_installs` and `default_installs`, plus
the catalogs to search and any manifests to include. Manifests compose: keep per-machine
manifests thin and put the substance in shared role manifests. See [Manifests](Manifests)
and [Conditional Items](Conditional-Items).

### The client

`managedsoftwareupdate.exe` is the engine, and it does all the work. It runs as SYSTEM
from an hourly scheduled task, and requires administrator rights. Around it:

- **cimiwatcher** — a Windows service that watches for trigger files and starts a run on
  demand, and applies staged client self-updates.
- **cimitrigger** — a small tool that writes those trigger files.
- **cimistatus** — the progress window shown during a triggered run.
- **Managed Software Center** — the self-service application.

The authoring tools ship in the same package: `cimiimport`, `cimipkg`, `makepkginfo`,
`makecatalogs`, `manifestutil` and `repoclean`. See [Command Line Tools](Command-Line-Tools).

### Managed Software Center

The user-facing application. It reads the state file the client writes, shows the items a
manifest marked `optional_installs`, and records a user's install and removal requests in
a device-local self-service manifest that the next run merges in. Users need no
administrator rights: the client, not the application, performs the install.

Admin intent always wins. A user cannot remove an item the manifest mandates. See
[Managed Software Center](Managed-Software-Center) and
[Optional Installs And Self Service](Optional-Installs-And-Self-Service).

## A run, end to end

```
  repository (static HTTP)                    endpoint
  ------------------------                    --------

                                        [ scheduled task / trigger ]
                                                    |
                                             preflight script
                                                    |
  manifests/<client id>.yaml  <---- GET ----  manifest resolution
      + included manifests                          |
                                              conditional items
                                                    |
                                             item list + catalog list
                                                    |
  catalogs/<name>.yaml        <---- GET ----  catalog load
                                                    |
                                    status check, per item -----> installed: nothing to do
                                                    |
                                            loop-guard, dependencies
                                                    |
                                       deferral: install window,
                                       blocking apps, active user
                                                    |
  pkgs/<installer.location>   <---- GET ----  download + SHA-256 verify
                                                    |
                                       install / update / uninstall
                                                    |
                                            convergence re-check
                                                    |
                                        InstallInfo.yaml + reports
                                                    |
                                            postflight script
```

Step by step:

1. **Manifest resolution.** The client works out which manifest is its own, trying the
   client-certificate CN, the configured `ClientIdentifier`, the machine name, the BIOS
   serial, then the catch-all names `Orphaned` and `site_default`. Only a 404 advances the
   chain — any other error aborts resolution rather than silently dropping the device onto
   a catch-all. See [Client Identifier Resolution](Client-Identifier-Resolution).
2. **Include tree and conditions.** Included manifests are walked depth-first, catalog
   lists are merged, and conditional blocks are evaluated once the full picture is known.
3. **Catalog load.** Each named catalog is downloaded and merged into one map keyed by
   item name. Where the same name appears in several catalogs, **the highest version
   wins** — catalog order is not a precedence.
4. **Status check.** Every item runs through a fixed cascade of detection gates and comes
   out as installed, pending, error or unknown, with a reason code.
5. **Loop guard and dependencies.** A package that has demonstrably been looping is
   suppressed; `requires` dependencies are pulled into the run ahead of their parent.
6. **Deferral.** Items outside an install window, blocked by a running application, or
   disruptive to a logged-in user during an automatic run, are set aside for this run.
7. **Download and install.** Payloads are fetched under `pkgs/`, verified against the
   SHA-256 in the pkgsinfo, cached, and installed. Immediately after a successful install
   the client re-runs the item's own detection, so a package that cannot converge is
   caught the first time rather than three sessions later.
8. **Report.** `InstallInfo.yaml` is written for Managed Software Center, and JSON reports
   plus a session log tree are written under `C:\ProgramData\ManagedInstalls`. See
   [Logging](Logging) and [Reporting Data Contract](Reporting-Data-Contract).

## What lives on the endpoint

| Path | Contents |
|---|---|
| `C:\Program Files\Cimian` | The binaries and support scripts. |
| `C:\ProgramData\ManagedInstalls` | Everything else: `Config.yaml`, the download cache, cached catalogs and manifests, icons, logs, reports and state. |

The client is configured by `C:\ProgramData\ManagedInstalls\Config.yaml`, whose keys are
PascalCase. Only two are needed to start: `SoftwareRepoURL` and, usually,
`ClientIdentifier`. Four of them can be overridden by policy under
`HKLM\SOFTWARE\Policies\Cimian`. See [Client Configuration](Client-Configuration).

## What Cimian does not do

Stated plainly, because the pull model invites the opposite assumption:

- There is no server, no console, no database and no inventory service. Reporting is JSON
  on the endpoint for something else to collect.
- There is no push. You cannot make a device install something from the repository side;
  you can only change what it will do on its next run, and trigger that run locally.
- Only `http` and `https` are supported. No `file://`, no UNC shares, no object-storage
  protocols, and no proxy configuration.
- Authentication is HTTP Basic, a bearer token, or mutual TLS. No Windows Integrated
  authentication, no custom request headers, no signed-URL schemes, and no request
  middleware.
- macOS and Linux are not targets. Windows 10 1809 and later, x64 and arm64.

## See also

- [Getting Started](Getting-Started)
- [Glossary](Glossary)
- [Frequently Asked Questions](Frequently-Asked-Questions)
- [Cimian for Munki Admins](Cimian-for-Munki-Admins)
- [The Cimian Repository](The-Cimian-Repository)
- [Manifests](Manifests)
- [Using Catalogs](Using-Catalogs)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Managed Software Center](Managed-Software-Center)
- [Architecture](Architecture)
