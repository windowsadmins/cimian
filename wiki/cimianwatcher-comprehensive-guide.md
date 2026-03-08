# CimianWatcher Testing and Documentation

This document provides comprehensive information about testing and using the CimianWatcher service component of the Cimian software management system.

## Overview

CimianWatcher is a Windows service that monitors for bootstrap triggers and automatically starts software installation processes. It's a key component that enables responsive, near-real-time software deployment via MDM systems like Microsoft Intune.

## Architecture

### Components
- **Service Executable**: `cimiwatcher.exe` - Native Windows service written in Go
- **Service Name**: `CimianWatcher`
- **Display Name**: `Cimian Bootstrap File Watcher`
- **Dependencies**: None (standalone service)
- **Installation Path**: `C:\Program Files\Cimian\cimiwatcher.exe`

### Bootstrap Monitoring
- **Flag File Location**: `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`
- **Polling Interval**: 10 seconds (configurable in source)
- **Trigger Method**: File existence and modification time detection
- **Action**: Executes `managedsoftwareupdate.exe --auto --show-status`

### Service Configuration
- **Start Type**: Automatic
- **Log On As**: Local System Account
- **Error Control**: Normal
- **Service Type**: Win32 Own Process

## Installation Methods

### 1. MSI Package Installation (Recommended)
The Cimian MSI installer includes automated service installation:

```powershell
# Install MSI (includes service setup)
msiexec /i "Cimian-x64-2025.07.12.msi" /qn

# Verify installation
Get-Service CimianWatcher
```

### 2. Manual Service Installation
If the MSI custom actions fail or for troubleshooting:

```cmd
# Install service (requires admin privileges)
"C:\Program Files\Cimian\cimiwatcher.exe" install

# Start service
"C:\Program Files\Cimian\cimiwatcher.exe" start

# Verify status
sc query CimianWatcher
```

### 3. PowerShell Service Management
Using Windows PowerShell cmdlets:

```powershell
# Check if service exists
Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue

# Start service if installed
Start-Service -Name "CimianWatcher"

# Stop service
Stop-Service -Name "CimianWatcher"

# Get detailed service information
Get-WmiObject -Class Win32_Service -Filter "Name='CimianWatcher'"
```

## Testing Procedures

### Basic Service Tests

#### 1. Service Installation Test
```powershell
# Test service installation
$service = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "✓ Service is installed" -ForegroundColor Green
    Write-Host "  Status: $($service.Status)"
    Write-Host "  StartType: $($service.StartType)"
} else {
    Write-Host "✗ Service not found" -ForegroundColor Red
}
```

#### 2. Executable Verification Test
```powershell
# Verify executable exists and is accessible
$exePath = "C:\Program Files\Cimian\cimiwatcher.exe"
if (Test-Path $exePath) {
    $fileInfo = Get-Item $exePath
    Write-Host "✓ Executable found" -ForegroundColor Green
    Write-Host "  Path: $($fileInfo.FullName)"
    Write-Host "  Size: $($fileInfo.Length) bytes"
    Write-Host "  Modified: $($fileInfo.LastWriteTime)"
    
    # Check if file is signed
    $signature = Get-AuthenticodeSignature $exePath
    Write-Host "  Signature: $($signature.Status)"
} else {
    Write-Host "✗ Executable not found at $exePath" -ForegroundColor Red
}
```

### Bootstrap Trigger Tests

#### 1. Manual Bootstrap Test
```powershell
# Create bootstrap flag file manually
$flagFile = "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$content = "Manual test trigger - $timestamp"

# Ensure directory exists
New-Item -ItemType Directory -Path (Split-Path $flagFile) -Force -ErrorAction SilentlyContinue

# Create flag file
Set-Content -Path $flagFile -Value $content -Encoding UTF8
Write-Host "✓ Bootstrap flag file created" -ForegroundColor Green
Write-Host "  Path: $flagFile"
Write-Host "  Content: $content"

# Wait and monitor for response
Write-Host "Waiting 15 seconds for service response..." -ForegroundColor Yellow
Start-Sleep -Seconds 15

# Check if file still exists (service may delete it)
if (Test-Path $flagFile) {
    Write-Host "⚠ Flag file still exists - service may not be responding" -ForegroundColor Yellow
} else {
    Write-Host "✓ Flag file processed by service" -ForegroundColor Green
}
```

