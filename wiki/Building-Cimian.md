# Building Cimian

This page covers building Cimian from source: what you need installed, how to clone
the repository, how to build everything or a single tool, where the output lands, how
to run the tests, and how the MSI is produced. It also lists the build failures you
are most likely to hit and what causes them.

If you want to understand the code you are about to change, read
[Architecture](Architecture) first.

## Prerequisites

- **Windows.** Every project targets `net10.0-windows`, so the build host must be
  Windows. x64 and arm64 hosts both work; there is no x86 build.
- **.NET SDK 10.0.x, preview quality.** The projects pin preview package versions
  of `Microsoft.Extensions.*`, so a stable-only SDK feed is not sufficient.
- **PowerShell 7 or newer.** `build.ps1` declares `#Requires -Version 7.0` and will
  refuse to run under Windows PowerShell 5.1.
- **Git**, with submodule support, because `cli/cimipkg` is a submodule.

No Visual Studio workloads are required. Managed Software Center is a WinUI 3
application, but the Windows App SDK arrives as a NuGet package reference and
publishes self-contained, so the SDK alone builds the whole solution.

These are optional, and only affect specific steps:

- **`signtool.exe`** (from the Windows SDK) to sign binaries and MSIs.
- **`nuget.exe`** on `PATH` for NuGet packaging. Without it the build falls back to
  assembling the `.nupkg` itself as a zip, which works but cannot be signed.
- **`IntuneWinAppUtil.exe`** on `PATH` for `.intunewin` output. Without it that step
  is skipped with a warning.

## Cloning

Clone with submodules, or the solution will not restore — `CimianTools.sln`
references `cli/cimipkg/Cimian.CLI.cimipkg.csproj`, which lives in the submodule:

```
git clone --recurse-submodules https://github.com/windowsadmins/cimian.git
```

If you already cloned without them:

```
git submodule update --init --recursive
```

## The full build

From the repository root, in PowerShell 7:

```
.\build.ps1
```

That is the whole pipeline: regenerate the Managed Software Center icon, build the
solution, publish every tool for both architectures, sign if a certificate is
configured, then build the MSI, the NuGet package and the `.pkg` for each
architecture.

`build.ps1` decides on signing without being asked. It reads `CIMIAN_CERT_SUBJECT`
and `CIMIAN_CERT_CN` from a `.env` file in the repository root — see `.env.example`
— and looks for a matching certificate with a private key, first in the current
user's store, then in the machine store. With no `.env` and no match, it logs a
warning and continues, producing unsigned artifacts. To skip the search entirely:

```
.\build.ps1 -NoSign
```

Useful variations:

```
.\build.ps1 -Binaries
```

```
.\build.ps1 -Architecture x64
```

```
.\build.ps1 -Clean
```

```
.\build.ps1 -PackageOnly
```

