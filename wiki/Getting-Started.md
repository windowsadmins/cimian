# Getting Started

This is the shortest real path from nothing to one machine installing one package: create a
repository, serve it, install the client, import an installer, generate catalogs, write a
manifest, and run. Every command here is meant to be pasted into an elevated PowerShell
prompt and run as written.

The walkthrough uses one machine as both the admin workstation and the managed client,
which is the quickest way to see the whole loop. Splitting the two is a matter of putting
the repository on a different host and giving each machine the half of the configuration it
needs; the steps are otherwise identical.

You need local administrator rights throughout. If you would rather stand up a throwaway
environment to experiment in, see [Demonstration Setup](Demonstration-Setup).

## 1. Install the tools and the client

Download the MSI for the machine's architecture from the project's releases and install it
silently. The MSI contains both the client and the authoring tools.

```powershell
$msi = (Get-ChildItem .\Cimian-*-x64.msi | Sort-Object Name -Descending | Select-Object -First 1).FullName
msiexec.exe /i "$msi" /qn /norestart /l*v "$env:TEMP\cimian_install.log"
```

This installs to `C:\Program Files\Cimian`, prepends that directory to the machine PATH,
registers the `CimianWatcher` service and registers an hourly scheduled task that runs
`managedsoftwareupdate.exe --auto`. Open a new PowerShell window so the PATH change applies,
then confirm the tools are reachable:

```powershell
managedsoftwareupdate --version
```

Releases published from the source repository are unsigned. Full detail, including mass
deployment, is on [Installing Cimian](Installing-Cimian).

## 2. Create the repository

Four directories. `catalogs\` is deliberately absent — `makecatalogs` creates it.

```powershell
New-Item -ItemType Directory -Path C:\CimianRepo\pkgsinfo, C:\CimianRepo\pkgs, C:\CimianRepo\manifests, C:\CimianRepo\icons
```

That is the whole repository. See [The Cimian Repository](The-Cimian-Repository) for what
each directory holds.

## 3. Serve it over HTTP

Cimian speaks only HTTP and HTTPS, and needs nothing but a static file server. Any will do;
this uses IIS because it is already on the machine. Enable the web server and its
PowerShell module:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole, IIS-ManagementScriptingTools -All
```

Publish the repository directory on port 8080:

```powershell
Import-Module WebAdministration
New-Website -Name CimianRepo -Port 8080 -PhysicalPath C:\CimianRepo
```

IIS refuses to serve a file whose extension has no MIME mapping, and answers 404 when it
does — which the client cannot tell apart from a genuinely missing file. `.yaml` has no
default mapping, so add one:

```powershell
Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST/CimianRepo' -Filter system.webServer/staticContent -Name . -Value @{fileExtension='.yaml'; mimeType='text/yaml'}
```

`.msi`, `.exe` and `.png` are mapped by default. Add `.nupkg`, `.msix`, `.appx` and `.ps1`
the same way if you intend to ship those payload types. Serving to other machines also needs
the port opened in the firewall, and a real deployment should use HTTPS — see
[Securing The Repository](Securing-The-Repository).

## 4. Configure the machine

Both halves of the configuration live in `C:\ProgramData\ManagedInstalls\Config.yaml`. The
client half points at the repository over HTTP; the admin-tool half points at the same
repository as a local path. They share a file and do not collide.

```powershell
New-Item -ItemType Directory -Force -Path C:\ProgramData\ManagedInstalls | Out-Null
@'
SoftwareRepoURL: http://localhost:8080
ClientIdentifier: WORKSTATION-01
RepoPath: C:\CimianRepo
repo_path: C:\CimianRepo
DefaultCatalog: Production
DefaultArch: x64
'@ | Set-Content -Encoding utf8 C:\ProgramData\ManagedInstalls\Config.yaml
```

`SoftwareRepoURL` and `ClientIdentifier` are read by the client; keys are PascalCase and an
unrecognised key is silently ignored. `RepoPath` and `repo_path` are both needed because the
authoring tools disagree on the spelling: `cimiimport` and `makepkginfo` read `RepoPath`,
while `makecatalogs` and `manifestutil` read `repo_path`.

`ClientIdentifier` is the name of the manifest this device asks for, and it is what step 7
creates. Use whatever you like; `WORKSTATION-01` is a placeholder.

Check what the client resolved:

```powershell
managedsoftwareupdate --show-config
```

The full key list is on [Client Configuration](Client-Configuration).

## 5. Import a package

