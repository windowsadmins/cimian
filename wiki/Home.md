# Cimian Documentation Wiki

Cimian is an enterprise Windows software deployment system modeled on [Munki](https://github.com/munki/munki). It runs the same detect/download/install loop Mac admins already know, uses the same repo layout (`pkgsinfo/ catalogs/ manifests/`), and speaks a pkginfo schema that is Munki-compatible where Windows realities allow.

## Start here

- [Cimian and Munki: a comparison for Mac admins](cimian-munki-comparison.md) - if you already know Munki, start here
- [Project structure](PROJECT_STRUCTURE.md) - repository layout and component map
- [How Cimian decides what needs to be installed](how-cimian-decides-what-needs-to-be-installed.md) - the detection pipeline explained

## Package authoring

- [Conditional items guide](conditional-items-guide.md) - NSPredicate-style conditions on manifests and pkgsinfo
- [Uninstall scripts supported](cimian-uninstall-scripts-supported.md) - full matrix of uninstall method types
- [`uninstallable` key usage](uninstallable-key-usage.md) - explicit vs auto-determined uninstallability
- [MSI ProductID handling](MSI-ProductID-Fix.md) - ProductCode and UpgradeCode stability
- [`chocolateyBeforeInstall` support](chocolateyBeforeInstall-support.md) - pre-install hook for Chocolatey-style packages
- [Chocolatey shim prevention](chocolatey-shim-prevention.md) - stopping Chocolatey from creating shim exes
- [PowerShell execution policy bypass](powershell-execution-policy-bypass.md) - how Cimian runs pkginfo scripts

## Client runtime and services

- [Bootstrap system](bootstrap-system-analysis-with-cimianwatcher.md) - zero-touch provisioning architecture
- [CimianWatcher comprehensive guide](cimianwatcher-comprehensive-guide.md) - the watcher service, testing, and overview
- [CimianWatcher dual-mode guide](cimianwatcher-dual-mode-guide.md) - GUI vs headless trigger modes
- [CimianWatcher enterprise deployment](cimianwatcher-enterprise-deployment.md) - MSI custom actions and scale scenarios
- [Install loop prevention](install-loop-prevention.md) - LoopGuard and exponential backoff
- [Self-update management](self-update-management.md) - operator-facing self-update controls
- [Self-update detection logic](self-update-detection-logic.md) - which packages trigger a self-update
- [Self-update mechanism analysis](cimian-selfupdate-mechanism-analysis.md) - internal mechanism reference

## Logging, status, and diagnostics

- [Cimian logging system](cimian-logging-system.md) - JSON-structured per-session log format
- [Error reporting guide](cimian-error-reporting-guide.md) - what errors get surfaced, where, and how
- [ReportMate status specification](cimian-reportmate-status-specification.md) - contract for the ReportMate integration
- [CimianStatus UI](cimianstatus-ui-modernization.md) - WPF status app design spec
- [Status classification implementation](status-classification-implementation.md) - how StatusReasonCode values are assigned
- [Item source traceability](item-source-traceability.md) - which manifest or condition caused each action
- [cimitrigger troubleshooting](cimitrigger-troubleshooting.md) - manual trigger utility diagnostics
- [Privilege elevation troubleshooting](privilege-elevation-troubleshooting.md) - UAC and service account issues

## Integrations and enterprise

- [CSP OMA-URI configuration](csp-oma-uri-configuration.md) - Intune CSP-based config delivery
- [Managed profiles and apps guide](managed-profiles-apps-guide.md) - Graph API integration for profiles and apps
- [`repoclean` tool](REPOCLEAN_TOOL.md) - repository pruning (keep N versions, remove orphans)

## Munki parity engineering

- [Cimian vs Munki: feature gap analysis](cimian-munki-gap-analysis.md) - engineering parity ledger
- [Munki LoopGuard spec](munki-loopguard-spec.md) - loop prevention spec aligned to Munki
- [Munki structured logging spec](munki-structured-logging-spec.md) - structured log contract aligned to Munki

---

*Source for this wiki lives alongside the codebase at `packages/CimianTools/wiki/`. Issues and corrections welcome.*
