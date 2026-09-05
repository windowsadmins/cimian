# Demonstration Setup

This page stands up a complete, throwaway Cimian deployment on a single Windows machine so
you can watch the whole cycle work: a repository, a locally served copy of it, a client
pointed at it, a package built from scratch, imported, assigned and installed, and then
removed again. Everything is created under `C:\CimianRepo` and `C:\CimianDemo`, and the
final section deletes all of it.

Use a machine you do not mind changing. The demonstration installs the Cimian client, which
registers a Windows service and two scheduled tasks, and it installs a package into
`C:\Program Files`.

## What you need

- Windows 10 1809 or later, x64 or arm64, with local administrator rights.
- A Cimian release MSI for the machine's architecture, from
  <https://github.com/windowsadmins/cimian/releases>. The file is named
  `Cimian-<yyyy.MM.dd.HHmm>-x64.msi` or `...-arm64.msi`. Releases are unsigned.
- Two elevated PowerShell windows. One will be occupied by the web server.

No .NET runtime is required; every Cimian binary is self-contained.

## 1. Install the client

From an elevated PowerShell window, in the directory holding the downloaded MSI:

```powershell
$msi = (Get-ChildItem .\Cimian-*-x64.msi | Sort-Object Name -Descending | Select-Object -First 1).FullName
msiexec.exe /i "$msi" /qn /norestart /l*v "$env:TEMP\cimian_install.log"
```

Installation prepends `C:\Program Files\Cimian` to the system PATH, which only affects
processes started afterwards. **Close that window and open a new elevated PowerShell
window** before continuing, then confirm the tools are on the path:

```powershell
managedsoftwareupdate --version
```

Full detail on what was installed is in [Installing Cimian](Installing-Cimian).

## 2. Create the repository

Create the four authored directories. `catalogs\` is left out deliberately; `makecatalogs`
creates it.

```powershell
New-Item -ItemType Directory -Path C:\CimianRepo\pkgsinfo, C:\CimianRepo\pkgs, C:\CimianRepo\manifests, C:\CimianRepo\icons
```

Generate the initial, empty catalogs:

```powershell
makecatalogs --repo_path C:\CimianRepo
```

That writes `C:\CimianRepo\catalogs\All.yaml` with no items, which is the correct starting
state. The layout is explained in [The Cimian Repository](The-Cimian-Repository).

## 3. Serve the repository over HTTP

Cimian speaks only HTTP and HTTPS — it cannot read a repository from a local path or a UNC
share — so the repo has to be served even when it is on the same machine.

Save this as `C:\CimianDemo\serve-repo.ps1`:

```powershell
$root = 'C:\CimianRepo'
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add('http://localhost:8080/')
$listener.Start()
Write-Host "Serving $root at http://localhost:8080/ - press Ctrl+C to stop"

