# CimianWatcher Service Diagnostic and Repair Script
# This script helps diagnose and fix common CimianWatcher service issues

param(
    [string]$InstallPath = "C:\Program Files\Cimian",
    [switch]$Fix,
    [switch]$Verbose
)

$ErrorActionPreference = 'Stop'

function Write-DiagnosticOutput {
    param(
        [string]$Message,
        [string]$Level = "Info"  # Info, Warning, Error, Success
    )
    
    $color = switch ($Level) {
        "Info" { "White" }
        "Warning" { "Yellow" }
        "Error" { "Red" }
        "Success" { "Green" }
        default { "White" }
    }
    
    $prefix = switch ($Level) {
        "Info" { "ℹ️" }
        "Warning" { "⚠️" }
        "Error" { "❌" }
        "Success" { "✅" }
        default { "  " }
    }
    
    Write-Host "$prefix $Message" -ForegroundColor $color
}

function Get-ServiceDiagnostics {
    param([string]$InstallPath)
    
    $diagnostics = @{
        ServiceExists = $false
        ServiceRunning = $false
        ExecutableExists = $false
        ExecutableWorking = $false
        ServiceDetails = $null
        LastError = $null
        EventLogErrors = @()
    }
    
    # Check if cimiwatcher.exe exists
    $cimiwatcherExe = Join-Path $InstallPath "cimiwatcher.exe"
    $diagnostics.ExecutableExists = Test-Path $cimiwatcherExe
    
    if ($diagnostics.ExecutableExists) {
        Write-DiagnosticOutput "CimianWatcher executable found: $cimiwatcherExe" "Success"
        
        # Test if executable responds to --version
        try {
            $versionOutput = & $cimiwatcherExe --version 2>&1
            if ($LASTEXITCODE -eq 0) {
                $diagnostics.ExecutableWorking = $true
                Write-DiagnosticOutput "Executable responds to --version: $versionOutput" "Success"
            } else {
                Write-DiagnosticOutput "Executable failed --version test (exit code: $LASTEXITCODE)" "Error"
                Write-DiagnosticOutput "Output: $versionOutput" "Error"
            }
        } catch {
            Write-DiagnosticOutput "Failed to test executable: $_" "Error"
            $diagnostics.LastError = $_.Exception.Message
        }
    } else {
        Write-DiagnosticOutput "CimianWatcher executable not found: $cimiwatcherExe" "Error"
    }
    
    # Check service status
    try {
        $service = Get-Service -Name "CimianWatcher" -ErrorAction Stop
        $diagnostics.ServiceExists = $true
        $diagnostics.ServiceRunning = ($service.Status -eq "Running")
        
        Write-DiagnosticOutput "CimianWatcher service exists with status: $($service.Status)" "Info"
        
        # Get detailed service information
        $serviceDetails = Get-WmiObject -Class Win32_Service -Filter "Name='CimianWatcher'" -ErrorAction SilentlyContinue
        if ($serviceDetails) {
            $diagnostics.ServiceDetails = $serviceDetails
            Write-DiagnosticOutput "Service display name: $($serviceDetails.DisplayName)" "Info"
            Write-DiagnosticOutput "Service start mode: $($serviceDetails.StartMode)" "Info"
            Write-DiagnosticOutput "Service path: $($serviceDetails.PathName)" "Info"
            Write-DiagnosticOutput "Service account: $($serviceDetails.StartName)" "Info"
            
            if ($diagnostics.ServiceRunning) {
                Write-DiagnosticOutput "Service process ID: $($serviceDetails.ProcessId)" "Info"
            }
        }
        
    } catch {
        Write-DiagnosticOutput "CimianWatcher service not found or inaccessible: $_" "Error"
        $diagnostics.LastError = $_.Exception.Message
    }
    
    # Check recent event log entries for CimianWatcher
    try {
        $recentEvents = Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddHours(-24)} -ErrorAction SilentlyContinue |
                       Where-Object { $_.Message -like "*CimianWatcher*" -or $_.Message -like "*cimiwatcher*" } |
                       Select-Object -First 10
        
        if ($recentEvents) {
            Write-DiagnosticOutput "Found $($recentEvents.Count) recent event log entries related to CimianWatcher:" "Info"
            foreach ($logEvent in $recentEvents) {
                $level = switch ($logEvent.LevelDisplayName) {
                    "Error" { "Error" }
                    "Warning" { "Warning" }
                    default { "Info" }
                }
                Write-DiagnosticOutput "  [$($logEvent.TimeCreated)] $($logEvent.LevelDisplayName): $($logEvent.Message)" $level
                if ($logEvent.LevelDisplayName -eq "Error") {
                    $diagnostics.EventLogErrors += $logEvent
                }
            }
        } else {
            Write-DiagnosticOutput "No recent event log entries found for CimianWatcher" "Info"
        }
    } catch {
        Write-DiagnosticOutput "Failed to check event logs: $_" "Warning"
    }
    
    return $diagnostics
}

