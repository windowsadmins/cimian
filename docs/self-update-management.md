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
# Restart CimianWatcher service (triggers self-update if pending)
managedsoftwareupdate --restart-service
```

### Advanced Commands

```cmd
# Manually perform self-update (advanced/internal use)
managedsoftwareupdate --perform-selfupdate
```

## How Self-Updates Work

1. **Detection**: During normal `managedsoftwareupdate` runs, the system checks if any Cimian packages (MSI or NUPKG) are available for installation
2. **Scheduling**: If a self-update is detected, it's scheduled for the next service restart instead of being performed immediately
3. **Execution**: When the CimianWatcher service restarts, it checks for and performs any pending self-updates
4. **Safety**: The system creates backups before updating and can rollback on failure

## Package Format Support

The self-update system supports both package formats:

- **MSI Packages**: Windows Installer packages for system-wide installation
- **NUPKG Packages**: Chocolatey/NuGet packages for managed environments

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

Output example:
```
🔄 Cimian Self-Update Status
════════════════════════════
📋 Status: Self-update pending
📦 Update details:
   version: 2025.08.19
   package: Cimian-x64-2025.08.19.msi
   scheduled: 2025-08-18T15:30:00Z

💡 To trigger the update:
   managedsoftwareupdate --restart-service
```

### Trigger Self-Update

```cmd
managedsoftwareupdate --restart-service
```

Output example:
```
🔄 Restarting CimianWatcher service...
✅ CimianWatcher service stopped
   Waiting for service to stop... done
✅ CimianWatcher service restarted successfully
ℹ️  Self-update will be processed if pending
```

### Clear Self-Update Flag

If you need to cancel a pending self-update:

```cmd
managedsoftwareupdate --clear-selfupdate
```

## Integration with Scripts

The self-update status can be checked in scripts:

```powershell
# Check if self-update is pending
& managedsoftwareupdate --check-selfupdate
if ($LASTEXITCODE -eq 0) {
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
