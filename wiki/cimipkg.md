# cimipkg

`cimipkg` builds a deployable Windows package from a project directory, in the same
spirit as munki-pkg on macOS. You describe the package in `build-info.yaml`, drop files
into `payload/` and scripts into `scripts/`, and `cimipkg` produces an installer you can
then import into the repo with [cimiimport](cimiimport). This page covers the project
layout, every `build-info.yaml` key, the `${VAR}` substitution contract, signing, version
handling and the complete flag reference.

## What it produces

The default output is a **Windows Installer package (`.msi`)** authored directly by
`cimipkg` — there is no WiX project and no external toolchain beyond `makecab.exe`. The
MSI is the format the Cimian client installs best: `managedsoftwareupdate` can read its
ProductCode, UpgradeCode and version back out of the registry, so installs are detectable
without you writing an installcheck script.

Two other formats are available:

| Flag | Output | Notes |
|---|---|---|
| *(none)* | `.msi` | Default. Deterministic UpgradeCode derived from `product.identifier`. |
| `--nupkg` | `.nupkg` | Chocolatey-compatible. Requires `nuget` on `PATH` or the build fails. |
| `--pkg` | `.pkg` | Legacy ZIP for the sbin installer. Marked for removal in source; do not use for new packages. |
| `--intunewin` | `.intunewin` | Produced *in addition* to whichever of the above was built. |

`--intunewin` wraps the built package with `IntuneWinAppUtil.exe`. If that tool is not on
`PATH`, the step is skipped with a warning and **the build still exits 0** — check for the
file rather than trusting the exit code.

Every artifact lands in `<project-directory>\build\`, which is deleted and recreated at
the start of every build.

## Project directory layout

A project is a directory containing `build-info.yaml`. Everything else is optional.

```
ExampleApp\
  build-info.yaml
  payload\
  scripts\
  .env
  build\
