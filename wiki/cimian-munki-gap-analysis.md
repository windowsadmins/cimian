# Cimian vs Munki — Feature Gap Analysis

**Date:** March 2026  
**Cimian Version:** C# (100% migrated, .NET 8/10)  
**Munki Version:** 7.x (Swift) / 6.x (Python)

## Features Confirmed Implemented in Cimian

| Feature | Cimian Location | Notes |
|---------|----------------|-------|
| `blocking_applications` | GUI: `ItemDetailViewModel.cs`, `SoftwareViewModel.cs` | Process.GetProcessesByName() + "Install Anyway"/"Cancel" dialog |
| Preflight/Postflight scripts | `ScriptService.cs`, `UpdateEngine.cs` | abort/warn/continue failure handling, 4 CLI flags |
| `RestartAction` | `InstallInfo.cs` (model), `UpdatesViewModel.cs` (UI sorting) | RequireRestart, RequireLogout properties |
| `OnDemand` | `PkgsInfo.cs`, `Program.cs` (--OnDemand flag) | Model + CLI flag + tests + docs |
| `unattended_install` | `ImportModels.cs`, `UpdateModels.cs` | Always present, default true for imports |
| `force_install_after_date` | `InstallInfo.cs` (GUI model), `ShellViewModel.cs` | Deadline ordering + warning text + aggressive notification mode |
| Pre-action alerts | ContentDialog via AlertService | preinstall_alert, preuninstall_alert, preupgrade_alert |
| Screenshots | FlipView carousel in `ItemDetailPage.xaml` | Full image gallery support |
| Custom sidebar | `preferences.yaml` → `sidebar_items` | Configurable sidebar sections |
| Custom branding | `branding.yaml` → `app_title`, `sidebar_header` | App title + header image |
| Notification escalation | "Obnoxious mode" via `aggressive_notification_days` | Progressively intrusive notifications |
| Toast notifications | Windows AppNotifications API | System-level notifications |
| `installable_condition` | `repoclean` models | Conditional installability |
| Install loop prevention | `LoopGuard` service | Exponential backoff with state persistence |
| Structured JSON logging | `SessionLogger` | Session-based JSONL event streams |
| Per-package scripts | `preinstall_script`, `postinstall_script`, etc. | 6 script hooks per package |
| `installcheck_script` | `CatalogItem.cs`, `StatusService.cs` | Script-based install detection |
| `featured_items` | `ManifestFile`, `ManifestService`, `WriteInstallInfo()` | Manifest → InstallInfo backend wiring |
| `force_install_after_date` plumbing | `CatalogItem`, `InstallInfoItem`, `UpdateEngine` | Model wired through to InstallInfo.yaml |
| `restart_action` plumbing | `CatalogItem`, `InstallInfoItem`, `UpdateEngine` | Model wired through to InstallInfo.yaml |
| `version_script` | `CatalogItem`, `StatusService.CheckVersionScript()` | Munki v7 parity — script stdout = version, empty/non-zero = not installed |
| `default_installs` | `ManifestFile`, `ManifestService`, `UpdateEngine` | Install-once semantics, not re-enforced after first install |
| `precache` | `CatalogItem`, `UpdateEngine.PrecacheOptionalItemsAsync()` | Download-only for optional items; `Precached` flag in InstallInfo |
| SSL client certificates | `CimianConfig`, `CimianHttpClientFactory` | mTLS via PFX file or Windows cert store thumbprint; custom CA support |

## True Remaining Gaps (Prioritized)

### Priority 1: Backend Enforcement Gaps

| # | Gap | Description | Implementation Effort |
|---|-----|-------------|----------------------|
| 1 | ~~`force_install_after_date` enforcement~~ | **DONE** — `IdentifyActions` forces optional items when deadline passes; deadline overrides `install_window` deferral | Implemented |
| 2 | ~~`RestartAction` enforcement~~ | **DONE** — `RequireRestart`/`RecommendRestart` schedules reboot; `RequireLogout` forces logoff. Auto/bootstrap only; interactive logs recommendation. | Implemented |

### Priority 2: UI Feature Gaps

| # | Gap | Description | Implementation Effort |
|---|-----|-------------|----------------------|
| 3 | ~~`featured_items` backend wiring~~ | **DONE** — Wired from manifests through to `WriteInstallInfo()` | Implemented |
| 4 | ~~MSC text search~~ | **DONE** — `AutoSuggestBox` + `ApplyFilters()` searches name, description, developer, category | Implemented |
| 5 | ~~Installation history view~~ | **DONE** — `HistoryPage` reads `sessions.json`, wired into navigation + DI | Implemented |
| 6 | Deep link URL scheme (`cimian://`) | Not implemented | Medium — protocol registration + URI handler |

### Priority 3: New Feature Gaps

| # | Gap | Description | Implementation Effort |
|---|-----|-------------|----------------------|
| 7 | ~~`version_script`~~ | **DONE** — Munki v7 parity. Priority 2 in detection chain. | Implemented |
| 8 | ~~`default_installs`~~ | **DONE** — Install-once semantics in ManifestService + UpdateEngine. | Implemented |
| 9 | ~~Admin-Provided Custom Conditions~~ | **DONE** — Scripts in `C:\ProgramData\ManagedInstalls\conditions\` (.ps1/.bat/.cmd/.exe), stdout parsed as key=value, merged into CustomFacts | Implemented |
| 10 | ~~AutoRemove~~ | **DONE** — `AutoRemove` config option; compares ManagedInstalls registry against manifests, queues orphaned packages for uninstall | Implemented |
| 11 | ~~Precache~~ | **DONE** — `precache` bool on catalog items; `PrecacheOptionalItemsAsync()` downloads to cache without installing. | Implemented |
| 12 | Localization / i18n | Framework ready but all strings hardcoded English | Large — extract strings, add resource files |
| 13 | License seat tracking | Track available license seats per package | Large — server-side component needed |
| 14 | ~~SSL Client Certificates~~ | **DONE** — mTLS via PFX/cert store + custom CA validation via `CimianHttpClientFactory` | Implemented |

## Unique Cimian Features (Not in Munki)

| Feature | Description |
|---------|-------------|
| Windows service monitoring | CimianWatcher as persistent Windows Service |
| MDM file trigger | Real-time file watcher for MDM-initiated installs |
| Bootstrap mode | Zero-touch provisioning with relaxed thresholds |
| Multi-format installers | MSI, EXE, PowerShell, NuPkg, MSIX |
| Item source traceability | Debug which manifest/condition caused each item |
| Managed profiles/apps | Intune Graph API integration |
| Chocolatey shim prevention | Block Chocolatey shim creation during install |
| DPAPI encrypted auth | Windows credential protection for repo auth |
| Install window scheduling | Time-based installation constraints |
| SSL client cert + custom CA | `CimianHttpClientFactory` — PFX file, Windows cert store thumbprint, custom CA chain validation |
