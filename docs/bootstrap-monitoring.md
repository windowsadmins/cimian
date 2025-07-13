# Cimian Bootstrap System - Windows Service Monitor

This document describes the Windows Service-based bootstrap monitoring system in Cimian that provides near-instantaneous response to bootstrap triggers from MDM systems.

## Problem Statement

The original bootstrap system relied on a scheduled task that runs on system startup and checks for the presence of the `.cimian.bootstrap` file. However, this approach has limitations:

- Only runs at system startup
- No real-time monitoring capability
- Cannot respond to MDM triggers outside of reboot cycles

## Solution: Windows Service Monitor

**The recommended and only supported approach**

- **Service**: `cimiwatcher.exe` - Native Windows service written in Go
- **Monitoring**: Polls the bootstrap flag file every 10 seconds
- **Response Time**: 10-15 seconds maximum
- **Reliability**: Highest - native Windows service with automatic restart
- **Requirements**: Administrative privileges for service installation

**Features:**
- Automatic installation during MSI setup
- Service logs to Windows Event Log
- Configurable polling interval (default 10 seconds)
- Automatic recovery from failures
- Can be managed via Windows Service Manager
- Lightweight and secure operation as SYSTEM account

## Installation and Setup

### Automatic Setup (Recommended)

The MSI installer automatically sets up the Windows Service monitor:

1. Installs the `cimiwatcher.exe` service executable
2. Registers the service with Windows Service Manager
3. Configures the service to start automatically at boot
4. Starts the service immediately after installation

The service runs continuously and monitors for the bootstrap flag file at:
`C:\ProgramData\ManagedInstalls\.cimian.bootstrap`

### Manual Setup

You can manually manage the CimianWatcher service using standard Windows service commands:

```cmd
# Install the service
"C:\Program Files\Cimian\cimiwatcher.exe" install

# Start the service
"C:\Program Files\Cimian\cimiwatcher.exe" start

# Stop the service
"C:\Program Files\Cimian\cimiwatcher.exe" stop

# Remove the service
"C:\Program Files\Cimian\cimiwatcher.exe" remove
```

Or using Windows Service Manager:
```powershell
# Check service status
Get-Service CimianWatcher

# Start the service
Start-Service CimianWatcher

# Stop the service
Stop-Service CimianWatcher
```

## Triggering Bootstrap from MDM

### Method 1: File-based Trigger (Recommended)

Use the provided PowerShell script:
```powershell
# Create the bootstrap flag file
& "C:\Program Files\Cimian\TriggerBootstrap.ps1"
```

Or manually create the file:
```powershell
# Create bootstrap flag file manually
$bootstrapFile = "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$content = "Bootstrap triggered at: $timestamp"
Set-Content -Path $bootstrapFile -Value $content -Encoding UTF8
```

### Method 2: Using managedsoftwareupdate.exe

```cmd
# Enable bootstrap mode (creates the flag file)
"C:\Program Files\Cimian\managedsoftwareupdate.exe" --set-bootstrap-mode

# Disable bootstrap mode
"C:\Program Files\Cimian\managedsoftwareupdate.exe" --clear-bootstrap-mode
```

## MDM Integration Examples

### Microsoft Intune - Remediation Script

Create a remediation script in Intune:

**Detection Script:**
```powershell
# Always return non-compliant to trigger remediation
exit 1
```

**Remediation Script:**
```powershell
# Download and run the bootstrap trigger
$scriptPath = "C:\Program Files\Cimian\TriggerBootstrap.ps1"
if (Test-Path $scriptPath) {
    & $scriptPath
    exit 0
} else {
    Write-Output "Cimian not installed"
    exit 1
}
```

### Group Policy - Startup Script

Add to Computer Configuration > Policies > Windows Settings > Scripts > Startup:

```powershell
C:\Program Files\Cimian\TriggerBootstrap.ps1
```

### SCCM - Package/Program

Create a package that runs:
```cmd
PowerShell.exe -ExecutionPolicy Bypass -File "C:\Program Files\Cimian\TriggerBootstrap.ps1"
```

## Monitoring and Troubleshooting

### Event Logs

All bootstrap activities are logged to the Windows Application Event Log under source "Cimian":

- Event ID 1001: Bootstrap setup activities
- Event ID 1002: Bootstrap trigger activities  
- Event ID 1003: Bootstrap execution activities

### Service Management

```powershell
# Check if service is running
Get-Service CimianWatcher

# View service configuration
Get-WmiObject -Class Win32_Service -Filter "Name='CimianWatcher'"

# Check bootstrap flag file
Test-Path "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"

# View recent event logs
Get-EventLog -LogName Application -Source "Cimian" -Newest 10
```

### Debug Mode

For troubleshooting, you can run the service in debug mode:

```cmd
# Run in debug mode (console output)
"C:\Program Files\Cimian\cimiwatcher.exe" debug
```

## Performance Impact

The Windows Service monitor is designed to be lightweight:

- **CPU Usage**: < 0.1% average
- **Memory Usage**: < 5MB 
- **Disk I/O**: Minimal (file existence check every 10 seconds)
- **Network Usage**: None (local monitoring only)

## Security Considerations

- Service runs as SYSTEM account with minimal privileges
- Only monitors file existence, does not read file contents
- Event logging provides audit trail
- File system permissions protect bootstrap flag file
- Service binary is signed (when code signing is configured)

## Backwards Compatibility

This system is fully backwards compatible with existing bootstrap implementations. The traditional scheduled task approach is still supported as a fallback, but the Windows Service approach provides superior responsiveness and reliability.

The original scheduled task bootstrap system remains in place as a fallback, ensuring compatibility with existing deployments that rely on the startup-based bootstrap check.