```

| Path | Required | Purpose |
|---|---|---|
| `build-info.yaml` | yes | Package metadata. That exact name — no `.yml`, `.json` or `.plist` alternative is searched. Missing file, or an empty `product.name` or `product.identifier`, fails the build. |
| `payload/` | no | Files to ship. Collected recursively. A package with no files under `payload/` is still valid. |
| `scripts/` | no | Install-time scripts. Only the **top level** of this directory is scanned. |
| `.env` | no | Values for `${VAR}` substitution. Auto-detected at the project root; override with `--env`. |
| `build/` | generated | Wiped and recreated on every build. Never put source files here. |

`cimipkg --create <path>` scaffolds `payload/`, `scripts/`, a `build-info.yaml` template
and a commented `.env`.

### Recognised script names

Scripts are matched by glob at the top level of `scripts/` only, sorted case-insensitively
ascending, and concatenated in that order. So `preinstall01.ps1` and `preinstall02.ps1`
both run, in that order, as one script.

| Glob | Runs | MSI custom action |
|---|---|---|
| `preinstall*.ps1` | before the payload is written | `CimianPreinstall` |
| `postinstall*.ps1` | after the payload is written | `CimianPostinstall` |
| `uninstall*.ps1` | on uninstall | `CimianUninstall` |

Only `.ps1` is recognised on the MSI path. The `.nupkg` and `.pkg` paths match the exact
name `uninstall.ps1` rather than the glob, and additionally process `.psm1`, `.psd1`,
`.sh`, `.cmd` and `.bat` files for placeholder substitution.

A non-zero exit from a preinstall or postinstall script fails the MSI. That is deliberate:
a package that reports success while its script failed produces an item that installchecks
refute on every subsequent run.

Each script's output is written to `%ProgramData%\ManagedInstalls\logs\packages\<ProductName>\`
as `preinstall.log`, `postinstall.log` or `uninstall.log`, and the first 200 lines are also
echoed into the MSI log.

### Variables available to scripts

`cimipkg` prepends a header to each combined script so it can find its own payload:

| Variable | Copy-type value | Installer-type value |
|---|---|---|
| `$payloadRoot` | the literal `install_location` | `$env:CIMIAN_INSTALLDIR`, falling back to `$PWD.Path` |
| `$payloadDir` | same as `$payloadRoot` | same as `$payloadRoot` |
| `$installLocation` | same as `$payloadRoot` | same as `$payloadRoot` |

If the payload contains any `.exe`, `cimipkg` also injects a preamble into the preinstall
action that stops scheduled tasks referencing those executables and kills matching running
processes under `%ProgramFiles%`. Without it, a self-updating tool holding its own binary
open has the replacement deferred to the next reboot while the receipt advances anyway.

## Installer-type and copy-type packages

Which one you get is decided by `install_location`, and the difference matters at install
time.

**Copy-type** — `install_location` is set. `cimipkg` resolves it against the well-known MSI
folders (`ProgramFiles64Folder`, `CommonAppDataFolder` and so on), and the payload is
installed there as real, tracked MSI components. The files stay on disk; uninstalling the
package removes them. Use this for scripts, configuration, fonts, add-ons — anything where
the payload *is* the product.

**Installer-type** — `install_location` is blank or absent. The payload is staged into a
temporary directory (`TempFolder\p_<guid>`, resolved at install time) and your postinstall
script is expected to do the real work: run the vendor's `setup.exe` or `msiexec` from
`$payloadRoot`. The wrapper MSI additionally sets `ARPSYSTEMCOMPONENT=1`, so it does not
appear in Programs and Features next to the vendor's own entry — but it stays visible to
MSI tooling, which is how the Cimian client tracks it.

Two consequences of installer-type worth knowing:

- The wrapper's own version has nothing to do with the wrapped application's version. The
  pkgsinfo `installs` array must describe the **wrapped** application, not the wrapper.
  See [Installs Arrays](Installs-Arrays).
- `product.installer_type` is a *separate* setting. It selects the Chocolatey install
  command and default arguments for `--nupkg`, and it relaxes the `--nupkg` requirement for
  an `install_location`. It does not by itself make an MSI installer-type.

For `--nupkg` only, a payload with no `install_location` and no `product.installer_type`
fails with `install_location must be specified when payload exists and the package is not
an installer.`

## build-info.yaml

The file is parsed with snake_case key names, and **unknown keys are silently ignored** —
a typo costs you the setting with no error.

### `product:` block

| Key | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | — | Required. MSI `ProductName` and the output filename stem. |
| `version` | string | `1.0.0` | Package version. See [Version handling](#version-handling). |
| `identifier` | string | — | Required. Reverse-domain id. Seeds the deterministic MSI UpgradeCode, names the cabinet, and becomes the nuspec id. Changing it changes the UpgradeCode. |
| `developer` | string | none | MSI `Manufacturer` (falls back to `Unknown`) and nuspec authors. |
| `description` | string | none | MSI `ARPCOMMENTS` and nuspec description. |
| `installer_type` | string | none | `msi`, `exe`, or any other token. Selects Chocolatey install/uninstall defaults for `--nupkg`. Lower-cased; unrecognised values are treated as `exe` for argument defaults. |
| `url` | string | none | MSI `ARPURLINFOABOUT` and nuspec projectUrl. |
| `copyright` | string | none | nuspec copyright only. |
| `license` | string | none | nuspec licenseUrl only. |
| `tags` | list of string | none | nuspec tags only, space-joined. |

### Top-level keys

| Key | Type | Default | Meaning |
|---|---|---|---|
| `install_location` | string | none | Payload destination. Blank makes the package installer-type. |
| `upgrade_code` | string | none | Explicit MSI UpgradeCode GUID. Omit it and `cimipkg` derives one deterministically from `product.identifier`. |
| `msi_properties` | map of string to string | none | Written verbatim into the MSI Property table. A `SecureCustomProperties` you supply is unioned with `PREVIOUSVERSIONSINSTALLED` rather than replacing it. |
| `signing_certificate` | string | none | signtool `/n` subject. Matched as a substring of the certificate subject. |
| `signing_thumbprint` | string | none | signtool `/sha1` thumbprint. Beats `signing_certificate` when both are set. |
| `key_path` | string | none | Substituted and round-tripped into the MSI for `cimiimport` to read. `cimipkg` itself does not use it. |
| `install_arguments` | string | see below | `--nupkg` only. Arguments passed to the wrapped installer. |
| `uninstall_arguments` | string | see below | `--nupkg` only. |
| `valid_exit_codes` | string | `0,3010` | `--nupkg` only. Comma-separated. |
| `software_detection` | string | none | `--nupkg` only. Uninstall matches registry `DisplayName -like "*value*"` in both Uninstall hives. |
| `override_uninstall_script` | bool | `false` | `--nupkg` only. Uses `scripts\uninstall.ps1` verbatim instead of the generated uninstall body. |
| `postinstall_action` | string | none | Accepted values `logout`, `restart`, `reboot`, `shutdown`, `none`. **Emits a log line only — no action is performed.** |
| `icon` | string | none | nuspec iconUrl only. Not used for the MSI or for Managed Software Center icons. |
| `signature` | map | none | Output, not input. Written back into a `.pkg`'s embedded `build-info.yaml`. |
| `category` | string | none | **Accepted and never read.** |
| `minimum_os_version` | string | none | **Accepted and never read.** |
| `blocking_applications` | list of string | none | **Accepted and never read.** Blocking applications belong in the pkgsinfo — see [Blocking Applications](Blocking-Applications). |

Default `install_arguments` when unset: `/qn /norestart ALLUSERS=1` for `installer_type: msi`,
`/S /silent` for `exe`, `/S` otherwise. Default `uninstall_arguments`: `/qn /norestart` for
`msi`, `/S /silent /uninstall` for `exe`, `/S` otherwise.

Every MSI also carries `CIMIAN_PKG_IDENTIFIER`, `CIMIAN_PKG_FULL_VERSION` and
`CIMIAN_PKG_BUILD_INFO` (base64 of the resolved `build-info.yaml`), plus fixed
`ALLUSERS=1`, `ARPNOREPAIR=1`, `ARPNOMODIFY=1`, `MSIFASTINSTALL=7`,
`MsiLogging=voicewarmup`, `REBOOT=ReallySuppress`, `MSIRESTARTMANAGERCONTROL=Disable`,
`REINSTALLMODE=amus` and `ProductLanguage=1033`.

## `${VAR}` substitution

`cimipkg` expands `${NAME}` placeholders in `build-info.yaml` fields and in script bodies,
so a package can be built from a checked-in project without checked-in secrets.

**Only the braced form `${NAME}` is substituted. A bare `$NAME` is never touched.** This is
not an oversight. Bare-`$` matching is indistinguishable from an ordinary PowerShell
variable, so combined with environment fallback it rewrote `$Path`, `$config` and similar
variables inside embedded scripts with build-agent values, turning every affected package
into an install-time failure. `$env:NAME` and `${env:NAME}` contain a colon and so never
match the pattern either.

The name must match `[A-Za-z_][A-Za-z0-9_]*`.

### The `.env` file

Line-based. Blank lines and lines starting with `#` are skipped. Each remaining line is
split on the **first** `=`; the key and value are trimmed and a single layer of surrounding
`'` or `"` is stripped from the value. Keys are matched case-insensitively.

