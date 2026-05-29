# CimianWatcher Dual-Mode Update Triggers

CimianWatcher now supports two different modes for triggering software updates:

## 1. GUI Mode (With CimianStatus Window)

**Trigger File:** `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`

**Behavior:** 
- Launches `managedsoftwareupdate.exe --auto --show-status -vv`
- Also launches `cimistatus.exe` so the status window is visible
- User can see real-time progress and status messages
- Ideal for interactive updates or when user feedback is desired

**How to Trigger:**
- From CimianStatus GUI: Click "Run Now" button
- From command line: `cimitrigger.exe gui`
- Programmatically: Create the `.cimian.bootstrap` file

## 2. Headless Mode (Silent Background Update)

**Trigger File:** `C:\ProgramData\ManagedInstalls\.cimian.headless`

**Behavior:**
- Launches `managedsoftwareupdate.exe --auto --show-status`
- Runs in the background with no visible window (`CreateNoWindow = true`) and does **not** launch `cimistatus.exe`
- The only command-line difference from GUI mode is the omission of the `-vv` verbosity flag
- Updates happen silently without user interaction
- Ideal for automated systems, scheduled tasks, or unattended updates

> **Note:** A flag file may include an `Args:` line to override the default arguments. When present, the service uses those arguments verbatim and skips launching `cimistatus.exe`, allowing callers such as Managed Software Center to manage their own UI.

**How to Trigger:**
- From command line: `cimitrigger.exe headless`
- Programmatically: Create the `.cimian.headless` file
- From C# code: Call `UpdateService.TriggerHeadlessUpdateAsync()`

## Service Behavior

The CimianWatcher Windows service monitors both trigger files simultaneously:

1. **File Detection:** Polls every 10 seconds for both `.cimian.bootstrap` and `.cimian.headless`
2. **Process Execution:** Starts the appropriate `managedsoftwareupdate.exe` command based on the trigger file
3. **Cleanup:** Deletes the trigger file *immediately after reading it* (before the update process starts). This early acknowledgement lets the Managed Software Center poll for deletion as the "trigger accepted" signal without timing out.
4. **Logging:** Records activity to the rolling Serilog file at `C:\ProgramData\ManagedInstalls\Logs\cimiwatcher.log` and to the Windows Event Log under the `CimianWatcher` source.

## File Format

Both trigger files contain metadata about when and how they were created:

```
Bootstrap triggered at: 2025-07-17 14:30:25
Mode: GUI|headless
Triggered by: CimianStatus UI|cimitrigger CLI|Custom Application
```

## Use Cases

### GUI Mode
- Manual updates initiated by users
- Troubleshooting scenarios where progress visibility is needed
- Initial software deployment where user confirmation is helpful

### Headless Mode
- Automated maintenance windows
- Scheduled updates via Task Scheduler
- Deployment scripts and automation
- Background updates in server environments
- Integration with configuration management tools

## Command-Line Tool

The `cimitrigger.exe` utility provides an easy way to trigger updates:

```cmd
# Trigger GUI update
cimitrigger.exe gui

# Trigger headless update  
cimitrigger.exe headless
```

## Error Handling

- If CimianWatcher service is not running, trigger files will remain until the service starts
- If `managedsoftwareupdate.exe` is not found, the service logs an error and continues monitoring
- Failed updates are logged in the Windows Event Log
- Trigger files are cleaned up even if the update process fails

## Monitoring

Check the Windows Event Log (Windows Logs > Application) for "CimianWatcher" source events to monitor:
- Service start/stop events
- Trigger file detection
- Update process execution
- Error conditions
