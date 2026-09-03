# Glossary

Every term the rest of this wiki relies on, alphabetically, with a pointer to the page that
covers it in full. Where Cimian's word differs from Munki's, or means something different,
the entry says so.

**All catalog** — a catalog named `All` that `makecatalogs` always generates, containing
every item in the repository whether or not its pkgsinfo names any catalogs. It exists as a
repository-wide index and is a poor choice for a manifest, because it hands a device the
newest version of everything regardless of promotion state. See
[Using Catalogs](Using-Catalogs).

**blocking application** — a process name in a pkgsinfo's `blocking_applications` list.
While any of them is running, the item's install, update or removal is deferred for that
whole run. Applies in every run mode, whether or not a user is logged in. See
[Blocking Applications](Blocking-Applications).

**bootstrap mode** — first-run provisioning mode, entered with `--bootstrap` or by the
presence of `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`. Loop protection is disabled
entirely and restart or logout actions are carried out. There is no convergence loop inside
the process; repetition comes from the hourly task, and the flag clears itself only when
every install and uninstall in a session succeeded. See
[Bootstrapping With Cimian](Bootstrapping-With-Cimian).

**catalog** — a generated file aggregating whole pkgsinfo bodies, one per catalog name, at
`catalogs/<name>.yaml`. Clients read catalogs and never read `pkgsinfo/`, so a pkgsinfo edit
has no effect until `makecatalogs` runs. Where an item appears in more than one catalog a
client uses, the highest version wins — unlike Munki, catalog order is not a precedence. See
[Using Catalogs](Using-Catalogs).

**catalog fingerprint** — the `loop_fingerprint` value that `makecatalogs` stamps into each
catalog item, a hash over the whole serialised item. The client clears a package's loop
suppression as soon as it sees a different value, so any pkgsinfo edit that reaches the
catalog releases the suppression. Machine-written; never author it by hand. See
[Install Loop Prevention](Install-Loop-Prevention).

**check block** — the `check:` key in a pkgsinfo, offering three alternative detection
methods (`check.registry`, `check.file`, `check.script`) for packages that do not suit an
`installs` array. `check.script` exit 0 means "already installed" — the opposite of
`installcheck_script`. See
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

**the client** — `managedsoftwareupdate` and its supporting binaries: the watcher service,
the trigger tool, the status window and Managed Software Center. Used in contrast to the
authoring tools, which run on an admin workstation. See
[managedsoftwareupdate](managedsoftwareupdate).

**client identifier** — the manifest name a device asks for. The `ClientIdentifier` setting
in `Config.yaml` supplies it; if it is unset or 404s, the client falls back through the
machine name, the BIOS serial, `Orphaned` and `site_default`. Same concept and same name as
Munki's. See [Client Identifier Resolution](Client-Identifier-Resolution).

**conditional item** — an entry in a manifest's `conditional_items` list: a `condition`
expression plus `managed_installs`, `managed_uninstalls`, `managed_updates` and
`optional_installs` that apply only when it matches. Cimian's condition language is its own
`AND`/`OR`/`NOT` expression syntax over a fixed set of system facts, not Munki's
NSPredicate, and conditional items cannot be nested. See
[Conditional Items](Conditional-Items) and
[Conditional Facts Reference](Conditional-Facts-Reference).

**convergence** — the state of a package's own detection reporting nothing left to do.
After every successful install the client immediately re-runs the item's detection; an item
that still says it needs work has not converged, and is suppressed for a re-probe interval
rather than reinstalling on the next run. See
[Install Loop Prevention](Install-Loop-Prevention).

**default install** — a name in a manifest's `default_installs`. It is installed once if it
is not already present, and is then never re-enforced: a user may remove it and it does not
come back. Same meaning as Munki's `default_installs`. See [Manifests](Manifests).

**deferral** — an item that was going to be acted on but was set aside for this run, by an
install window, a running blocking application, or the active-user check in an automatic
run. A deferred item reports as pending, never as installed. See
[Item Status Reference](Item-Status-Reference).

**featured item** — a name in a manifest's `featured_items`, collected across the whole
manifest tree and surfaced to Managed Software Center for promotion. It is presentational
only and queues nothing; the item must also appear in another list to be actionable. There
is no per-package `featured` key in a pkgsinfo. See [Featured Items](Featured-Items).

**force install** — `force_install_after_date` in a pkgsinfo. Once that date has passed the
item installs even outside its install window, and even if it is only offered as an optional
install. See [Force Installs And Deadlines](Force-Installs-And-Deadlines).

