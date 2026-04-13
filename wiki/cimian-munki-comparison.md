# Cimian and Munki: a comparison for Mac admins

If you already know [Munki](https://github.com/munki/munki), you already know Cimian. That is the design. Cimian was built so a Mac admin can walk up to a Windows deployment pipeline and feel at home on day one. The repo layout is identical. The pkginfo schema uses the field names you already know. `managedsoftwareupdate` runs the same detect/download/install loop. Conditional items, blocking applications, force-install deadlines, preflight and postflight scripts, receipts-style detection, optional installs, self-service - all of it works the way you expect.

This doc is the orientation. It does not re-explain what a manifest or a catalog is. It does tell you where every Munki concept lives on the Windows side, which tool replaces which, and exactly where the two systems diverge because Windows is not macOS.

For the engineering-level parity ledger (which individual Munki features are implemented, which are not), see [cimian-munki-gap-analysis.md](cimian-munki-gap-analysis.md). This doc is the admin's starter guide; that doc is the feature-by-feature spreadsheet.

## The 30-second mental model

- **The repo is the same tree.** `pkgsinfo/`, `catalogs/`, `manifests/`, `icons/`, `pkgs/`. Drop it on any HTTPS server and point clients at it, exactly like Munki.
- **Pkginfo is YAML, with the field names you already know.** `name`, `version`, `catalogs`, `requires`, `update_for`, `blocking_applications`, `installs`, `preinstall_script`, `postinstall_script`, `installcheck_script`, `uninstallcheck_script`, `OnDemand`, `force_install_after_date`, `unattended_install`, and friends - all present, all behaving the same way.
- **The client runs the same loop.** `managedsoftwareupdate.exe` downloads catalogs, walks the manifest, resolves conditional items, checks receipts, downloads pkgs, runs the installer, and writes reports. If you squint at the log output, you might not notice you are on Windows.

## Rosetta stone

| Munki | Cimian |
|---|---|
| `munkiimport` | `cimiimport.exe` |
| `managedsoftwareupdate` | `managedsoftwareupdate.exe` |
| `makecatalogs` | `makecatalogs.exe` |
| `makepkginfo` | `makepkginfo.exe` |
| `manifestutil` | `manifestutil.exe` |
| Managed Software Center | `cimistatus.exe` (WPF) plus ManagedSoftwareCenter (WinUI 3) |
| `/Library/Managed Installs/` | `C:\ProgramData\ManagedInstalls\` |
| `/Library/Preferences/ManagedInstalls.plist` | `C:\ProgramData\ManagedInstalls\Config.yaml` |
| `.pkg` / `.dmg` / `.app` | `.msi` / `.exe` / `.nupkg` / `.ps1` / `.msix` |
| `preinstall_script` (bash) | `preinstall_script` (PowerShell) |
| launchd agent / daemon | `cimiwatcher` Windows service |
| `/Library/Managed Installs/Logs/*.log` | `C:\ProgramData\ManagedInstalls\Logs\{YYYY-MM-DD-HHMMss}\*.jsonl` |
| `/Library/Managed Installs/ManagedInstallReport.plist` | `C:\ProgramData\ManagedInstalls\InstallInfo.yaml` and `reports\*.json` |
| `.gobootstrap` flag | `.cimian.bootstrap` flag |
| Receipts plist array in pkginfo | `installs` array plus MSI ProductCode receipts at `C:\ProgramData\ManagedInstalls\Receipts\` |
| NSPredicate conditionals | NSPredicate-compatible conditionals (same syntax) |
| ClientIdentifier | ClientIdentifier (optional override via cert CN) |

## What maps 1:1 (the happy path)

Most pkginfo fields transfer verbatim. `name`, `display_name`, `version`, `catalogs`, `requires`, `update_for`, `blocking_applications`, `unattended_install`, `unattended_uninstall`, `installer` / `uninstaller` blocks, `installs` detection arrays, `preinstall_script`, `postinstall_script`, `preuninstall_script`, `postuninstall_script`, `installcheck_script`, `uninstallcheck_script`, `OnDemand`, `force_install_after_date`, `version_script`, `default_installs`, `precache`, `installable_condition`, `supported_architectures`, `minimum_os_version`, and `maximum_os_version` all exist in Cimian and behave as documented in Munki.

**`installcheck_script` exit codes match Munki.** This is worth calling out explicitly because it is the question people always ask:

- **Exit 0** means "this package needs installing." Cimian will proceed with the install.
- **Exit non-zero** means "this package is already installed (or not applicable)." Cimian will skip.

Same semantics as Munki. If you are reusing `installcheck_script` bodies from a Munki repo, they keep working unchanged - just translated from bash to PowerShell.

**Conditional items use NSPredicate.** The `condition` field on conditional items accepts the same NSPredicate strings you already write. `os_version`, `architecture`, `free_disk_space`, `hostname`, `serial_number`, and the rest of the system facts are available to your predicates. You can also drop custom admin-provided scripts in `C:\ProgramData\ManagedInstalls\conditions\` (`.ps1`, `.bat`, `.cmd`, `.exe`) whose stdout is parsed as `key=value` pairs and merged into the conditions fact set - Cimian's equivalent of Munki's admin-provided conditions.

**Manifests compose the same way.** `catalogs`, `managed_installs`, `managed_updates`, `managed_uninstalls`, `optional_installs`, `featured_items`, `conditional_items`, and nested manifests all work. Cimian's conditional_items tree is a superset of Munki's flat-list-with-conditional-items model; a Munki manifest you drop in will parse and behave correctly.

**Preflight and postflight scripts run at the same points in the cycle** and respect the same abort/warn/continue failure semantics. See `cli/managedsoftwareupdate/Services/ScriptService.cs` for the exact contract; it matches Munki.

**Self-service is real.** `cimistatus.exe` and the WinUI 3 `ManagedSoftwareCenter` together fill the role of Managed Software Center: browsing optional installs, initiating on-demand installs, viewing install history, seeing upcoming force-install deadlines. Text search, category filters, aggressive notification mode (the Windows equivalent of Munki's "nag" behavior), toast notifications, and screenshot carousels for optional items are all there.

## Windows quirks that actually diverge

This is the section worth reading slowly. Everything above just works. Everything below is where your Mac instincts will mislead you.

### Config is YAML, not plist

`C:\ProgramData\ManagedInstalls\Config.yaml` replaces `ManagedInstalls.plist`. The keys are the same - `SoftwareRepoURL`, `ClientIdentifier`, `Catalogs`, and so on - but the file is YAML with the same case-sensitivity rules Munki applies. You can also override values via the registry at `HKLM\SOFTWARE\Cimian\Config` or via Intune CSP, which is handy for MDM-managed config that does not require writing a file.

### Logs are JSON-structured per-session directories

Munki writes flat text logs to `/Library/Managed Installs/Logs/`. Cimian writes a fresh directory per run at `C:\ProgramData\ManagedInstalls\Logs\{YYYY-MM-DD-HHMMss}\` containing `session.json`, `events.jsonl`, `summary.json`, `install.log`, and `debug.log`. This is great for osquery, ReportMate, and any log-shipping pipeline, but it is different for your `tail -f` muscle memory. The human-readable `install.log` inside each session dir is the closest match to what Munki produces.

### Installer types are Windows-native

No `.pkg`, no `.dmg`, no `.app`. Cimian supports `.msi`, `.exe`, `.nupkg` (Chocolatey format), `.ps1`, and `.msix` / `.appx` / `.msixbundle` / `.appxbundle`. Dispatch lives in `cli/managedsoftwareupdate/Services/InstallerService.cs`. `cimipkg.exe` is what you will reach for where you used to reach for `productbuild` / `pkgbuild` - it takes a project directory with a payload and metadata and produces a signed MSI (and optionally a `.nupkg` and a `.intunewin` at the same time).

### Authenticode code signing is required

This is not a recommendation. The Cimian build system will not produce unsigned binaries, and unsigned PowerShell scripts embedded in MSIs will be rejected at install time on hardened clients. Your first time through, expect to configure a code-signing certificate and the `cimipkg` `--sign-cert` / `--sign-thumbprint` flags before anything ships. Munki has no equivalent constraint - macOS treats script execution more permissively - so this is often the biggest single surprise.

### Scripts are PowerShell, not bash

Your exit-code contracts still work the way you know them. `installcheck_script` exit 0 still means "needs install," preinstall failures still abort the install, and postinstall scripts still run after success. But the syntax is PowerShell. Cimian always invokes PowerShell with `-NoProfile -ExecutionPolicy Bypass` so scripts are not blocked by corporate execution policy; see [powershell-execution-policy-bypass.md](powershell-execution-policy-bypass.md) for the details.

### CimianWatcher is a Windows service

Munki hooks into launchd for periodic runs and bootstrap mode. Cimian hooks into a real Windows service called `cimiwatcher` that polls for bootstrap flag files and MDM-dropped trigger files. Think of it as the launchd agent and the bootstrap mechanism merged into a single always-running process. The service runs as SYSTEM, auto-restarts on failure, and is installed by the main Cimian MSI. See [bootstrap-system-analysis-with-cimianwatcher.md](bootstrap-system-analysis-with-cimianwatcher.md).

### MDM file-trigger for real-time installs

A simple but powerful Cimian feature with no Munki cousin: drop a file named `.cimian.bootstrap` at `C:\ProgramData\ManagedInstalls\` and `cimiwatcher` will kick off a full deployment cycle within about 10 seconds. Intune Win32 apps, proactive remediation scripts, PowerShell one-liners, any MDM that can write a file can trigger a run. The flag file is the whole API.

### MSI ProductCode and UpgradeCode tracking

Windows Installer has its own receipts model based on ProductCode (immutable GUID per install) and UpgradeCode (stable GUID per product). Cimian generates deterministic UpgradeCodes from a hash of the product identifier so upgrades work correctly, and persists MSI receipts at `C:\ProgramData\ManagedInstalls\Receipts\<identifier>.yaml`. For the most part this is invisible - write your pkginfo the way you would on Munki and Cimian handles the ProductCode bookkeeping for you - but if you are authoring MSIs manually, see [MSI-ProductID-Fix.md](MSI-ProductID-Fix.md).

### NuPkg and Chocolatey fallback

Cimian treats Chocolatey `.nupkg` files as a first-class installer type. It can consume any existing Chocolatey community package as a `nupkg` installer, including the `chocolateyBeforeInstall.ps1` hook; see [chocolateyBeforeInstall-support.md](chocolateyBeforeInstall-support.md). This gives you free access to the Chocolatey community catalog without ceding control to the Chocolatey agent.

### Intune `.intunewin` packaging

`cimipkg` can emit `.intunewin` packages alongside the MSI and NuPkg. If your environment ships through Intune Win32 apps, this removes the separate `IntuneWinAppUtil.exe` step from your pipeline. Munki has no analog - Intune integration is simply not a macOS concern in the same way.

### winget is not integrated

Cimian does not talk to winget. If you want a winget package in your catalog, wrap it in a `.ps1` or `.nupkg` that invokes `winget install` as the payload. This is a deliberate design choice: Cimian's authoritative receipts model does not mesh with winget's detection model.

## Cimian-only tools with no Munki cousin

- **`cimipkg.exe`** - package builder that takes a project directory and produces signed MSI plus NuPkg plus `.intunewin` in one shot. Fills the `productbuild` / `pkgbuild` / `munki-pkg` niche.
- **`cimiwatcher.exe`** - the Windows service that polls bootstrap and self-update flag files. Runs as SYSTEM, handles service lifecycle during self-updates.
- **`cimitrigger.exe`** - manual on-demand trigger utility. Useful for administrators who want to kick off a run from a shortcut or a remote session without editing bootstrap flags by hand.
- **`cimistatus.exe`** - WPF status monitor tray app, distinct from the ManagedSoftwareCenter GUI. Lightweight view into service state, recent sessions, and pending actions.
- **`repoclean.exe`** - repository pruning tool. Munki has one too (`repoclean`), but Cimian's C# port has some Cimian-specific extensions around YAML pkginfo handling; see [REPOCLEAN_TOOL.md](REPOCLEAN_TOOL.md).

## Your first pkginfo import

Assume you already have a signed MSI in hand and a working repo directory at `C:\CimianRepo\`. A minimum-viable import looks like this:

1. **Import the installer.**
   ```powershell
   sudo cimiimport.exe C:\Downloads\MyApp-1.2.3.msi --subfolder apps\productivity
   ```
   `cimiimport` inspects the MSI, extracts ProductCode / UpgradeCode / display name / version, and writes `C:\CimianRepo\pkgsinfo\apps\productivity\MyApp-1.2.3.yaml`. Same ergonomics as `munkiimport`.

2. **Edit the pkginfo if needed.** Open the generated YAML, adjust `catalogs`, add a `display_name` or `developer`, set `unattended_install: true`, add a `blocking_applications` list, whatever your workflow needs. The fields are the Munki fields.

3. **Rebuild catalogs.**
   ```powershell
   sudo makecatalogs.exe --repo-url C:\CimianRepo
   ```
   Produces `catalogs\Production.yaml` (or whatever catalog names you use), just like Munki.

4. **Add it to a manifest.**
   ```powershell
   sudo manifestutil.exe --add-pkg MyApp --manifest site_default
   ```
   Or edit `manifests\site_default.yaml` by hand if you prefer.

5. **Test on a client.**
   ```powershell
   sudo managedsoftwareupdate.exe -v --checkonly
   ```
   The `--checkonly` flag is your safety net: it does a full detection pass and prints what would be installed without actually installing anything. Always use `--checkonly` first when testing.

6. **When satisfied, run for real.**
   ```powershell
   sudo managedsoftwareupdate.exe -v --auto
   ```
   Identical verbs to Munki. Logs land at `C:\ProgramData\ManagedInstalls\Logs\{timestamp}\`.

If any step surprises you, the surprise is almost certainly a Windows quirk from the section above, not a Cimian-specific oddity.

## Where to go next

- [cimian-munki-gap-analysis.md](cimian-munki-gap-analysis.md) - the engineering-level parity ledger. Which Munki features are implemented, which are not, which are done differently.
- [conditional-items-guide.md](conditional-items-guide.md) - conditional manifests and NSPredicate syntax reference.
- [how-cimian-decides-what-needs-to-be-installed.md](how-cimian-decides-what-needs-to-be-installed.md) - deep dive into the detection pipeline.
- [bootstrap-system-analysis-with-cimianwatcher.md](bootstrap-system-analysis-with-cimianwatcher.md) - bootstrap mode and the watcher service.
- [cimian-logging-system.md](cimian-logging-system.md) - the JSON log format and session directories.
- [cimian-uninstall-scripts-supported.md](cimian-uninstall-scripts-supported.md) - the full uninstall method matrix (MSI, EXE, NuPkg, MSIX, scripts, uninstalls arrays).
- [install-loop-prevention.md](install-loop-prevention.md) - LoopGuard, Cimian's backoff system for self-healing against install loops.
