# Command-Line Tools

Cimian ships as a set of small command-line programs rather than one monolithic
binary. This page introduces the whole set, says which programs you run on a
managed client and which you run against a Cimian repository, and links to the
reference page for each one.

Every tool is a native Windows executable installed into `C:\Program Files\Cimian`,
which the installer adds to the machine `PATH`. You can therefore run any of them
by name from an elevated Command Prompt or PowerShell session.

## Client tools and repository tools

Cimian does not ship two separate installers. The MSI puts every executable on
every machine it installs on. The distinction below is about where each tool is
*meant* to be run, not about what is present on disk.

**Client tools** operate on the local machine: they read the client's
configuration, contact the repo over HTTP, and change what software is installed.
They almost always require administrator rights.

**Repository tools** operate on a Cimian repository — a directory tree of
`pkgsinfo/`, `pkgs/`, `catalogs/`, `manifests/` and `icons/` that you serve to
clients over HTTPS. You run these on an admin workstation that has the repository
checked out or mounted. They never touch the local machine's installed software.

## The tools

| Tool | Runs on | Purpose |
|---|---|---|
| [managedsoftwareupdate](managedsoftwareupdate) | Client | The update engine. Reads manifests and catalogs, decides what needs to be installed, updated or removed, and does it. |
| [cimiwatcher](cimiwatcher) | Client | The `CimianWatcher` Windows service. Polls for trigger files and launches `managedsoftwareupdate` as SYSTEM, so an on-demand run needs no UAC prompt. |
| [cimitrigger](cimitrigger) | Client | Asks `CimianWatcher` to run an update now, falling back to direct elevation if the service does not answer. |
| [cimistatus](cimistatus) | Client | The progress window shown during a run. |
| [cimipkg](cimipkg) | Repository | Builds a deployment package (`.msi` by default) from a project directory of payload files, scripts and a `build-info.yaml`. |
| [cimiimport](cimiimport) | Repository | Imports an installer into the repository: extracts metadata, writes the pkgsinfo, copies the installer into `pkgs/`, then rebuilds the catalogs. |
| [makepkginfo](makepkginfo) | Repository | Generates pkgsinfo YAML for an installer and prints it to stdout, without importing anything. |
| [makecatalogs](makecatalogs) | Repository | Scans `pkgsinfo/` and regenerates the catalog files clients download. |
| [manifestutil](manifestutil) | Repository | Lists and edits manifests, and edits the self-service manifest. |
| [repoclean](repoclean) | Repository | Removes superseded versions of items from the repository, keeping the newest few. |

## Which tool for which job

To get software onto a client for the first time, you build or obtain an
installer, import it with `cimiimport` (which calls `makecatalogs` for you), add
the item to a manifest with `manifestutil`, and then either wait for the client's
hourly run or force one with `cimitrigger`. See
[Installing Software](Installing-Software) for the end-to-end walkthrough.

To find out what a client thinks it should do without changing anything, run
`managedsoftwareupdate --checkonly` on that client. It writes `InstallInfo.yaml`
and exits without installing.

To package software that has no usable installer of its own — a folder of files,
a script, a set of user settings — use `cimipkg` to build an MSI around it. See
[Introduction to pkgsinfo Files](Introduction-To-pkgsinfo-Files).

## Getting help from a tool

Every tool accepts `--help`. `managedsoftwareupdate` also accepts `-V` and
`--version`; `makecatalogs`, `manifestutil` and `repoclean` accept `-V` only.

Note that flag naming is not consistent across the set. `cimiimport` mixes
hyphens and underscores (`--postinstall-script` but `--minimum_os_version`),
`makepkginfo` uses underscores almost throughout (`--installcheck_script`, but
`--pkg-version`), and `repoclean` uses hyphens. Each tool's page gives the exact
spellings.

## See also

- [How Cimian Runs](How-Cimian-Runs)
- [Client Configuration](Client-Configuration)
- [The Cimian Repository](The-Cimian-Repository)
- [Manifests](Manifests)
- [Troubleshooting](Troubleshooting)