```
ExampleApiKey=abc123
ExampleServiceAccount='EXAMPLE\svc-deploy'
```

In addition, every process environment variable whose name begins with `CIMIPKG_` is merged
into the same dictionary **with the prefix kept**, so a `CIMIPKG_LICENSE` environment
variable resolves `${CIMIPKG_LICENSE}`. Values from `.env` win on conflict. The prefix
exists so that arbitrary environment variables — tokens, credentials, `PATH` — cannot leak
into a script.

### Resolution order

For `build-info.yaml` fields:

1. Built-in tokens: `${TIMESTAMP}` (`yyyy.MM.dd.HHmm`), `${DATE}` (`yyyy.MM.dd`),
   `${DATETIME}` (`yyyy.MM.dd.HHmmss`), and `${version}`, which back-references the resolved
   `product.version`. Inside `product.version` itself `${version}` stays literal.
2. The `.env` / `CIMIPKG_` dictionary. An empty value counts as unresolved.
3. The process environment. A whitespace-only value counts as unresolved.
4. Unresolved: **the literal `${NAME}` is left in place. No error, no warning.**

Only these eleven fields are expanded: `product.version`, `product.name`,
`product.identifier`, `product.description`, `signing_certificate`, `signing_thumbprint`,
`install_location`, `install_arguments`, `uninstall_arguments`, `upgrade_code` and
`key_path`. A `${VAR}` anywhere else in the file survives into the built package verbatim.