Take any MSI or EXE installer and import it. `cimiimport` extracts the metadata, writes a
pkgsinfo into `pkgsinfo\`, copies the payload into `pkgs\`, and then runs `makecatalogs`
for you.

```powershell
cimiimport C:\Downloads\ExampleApp-1.2.0.msi
```

It prompts for name, version, developer, description, category, architectures, catalogs and
the location within `pkgs\`. Press Enter to accept each extracted default, but set
**Catalogs** to `Production` — that is the catalog the manifest in step 7 uses. The final
prompt, `Import this item? (y/n) [n]:`, defaults to **no**, so answer `y`.

Look at what it wrote before moving on:

```powershell
Get-ChildItem -Recurse C:\CimianRepo\pkgsinfo, C:\CimianRepo\pkgs
```

The generated file is a normal pkgsinfo and you can edit it — particularly its `installs:`
array, which is the detection that decides whether this package is already present. See
[cimiimport](cimiimport), [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
and [Installs Arrays](Installs-Arrays).

## 6. Generate the catalogs

`cimiimport` already ran this, but any hand edit to a pkgsinfo needs it again. **Nothing you
change in `pkgsinfo\` reaches a device until catalogs are regenerated.**

```powershell
makecatalogs --repo_path C:\CimianRepo
```

Confirm the catalog exists and is reachable over HTTP — this proves the whole serving path
at once:

```powershell
Invoke-WebRequest http://localhost:8080/catalogs/Production.yaml -UseBasicParsing | Select-Object -ExpandProperty Content
```

See [makecatalogs](makecatalogs) and [Using Catalogs](Using-Catalogs).

## 7. Write a manifest

The manifest names the catalogs to search and the items to install. Its file name must match
the `ClientIdentifier` from step 4.

```powershell
@'
name: WORKSTATION-01
catalogs:
  - Production
managed_installs:
  - ExampleApp
optional_installs: []
'@ | Set-Content -Encoding utf8 C:\CimianRepo\manifests\WORKSTATION-01.yaml
```

Replace `ExampleApp` with the `name` value from the pkgsinfo you just imported — the item
name, not the display name and not the file name.

For later edits, `manifestutil` adds and removes items without hand-editing:

```powershell
manifestutil --manifest WORKSTATION-01 --add-pkg ExampleApp --section managed_installs
```

Note that `manifestutil` understands only a subset of the manifest keys and drops the rest
when it rewrites a file. See [Manifests](Manifests) and [manifestutil](manifestutil).

## 8. Check what the client would do

A check-only run resolves the manifest, loads the catalogs, checks the state of every item
and prints what it would do, without downloading or installing anything.

```powershell
managedsoftwareupdate --checkonly -vv
```

Read the output for three things: which manifest it resolved (a warning here means it fell
through to a catch-all name), that the catalog loaded, and that `ExampleApp` is listed as
pending. If the manifest did not resolve, start at
[Client Identifier Resolution](Client-Identifier-Resolution); anything else, at
[Troubleshooting](Troubleshooting).

## 9. Run it

Without a mode flag this is a manual run: check, download, install, remove.

```powershell
managedsoftwareupdate -vv
```

The package is downloaded to `C:\ProgramData\ManagedInstalls\Cache`, verified against the
hash in the catalog, installed, and then re-checked — the client immediately re-runs the
item's own detection to prove the install actually converged. See
[managedsoftwareupdate](managedsoftwareupdate).

## 10. See the result

The state file Managed Software Center reads:

```powershell
Get-Content C:\ProgramData\ManagedInstalls\InstallInfo.yaml
```

The human-readable trace of the last run, which is truncated and rewritten each session:

```powershell
Get-Content C:\ProgramData\ManagedInstalls\reports\run.log -Tail 40
```

The per-item record, which is what a reporting system collects:

```powershell
Get-Content C:\ProgramData\ManagedInstalls\reports\items.json
```

Then run the check again. The item should now report as installed, and nothing should be
pending:

```powershell
managedsoftwareupdate --checkonly
```

If it is still pending, the install worked and the detection is wrong — that is the single
most common authoring defect, and it is what
[How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
and [Install Loop Prevention](Install-Loop-Prevention) are about.

Finally, open **Managed Software Center** from the Start menu to see the same state as a
user does. Nothing further is needed to keep this machine current: the hourly scheduled task
installed in step 1 now repeats step 9 on its own.

## What to read next

- [Overview](Overview) — the model and the pieces, if you skipped it.
- [Cimian for Munki Admins](Cimian-for-Munki-Admins) — if you already run Munki.
- [Glossary](Glossary) — every term this wiki uses.
- [Manifests](Manifests) — the recommended role-based layout, before your manifests multiply.
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys) — the complete key reference, including
  the keys that are accepted and then ignored.
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
  — read before authoring a second package.
- [Using Catalogs](Using-Catalogs) and [Promoting Between Catalogs](Promoting-Between-Catalogs)
  — a development, testing and production workflow.
- [Cimian With Git](Cimian-With-Git) — putting the repository in version control.
- [Securing The Repository](Securing-The-Repository) — HTTPS and authentication.
- [Bootstrapping With Cimian](Bootstrapping-With-Cimian) — provisioning a freshly imaged
  machine.
- [Frequently Asked Questions](Frequently-Asked-Questions)