#### 2. Automated Bootstrap Response Test
```powershell
# Monitor Windows Event Log for service activity
$events = Get-EventLog -LogName Application -Source "CimianWatcher" -Newest 5 -ErrorAction SilentlyContinue
if ($events) {
    Write-Host "✓ Service event log entries found:" -ForegroundColor Green
    $events | ForEach-Object {
        Write-Host "  $($_.TimeGenerated): $($_.Message.Substring(0, [Math]::Min(80, $_.Message.Length)))..."
    }
} else {
    Write-Host "⚠ No recent service log entries found" -ForegroundColor Yellow
}
```

### Performance and Monitoring Tests

#### 1. Service Resource Usage Test
```powershell
# Monitor service resource usage
$process = Get-Process -Name "cimianwatcher" -ErrorAction SilentlyContinue
if ($process) {
    Write-Host "✓ Service process running" -ForegroundColor Green
    Write-Host "  Process ID: $($process.Id)"
    Write-Host "  CPU Usage: $($process.CPU) seconds"
    Write-Host "  Memory Usage: $([Math]::Round($process.WorkingSet64 / 1MB, 2)) MB"
    Write-Host "  Start Time: $($process.StartTime)"
} else {
    Write-Host "✗ Service process not found" -ForegroundColor Red
}
```

#### 2. File System Monitoring Test
```powershell
# Test file system access to bootstrap directory
$monitorPath = "C:\ProgramData\ManagedInstalls"
try {
    $acl = Get-Acl $monitorPath
    Write-Host "✓ Bootstrap directory accessible" -ForegroundColor Green
    Write-Host "  Path: $monitorPath"
    Write-Host "  Owner: $($acl.Owner)"
} catch {
    Write-Host "✗ Cannot access bootstrap directory: $($_.Exception.Message)" -ForegroundColor Red
}
```

## Troubleshooting Guide

### Common Issues and Solutions

#### 1. Service Not Installing
**Symptoms**: MSI installs successfully but service doesn't appear
**Possible Causes**:
- Insufficient privileges during installation
- Antivirus software blocking service registration
- Corrupted service executable

**Solutions**:
```powershell
# Check MSI installation logs
Get-EventLog -LogName Application -Source "MsiInstaller" -Newest 10 | 
    Where-Object { $_.Message -like "*Cimian*" }

# Manually install service with explicit admin rights
Start-Process -FilePath "C:\Program Files\Cimian\cimiwatcher.exe" -ArgumentList "install" -Verb RunAs -Wait

# Verify service registration in registry
Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\CimianWatcher" -ErrorAction SilentlyContinue
```

#### 2. Service Installed but Not Starting
**Symptoms**: Service appears in Services.msc but fails to start
**Possible Causes**:
- Missing dependencies
- Incorrect permissions
- Port conflicts
- Corrupted executable

**Solutions**:
```powershell
# Check service startup errors in Event Log
Get-EventLog -LogName System -Source "Service Control Manager" -Newest 10 | 
    Where-Object { $_.Message -like "*CimianWatcher*" }

# Test executable directly
Start-Process -FilePath "C:\Program Files\Cimian\cimiwatcher.exe" -ArgumentList "debug" -NoNewWindow

# Reset service configuration
sc delete CimianWatcher
"C:\Program Files\Cimian\cimiwatcher.exe" install
```

#### 3. Service Running but Not Responding to Bootstrap
**Symptoms**: Service appears healthy but doesn't trigger on flag file creation
**Possible Causes**:
- Incorrect file path monitoring
- Permission issues accessing flag file location
- Service logic errors