For **script bodies**, only the `.env` / `CIMIPKG_` dictionary is consulted. Built-in tokens
do not work in scripts, and unprefixed process environment variables are not consulted.

### Why the fail-soft behaviour is a footgun

Because an unresolved placeholder is left literal rather than failing the build, a script
that expects a value ships with the placeholder text in it. A build machine missing one
`.env` entry produces a package that installs cleanly, reports success, and configures the
application with the literal string `${ExampleApiKey}`. The application then fails at
runtime, and nothing in the install record says why.

If a value is required, assert it in the script rather than trusting the build:

```powershell
$apiKey = '${ExampleApiKey}'
if ($apiKey -notmatch '^[A-Za-z0-9]{6,}$') {
    Write-Error 'ExampleApiKey was not substituted at build time'
    exit 1
}
```

A `${...}` substitution whose result is unparseable PowerShell fails the build. Set
`CIMIAN_PKG_SKIP_SCRIPT_VALIDATION=1` to bypass that check, which you should not need.

## Signing

`cimipkg` signs with `signtool.exe`, never `Set-AuthenticodeSignature`. It looks for
`signtool.exe` under the Windows Kits 10 directory by architecture and version, then on
`PATH`, validating each candidate's PE machine type. The command it runs is:

```
signtool sign /sha1 <thumbprint> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "<file>"
```

with `/n "<subject>"` substituted for `/sha1` when only a subject is configured.

Certificate lookup checks `StoreName.My` in the CurrentUser store, then LocalMachine. A
thumbprint is compared case-insensitively for an exact match; a subject is matched as a
**substring** of the certificate subject, so an over-broad subject can select the wrong
certificate.

What actually gets signed:

- The combined pre/post/uninstall script is Authenticode-signed at build time and embedded
  signed, so [PowerShell execution policy](Scripts-In-pkgsinfo) does not block it.
- The `.msi` itself is signed when either `signing_certificate` or `signing_thumbprint`
  resolves to a value.
- For `--nupkg`, all `tools\*.ps1` are signed and then `nuget sign` runs on the archive.
  **NuGet signing failures are logged as warnings and do not fail the build.**

`--sign-thumbprint` and `--sign-cert` override the YAML values after substitution, which is
the usual way to keep the certificate identity out of a checked-in project.

## Version handling

`product.version` is parsed before the build. An unparseable version fails the build with
`Invalid version format`. Three shapes are accepted:

- **Date-based `YYYY.MM.DD` or `YYYY.MM.DD.<revision>`**, where the four-digit major is
  between 2000 and 2100. Month and day are validated and zero-padded, so `2026.9.3` becomes
  `2026.09.03`. A month of 13 or a day of 32 is an error.
- **Semantic `x.y.z`**, optionally `x.y.z.w`, `-prerelease` and `+build`. A four-digit major
  outside the 2000-2100 range — a `6000.5.4` scheme, for example — is treated as semver, not
  as a broken date.
- **Simple `x`, `x.y`, `x.y.z`, `x.y.z.w`**, padded out to at least three components.

