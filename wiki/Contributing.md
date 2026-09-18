# Contributing

This page is about contributing to Cimian itself — the client, the command-line tools and the
applications — rather than about using it. It covers where the source lives, how work is tracked,
the branch and pull-request conventions the repository requires, how to build and test a change
before you propose it, and what a bug report needs to contain to be actionable.

Cimian is C# on .NET 10 and Windows-only. If you have never built it, start at
[Building Cimian](Building-Cimian) and come back here for the process around the build.

## Where the source lives

The project is on GitHub at `windowsadmins/cimian`. Everything in this wiki documents that
repository at its current state.

`cimipkg` is a separate repository, `windowsadmins/cimian-pkg`, included here as a git submodule
at `cli/cimipkg`. It builds as part of the solution, so clone recursively or the solution will
not restore:

```powershell
git clone --recursive https://github.com/windowsadmins/cimian.git
```

If you already have a clone without submodules:

```powershell
git submodule update --init --recursive
```

A change to `cimipkg` is a pull request against `cimian-pkg`, and a change to anything else is a
pull request here.

## Everything in this repository is public

The code, the commit messages, the pull-request titles and bodies, the issues, this wiki and the
edit history of all of them. A correction does not retract what was published: an edited
pull-request body remains in GitHub's edit history, and a rewritten commit remains in every fork,
clone and API response of it.

Some contributors also run Cimian in a private environment. Nothing from that environment belongs
here. Do not publish machine or server names, internal host names or DNS domains, internal URLs or
file shares, the names of staff or vendor contacts, references to a private issue tracker or its
identifiers, asset tags, serial numbers, user names, certificate subjects, tenant or organisation
names, licence keys, or which software titles are deployed where and in what quantity. That
applies to code comments and test fixtures as much as to prose.

Write for a reader with no access to any of that — which produces better reports as well as safer
ones. "A workstation re-hashing a nine-gigabyte cached package on every run" tells a stranger what
is wrong; a host name tells them nothing they can act on. Where a real value is genuinely needed
for the software to run, put it in configuration or an environment variable with a neutral
default, never hardcoded.

If you find published material that breaks this, report it to the maintainers rather than quietly
deleting it. Assessing the exposure matters more than tidying the file.

## Issues

**Work is tracked by GitHub issues in this repository.** Reference one from a commit or a pull
request as `#<n>`, and close it from a pull-request body with `Closes #<n>`.

If work needs tracking and no issue exists, open one stating the problem in public terms:

```powershell
gh issue create
```

Do not reference a private issue tracker in a commit, pull request, issue or release note. Such
identifiers resolve nowhere for a public reader, communicate nothing, and disclose the shape of a
private backlog.

## Branches and pull requests

Start from `origin/main` in a worktree. Never branch off a local `main`, which is routinely stale:

```powershell
git fetch origin main
git worktree add .worktrees/example-fix origin/main
```

Worktrees live at `.worktrees/<name>` inside the repository and are excluded by `.gitignore`.

Name the branch `<type>/<slug>`, where type is one of `feature`, `fix`, `chore`, `ci` or `docs`.
`fix/catalog-parse-error` and `docs/installs-array` are both fine; a bare slug or a personal
prefix is not.

Work and verify in that worktree, then commit, push the branch and open a pull request. **Check
for an existing pull request on that branch first and append to it rather than opening a second
one.**

**Never push `main` directly.** The pull-request merge is what lands work.

Say plainly in the pull-request body what you ran and what the result was, including any
pre-existing failures you did not cause. A reviewer cannot distinguish "the suite passes" from
"the suite passed before my change too" unless you say so.

When your pull request merges, clean up without being asked — remove the merged branch, its stale
tracking ref and its worktree, and say what you removed.

## Commit messages

A descriptive, imperative subject line describing what the commit does: "Reject a catalog whose
pkgsinfo failed to parse", not "fixes" or "wip".

The conventions the repository actually requires:

- Plain prose subjects. **No emoji.**
- **No bracketed tags** such as `[hotfix]`, on any branch.
- **No `Co-Authored-By` trailers**, and no assistant session links or `Claude-Session:` trailers.
- The tracking reference, if there is one, is a GitHub issue: `#<n>`.

