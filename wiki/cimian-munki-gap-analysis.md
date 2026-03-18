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

## True Remaining Gaps (Prioritized)

### Priority 1: Backend Enforcement Gaps

| # | Gap | Description | Implementation Effort |
|---|-----|-------------|----------------------|
| 1 | `force_install_after_date` enforcement | Model/UI exists but `managedsoftwareupdate` needs to auto-install when deadline passes (not just UI warnings) | Medium — add to `CatalogItem` + `InstallInfoItem`, enforce in `UpdateEngine` |
| 2 | `RestartAction` enforcement | Model exists, UI sorts by it. Post-install restart/logout not actually triggered | Medium — add to post-install flow in `UpdateEngine` |

### Priority 2: UI Feature Gaps

| # | Gap | Description | Implementation Effort |
|---|-----|-------------|----------------------|
| 3 | `featured_items` backend wiring | UI is fully built, but `WriteInstallInfo()` never populates `FeaturedItems` from manifests | Small — add `featured_items` to `ManifestFile`, wire through to `WriteInstallInfo()` |
| 4 | MSC text search | No search/filter in the item list | Medium — add SearchBox + filter logic to `SoftwareViewModel` |
| 5 | Installation history view | No UI to show past install actions | Medium — read from session logs |
| 6 | Deep link URL scheme (`cimian://`) | Not implemented | Medium — protocol registration + URI handler |

### Priority 3: New Feature Gaps

| # | Gap | Description | Implementation Effort |
|---|-----|-------------|----------------------|
| 7 | `version_script` | Script that returns installed version (alternative to registry/file checks) | Medium — add to `CatalogItem`, run in `StatusService` |
| 8 | `default_installs` | Manifest key for items installed only on first run (not enforced on subsequent runs) | Medium — add to `ManifestFile`, track in state |
| 9 | Admin-Provided Custom Conditions | Script-drop-folder mechanism for extending system facts | Medium — scan folder, run scripts, merge into facts |
| 10 | AutoRemove | Remove packages no longer in any manifest | Large — dependency tracking + safe removal |
| 11 | Precache | Download but don't install (for bandwidth management) | Medium — download-only mode in installer |
| 12 | Localization / i18n | Framework ready but all strings hardcoded English | Large — extract strings, add resource files |
| 13 | License seat tracking | Track available license seats per package | Large — server-side component needed |

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
