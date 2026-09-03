# Frequently Asked Questions

Recurring questions about hosting a repository, running the client, authoring packages, and
how Cimian relates to Munki and to Intune. Answers are short and point at the page that
covers the subject properly. Where the honest answer is "it does not do that", it says so.

## Hosting the repository

### Does Cimian need a special server?

No. There is no Cimian server component, no API and no database. The client makes plain
`GET` requests for files at four fixed path shapes, so any static HTTP or HTTPS server is
enough — IIS, nginx, Apache, a CDN, a storage bucket behind an HTTP endpoint.

Two things the server must get right: return a real **404** for a missing file, because only
a 404 advances the manifest fallback chain and anything else aborts resolution; and have a
MIME mapping for `.yaml` and every payload extension you ship, because some servers answer
404 for an unmapped extension. See [The Cimian Repository](The-Cimian-Repository).

### Can the repository live in cloud object storage?

Yes, provided the bucket is fronted by something that serves plain HTTP `GET` at the same
path layout. The client validates the URL scheme and accepts only `http` and `https`, so
`s3://`, `abfss://` and similar native protocols cannot be used, and neither can a UNC share
or a `file://` path.

Be aware of what is not supported on that path: no storage-provider signed URLs or SAS
tokens, no custom request headers, and no request middleware to inject them. Authentication
is HTTP Basic, a bearer token, or mutual TLS, and nothing else. See
[Securing The Repository](Securing-The-Repository).

### What happens when the repository is unreachable?

Catalogs fall back to the copy cached on the device, so a run can still evaluate items and
install from cache. Manifest resolution is stricter: any non-404 failure aborts it outright
rather than falling through to a catch-all manifest, and the device gets no managed items
for that run. That is deliberate — a transient server error must never silently reconfigure
a machine.

### Is there proxy support?

No. The client sets no proxy of its own and there is no proxy setting in the configuration.

## Cimian and other management systems

### Can I use Cimian alongside Intune?

Yes, and it is a common arrangement: Intune handles enrollment, policy and compliance, and
Cimian handles the application estate. Deploy the client itself as a Win32 app in device
context, detected by a file or registry check rather than an MSI product code — the
ProductCode changes on every build. See
[Deploying Cimian With Intune](Deploying-Cimian-With-Intune).

Configuration can be delivered by policy: the client reads `SoftwareRepoURL`,
`ClientIdentifier`, `InstallerTimeout` and `CacheRetentionDays` from
`HKLM\SOFTWARE\Policies\Cimian`, and policy wins over `Config.yaml`. Those four values are
the whole policy surface — no other setting can be set that way. See
[Configuring Clients With Intune](Configuring-Clients-With-Intune).

One trap when reusing scripts across the two systems: **the polarity is opposite**. Cimian's
`installcheck_script` treats exit 0 as "install is needed"; an Intune Win32 detection script
treats exit 0 as "the app is present". Assigning the same package through both systems is
also a bad idea — each will act on what the other did.

### Does Cimian manage Windows Updates, or talk to winget?

No to both. Cimian manages the packages you put in your repository. There is a
`chocolatey` installer type, and anything else can be wrapped in a PowerShell payload, but
detection and state are Cimian's own — it does not read another manager's inventory.

## Running the client

### Do I need the .NET runtime on endpoints?

No. Every binary is published self-contained, so a machine needs no .NET runtime or SDK
installed. Windows 10 1809 (build 17763) is the nominal floor, on x64 or arm64; there is no
x86 build.

### Is it signed?

Releases published from the source repository are **unsigned**. If your environment requires
signed binaries — and many do — sign the MSI yourself before distributing it. See
[Installing Cimian](Installing-Cimian).

### Can users install software without admin rights?

Yes. Put the item in a manifest's `optional_installs` and it appears in Managed Software
Center. The application performs no installs itself: a user's choice is recorded in the
device-local self-service manifest, and the client installs it as SYSTEM on its next run.

Admin intent always wins. A user cannot remove an item the manifest mandates as an install,
uninstall or default install; that request is logged and ignored. See
[Optional Installs And Self Service](Optional-Installs-And-Self-Service).

### How do I make a run happen right now?

From the machine, elevated:

```
managedsoftwareupdate --auto --show-status
```

Without a shell, drop a trigger file and let the watcher service pick it up within ten
seconds — this is what a remote management tool or an Intune remediation script would do:

```
cimitrigger headless
```

Or write `C:\ProgramData\ManagedInstalls\.cimian.headless` directly, which is the same
thing without the tool. There is no way to trigger a run from the repository side: Cimian
pulls, and nothing listens on the network for an instruction. See
[cimitrigger](cimitrigger) and [cimiwatcher](cimiwatcher).

### How do I see what would happen without changing anything?

```
managedsoftwareupdate --checkonly -vv
```

It resolves manifests, loads catalogs, checks every item and prints the result. Nothing is
downloaded or installed. Note that the postflight script does not run in this mode.

### Where are the logs?

