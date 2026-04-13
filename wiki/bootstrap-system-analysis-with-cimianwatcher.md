# Bootstrap System

Cimian's bootstrap system provides zero-touch, MDM-initiated software deployment for Windows endpoints. A single flag file on disk triggers a full `managedsoftwareupdate` run within seconds, whether the client is freshly provisioned or already enrolled.

## Architecture

The system has two cooperating components:

1. **`managedsoftwareupdate.exe`** (`cli/managedsoftwareupdate/Program.cs`) exposes `--set-bootstrap-mode` and `--clear-bootstrap-mode` flags that create or remove the bootstrap flag file at `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`. No scheduled tasks, no service registration - just the file.

2. **CimianWatcher service** (`cli/cimiwatcher/Program.cs`) is a native Windows service that polls the bootstrap flag file every 10 seconds. When a new or modified flag file is detected, it invokes `managedsoftwareupdate.exe --auto --show-status` to run a full deployment cycle. Runs as SYSTEM, auto-restarts on failure.

Both components monitor the same file. No scheduled tasks are involved. (Earlier versions used a `CimianBootstrapCheck` scheduled task as a secondary trigger - this has been removed entirely; see `cimianwatcher-comprehensive-guide.md` for the cleanup path for legacy installations.)

## Flag file

- **Path:** `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`
- **Contents:** timestamp of when bootstrap mode was enabled. Body is informational only - the presence of the file is what matters.
- **Companion flag:** `.cimian.headless` at the same path selects headless (non-interactive) execution.

## Typical flows

### MDM-initiated install (Intune, etc.)

1. MDM drops `.cimian.bootstrap` via Win32 app, PowerShell script, or proactive remediation
2. CimianWatcher detects the file within 10 seconds
3. CimianWatcher invokes `managedsoftwareupdate.exe --auto --show-status`
4. Deployment runs end-to-end
5. `managedsoftwareupdate` clears the flag file on successful exit

### Manual admin trigger

```powershell
sudo managedsoftwareupdate.exe --set-bootstrap-mode
# CimianWatcher picks it up within 10 seconds and runs
# Clear the flag manually if needed:
sudo managedsoftwareupdate.exe --clear-bootstrap-mode
```

### Fresh device enrollment

1. Device image or provisioning package deploys Cimian with CimianWatcher service registered
2. First-boot script drops `.cimian.bootstrap`
3. CimianWatcher runs the initial deployment with relaxed thresholds (longer timeouts, no install-window gating)
4. Flag file is cleared after first successful run

## Performance characteristics

| Metric | Value |
|---|---|
| Response time | ~10 seconds (poll interval) plus execution time |
| CimianWatcher memory | ~5 MB |
| CimianWatcher CPU | less than 0.1% idle |
| Reliability | Service auto-restart on failure; polling survives sleep/wake |

## Enterprise integration

The flag-file pattern is designed to be driven by any system that can write a file:

- **Microsoft Intune** - Win32 app install scripts, proactive remediation scripts, PowerShell scripts
- **MDM policies** - any CSP that can run a PowerShell command
- **Orchestration tools** - Azure Arc, Configuration Manager, custom RMM tooling
- **Local automation** - Group Policy, scheduled tasks outside Cimian, manual admin flows

No MDM-specific code lives in Cimian itself - the flag file is the entire API.

## Related documentation

- [CimianWatcher comprehensive guide](cimianwatcher-comprehensive-guide.md) - service internals, testing, and overview
- [CimianWatcher dual-mode guide](cimianwatcher-dual-mode-guide.md) - GUI vs headless trigger modes
- [CimianWatcher enterprise deployment](cimianwatcher-enterprise-deployment.md) - MSI custom actions and scale scenarios
- [Self-update management](self-update-management.md) - how CimianWatcher handles self-update flag files alongside bootstrap
