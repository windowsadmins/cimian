# MSC GUI test harness

Drive every Managed Software Center self-service flow from the terminal — no
window, no clicking, no waiting on the watcher poll. Use it to iterate on GUI
features in seconds instead of build → deploy → click → squint.

## Why this works

The MSC window is a **thin shell**. No button installs anything directly. Every
interaction does exactly two things, then reads the result back:

```
  [Install] / [Remove] click
        │
        ├─ 1. mutate  C:\ProgramData\ManagedInstalls\SelfServeManifest.yaml
        │        managed_installs:   <- Install adds here
        │        managed_uninstalls: <- Remove adds here
        │
        └─ 2. run     managedsoftwareupdate --item <Name> --no-preflight ...
                 (GUI does this via a flag file the CimianWatcher service picks up)
                          │
                          ▼
              C:\ProgramData\ManagedInstalls\InstallInfo.yaml   <- the result the GUI renders
                 per item: installed / status / will_be_installed / will_be_removed / uninstallable
```

So the harness reproduces step 1 (mutate the manifest the same way the GUI's
`SelfServiceManifestService` does) and step 2 (run the engine), then reads
`InstallInfo.yaml` — exactly the data the Software/Updates tabs bind to.

The big speed win: **plan mode**. Running the engine with `--checkonly` resolves
the *decision* (`will-be-installed` / `will-be-removed`) and writes it to
`InstallInfo.yaml` **without installing or removing anything**. That proves
"clicking Install would install this / clicking Remove would remove this" in a
couple of seconds. Only pass `-Apply` when you want the real install/removal.

## Contract map (where the real code lives — mirror it, don't fork it)

| Concern | File |
|---|---|
| SelfServe mutation (`AddInstallRequest` / `AddRemovalRequest` / `RemoveRequest`) | `shared/core/Services/SelfServiceManifestService.cs` |
| GUI trigger / flag file | `gui/ManagedSoftwareCenter/Services/TriggerService.cs` |
| Flag `Args:` line builder | `shared/core/Services/BootstrapArgsBuilder.cs` |
| Engine result writer (`WriteInstallInfo`) | `cli/managedsoftwareupdate/Services/UpdateEngine.cs` |
| Loop suppression | `shared/core/Services/LoopGuard.cs` |
| Canonical paths | `shared/core/Services/CimianPaths.cs` |
| ViewModel commands behind each button | `gui/ManagedSoftwareCenter/ViewModels/{Software,Updates,ItemDetail}ViewModel.cs` |

## Usage

Dot-source to get the functions:

```powershell
. .\tests\gui-harness\MscHarness.ps1

Show-MscState                 # what the Updates tab would show right now
Invoke-MscInstall Blender     # PLAN: mimic Install click (safe — no install)
Invoke-MscInstall Blender -Apply   # actually install
Invoke-MscRemove VLC          # PLAN: mimic Remove click
Invoke-MscRemove VLC -Apply        # actually remove
Invoke-MscCancel Blender      # mimic Cancel on a pending request
Invoke-MscInstallNow          # mimic "Install Now" over everything pending
Invoke-MscScenario Blender    # install → assert → remove → assert (plan mode)
Invoke-MscScenario Blender -Apply  # same, end to end for real
```

Or one-shot, no dot-sourcing:

```powershell
pwsh .\tests\gui-harness\MscHarness.ps1 state
pwsh .\tests\gui-harness\MscHarness.ps1 install Blender
pwsh .\tests\gui-harness\MscHarness.ps1 scenario Blender -Apply
```

### `-ViaWatcher` (full IPC test)

By default the harness runs the engine directly (fast, deterministic). Add
`-ViaWatcher` to instead drop the bootstrap flag file and wait for the
**CimianWatcher** service to pick it up — exercising the exact path the GUI
uses, including the service hop. Slower, but it's how you verify the watcher
contract itself.

```powershell
Invoke-MscInstall Blender -Apply -ViaWatcher
```

## Stuck items

An item gets wedged in "Pending" when one of these is true:

1. **It's in `SelfServeManifest.yaml` but never converges** — e.g. a pkginfo
   whose `installs`/verification check never passes, so the engine reinstalls
   it every run. Symptom: stays "will-be-installed" forever.
2. **LoopGuard suppressed it.** After repeated reinstalls of the same version,
   `LoopGuard` suppresses the package (see `reports\state.json`). It still shows
   pending in the UI but the engine silently skips it.

Diagnose and clear:

```powershell
Get-MscLoopState             # table of every suppressed package + reason + until-when
Clear-MscStuck Gimp          # drop from SelfServe + clear its loop suppression + refresh
Clear-MscStuck -All          # clear ALL loop suppression (nuclear)
```

`Clear-MscStuck <name>`:
1. removes the item from `SelfServeManifest.yaml` (stop re-requesting it),
2. runs `managedsoftwareupdate --clear-loop <name>` (lift suppression),
3. runs a `--checkonly` to rewrite `InstallInfo.yaml` so the GUI drops it.

> The real fix for a pkginfo-induced loop is the pkginfo (wrong `installs`
> check, missing silent `installer.switches`, etc.). `Clear-MscStuck` unwedges
> the client; correct the pkginfo in the deployment repo so it stops recurring.

## Complementary layer: ViewModel unit tests

This harness covers the **engine/contract** end (real files, real engine). The
**GUI-logic** end — "does clicking Install call `AddInstallRequestAsync` then
`TriggerInstallItemAsync` with the right name?" — belongs in fast xUnit tests
that instantiate the ViewModels with fake `ISelfServiceManifestService` /
`ITriggerService`. Together they cover the whole click-to-install path without a
running window.