**Solutions**:
```powershell
# Verify service can access bootstrap directory
$testFile = "C:\ProgramData\ManagedInstalls\test.tmp"
try {
    "test" | Out-File -FilePath $testFile
    Remove-Item $testFile
    Write-Host "✓ Directory access OK" -ForegroundColor Green
} catch {
    Write-Host "✗ Directory access failed: $($_.Exception.Message)" -ForegroundColor Red
}

# Monitor service in debug mode (requires stopping service first)
Stop-Service CimianWatcher
Start-Process -FilePath "C:\Program Files\Cimian\cimiwatcher.exe" -ArgumentList "debug" -NoNewWindow
```

### Debug Mode Testing

For in-depth troubleshooting, run the service in debug mode:

```cmd
# Stop the service if running
sc stop CimianWatcher

# Run in debug mode (console output)
"C:\Program Files\Cimian\cimiwatcher.exe" debug
```

Debug mode provides real-time console output showing:
- Service initialization
- File system monitoring events
- Bootstrap trigger detection
- Command execution results
- Error messages and stack traces

### Log Analysis

The service logs to Windows Event Log under the "CimianWatcher" source:

```powershell
# View all service events
Get-EventLog -LogName Application -Source "CimianWatcher" | 
    Sort-Object TimeGenerated -Descending |
    Select-Object TimeGenerated, EntryType, Message

# Filter for error events only
Get-EventLog -LogName Application -Source "CimianWatcher" -EntryType Error

# Monitor events in real-time
Get-EventLog -LogName Application -Source "CimianWatcher" -Newest 1 -Wait
```

## Performance Characteristics

### Resource Usage (Typical)
- **CPU Usage**: < 0.1% average, brief spikes during file checks
- **Memory Usage**: ~5-10 MB resident set size
- **Disk I/O**: Minimal, periodic file existence checks only
- **Network Usage**: None (local file monitoring only)

### Responsiveness
- **Polling Interval**: 10 seconds (configurable in source)
- **Detection Latency**: 0-10 seconds after flag file creation
- **Bootstrap Start Time**: < 5 seconds after detection
- **Resource Cleanup**: Automatic, no manual intervention required

## Security Considerations

### Service Security
- **Execution Context**: Local System Account (NT AUTHORITY\SYSTEM)
- **Required Privileges**: Service logon rights, file system access
- **Attack Surface**: Minimal - only monitors file existence
- **Input Validation**: File path and timestamp validation only

### File System Security
- **Monitor Location**: `C:\ProgramData\ManagedInstalls` (system directory)
- **Access Requirements**: Read access to monitoring directory
- **File Content**: Service does not read flag file contents
- **Cleanup**: Automatic removal of processed flag files

### Network Security
- **Network Usage**: None - purely local file system operation
- **Firewall Requirements**: No inbound/outbound rules needed
- **Communication**: Local process execution only

## Integration Examples

### MDM Integration (Microsoft Intune)

#### Proactive Remediation Script
```powershell
# Detection Script (always returns non-compliant to trigger)
exit 1

# Remediation Script
$flagFile = "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$content = "Intune triggered bootstrap - $timestamp"

try {
    New-Item -ItemType Directory -Path (Split-Path $flagFile) -Force -ErrorAction SilentlyContinue
    Set-Content -Path $flagFile -Value $content -Encoding UTF8
    Write-Output "Bootstrap trigger created successfully"
    exit 0
} catch {
    Write-Output "Failed to create bootstrap trigger: $($_.Exception.Message)"
    exit 1
}
```

#### PowerShell Script Deployment
```powershell
# Deploy as Intune PowerShell script
$bootstrapTrigger = {
    $flagFile = "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"
    $content = "Intune script trigger - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    
    New-Item -ItemType Directory -Path (Split-Path $flagFile) -Force -ErrorAction SilentlyContinue
    Set-Content -Path $flagFile -Value $content
}

Invoke-Command -ScriptBlock $bootstrapTrigger
```

### Group Policy Integration

#### Startup Script
Add to Computer Configuration > Windows Settings > Scripts > Startup:
```cmd
powershell.exe -Command "Set-Content -Path 'C:\ProgramData\ManagedInstalls\.cimian.bootstrap' -Value 'GPO startup trigger - %date% %time%'"
```