function Repair-CimianWatcherService {
    param(
        [string]$InstallPath,
        [hashtable]$Diagnostics
    )
    
    Write-DiagnosticOutput "=== Attempting to repair CimianWatcher service ===" "Info"
    
    $cimiwatcherExe = Join-Path $InstallPath "cimiwatcher.exe"
    
    if (-not $Diagnostics.ExecutableExists) {
        Write-DiagnosticOutput "Cannot repair: CimianWatcher executable not found" "Error"
        return $false
    }
    
    if (-not $Diagnostics.ExecutableWorking) {
        Write-DiagnosticOutput "Cannot repair: CimianWatcher executable is not working properly" "Error"
        return $false
    }
    
    try {
        # Step 1: Stop service if running
        if ($Diagnostics.ServiceExists -and $Diagnostics.ServiceRunning) {
            Write-DiagnosticOutput "Stopping CimianWatcher service..." "Info"
            try {
                Stop-Service -Name "CimianWatcher" -Force -ErrorAction Stop
                Start-Sleep -Seconds 5
                Write-DiagnosticOutput "Service stopped successfully" "Success"
            } catch {
                Write-DiagnosticOutput "Failed to stop service gracefully, attempting force kill..." "Warning"
                try {
                    Get-Process -Name "cimiwatcher" -ErrorAction SilentlyContinue | Stop-Process -Force
                    Start-Sleep -Seconds 3
                    Write-DiagnosticOutput "Force killed cimiwatcher processes" "Success"
                } catch {
                    Write-DiagnosticOutput "Failed to force kill cimiwatcher processes: $_" "Error"
                }
            }
        }
        
        # Step 2: Uninstall existing service if it exists
        if ($Diagnostics.ServiceExists) {
            Write-DiagnosticOutput "Uninstalling existing CimianWatcher service..." "Info"
            try {
                & $cimiwatcherExe uninstall
                Start-Sleep -Seconds 3
                Write-DiagnosticOutput "Service uninstalled successfully" "Success"
            } catch {
                Write-DiagnosticOutput "Failed to uninstall service: $_" "Warning"
                # Try using sc.exe as fallback
                try {
                    & sc.exe delete CimianWatcher
                    Start-Sleep -Seconds 3
                    Write-DiagnosticOutput "Service deleted using sc.exe" "Success"
                } catch {
                    Write-DiagnosticOutput "Failed to delete service using sc.exe: $_" "Error"
                }
            }
        }
        
        # Step 3: Install service fresh
        Write-DiagnosticOutput "Installing CimianWatcher service..." "Info"
        & $cimiwatcherExe install
        if ($LASTEXITCODE -ne 0) {
            throw "Service installation failed with exit code: $LASTEXITCODE"
        }
        Start-Sleep -Seconds 3
        Write-DiagnosticOutput "Service installed successfully" "Success"
        
        # Step 4: Start service
        Write-DiagnosticOutput "Starting CimianWatcher service..." "Info"
        & $cimiwatcherExe start
        if ($LASTEXITCODE -ne 0) {
            throw "Service start failed with exit code: $LASTEXITCODE"
        }
        Start-Sleep -Seconds 5
        Write-DiagnosticOutput "Service start command completed" "Success"
        
        # Step 5: Verify service is actually running
        $service = Get-Service -Name "CimianWatcher" -ErrorAction Stop
        if ($service.Status -eq "Running") {
            Write-DiagnosticOutput "✅ CimianWatcher service is now running successfully!" "Success"
            return $true
        } else {
            Write-DiagnosticOutput "Service was started but is not running (Status: $($service.Status))" "Error"
            return $false
        }
        
    } catch {
        Write-DiagnosticOutput "Repair failed: $_" "Error"
        return $false
    }
}

# Main execution
Write-DiagnosticOutput "=== CimianWatcher Service Diagnostics ===" "Info"
Write-DiagnosticOutput "Installation path: $InstallPath" "Info"

if ($Fix) {
    Write-DiagnosticOutput "Fix mode enabled - will attempt to repair issues" "Info"
}

# Run diagnostics
$diagnostics = Get-ServiceDiagnostics -InstallPath $InstallPath

# Summary
Write-DiagnosticOutput "`n=== Diagnostic Summary ===" "Info"
Write-DiagnosticOutput "Executable exists: $($diagnostics.ExecutableExists)" $(if ($diagnostics.ExecutableExists) { "Success" } else { "Error" })
Write-DiagnosticOutput "Executable working: $($diagnostics.ExecutableWorking)" $(if ($diagnostics.ExecutableWorking) { "Success" } else { "Error" })
Write-DiagnosticOutput "Service exists: $($diagnostics.ServiceExists)" $(if ($diagnostics.ServiceExists) { "Success" } else { "Error" })
Write-DiagnosticOutput "Service running: $($diagnostics.ServiceRunning)" $(if ($diagnostics.ServiceRunning) { "Success" } else { "Error" })

if ($diagnostics.EventLogErrors.Count -gt 0) {
    Write-DiagnosticOutput "Recent error events: $($diagnostics.EventLogErrors.Count)" "Error"
}

# Determine if repair is needed
$needsRepair = -not ($diagnostics.ExecutableExists -and $diagnostics.ExecutableWorking -and $diagnostics.ServiceExists -and $diagnostics.ServiceRunning)

if ($needsRepair) {
    Write-DiagnosticOutput "`nService issues detected." "Warning"
    
    if ($Fix) {
        $repairSuccess = Repair-CimianWatcherService -InstallPath $InstallPath -Diagnostics $diagnostics
        
        if ($repairSuccess) {
            Write-DiagnosticOutput "`n🎉 CimianWatcher service repair completed successfully!" "Success"
            exit 0
        } else {
            Write-DiagnosticOutput "`n❌ CimianWatcher service repair failed!" "Error"
            exit 1
        }
    } else {
        Write-DiagnosticOutput "Run with -Fix parameter to attempt automatic repair." "Info"
        exit 1
    }
} else {
    Write-DiagnosticOutput "`n✅ CimianWatcher service is working properly!" "Success"
    exit 0
}
