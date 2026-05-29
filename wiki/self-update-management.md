# Cimian Self-Update Management

Cimian includes comprehensive self-update functionality that allows it to safely update itself when newer versions appear in the managed software repository.

## Overview

The self-update system is designed to be safe and robust:

- **Automatic Detection**: During normal operation, `managedsoftwareupdate` detects when Cimian packages appear in the repository
- **Deferred Execution**: Self-updates are scheduled for service restart to avoid file locking issues
- **Backup & Rollback**: Automatic backup of current binaries with rollback capability on failure
- **Service Coordination**: Proper coordination between services to prevent conflicts

## Self-Update Commands

All self-update management is integrated into the main `managedsoftwareupdate.exe` binary:

### Check Self-Update Status

```cmd
# Simple status check (for scripts)
managedsoftwareupdate --check-selfupdate

# Detailed status display (user-friendly)
managedsoftwareupdate --selfupdate-status
```

### Clear Self-Update Flag

```cmd
# Clear any pending self-update flag
managedsoftwareupdate --clear-selfupdate
```

### Trigger Self-Update

```cmd
# Restart the CimianWatcher Windows service via sc.exe stop/start.
# Any pending self-update flag is then picked up the next time the service starts.
managedsoftwareupdate --restart-service
```

`--restart-service` invokes `sc.exe stop CimianWatcher` followed by `sc.exe start CimianWatcher`. It does not perform the install itself — it simply restarts the service so the queued self-update can run.

### Advanced Commands

```cmd
# Manually perform a pending self-update in the current process
# (advanced/internal use; normally handled by CimianWatcher)
managedsoftwareupdate --perform-selfupdate
```

## How Self-Updates Work

1. **Detection**: During normal `managedsoftwareupdate` runs, the system checks if any Cimian packages (MSI or NUPKG) are available for installation
2. **Scheduling**: If a self-update is detected, it's scheduled for the next service restart instead of being performed immediately
3. **Execution**: When the CimianWatcher service restarts, it checks for and performs any pending self-updates
4. **Safety**: The system creates backups before updating and can rollback on failure

## Package Format Support

`SelfUpdateService` (in `shared/core/Services/SelfUpdateService.cs`) dispatches on
`InstallerType` and supports:

- **MSI** — installed via `msiexec.exe /i ... /quiet /norestart` with verbose
  logging to `CimianPaths.LogsDir`.
- **PKG** — installed via the sbin-installer at
  `C:\Program Files\sbin\installer.exe`.
- **NUPKG** — also installed via the sbin-installer (same code path as PKG).

Any other installer type returns "Unsupported installer type for
self-update".

## Integration with Existing Workflows

Self-updates integrate seamlessly with existing Cimian workflows:

- **Normal Operation**: Self-update detection happens during regular software management runs
- **Bootstrap Mode**: Self-updates are processed during bootstrap sequences
- **Service Management**: CimianWatcher service automatically handles scheduled self-updates

## Safety Features

- **File Locking Prevention**: Updates are deferred to avoid conflicts with running processes
- **Backup Creation**: Current binaries are backed up before replacement
- **Rollback Capability**: Failed updates can be rolled back to the previous version
- **Comprehensive Logging**: All self-update operations are logged for troubleshooting

## Example Usage Scenarios

### Check if Self-Update is Available

```cmd
managedsoftwareupdate --selfupdate-status
```

Illustrative output when an update is pending (exact wording is produced by `ShowSelfUpdateStatus` in `cli/managedsoftwareupdate/Program.cs`):
```
Cimian Self-Update Status
════════════════════════════
[STATUS]: Self-update pending

   Item: Cimian
   Version: 2025.08.19
   Installer: msi
   Scheduled: 2025-08-18T15:30:00.0000000-07:00

To trigger the update:
   managedsoftwareupdate --restart-service
```

When no update is pending the command prints `[STATUS]: No self-update pending` followed by `Cimian is up to date`.

### Trigger Self-Update

```cmd
managedsoftwareupdate --restart-service
```

Illustrative output:
```
Restarting Cimian Watcher Service
══════════════════════════════════
Stopping CimianWatcher...
Starting CimianWatcher...
CimianWatcher restarted successfully

Note: If a self-update was pending, it will be applied now.
```

### Clear Self-Update Flag

If you need to cancel a pending self-update:

```cmd
managedsoftwareupdate --clear-selfupdate
```

## Integration with Scripts

The self-update status can be checked in scripts. Note that `--check-selfupdate` currently returns exit code `0` in both the "pending" and "no update pending" cases (only a metadata read error returns `1`), so scripts should parse stdout rather than rely solely on `$LASTEXITCODE`:

```powershell
# Check if self-update is pending
$output = & managedsoftwareupdate --check-selfupdate
if ($output -match 'Update available') {
    Write-Host "Self-update is pending"
    # Optionally trigger the update
    & managedsoftwareupdate --restart-service
} else {
    Write-Host "No self-update pending"
}
```

## Troubleshooting

### View Self-Update Logs

Self-update operations are logged in the standard Cimian log files. Use verbose logging for detailed information:

```cmd
managedsoftwareupdate -vv --selfupdate-status
```

### Manual Self-Update Process

In rare cases where automatic self-update fails, you can manually perform the process:

1. Check status: `managedsoftwareupdate --selfupdate-status`
2. Clear flag: `managedsoftwareupdate --clear-selfupdate`
3. Run normal update: `managedsoftwareupdate --auto`
4. Check for new flag: `managedsoftwareupdate --selfupdate-status`
5. Trigger update: `managedsoftwareupdate --restart-service`

### Rollback Failed Update

If a self-update fails, the system will attempt automatic rollback. Check logs for details and ensure the CimianWatcher service is running properly.
