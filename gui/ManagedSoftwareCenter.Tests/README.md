# Hub navigation and bulk installation regression tests

Run on Windows with .NET SDK 10:

```powershell
dotnet test gui/ManagedSoftwareCenter.Tests/ManagedSoftwareCenter.Tests.csproj -p:RuntimeIdentifier=win-x64
```

Tests instantiate the actual compiled GUI viewmodels, replacing only service
boundaries with mocks. They do not write the machine self-service manifest,
start CimianWatcher, install applications, or require tenant credentials.

On the pinned upstream commit `e63721d673eaa3f545cfe456e1468dd7654629be`,
the four targeted-install navigation cases fail: Software, Categories and
Details switch to Updates, and Updates emits redundant navigation events.
The broad-operation case passes. The navigation fix keeps targeted requests
on the current page while retaining Updates navigation for broad operations.

Bulk tests cover preserving distinct tenant/app item names, deduplication,
writing all requests before one batch trigger, declined alerts, filtering,
installed/pending eligibility, withdrawn offers, partial manifest failures,
and trigger failure/retry feedback.

## UI acceptance checks

- Open Software and enable **Select apps**. Selecting tiles must not open details.
- Select two available apps; the button must read **Install selected (2)**.
- Current/installed and already-pending items must not be selectable for install.
- Changing the search/category clears selection, preventing hidden installs.
- Submit the selected apps: retain Software, mark accepted requests pending,
  and send one targeted batch. Existing alert/blocking-application checks apply
  individually; declining one must not discard other accepted selections.
- Turn selection mode off: normal tile-to-details navigation returns.
- An individual install must also retain the current page; explicit broad
  checks still open Updates. Deadline-driven navigation remains unchanged.

Local builds are unsigned test artifacts, not production releases. Integration
into Impel's pinned client requires the normal reviewed/signing release flow.

## Safe interactive fixture

Build with `-p:HubUiTest=true -c Debug` to compile a fixture using the same
pages and viewmodels with in-memory catalog, manifest and trigger services.
The window title is **Hub UI TEST — simulated installs only**. Install buttons
only update fake item state and append fixed fixture names to
`ui-fixture-events.jsonl` beside the executable. No actual install, watcher
signal or tenant manifest mutation occurs. Release configuration rejects this
flag, and normal builds do not contain the fixture or its Moq dependency.

Use the existing build.ps1 WinUI resource-copy procedure after publishing
(copy application PRI and XBF files from the build directory). When launching
from a packaged desktop terminal host, use `Launch-Unpackaged.ps1 -Executable
<full-path>` so the child does not inherit the terminal host's package identity.
Otherwise WinUI can resolve `ms-appx` theme resources against the wrong app.

## Verified locally (2026-10-19)

- Baseline: four targeted-navigation test cases failed against the pinned
  upstream implementation; the broad-operation case passed.
- Fixed normal build: **15 passed, 0 failed, 0 skipped**, including absence of
  the simulated-install fixture from the normal GUI assembly.
- Native WinUI fixture: selected Alpha and Beta, saw **Install selected (2)**,
  attempted to select an already-installed item (count remained two), then
  submitted. Software stayed selected and both tiles showed Install pending,
  followed by Installed. The fixture log recorded two saved requests followed
  by exactly one targeted batch containing both names.
- Native WinUI fixture: a separate Gamma single-install click stayed on
  Software and showed Install pending immediately, then Installed.
- Native WinUI fixture: explicit Ctrl+R still navigated to Updates.
- The real-catalog debug UI also supported selecting two apps without leaving
  Software; those selections were not submitted and the debug window was closed.
- The Release fixture guard was executed and rejected `HubUiTest=true`.
- `git diff --check` passed. Test results are in `TestResults/hub-regression.trx`;
  fixture events are beside the fixture executable. Generated binaries and logs
  are not included in the patch.

No production install, tenant manifest, release pin or release pointer was
changed. This verifies the UI/request boundary, not signed distribution or
vendor installer execution. Existing upstream dependency vulnerability warnings
remain and must be considered separately before a production client release.