Keep commits focused. Never commit test or debug artifacts, secrets, `*.env*` files, `*.pem`
files or credential files of any kind.

## Building and testing before you open a pull request

You need PowerShell 7 or later and the .NET 10 SDK. The build pins preview-quality
`Microsoft.Extensions` packages, so a stable-only SDK installation will not restore.

The three commands continuous integration runs, in order. Run all three locally before proposing
a change:

```powershell
dotnet restore CimianTools.sln
```

```powershell
dotnet build CimianTools.sln --configuration Release --no-restore
```

```powershell
dotnet test tests/Cimian.Tests/Cimian.Tests.csproj --configuration Release --runtime win-x64
```

That is the whole of CI. It builds and tests; it does not run `build.ps1`, does not produce an
MSI, and does not run the smoke or container tests.

To produce runnable binaries, use the build script rather than `dotnet publish` — it forces the
self-contained, single-file publish settings the tools need, and hand-copies the WinUI 3
companion files that Managed Software Center cannot publish as a single file:

```powershell
.\build.ps1 -Binaries
```

```powershell
.\build.ps1 -Binary managedsoftwareupdate
```

Binaries land in `release\x64\` and `release\arm64\`. There is a further post-build check that is
not part of CI and is worth running when you have touched a tool's argument parsing or startup
path — it verifies each binary exists, runs, and answers `--version` and `--help`:

```powershell
.\tests\smoke-test.ps1
```

When you test the client itself, pass `--checkonly` so a test run cannot install or remove
anything on your own machine:

```powershell
.\release\x64\managedsoftwareupdate.exe --checkonly -vv
```

[Building Cimian](Building-Cimian) covers the build script's full parameter set, signing, and the
packaging steps.

## What a good bug report contains

Cimian's failures are usually decisions rather than crashes — an item that reinstalls forever, a
manifest that resolves to the wrong name, an install that reports success while nothing changed.
Those are only diagnosable from the run's own record, so a report built from the files below is
worth far more than a description of the symptom.

State first, in public terms:

- The client version, from `managedsoftwareupdate --version`.
- What you expected to happen and what happened instead, described as a failure mode rather than
  as an incident on a particular machine.
- Whether it reproduces, and on how many machines out of how many.
- The relevant part of the pkgsinfo or manifest, reduced to the smallest thing that still shows
  the problem. Rename the item to `ExampleApp` and the manifest to `WORKSTATION-01`.

Then attach the evidence. The effective configuration, which shows the repository URL, client
identifier, catalogs and cache state actually in use:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --show-config
```

The session directory for the affected run, plus the reports directory — together these
reconstruct what the run saw, what it decided and why:

```powershell
Compress-Archive -Path "$env:ProgramData\ManagedInstalls\logs","$env:ProgramData\ManagedInstalls\reports" -DestinationPath "$env:TEMP\cimian-logs.zip"
```

A clean transcript from a fresh check-only run at high verbosity, which writes a session
directory of its own without changing anything on the machine:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --checkonly -vvv
```

Then add whichever applies: the matching installer log from `logs\installs\` for an installer
that failed, `logs\selfupdate\` for a Cimian update that did not take, and
`logs\cimiwatcher.log` for a machine where no run is happening at all. For a suspected install
loop, include the loop diagnostics:

```powershell
& "$env:ProgramFiles\Cimian\managedsoftwareupdate.exe" --loop-status
```

[Logging](Logging) explains the layout of everything above and which file answers which question;
[Reporting Data Contract](Reporting-Data-Contract) documents the exact shape of the report files.

**Redact before you attach.** Logs carry the machine name, the signed-in user, your repository
URL, and the full list of software the machine manages — all of which this repository's rules
keep out of public view. Trim the transcript to the item in question, replace names with the
placeholders above, and check the file you are attaching rather than the command you ran to make
it.

## See also

- [Building Cimian](Building-Cimian)
- [Architecture](Architecture)
- [Release Process](Release-Process)
- [Logging](Logging)
- [Reporting Data Contract](Reporting-Data-Contract)
- [Troubleshooting](Troubleshooting)
- [Command Line Tools](Command-Line-Tools)
- [Frequently Asked Questions](Frequently-Asked-Questions)