The parsed version is what appears in the output filename. For `--nupkg` and `--pkg` the
normalised form is also written back into the package metadata: date versions lose their
zero padding (`2026.09.03` becomes `2026.9.3`) and semver build metadata after `+` is
dropped, because NuGet ignores it.

The MSI is different, because MSI `ProductVersion` is limited to `major.minor.build` with
major and minor capped at 255 and build at 65535. A date version is converted as follows:

- Major is `year - 2000`.
- If `month * 100 + day` fits in 255, the result is `YY.(MM*100+DD).<revision>`.
- Otherwise the minute component is dropped: the result is `YY.<month>.(day*100 + hour)`.
  So `2026.04.05.1423` becomes `26.4.514`, and `2026.04.24.1640` becomes `26.4.2416`.

The unabridged version is preserved in the `CIMIAN_PKG_FULL_VERSION` property, and that is
the value the Cimian client reads back. Do not use the ARP `DisplayVersion` for comparison.
See [Version Comparisons](Version-Comparisons).

## Flag reference

```
cimipkg [<project-directory>] [--verbose] [--pkg] [--nupkg] [--intunewin]
        [--env <path>] [--sign-thumbprint <hex>] [--sign-cert <subject>]
        [--skip-import] [--create <path>]
        [--resign <pkg>] [--resign-cert <name>] [--resign-thumbprint <hex>]
```

| Flag | Alias | Argument | Default | Effect |
|---|---|---|---|---|
| `<project-directory>` | — | path | `.` | Directory containing `build-info.yaml`. Must exist or the tool exits 1. |
| `--verbose` | `-v` | no | off | Debug logging, and a stack trace on error. |
| `--pkg` | — | no | off | Build the legacy `.pkg` ZIP instead of an MSI. Slated for removal. |
| `--nupkg` | — | no | off | Build a Chocolatey `.nupkg` instead of an MSI. |
| `--intunewin` | — | no | off | Additionally produce an `.intunewin` from whatever was built. |
| `--env` | `-e` | path | `<project>\.env` | `.env` file for substitution. |
| `--sign-thumbprint` | — | hex | none | Overrides `signing_thumbprint`. |
| `--sign-cert` | — | subject | none | Overrides `signing_certificate`. |
| `--skip-import` | — | no | off | Suppress the post-build prompt that offers to run `cimiimport`. Use this in CI. |
| `--create` | `-c` | path | none | Scaffold a new project at the path and exit. Nothing is built. |
| `--resign` | — | path | none | Re-sign an existing `.pkg` in place and exit. `.pkg` only; there is no MSI re-sign. |
| `--resign-cert` | — | subject | none | Certificate subject for `--resign`. |
| `--resign-thumbprint` | — | hex | none | Certificate thumbprint for `--resign`. |

Mode precedence is `--create`, then `--resign`, then build. Format precedence in build mode
is `--pkg`, then `--nupkg`, then MSI.

Exit codes are `0` on success and `1` for a missing project directory or any unhandled
error. A failing `cimiimport` launched from the post-build prompt is logged as a warning and
does not change `cimipkg`'s exit code.

## Environment variables

| Variable | Effect |
|---|---|
| `CIMIPKG_*` | Merged into the substitution dictionary with the prefix retained. |
| `CIMIAN_PKG_SKIP_SCRIPT_VALIDATION=1` | Skip the build-time PowerShell parse check on embedded scripts. |
| `CIMIAN_PKG_DISABLE_LAUNCH_CONDITIONS=1` | Omit the pending-reboot launch condition from the MSI. |

`CIMIAN_INSTALLDIR` is set by the MSI at *install* time for installer-type packages. It is
not read at build time.

## Worked example: a copy-type package

`Example App` here is a set of files that live under `%ProgramFiles%` and a script that
registers them. Create the project:

```
cimipkg --create C:\pkgs\ExampleApp
```