while ($listener.IsListening) {
    $context = $listener.GetContext()
    $response = $context.Response
    $relative = [Uri]::UnescapeDataString($context.Request.Url.AbsolutePath).TrimStart('/') -replace '/', '\'
    $path = Join-Path $root $relative

    if ($relative -and (Test-Path -LiteralPath $path -PathType Leaf)) {
        $response.StatusCode = 200
        $response.ContentType = 'application/octet-stream'
        if ($context.Request.HttpMethod -eq 'GET') {
            $bytes = [System.IO.File]::ReadAllBytes($path)
            $response.ContentLength64 = $bytes.Length
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
    }
    else {
        $response.StatusCode = 404
    }

    Write-Host ("{0} {1} -> {2}" -f $context.Request.HttpMethod, $context.Request.Url.AbsolutePath, $response.StatusCode)
    $response.Close()
}
```

Run it in a **second** elevated PowerShell window and leave it running. Binding
`http://localhost:8080/` needs elevation.

```powershell
powershell.exe -ExecutionPolicy Bypass -File C:\CimianDemo\serve-repo.ps1
```

This is a demonstration server, not a production one. It serves any file under
`C:\CimianRepo` to anything on the loopback interface, answers `HEAD` without a
`Content-Length` so downloads cannot resume, and performs no path validation. What it does
get right is the one thing that matters to the client: a missing file returns a real HTTP
404, which is what allows manifest fallback to work. See
[The Cimian Repository](The-Cimian-Repository) for the requirements a real web server has to
meet.

Check it from the first window:

```powershell
Invoke-WebRequest http://localhost:8080/catalogs/All.yaml -UseBasicParsing | Select-Object StatusCode
```

## 4. Configure the machine as both client and admin workstation

One file does both jobs. The client reads PascalCase keys and ignores everything else;
`cimiimport` reads `RepoPath`; `makecatalogs` and `manifestutil` read `repo_path`. Setting
all of them keeps the whole toolchain pointed at the same place.

On an arm64 machine, use `arm64` for `DefaultArch`.

```powershell
Set-Content -Encoding utf8 -Path C:\ProgramData\ManagedInstalls\Config.yaml -Value @'
SoftwareRepoURL: http://localhost:8080
ClientIdentifier: WORKSTATION-01
RepoPath: C:\CimianRepo
repo_path: C:\CimianRepo
DefaultCatalog: Production
DefaultArch: x64
'@
```

`C:\ProgramData\ManagedInstalls` already exists if the client has run; create it first if
`Set-Content` complains:

```powershell
New-Item -ItemType Directory -Force -Path C:\ProgramData\ManagedInstalls
```

Confirm the client agrees with you about its settings:

```powershell
managedsoftwareupdate --show-config
```

Every key is documented in [Client Configuration](Client-Configuration).

## 5. Build a test package

`cimipkg` builds an MSI from a project directory. Scaffold one:

```powershell
cimipkg --create C:\CimianDemo\ExampleApp
```

Replace the generated `build-info.yaml`. Because `install_location` is set, this is a
copy-type package: the payload tree is reproduced verbatim under that directory as tracked
MSI components.

```powershell
Set-Content -Encoding utf8 -Path C:\CimianDemo\ExampleApp\build-info.yaml -Value @'
product:
  name: ExampleApp
  version: 1.0.0
  developer: Example Vendor
  identifier: com.example.exampleapp
  description: A trivial package used to exercise a Cimian repository
install_location: C:\Program Files\Example App
'@
```

Give it something to install:

```powershell
Set-Content -Encoding utf8 -Path C:\CimianDemo\ExampleApp\payload\readme.txt -Value 'Installed by Cimian.'
```

Build it. `--skip-import` suppresses the prompt that would otherwise offer to run
`cimiimport` for you, so that the import below is an explicit step.

```powershell
cimipkg --skip-import C:\CimianDemo\ExampleApp
```

The result is `C:\CimianDemo\ExampleApp\build\ExampleApp-1.0.0.msi`. [cimipkg](cimipkg)
covers the full project format, `${VAR}` substitution and signing.

## 6. Import it into the repository

```powershell
cimiimport C:\CimianDemo\ExampleApp\build\ExampleApp-1.0.0.msi
```

`cimiimport` reads the MSI, then prompts for each metadata field with a default in brackets.
Press Enter to accept a default. For this walkthrough:

| Prompt | Answer |
|---|---|
| `Name [ExampleApp]: ` | Enter |
| `Version [1.0.0]: ` | Enter |
| `Developer [Example Vendor]: ` | Enter |
| `Description [...]: ` | Enter |
| `Category []: ` | `Utilities` |
| `Architecture(s) [x64]: ` | Enter |
| `Catalogs [Production]: ` | Enter |
| `Location in repo [...]: ` | `\demo` |
| `Import this item? (y/n) [n]: ` | `y` |

**Only a literal `y` proceeds.** Anything else cancels and exits 0, so a cancelled import
looks like a successful one to a script.

Do not reach for `--nointeractive` here. The fallback that fills an empty catalog list with
your configured default lives in the prompt, so a non-interactive import of this package
would write a pkgsinfo with no catalogs and no catalog would ever contain it.

`cimiimport` copies the payload, writes the pkgsinfo, and runs `makecatalogs` itself. Look
at what it wrote:

```powershell
Get-Content C:\CimianRepo\pkgsinfo\demo\ExampleApp-x64-1.0.0.yaml
```

It should look close to this. The `installs` entry is what the client uses to decide whether
the package is present — here, the MSI's registration in the Windows Installer database.

```yaml
name: ExampleApp
display_name: ExampleApp
version: 1.0.0
catalogs:
- Production
category: Utilities
description: A trivial package used to exercise a Cimian repository
developer: Example Vendor
installer:
  location: /demo/ExampleApp-x64-1.0.0.msi
  type: msi
installs:
- type: msi
  product_code: '{...}'
  upgrade_code: '{...}'
  display_name: ExampleApp
supported_architectures:
- x64
unattended_install: true
unattended_uninstall: true
```

Confirm the catalog picked it up:

```powershell
Select-String -Path C:\CimianRepo\catalogs\Production.yaml -Pattern '^- name: ExampleApp$'
```

If that returns nothing, the catalogs list in the pkgsinfo is wrong or `makecatalogs` failed;
fix the pkgsinfo and run `makecatalogs --repo_path C:\CimianRepo` again.

## 7. Assign it to the client

The client asks for a manifest named after its `ClientIdentifier` first, so it will request
`WORKSTATION-01`.

```powershell
Set-Content -Encoding utf8 -Path C:\CimianRepo\manifests\WORKSTATION-01.yaml -Value @'
name: WORKSTATION-01
catalogs:
- Production
managed_installs:
- ExampleApp
'@
```

[manifestutil](manifestutil) can do the same edit without a text editor:

```powershell
manifestutil --manifest WORKSTATION-01 --add-pkg ExampleApp
```

## 8. Check what would happen

`--checkonly` runs the whole detection pass — manifest resolution, catalog download, status
checks — and installs nothing.

```powershell
managedsoftwareupdate --checkonly -vv
```

The output should resolve `WORKSTATION-01`, load the `Production` catalog, and list
`ExampleApp` as needing installation. The server window shows the matching requests and
their status codes.

The plan is also written to disk, which is what Managed Software Center reads:

```powershell
Get-Content C:\ProgramData\ManagedInstalls\InstallInfo.yaml
```

If the manifest does not resolve, start at
[Client Identifier Resolution](Client-Identifier-Resolution).

## 9. Install it

```powershell
managedsoftwareupdate -v
```

This is a manual run: it downloads the payload into the cache, installs it, and writes its
reports. `--auto` is what the hourly scheduled task uses; it additionally defers items that
would interrupt a signed-in user.

Verify the file landed:

```powershell
Get-Content 'C:\Program Files\Example App\readme.txt'
```

Verify Cimian's own receipt:

```powershell
Get-ItemProperty HKLM:\SOFTWARE\ManagedInstalls\ExampleApp
```

Read the session log. Each run gets its own directory under `logs\`:

```powershell
Get-Content (Get-ChildItem C:\ProgramData\ManagedInstalls\logs -Recurse -Filter install.log | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
```

Then prove convergence — a second check-only run should report nothing to do:

```powershell
managedsoftwareupdate --checkonly
```

A package that still reports "needs install" immediately after a successful install is the
exact failure [Install Loop Prevention](Install-Loop-Prevention) exists to catch, and it
will appear in `C:\ProgramData\ManagedInstalls\reports\loop_suppressed.json`.

## 10. Remove it again

Change the manifest so the item is removed rather than installed:

```powershell
Set-Content -Encoding utf8 -Path C:\CimianRepo\manifests\WORKSTATION-01.yaml -Value @'
name: WORKSTATION-01
catalogs:
- Production
managed_uninstalls:
- ExampleApp
'@
```

```powershell
managedsoftwareupdate -v
```

The item has an MSI installer type and an `installs` entry carrying a ProductCode, so Cimian
synthesises an `msiexec /x` removal without you declaring an uninstaller. Confirm:

```powershell
Test-Path 'C:\Program Files\Example App\readme.txt'
```

[Uninstalling Software](Uninstalling-Software) covers the cases where removal has to be
declared explicitly.

## Teardown

Stop the web server with Ctrl+C in its window.

If you skipped step 10, remove the demonstration package first:

```powershell
Get-CimInstance Win32_Product -Filter "Name='ExampleApp'" | Invoke-CimMethod -MethodName Uninstall
```

Remove the client. This takes out the service, both scheduled tasks, the PATH entry and the
registry stamp:

```powershell
Get-CimInstance Win32_Product -Filter "Name LIKE 'Cimian%'" | Invoke-CimMethod -MethodName Uninstall
```

Delete the client's working data, the repository and the demonstration project:

```powershell
Remove-Item -Recurse -Force C:\ProgramData\ManagedInstalls, C:\CimianRepo, C:\CimianDemo
```

Confirm nothing is left behind:

```powershell
Get-Service CimianWatcher -ErrorAction SilentlyContinue
Get-ScheduledTask -TaskName 'Cimian *' -ErrorAction SilentlyContinue
```

[Removing Cimian](Removing-Cimian) covers uninstall in full, including what to do when the
service or the tasks survive.

## Where to go next

The routine version of steps 5 to 9 — including optional installs, targeting a subset of
machines, and promoting between catalogs — is
[Installing Software](Installing-Software).

## See also

- [Getting Started](Getting-Started)
- [Installing Software](Installing-Software)
- [Installing Cimian](Installing-Cimian)
- [The Cimian Repository](The-Cimian-Repository)
- [Client Configuration](Client-Configuration)
- [Manifests](Manifests)
- [cimipkg](cimipkg)
- [cimiimport](cimiimport)
- [makecatalogs](makecatalogs)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Removing Cimian](Removing-Cimian)
- [Troubleshooting](Troubleshooting)
