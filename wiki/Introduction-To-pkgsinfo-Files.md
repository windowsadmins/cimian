# Introduction To pkgsinfo Files

A pkgsinfo file is the metadata that tells Cimian what a package is, where its payload
lives, how to install it, and how to tell whether it is already installed. Every item a
client can install exists because some pkgsinfo file describes it. This page covers what
a pkgsinfo contains, where it sits in the repo, how one gets created, and how it becomes
something a client can act on.

If you have used Munki, this is the same idea as a pkginfo, with a different key set. Do
not assume a Munki key works here — see [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys).

## Where pkgsinfo files live

A Cimian repo has this shape:

```
C:\CimianRepo\
  pkgsinfo\
  pkgs\
  catalogs\
  manifests\
  icons\
```

`pkgsinfo/` holds the metadata, one YAML file per package version. `makecatalogs` scans it
recursively, so you can organise it into subdirectories however you like — by vendor, by
category, by team. The subdirectory layout carries no meaning; it is purely for your own
navigation.

Only files with the extension `.yaml` are scanned. A file named `ExampleApp.yml` is
invisible to `makecatalogs` and its package silently does not exist.

`pkgs/` holds the payloads themselves. The `installer.location` key in a pkgsinfo is a path
relative to `pkgs/`, so a pkgsinfo declaring `location: apps/ExampleApp-1.2.0.msi` refers to
`C:\CimianRepo\pkgs\apps\ExampleApp-1.2.0.msi`, which the client fetches from
`https://cimian.example.com/repo/pkgs/apps/ExampleApp-1.2.0.msi`.

## How a pkgsinfo gets created

Three routes, all producing the same kind of file:

`cimiimport` is the usual one. Point it at an installer and it extracts the name, version,
developer and product codes, computes the hash and size, copies the payload into `pkgs/`,
writes the pkgsinfo, and runs `makecatalogs`.

```
cimiimport C:\Downloads\ExampleApp-1.2.0.msi --repo_path C:\CimianRepo
```

`makepkginfo` writes a pkgsinfo to stdout without touching the repo, which is useful when
you want to review or script the metadata before committing it.

Writing the YAML by hand is entirely supported and is often the right answer for
script-only packages, which have no installer to introspect.

Whichever route you take, the file is not live until `makecatalogs` has run and republished
the catalogs.

## The chain from pkgsinfo to client

A pkgsinfo does not reach a device directly. It travels through three stages, and each
stage has its own idea of which keys exist.

1. **Authoring.** `cimiimport` or `makepkginfo` writes the file into `pkgsinfo/`.
2. **Catalog generation.** `makecatalogs` reads every pkgsinfo, groups items by the
   `catalogs:` they name, and writes `catalogs/<Name>.yaml` — each catalog being a single
   `items:` sequence containing full copies of the pkgsinfo bodies. An `All` catalog is
   always written and always contains every item, whether or not the item names any catalog.
3. **Client consumption.** `managedsoftwareupdate` downloads the catalogs its manifests ask
   for, matches item names against the manifest, and acts.

This matters because the three stages parse the file with different key sets, and any key a
stage does not recognise is dropped without comment. A key is only genuinely supported if it
survives all three legs. Several keys are written happily by the authoring tools, carried
into the catalog, and then ignored by the client. The
[Supported pkgsinfo Keys](Supported-pkgsinfo-Keys) page lists every one of them.

The same tolerance means a **typo is never an error**. `unattendend_install: true` parses
cleanly, publishes cleanly, and does nothing.

## A minimal pkgsinfo

This is a complete, working pkgsinfo for an MSI. Save it as
`C:\CimianRepo\pkgsinfo\apps\ExampleApp-1.2.0.yaml`.

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
  product_code: '{9F3A2C41-6B7E-4D18-9C2A-8E15D0447B33}'
```

Reading it in order:

`name` is the package's identity. It is how a manifest asks for the item, how catalogs are
keyed, and how the client's install receipt is named. It is matched case-insensitively
everywhere, so `ExampleApp` and `exampleapp` are the same item. Once a package is deployed,
changing its `name` creates a new, unrelated package rather than renaming the old one.

`display_name` is what a user sees in Managed Software Center. If it is absent, the raw
`name` is shown.

`version` is the other half of the identity. Cimian compares versions numerically after
normalising them, and the highest version wins. A version string it cannot parse compares
as *equal* to everything, which means an unparseable version never triggers an update —
see [Version Comparisons](Version-Comparisons).

`catalogs` lists which catalogs this item is published into. This key is consumed by
`makecatalogs` and never reaches the client; the client only ever sees whichever catalog
files it was told to download. Publishing to `Testing` first and moving to `Production`
later is the normal promotion path — see [Promoting Between Catalogs](Promoting-Between-Catalogs).

`installer.location` is the payload path relative to `pkgs/`. `installer.type` tells the
client which installation mechanism to use; see [Installer Types](Installer-Types).

`installs` is the detection contract: how the client decides whether this package is
already present. Here it says "look up this MSI ProductCode in the uninstall registry, and
compare the registered version against `version`". Without an `installs` array (or another
detection mechanism) an MSI package has no way to report itself installed and will be
attempted on every run. Detection is covered in
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
and [Installs Arrays](Installs-Arrays).

## A realistic pkgsinfo

A production item usually carries description and grouping metadata for the GUI, a hash and
size so the download can be verified, blocking applications, and a postinstall step.

```yaml
name: ExampleApp
display_name: Example App
version: 2026.01.15.1200
description: |
  Example App is a document editor used by the design team.
  Licensing is handled at first launch.