**included manifest** — a manifest named in another manifest's `included_manifests`. It is
fetched under `manifests/`, may itself include others to any depth, and its catalogs and
item lists merge into the run. Cycles terminate rather than looping. See
[Manifests](Manifests).

**installcheck script** — the `installcheck_script` key: inline PowerShell that decides
whether an install is needed. It is a predicate and is **not** inverted — **exit 0 means the
install is needed**, non-zero means it is not. That is the same convention as Munki, and the
opposite of an Intune Win32 detection script. A timeout leaves the item in error and does
**not** install. See
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

**InstallInfo.yaml** — the state file the client writes at
`C:\ProgramData\ManagedInstalls\InstallInfo.yaml` and Managed Software Center reads. It
lists managed installs, updates, removals, optional installs, problem items and featured
items as of the last run. See [Managed Software Center](Managed-Software-Center).

**installs array** — the `installs:` list in a pkgsinfo: the entries whose presence proves
the package is installed, and Cimian's preferred detection. Entry types are `file`,
`directory`, `msi`, `msix` and `appx`; there is no `plist`, `application` or `bundle` type,
because those are macOS concepts. Every entry must pass; any one failing queues the item.
See [Installs Arrays](Installs-Arrays).

**install window** — the `install_window` key: a start and end time and an optional weekday
list restricting when an item may be acted on. Unparseable values fail open, meaning no
restriction. A passed `force_install_after_date` overrides it. See
[Force Installs And Deadlines](Force-Installs-And-Deadlines).

**item** — one named entry in a manifest or a catalog. Item names are matched
case-insensitively everywhere and are the join between the two: a manifest names an item,
and a catalog supplies its metadata.

**loop protection** — the client's active defence against a package that installs
successfully and is still reported as needing installation afterwards, forever. It watches
attempts per package, suppresses one that is clearly looping for a bounded window, and lifts
the suppression by itself when the catalog fingerprint changes or the item converges. Also
called LoopGuard in log output and diagnostics. Munki has no equivalent. See
[Install Loop Prevention](Install-Loop-Prevention).

**Managed Software Center** — the self-service application users see, listing optional
installs, pending updates and install history. It performs no installs itself: it records
requests in the self-service manifest, and the client acts on them as SYSTEM on its next
run. The Windows analogue of Munki's application of the same name. See
[Managed Software Center](Managed-Software-Center).

**managed install** — a name in a manifest's `managed_installs`: install it and keep it
installed, re-enforced on every run. Same meaning as Munki's. See [Manifests](Manifests).

**managed uninstall** — a name in a manifest's `managed_uninstalls`: remove it and keep it
removed. It only acts on items that are actually removable — see **uninstallable**. See
[Uninstalling Software](Uninstalling-Software).

**managed update** — a name in a manifest's `managed_updates`: patch it if it is present,
never install it if it is absent. Pairing this with `optional_installs` is how you keep a
user-chosen application current without forcing it onto anybody. Same meaning as Munki's.
See [Manifests](Manifests).

**manifest** — the per-client list of what should be installed, at
`manifests/<name>.yaml`. It names items and catalogs; it never describes a package. Same
concept as Munki's, in YAML rather than a plist. See [Manifests](Manifests).

**on-demand item** — a pkgsinfo with `OnDemand: true`. It is never considered installed,
never gets a receipt, runs every session, and is exempt from loop protection. The key is
deliberately PascalCase, unlike every other pkgsinfo key. It also precedes the install-check
in the detection cascade, so an on-demand item's `installcheck_script` is never consulted.
Same idea as Munki's OnDemand items. See [On Demand Items](On-Demand-Items).

**optional install** — a name in a manifest's `optional_installs`: offered in Managed
Software Center, installed only when a user asks. Nothing happens until then. Same meaning
as Munki's. See [Optional Installs And Self Service](Optional-Installs-And-Self-Service).

**payload** — the installer file a pkgsinfo points at with `installer.location`, stored
under `pkgs/` in the repository and downloaded into the client's cache. A `location` that is
already an absolute `http://` or `https://` URL is used verbatim and bypasses `pkgs/`.
Cimian has no `installer_item_location` key; that is Munki's spelling. See
[The Download Cache](The-Download-Cache).

**pkgsinfo** — a single package metadata file, `pkgsinfo/**/<anything>.yaml`, describing one
version of one package. Munki calls this a pkginfo and writes it as a plist; Cimian's
directory, file extension and several key names differ, so a Munki pkginfo is not usable
unchanged. Only `.yaml` is scanned — a `.yml` file is invisible. See
[Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files) and
[Supported pkgsinfo Keys](Supported-pkgsinfo-Keys).