`-Binaries` stops after publishing and signing the executables. `-Architecture`
accepts `x64`, `arm64` or `both`, which is the default. `-Clean` empties `release\`
and removes every `bin` and `obj` directory before building. `-PackageOnly` packages
whatever is already in `release\<arch>` without rebuilding, and `-MsiOnly` and
`-NupkgOnly` narrow that to one format. `-Configuration` takes `Debug` or `Release`,
defaulting to `Release`.

For day-to-day iteration, `-Dev` builds Debug, stops the running Cimian services
first so binaries are not locked, and forces signing off. Add `-Install` to install
the resulting MSI when it finishes, which needs an elevated shell:

```
.\build.ps1 -Dev -Install
```

## Building a single tool

Pass the tool name to `-Binary`. It publishes only that tool, for the selected
architectures, and skips all packaging:

```
.\build.ps1 -Binary managedsoftwareupdate
```

Valid names are `managedsoftwareupdate`, `cimiimport`, `cimipkg`, `makecatalogs`,
`makepkginfo`, `cimitrigger`, `manifestutil`, `repoclean`, `cimiwatcher`,
`cimistatus`, and `ManagedSoftwareCenter`. Anything else fails immediately with the
valid list. Note that `cimistatus` builds the WPF application under
`gui/CimianStatus`, not the console project under `cli/cimistatus`.

To compile without publishing or packaging at all — the fastest way to find out
whether your change builds — use the SDK directly:

```
dotnet build CimianTools.sln --configuration Release
```

## Where the output goes

| Path | Contents |
|---|---|
| `release\x64\` | The complete x64 publish tree: every tool, plus the Managed Software Center application and its companion files. |
| `release\arm64\` | The same for arm64. |
| `release\Cimian-<version>-<arch>.msi` | The installer. |
| `release\CimianTools-<arch>.<version>.nupkg` | The Chocolatey package. |
| `release\CimianTools-<arch>-<version>.pkg` | A legacy payload archive, also published on releases. |

The version is a calendar stamp. MSI, `.pkg` and zip names use
`yyyy.MM.dd.HHmm`; the NuGet package uses the shorter `yy.M.d.HHmm` form of the same
moment. Pin it with `-ReleaseVersion`, which requires the `yyyy.MM.dd.HHmm` form
exactly:

```
.\build.ps1 -NoSign -ReleaseVersion 2026.01.15.0930
```

Without the pin, the version comes from the clock at the moment the build starts.

A `release\<arch>` directory is a real, runnable tree — you can execute
`release\x64\managedsoftwareupdate.exe` directly. The command-line tools are
self-contained single files and can be copied individually. Managed Software Center
cannot: it needs its `.pri`, `.xbf` and runtime files alongside it, and copying only
the executable produces "This app can't run on your PC".

## Running the tests

The unit tests are xUnit, using Moq and FluentAssertions. This is the command the
continuous integration workflow runs:

```
dotnet test tests/Cimian.Tests/Cimian.Tests.csproj --configuration Release --runtime win-x64
```

They are ordinary in-process unit tests — no machine state is changed, nothing is
installed, and no network is used. Coverage is organised by the component under
test: version comparison and the predicate engine, the loop guard, catalog and
manifest deserialisation, configuration loading and directory casing, the download
and installer services, script execution, session logging and retention, the trigger
and watcher services, catalog and pkgsinfo generation, and the self-service manifest.
Fixtures under `tests/fixtures` are hand-authored YAML and JSON — catalogs,
manifests, and simulated system facts. Never add a fixture captured from a real
machine.

There are two test project files in the tree: `tests/Cimian.Tests.csproj`, which is
the entry listed in the solution, and `tests/Cimian.Tests/Cimian.Tests.csproj`, which
is the one the workflow runs and the one that references `cimipkg`. Use the path
above so you are running what CI runs.

### The smoke test

The smoke test validates built binaries rather than code. It checks that each of the
ten command-line executables exists, prints a version, prints help, and then exercises
three real operations: a `makepkginfo` stub, a `cimipkg` project creation, and a
`manifestutil` listing. It exits non-zero if anything fails.

Build the binaries first, then run it:

```
.\build.ps1 -NoSign -Binaries -Architecture x64
```

```
pwsh .\tests\smoke-test.ps1
```

It auto-detects `release\<arch>` for the host architecture. Point it elsewhere with
`-BinaryPath`. It is not run by continuous integration, so run it yourself before
proposing a change that touches argument parsing or a tool's startup path.

### Other harnesses

`tests/gui-harness/MscHarness.ps1` drives Managed Software Center's self-service
flows from a terminal by manipulating `SelfServeManifest.yaml` and reading back
`InstallInfo.yaml`, so GUI behaviour can be exercised without the window. It changes
state on the machine it runs on.

`tests/docker` builds a Windows Server Core container for comparing outputs between
two binary sets. It expects a mounted repository and a second, legacy set of
binaries that this repository no longer produces, so it does not run as-is. Neither
harness is part of continuous integration.

## How the MSI is produced

There is no WiX source in this repository. The MSI is authored by `cimipkg.exe` —
the same tool sites use to package their own software, documented on
[cimipkg](cimipkg) — from the submodule at `cli/cimipkg`. The build is therefore
self-hosting: it uses the `cimipkg.exe` it has just built, preferring the host
architecture's copy.

For each architecture, `build.ps1` stages a temporary project directory containing
a `payload` folder, a `scripts` folder and a `build-info.yaml`, then runs
`cimipkg.exe --verbose --skip-import` against it and copies the resulting `.msi`
into `release\` under its final name. `--skip-import` matters: without it `cimipkg`
offers to run `cimiimport` afterwards and blocks on a prompt.

The payload is the entire publish tree minus `*.pdb` and `*.xml`, because the WinUI 3
companion files have to ride along with the application. Set `CIMIAN_MSI_KEEP_PDB=1`
to keep symbols. The build reports the payload size and warns above 250 MB; set
`CIMIAN_MSI_PAYLOAD_SOFT_CAP_MB` to move that warning or
`CIMIAN_MSI_PAYLOAD_HARD_CAP_MB` to a non-zero value to make it fatal. The build
also copies the MSI support scripts from `build\msi` into the payload and the
install, upgrade and uninstall custom-action scripts from `build\pkg` into the
scripts folder, substituting the version into each.

Because the MSI is authored outside this repository, its internal structure —
product code generation, upgrade table, custom action conditions — is not something
you can change here. See [Installing Cimian](Installing-Cimian) for the installed
result.

## Common build failures

**`The script 'build.ps1' cannot be run because it contained a "#requires" statement for PowerShell 7.0`.**
You are in Windows PowerShell 5.1. Start `pwsh` and run it again.

**Restore or build fails on `cli\cimipkg\Cimian.CLI.cimipkg.csproj` not existing.**
The submodule is not checked out. Run `git submodule update --init --recursive`.

**`NETSDK1045`, or a complaint that `net10.0-windows` is not a known target
framework.** The installed SDK is older than 10.0. Install .NET SDK 10.0.x at
preview quality; a stable-channel installer may not offer it.

**Package restore cannot find `Microsoft.Extensions.*` at `10.0.0-preview.*`.** The
NuGet feed configured for the machine does not carry preview packages. Restore
against the public nuget.org feed.

**`cimipkg.exe not found - build it first`, and no MSI appears.** You ran a
packaging-only mode such as `-MsiOnly` or `-PackageOnly` with an empty or partial
`release\<arch>`. Run a full build, or `.\build.ps1 -Binaries` first.

**`No signing certificate configured` or `No enterprise certificate found`.** These
are warnings, not errors. The build completes and the artifacts are unsigned. Add a
`.env` with `CIMIAN_CERT_SUBJECT`, or pass `-NoSign` to stop the search.

**`IntuneWinAppUtil.exe not found`.** `-IntuneWin` skips silently apart from the
warning; nothing else in the build is affected.

**The build succeeds but tools report different versions.** A bare
`dotnet build` stamps each project with the clock at the moment that project
compiles. Build through `build.ps1`, which pins one version across the whole run.

**Files are locked, or the publish step fails to overwrite an executable.** Cimian is
installed and running on the build machine. Use `-Dev`, which stops the services
first, or stop `CimianWatcher` and close Managed Software Center by hand.

**Managed Software Center exits immediately, or reports "This app can't run on your
PC".** Its publish tree was copied partially. Copy `release\<arch>` whole.

## See also

- [Architecture](Architecture)
- [Release Process](Release-Process)
- [Contributing](Contributing)
- [Installing Cimian](Installing-Cimian)
- [cimipkg](cimipkg)
- [Command-Line Tools](Command-Line-Tools)