#### Scheduled Task
```xml
<!-- Task Scheduler XML for periodic bootstrap triggers -->
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>2025-01-01T09:00:00</StartBoundary>
      <Schedule>
        <ScheduleByDay>
          <DaysInterval>1</DaysInterval>
        </ScheduleByDay>
      </Schedule>
    </CalendarTrigger>
  </Triggers>
  <Actions>
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-Command "Set-Content -Path 'C:\ProgramData\ManagedInstalls\.cimian.bootstrap' -Value 'Scheduled trigger - $(Get-Date)'"</Arguments>
    </Exec>
  </Actions>
</Task>
```

## API Reference

### Command Line Interface

The `cimiwatcher.exe` supports the following commands:

```cmd
cimiwatcher.exe install    # Install service
cimiwatcher.exe remove     # Remove service
cimiwatcher.exe start      # Start service
cimiwatcher.exe stop       # Stop service
cimiwatcher.exe pause      # Pause service
cimiwatcher.exe continue   # Resume service
cimiwatcher.exe debug      # Run in debug mode (console)
```

### Exit Codes
- **0**: Success
- **1**: General error
- **2**: Invalid arguments
- **5**: Access denied
- **1056**: Service already running
- **1060**: Service not installed

### Configuration

Currently, configuration is compile-time only. Key constants in source:
- `bootstrapFlagFile`: File path to monitor
- `cimianExePath`: Path to main Cimian executable  
- `pollInterval`: Time between file system checks
- `serviceName`: Windows service name
- `serviceDisplayName`: Display name in Services.msc

## Version History

### Current Version (2025.07.12)
- Initial stable release
- Bootstrap file monitoring
- Windows service integration
- Event logging support
- Debug mode for troubleshooting

### Future Enhancements
- Configuration file support
- Multiple monitor paths
- Custom polling intervals
- Web interface for status
- Performance metrics collection

## Support and Maintenance

### Health Monitoring
```powershell
# Comprehensive health check script
function Test-CimianWatcher {
    $results = @{}
    
    # Service status
    $service = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
    $results.ServiceInstalled = $service -ne $null
    $results.ServiceRunning = $service.Status -eq "Running"
    
    # Executable status
    $exePath = "C:\Program Files\Cimian\cimiwatcher.exe"
    $results.ExecutableExists = Test-Path $exePath
    
    # Directory access
    $monitorPath = "C:\ProgramData\ManagedInstalls"
    $results.DirectoryAccessible = Test-Path $monitorPath
    
    # Recent activity
    $events = Get-EventLog -LogName Application -Source "CimianWatcher" -Newest 1 -ErrorAction SilentlyContinue
    $results.RecentActivity = $events -ne $null -and $events[0].TimeGenerated -gt (Get-Date).AddHours(-1)
    
    return $results
}

# Run health check
$health = Test-CimianWatcher
$health | Format-Table -AutoSize
```

### Maintenance Tasks
```powershell
# Monthly maintenance script
function Invoke-CimianWatcherMaintenance {
    # Clean old event logs (keep last 100 entries)
    $events = Get-EventLog -LogName Application -Source "CimianWatcher" | 
              Sort-Object TimeGenerated -Descending | 
              Select-Object -Skip 100
    
    # Archive and remove old events
    if ($events) {
        $archivePath = "C:\ProgramData\ManagedInstalls\logs\cimianwatcher_$(Get-Date -Format 'yyyyMM').log"
        $events | Export-Csv -Path $archivePath -NoTypeInformation
        # Note: Actual event log cleanup requires admin tools
    }
    
    # Service restart for health
    Restart-Service -Name "CimianWatcher" -Force
    
    # Verify functionality
    Start-Sleep -Seconds 5
    $testResult = Test-CimianWatcher
    return $testResult
}
```

This documentation provides comprehensive coverage of the CimianWatcher service, including installation, testing, troubleshooting, and integration guidance for enterprise deployment scenarios.