**preflight and postflight scripts** — optional PowerShell scripts on the endpoint that run
before and after a session. Preflight may rewrite the repository URL and client identifier,
which the client re-reads afterwards. Neither has a timeout, and postflight does not run in
a check-only run or after a crash. See
[Preflight And Postflight Scripts](Preflight-And-Postflight-Scripts).

**receipt** — the client's own record that it installed an item, kept under
`HKLM\SOFTWARE\ManagedInstalls\<Name>` as a `version` value. It is the last detection gate
before the fallback, and it believes itself: if the software was removed outside Cimian and
the pkgsinfo declares no other check, the receipt still reports it installed. Note that
Cimian has **no `receipts` key** in a pkgsinfo — Munki's package-receipt array has no
equivalent here. See
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

**recurring item** — a pkgsinfo with `recurring: true`, exempt from loop suppression for
idempotent maintenance work, but without the never-installed, no-receipt semantics of an
on-demand item. See [Install Loop Prevention](Install-Loop-Prevention).

**the repo** — the served Cimian repository: the five directories `pkgsinfo/`, `pkgs/`,
`catalogs/`, `manifests/` and `icons/` published over HTTP or HTTPS. Not to be confused with
the Cimian source repository. See [The Cimian Repository](The-Cimian-Repository).

**restart action** — the `restart_action` key, one of the literal, case-sensitive values
`RequireRestart`, `RecommendRestart`, `RequireLogout` or `RecommendLogout`. In an automatic
or bootstrap run a restart is actually performed, with a five-minute grace period; in a
manual run it is only recommended. See [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys).

**run** — one execution of `managedsoftwareupdate`. Used interchangeably with **session**.

**run type** — how a run was invoked, recorded in the session log as exactly one of
`bootstrap`, `auto`, `checkonly`, `installonly` or `manual`. See
[managedsoftwareupdate](managedsoftwareupdate).

**self-service manifest** — `C:\ProgramData\ManagedInstalls\SelfServeManifest.yaml`, the
device-local record of what a user asked for in Managed Software Center. It is merged last,
and never overrides an admin action that mandates an item's presence. Munki calls the same
thing SelfServeManifest. See
[Optional Installs And Self Service](Optional-Installs-And-Self-Service).

**session** — one run, and the directory of logs it produces at
`C:\ProgramData\ManagedInstalls\logs\YYYY-MM-DD\HHmm\`. A session killed part way through is
marked aborted by the next run rather than being left looking healthy. See
[Logging](Logging).

**site_default** — the last manifest name a client tries when nothing more specific exists,
after `Orphaned`. Whatever it contains is what an unrecognised device gets. Same name and
role as Munki's. See [Client Identifier Resolution](Client-Identifier-Resolution).

**status and reason code** — the outcome of a detection check for one item: a status
(`installed`, `pending`, `error`, `unknown`), whether action is needed, a human reason and a
machine-readable reason code such as `file_missing` or `version_outdated`. These are what
the logs and reports carry. See [Item Status Reference](Item-Status-Reference).

**unattended install** — `unattended_install: true` in a pkgsinfo, declaring the item safe
to install while somebody is using the machine. Without it, the item is deferred during an
automatic run whenever a user is active; it has no effect on a manual run. There is a
matching `unattended_uninstall`. Same meaning as Munki's. See
[Supported pkgsinfo Keys](Supported-pkgsinfo-Keys).

**uninstallable** — whether Cimian can remove an item. The `uninstallable` key defaults to
**true** and setting it to false overrides everything else; otherwise removability is
derived from what the pkgsinfo declares — an `uninstaller` entry, an `uninstall_script`, an
MSI or EXE installer type, or an MSIX identity. Note that `uninstall_method` and
`uninstaller_path` do not exist in Cimian. See
[Uninstalling Software](Uninstalling-Software).

**update chain** — the `requires` and `update_for` keys. `requires` names dependencies,
installed ahead of the item, and a failed dependency aborts its parent. `update_for` declares
an item to be an update to another, installed after it, and a failure only warns. Both are
the same idea as Munki's. See
[Dependencies And Update Chains](Dependencies-And-Update-Chains).

**version script** — the `version_script` key: inline PowerShell whose standard output is
taken as the installed version. Output that is not a parseable version compares as equal to
the catalog version, which makes the item permanently current. See
[Version Comparisons](Version-Comparisons).

## See also

- [Overview](Overview)
- [Getting Started](Getting-Started)
- [Frequently Asked Questions](Frequently-Asked-Questions)
- [Cimian for Munki Admins](Cimian-for-Munki-Admins)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Item Status Reference](Item-Status-Reference)