Put the files under `C:\pkgs\ExampleApp\payload\`. Because `install_location` is set, the
payload tree is reproduced verbatim beneath it — a file at `payload\bin\example.exe` lands
at `C:\Program Files\Example App\bin\example.exe`.

`C:\pkgs\ExampleApp\build-info.yaml`:

```yaml
product:
  name: ExampleApp
  version: 2026.09.03
  developer: Example Vendor
  identifier: com.example.exampleapp
  description: Example App runtime files
install_location: C:\Program Files\Example App
```

`C:\pkgs\ExampleApp\scripts\postinstall.ps1` — `$payloadRoot` is already set to
`C:\Program Files\Example App` by the injected header, so the script does not hardcode it:

```powershell
$ErrorActionPreference = 'Stop'

$exe = Join-Path $payloadRoot 'bin\example.exe'
if (-not (Test-Path $exe)) {
    Write-Error "Payload missing: $exe"
    exit 1
}

& $exe --register
exit $LASTEXITCODE
```

Build it:

```
cimipkg --skip-import C:\pkgs\ExampleApp
```

The result is `C:\pkgs\ExampleApp\build\ExampleApp-2026.09.03.msi`.

## Worked example: an installer-type package

Here `Example App` ships as a vendor `setup.exe` that must be run with its own silent
switches. Put the vendor installer in `payload\`, leave `install_location` out entirely,
and let the postinstall script do the work.

`C:\pkgs\ExampleAppSetup\build-info.yaml`:

```yaml
product:
  name: ExampleApp
  version: 4.2.1
  developer: Example Vendor
  identifier: com.example.exampleapp.setup
  description: Example App, installed from the vendor package
  installer_type: exe
key_path: C:\Program Files\Example App\ExampleApp.exe
```

`C:\pkgs\ExampleAppSetup\scripts\postinstall.ps1` — `$payloadRoot` resolves at install time
to the staging directory the MSI extracted into:

```powershell
$ErrorActionPreference = 'Stop'

$setup = Join-Path $payloadRoot 'ExampleAppSetup.exe'
$proc = Start-Process -FilePath $setup -ArgumentList '/S','/norestart' -Wait -PassThru

if ($proc.ExitCode -notin @(0, 3010)) {
    Write-Error "Vendor installer failed with $($proc.ExitCode)"
    exit $proc.ExitCode
}

exit 0
```

Build and sign in one step:

```
cimipkg --sign-cert "Example Vendor Code Signing" --skip-import C:\pkgs\ExampleAppSetup
```

The wrapper MSI is hidden from Programs and Features; the vendor's own entry remains. When
you import this, the pkgsinfo `installs` array must point at the installed application —
`C:\Program Files\Example App\ExampleApp.exe` — and not at the wrapper. Setting `key_path`
as above lets [cimiimport](cimiimport) generate that entry for you.

## Limitations

- `category`, `minimum_os_version` and `blocking_applications` are accepted in
  `build-info.yaml` and have no effect. Set those in the pkgsinfo instead.
- `postinstall_action` writes a log line and performs no logout, restart or shutdown.
- An unresolved `${VAR}` is silently left as literal text in both YAML fields and scripts.
- Unknown `build-info.yaml` keys are silently ignored, so a misspelled key is invisible.
- `--intunewin` fails open: a missing `IntuneWinAppUtil.exe` produces a warning and a
  successful exit with no `.intunewin` file.
- `nuget sign` failures during a `--nupkg` build are warnings, not errors.
- `--resign` works on `.pkg` archives only. There is no in-place re-sign for an MSI;
  rebuild instead.
- Every build produces a fresh ProductCode. If a pkgsinfo pins a ProductCode in its
  `installs` array, a rebuild of the same version will never match it again and the item
  will reinstall on every run. See [Install Loop Prevention](Install-Loop-Prevention).
- The build directory is deleted at the start of every build, so previous artifacts are
  not kept.

## See also

- [cimiimport](cimiimport)
- [makepkginfo](makepkginfo)
- [Command Line Tools](Command-Line-Tools)
- [Installer Types](Installer-Types)
- [Installs Arrays](Installs-Arrays)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Version Comparisons](Version-Comparisons)