Under `C:\ProgramData\ManagedInstalls`: a per-session tree in `logs\YYYY-MM-DD\HHmm\`, and
in `reports\` a `run.log` that is rewritten every session plus JSON reports of sessions,
events, items and loop suppressions. See [Logging](Logging).

## Authoring packages

### Why does my package install every run?

Because its detection still says it is missing after the install succeeded. The installer is
almost never the problem. The usual causes are a version floor the payload can never report,
a checksum that can never match, an `installs` entry pointing at a path the installer never
creates or at a launcher stub, an MSI identified by a ProductCode that changes each build, or
an install-check script that always exits 0.

The client catches this on the first install rather than the fifth: after a successful
install it immediately re-runs the item's own detection, and suppresses the package if it
still reports work to do. Diagnose with `managedsoftwareupdate --loop-status`, fix the
pkgsinfo, and regenerate catalogs — a changed catalog fingerprint clears the suppression
fleet-wide by itself. See [Install Loop Prevention](Install-Loop-Prevention).

### Why does an item say Installed when it is not there?

Three common reasons, all of them detection:

- **The pkgsinfo declares no checks at all** and its installer type is `nopkg`, `script` or
  empty. The fallback treats a script-only item as installed, because there is nothing to
  verify. Payload-bearing types fall the other way and report pending.
- **A stale receipt.** If nothing else is configured, the client trusts its own record that
  it installed the item. Software removed outside Cimian still reports as installed.
- **An unparseable version.** A version string the comparison cannot parse compares as
  *equal*, so it never triggers an update.

The fix is the same in each case: give the item real detection, normally an `installs`
array. See
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
and [Installs Arrays](Installs-Arrays).

### Why did my new pkgsinfo key do nothing?

Most likely one of three things.

**You did not regenerate catalogs.** Clients read catalogs, never `pkgsinfo/`. Run
[makecatalogs](makecatalogs).

**The key is misspelled.** Unrecognised keys are silently ignored at every stage — there is
no schema validation anywhere in the chain, and `makecatalogs` will not warn you.
`unattendend_install: true` parses, publishes, and does nothing.

**The key is real but ignored.** A few keys are written by the authoring tools and carried
into the catalog while the client has no property for them: `uninstallcheck_script`,
`identifier`, `installer.arguments` (use `installer.args`), `installer.identity_name`, and
the top-level `installer_type` scalar (use `installer.type`). See
[Supported pkgsinfo Keys](Supported-pkgsinfo-Keys), which lists every one of them.

### Does anything validate my pkgsinfo?

Barely. `makecatalogs` fails the run if a file cannot be deserialised at all, but it does not
check that `name` or `version` are present, that an `installs` entry is well formed, that a
`restart_action` value is spelled correctly, or that a version string is sane. Treat a green
`makecatalogs` as "the YAML parsed", nothing more.

## Cimian and Munki

### How does this differ from Munki?

The model is the same and most of the vocabulary carries over: repository, catalogs,
manifests, `managed_installs` and friends, conditional items, optional installs and
self-service, blocking applications, force-install deadlines, preflight and postflight
scripts, `site_default`. The differences that matter day to day:

- **YAML, not plists**, and the directory is `pkgsinfo/` rather than `pkgsinfo` holding
  plists.
- **Windows installer types** — `msi`, `exe`, `msix`/`appx`, `nupkg`, `chocolatey`,
  `ps1`/`nopkg` — and Windows detection: `installs` entries of type `file`, `directory`,
  `msi`, `msix`, `appx`. There is no `plist`, `application` or `bundle` entry type.
- **The installer block is nested.** `installer.location`, `installer.type`,
  `installer.hash`; there is no `installer_item_location`.
- **No `receipts` array and no `uninstall_method`.** Removability is derived from what the
  pkgsinfo declares, and the client's own install record lives in the registry.
- **Catalog precedence is highest-version-wins**, not the order the manifest lists catalogs.
- **Conditions are their own expression language**, not NSPredicate, over a fixed fact set,
  and cannot be nested.
- **Loop protection**, which Munki has no equivalent of.
- **Absent:** repo plugins, request middleware, licence seat tracking, and proxy support.

The full mapping is on [Cimian for Munki Admins](Cimian-for-Munki-Admins).

### Can I use my Munki pkginfo files unchanged?

No. The format is different (YAML rather than a plist), several key names differ, the
installer types are Windows ones, and the detection entry types have no macOS equivalents.
The *shape* transfers — a Munki pkginfo tells you what the Cimian one needs to say — but the
file has to be rewritten. In practice you re-import the Windows installer with
[cimiimport](cimiimport) and use the Munki original as a reference for the metadata.

## See also

- [Overview](Overview)
- [Getting Started](Getting-Started)
- [Glossary](Glossary)
- [Cimian for Munki Admins](Cimian-for-Munki-Admins)
- [Troubleshooting](Troubleshooting)
- [The Cimian Repository](The-Cimian-Repository)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
