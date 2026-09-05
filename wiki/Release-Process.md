# Release Process

This page describes how a Cimian release is cut, what the continuous integration
and release workflows do, and — as importantly — what they do not do. Read it
before tagging, and read the last section before assuming a published release is
ready to deploy.

The workflows live in `.github/workflows`: `ci.yml` and `release.yml`.

## What continuous integration does

`ci.yml` runs on every pull request against `main` and on every push to `main`. It
uses a `windows-latest` runner with a 30-minute timeout, and a concurrency group
that cancels an in-progress run when a new commit arrives on the same ref.

The job checks out the repository with submodules, installs .NET SDK 10.0.x at
preview quality, and then runs three commands: `dotnet restore CimianTools.sln`,
`dotnet build CimianTools.sln --configuration Release --no-restore`, and
`dotnet test tests/Cimian.Tests/Cimian.Tests.csproj --configuration Release
--runtime win-x64`. The test results are uploaded as a `.trx` artifact whether the
job passes or fails.

NuGet caching is deliberately not used. Cold restore costs roughly a minute and is
reliable; the caching actions were not.

That is the entire gate. A green CI run means the solution compiles and the unit
tests pass. It does not mean an installer was produced, or that one would work.

## The tag convention

A release is cut by pushing a tag. The release workflow triggers only on tags
matching this pattern:

```
[0-9][0-9][0-9][0-9].[0-9][0-9].[0-9][0-9].[0-9][0-9][0-9][0-9]
```

That is a calendar build stamp, `YYYY.MM.DD.HHmm`, zero-padded throughout, with
**no `v` prefix and no other characters**. `2026.01.15.0930` triggers a release;
`v2026.01.15.0930`, `2026.1.15.0930` and `2026.01.15` do not — they push without
error and simply build nothing.

The tag is the release identity. It is not a label attached to a build; it *is* the
version the build stamps into every artifact.

## What the tag triggers

`release.yml` runs on a `windows-latest` runner with a 90-minute timeout and
`contents: write` permission. In order, it:

1. Checks out the repository with `submodules: recursive`, so `cimipkg` is present.
2. Installs .NET SDK 10.0.x at preview quality.
3. Runs the packaging build, unsigned, pinned to the tag:
   `.\build.ps1 -NoSign -ReleaseVersion '<tag>'`.
4. Verifies that the produced artifact and the tag agree.
5. Zips each architecture's raw binary tree.
6. Creates the GitHub release and uploads every artifact.

## Artifacts and naming

| Artifact | Name |
|---|---|
| Windows Installer package | `Cimian-<YYYY.MM.DD.HHmm>-<arch>.msi` |
| Chocolatey package | `CimianTools-<arch>.<YY.M.D.HHmm>.nupkg` |
| Legacy payload archive | `CimianTools-<arch>-<YYYY.MM.DD.HHmm>.pkg` |
| Raw binary archive | `Cimian-<YYYY.MM.DD.HHmm>-<arch>.zip` |

`<arch>` is `x64` or `arm64`, and both are built. The version in the MSI, `.pkg` and
zip names is the tag verbatim; the NuGet package uses the shorter `YY.M.D.HHmm`
form of the same moment, so `2026.01.15.0930` becomes `26.1.15.0930` there.

The zip is not produced by `build.ps1` — the workflow makes it by compressing
`release/<arch>/*` after the build. It exists so repository automation can pick up
`cimiimport.exe` and `makecatalogs.exe` without cracking open an installer or
treating a NuGet package as an archive.

Assets are uploaded x64 first so the release page lists architectures in that order.
The upload glob also matches `*.intunewin`, but the workflow does not pass
`-IntuneWin`, so no `.intunewin` is ever published. Wrap the MSI yourself — see
[Deploying Cimian With Intune](Deploying-Cimian-With-Intune).

## The tag-to-artifact consistency check

After the build, the workflow finds the first `release\Cimian-*-x64.msi`, matches
its name against `^Cimian-(\d{4}\.\d{2}\.\d{2}\.\d{4})-x64\.msi$`, and compares the
captured datestamp with the tag. Any of three conditions fails the job outright:

- no x64 MSI was produced at all,
- the MSI name does not parse into a datestamp,
- the datestamp is not equal to the tag.

`-ReleaseVersion` already pins the version, so this check is a guard against the
build ignoring the pin, or against a stale artifact left in `release\` being picked
up. It is the only correctness check the release workflow performs on what it is
about to publish.

The same step records the SDK version, the runner's OS caption and the `cimipkg`
submodule's `git describe` output. Those, plus a link to the workflow run, become a
"Build info" block at the top of the release notes. The rest of the notes are
GitHub's generated changelog with contributor attribution and the "New Contributors"
section removed, followed by a signing section.

## What CI does not do

Be explicit about this when you decide whether to trust a release:

- **CI never runs `build.ps1`.** No installer, package or publish tree is produced
  or exercised on a pull request. A change that breaks packaging passes CI and is
  discovered only when someone tags.
- **CI never runs the smoke test.** `tests/smoke-test.ps1` validates that the built
  executables start, report a version, print help and perform basic operations.
  Nothing runs it automatically, in either workflow.
- **CI never runs the container tests.** The harness under `tests/docker` is not
  wired into any workflow.
- **The release workflow runs no tests at all.** It builds and publishes. Test
  coverage for a release is whatever CI ran on the commit before it was tagged.
- **Released artifacts are unsigned.** The workflow builds with `-NoSign`, and there
  is no signing infrastructure in this repository. If your environment requires
  signed binaries — and for a system that installs software as SYSTEM, it should —
  you must sign them yourself. Extract the payload, sign the executables and
  libraries, repackage, and sign the MSI. The release notes carry a `signtool`
  recipe for this.

## Verifying a release locally before tagging

Do this on the exact commit you intend to tag, using the exact tag string. The build
takes a while; on a hosted runner the release job routinely runs for tens of
minutes, and locally it is comparable.

Run the unit tests, the way CI does:

```
dotnet test tests/Cimian.Tests/Cimian.Tests.csproj --configuration Release --runtime win-x64
```

Reproduce exactly what the release workflow will build:

```
.\build.ps1 -NoSign -ReleaseVersion 2026.01.15.0930
```

Confirm the artifact names carry the tag, which is the same thing the workflow's
consistency check does:

```
Get-ChildItem release -Include *.msi,*.nupkg,*.pkg -Recurse | Select-Object Name, Length
```

Run the smoke test the workflow will not run:

```
pwsh .\tests\smoke-test.ps1
```

Then install the MSI on a scratch machine and verify the result — the shipped script
checks the executables, the service, the scheduled task, the `PATH` entry and the
registry stamp:

```
& "C:\Program Files\Cimian\verify-installation.ps1"
```

When all of that is clean, tag and push:

```
git tag 2026.01.15.0930
git push origin 2026.01.15.0930
```

If a release goes out wrong, cut a new tag with a later stamp. Do not delete and
re-push a tag that has already produced a release: consumers may already have the
old artifacts, and the version stamped inside them will not change.

## See also

- [Building Cimian](Building-Cimian)
- [Architecture](Architecture)
- [Contributing](Contributing)
- [Installing Cimian](Installing-Cimian)
- [Updating Cimian](Updating-Cimian)
- [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