category: Productivity
developer: Example Vendor
icon_name: ExampleApp.png
catalogs:
- Production
supported_architectures:
- x64
minimum_os_version: 10.0.19045
installer:
  location: apps/ExampleApp-2026.01.15.1200.msi
  type: msi
  hash: 7d2b1f6c0a4e9385c1d7be22f04a6c19d853e7a0b41c96f8d2350ae7cb914f6d
  size: 184552448
  args:
  - ALLUSERS=1
  - DESKTOPSHORTCUT=0
installs:
- type: msi
  upgrade_code: '{4C8B1E27-5A93-4F60-B7D1-2E9A6C0348FF}'
  version: 2026.01.15.1200
  key_path: C:\Program Files\Example Vendor\Example App\ExampleApp.exe
blocking_applications:
- ExampleApp
unattended_install: true
restart_action: RecommendRestart
installer_timeout: 1800
postinstall_script: |
  $license = 'C:\ProgramData\Example Vendor\license.dat'
  if (-not (Test-Path $license)) {
      Write-Output "CIMIAN-WARNING: license file not present after install"
  }
```

Points worth noting in that example. `description` uses a YAML block scalar; Cimian writes
multi-line strings back in block style, so embedded PowerShell survives a rewrite intact.
`hash` is a SHA-256 digest of the payload and the client refuses a download that does not
match it. `blocking_applications` names processes, not paths — if `ExampleApp.exe` is
running the item is deferred for the whole run rather than retried mid-session; see
[Blocking Applications](Blocking-Applications). `installer_timeout` is in **seconds**.
The `key_path` entry adds a second check on top of the MSI registry lookup: even after the
UpgradeCode resolves, the named executable's file version must be at least the catalog
version.

## Name and version are the item's identity

Everything downstream keys on `name`. Within a run, the client builds one flat map of
available items keyed by lowercased name, merging every catalog it loaded. When two entries
share a name, **the higher version wins** — catalog order establishes no precedence at all.
An item in `Testing` at a higher version than the one in `Production` will be the one that
installs, on any device subscribed to both.

That merge rule is why **duplicate `name` + `version` inside one catalog is a problem**.
`makecatalogs` does not detect it: it does no duplicate checking, no required-field
validation, and no version-string sanity checking. Deserialisation is the only gate. Two
pkgsinfo files declaring `ExampleApp` at `1.2.0` both parse, both publish, and both land in
the catalog. The client then compares them, finds neither version higher, and keeps whichever
it happened to load first — which is stable only by accident. The two entries can differ in
every other respect: different payload, different detection, different scripts. Nothing warns
you, and the promotion state of the package becomes unreadable, because "is 1.2.0 in
Production?" now has two answers.

The practical rules that follow:

- One pkgsinfo file per name-and-version. Bump the version for every rebuild of a payload,
  even a rebuild that changes nothing user-visible.
- Never edit a published pkgsinfo in place to change what the payload is. Publish a new
  version instead.
- If you re-cut a package because the previous build was wrong, give it a new version rather
  than reusing the number.

Note also that editing *any* field of a published pkgsinfo has a side effect: `makecatalogs`
stamps each catalog item with a `loop_fingerprint` computed over the whole serialised item,
and the client clears that package's install-loop suppression the moment the fingerprint
changes. A description-only edit is enough to release a suppression fleet-wide. See
[Install Loop Prevention](Install-Loop-Prevention).

## Publishing a change

Editing a pkgsinfo changes nothing on its own. Catalogs are generated artefacts, and the
client reads catalogs, not pkgsinfo files.

```
makecatalogs --repo_path C:\CimianRepo
```

`makecatalogs` exits non-zero if any pkgsinfo failed to parse, so a pipeline cannot publish
an incomplete catalog on a green exit code. Note that the catalogs are written before that
check runs — the non-zero exit is a guard, not a rollback, so a failed run leaves partial
catalogs on disk that you must fix and regenerate.

An empty-string value is dropped on rewrite: `description: ""` does not survive a tool that
rewrites the file. Omit the key instead.

## See also

- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Installer Types](Installer-Types)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Installs Arrays](Installs-Arrays)
- [Version Comparisons](Version-Comparisons)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Using Catalogs](Using-Catalogs)
- [Promoting Between Catalogs](Promoting-Between-Catalogs)
- [The Cimian Repository](The-Cimian-Repository)
- [makecatalogs](makecatalogs)
- [cimiimport](cimiimport)
