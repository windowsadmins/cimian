# Cimian Installation Verification Script
# This script verifies that CimianWatcher service and scheduled tasks are working properly

param(
    [string]$InstallPath = "C:\Program Files\Cimian"
)

$ErrorActionPreference = 'Stop'

Write-Host "Verifying Cimian installation..." -ForegroundColor Green
Write-Host "Installation path: $InstallPath" -ForegroundColor Green

$allChecksPass = $true

# Check 1: Verify all executables exist
Write-Host "`n=== Checking Executables ===" -ForegroundColor Yellow
$requiredExes = @(
    "cimiimport.exe",
    "cimipkg.exe", 
    "cimistatus.exe",
    "cimitrigger.exe",
    "cimiwatcher.exe",
    "makecatalogs.exe",
    "makepkginfo.exe",
    "managedsoftwareupdate.exe",
    "manifestutil.exe"
)

foreach ($exe in $requiredExes) {
    $exePath = Join-Path $InstallPath $exe
    if (Test-Path $exePath) {
        Write-Host "✅ $exe found" -ForegroundColor Green
    } else {
        Write-Host "❌ $exe missing" -ForegroundColor Red
        $allChecksPass = $false
    }
}

# Check 2: Verify CimianWatcher service
Write-Host "`n=== Checking CimianWatcher Service ===" -ForegroundColor Yellow
try {
    $service = Get-Service -Name "CimianWatcher" -ErrorAction Stop
    if ($service.Status -eq "Running") {
        Write-Host "✅ CimianWatcher service is running" -ForegroundColor Green
        
        # Try to get service details
        $serviceDetails = Get-WmiObject -Class Win32_Service -Filter "Name='CimianWatcher'" -ErrorAction SilentlyContinue
        if ($serviceDetails) {
            Write-Host "   Display Name: $($serviceDetails.DisplayName)" -ForegroundColor Cyan
            Write-Host "   Start Mode: $($serviceDetails.StartMode)" -ForegroundColor Cyan
            Write-Host "   Process ID: $($serviceDetails.ProcessId)" -ForegroundColor Cyan
            Write-Host "   Executable: $($serviceDetails.PathName)" -ForegroundColor Cyan
        }
    } else {
        Write-Host "❌ CimianWatcher service exists but is not running (Status: $($service.Status))" -ForegroundColor Red
        $allChecksPass = $false
        
        # Try to start the service and diagnose
        Write-Host "   Attempting to start service..." -ForegroundColor Yellow
        try {
            Start-Service -Name "CimianWatcher" -ErrorAction Stop
            Start-Sleep -Seconds 3
            $service = Get-Service -Name "CimianWatcher" -ErrorAction Stop
            if ($service.Status -eq "Running") {
                Write-Host "   ✅ Service started successfully" -ForegroundColor Green
            } else {
                Write-Host "   ❌ Service failed to start (Status: $($service.Status))" -ForegroundColor Red
            }
        } catch {
            Write-Host "   ❌ Failed to start service: $_" -ForegroundColor Red
        }
    }
} catch {
    Write-Host "❌ CimianWatcher service not found or inaccessible: $_" -ForegroundColor Red
    $allChecksPass = $false
}

# Check 3: Verify scheduled task
Write-Host "`n=== Checking Scheduled Task ===" -ForegroundColor Yellow
try {
    $task = Get-ScheduledTask -TaskName "Cimian Managed Software Update Hourly" -ErrorAction Stop
    if ($task.State -eq "Ready") {
        Write-Host "✅ Cimian scheduled task exists and is ready" -ForegroundColor Green
        Write-Host "   Last Run Time: $($task.LastRunTime)" -ForegroundColor Cyan
        Write-Host "   Next Run Time: $($task.NextRunTime)" -ForegroundColor Cyan
        Write-Host "   State: $($task.State)" -ForegroundColor Cyan
        
        # Check task action
        $taskInfo = Get-ScheduledTaskInfo -TaskName "Cimian Managed Software Update Hourly" -ErrorAction SilentlyContinue
        if ($taskInfo) {
            Write-Host "   Last Result: $($taskInfo.LastTaskResult)" -ForegroundColor Cyan
        }
    } else {
        Write-Host "❌ Cimian scheduled task exists but is not ready (State: $($task.State))" -ForegroundColor Red
        $allChecksPass = $false
    }
} catch {
    Write-Host "❌ Cimian scheduled task not found: $_" -ForegroundColor Red
    $allChecksPass = $false
}

# Check 4: Verify PATH entry
Write-Host "`n=== Checking PATH Environment Variable ===" -ForegroundColor Yellow
$systemPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
if ($systemPath -split ';' -contains $InstallPath) {
    Write-Host "✅ Cimian is in system PATH" -ForegroundColor Green
} else {
    Write-Host "❌ Cimian is not in system PATH" -ForegroundColor Red
    $allChecksPass = $false
}

# Check 5: Verify registry entries
Write-Host "`n=== Checking Registry Entries ===" -ForegroundColor Yellow
try {
    $regPath = "HKLM:\SOFTWARE\Cimian"
    if (Test-Path $regPath) {
        $version = Get-ItemProperty -Path $regPath -Name "Version" -ErrorAction SilentlyContinue
        $installPath = Get-ItemProperty -Path $regPath -Name "InstallPath" -ErrorAction SilentlyContinue
        
        if ($version) {
            Write-Host "✅ Version in registry: $($version.Version)" -ForegroundColor Green
        } else {
            Write-Host "❌ Version not found in registry" -ForegroundColor Red
            $allChecksPass = $false
        }
        
        if ($installPath) {
            Write-Host "✅ Install path in registry: $($installPath.InstallPath)" -ForegroundColor Green
        } else {
            Write-Host "❌ Install path not found in registry" -ForegroundColor Red
            $allChecksPass = $false
        }
    } else {
        Write-Host "❌ Cimian registry key not found" -ForegroundColor Red
        $allChecksPass = $false
    }
} catch {
    Write-Host "❌ Failed to check registry: $_" -ForegroundColor Red
    $allChecksPass = $false
}

# Check 6: Test executable functionality
Write-Host "`n=== Testing Executable Functionality ===" -ForegroundColor Yellow
$managedSoftwareUpdateExe = Join-Path $InstallPath "managedsoftwareupdate.exe"
if (Test-Path $managedSoftwareUpdateExe) {
    try {
        $versionOutput = & $managedSoftwareUpdateExe --version 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "✅ managedsoftwareupdate.exe --version works" -ForegroundColor Green
            Write-Host "   Output: $versionOutput" -ForegroundColor Cyan
        } else {
            Write-Host "❌ managedsoftwareupdate.exe --version failed (exit code: $LASTEXITCODE)" -ForegroundColor Red
            Write-Host "   Output: $versionOutput" -ForegroundColor Red
            $allChecksPass = $false
        }
    } catch {
        Write-Host "❌ Failed to test managedsoftwareupdate.exe: $_" -ForegroundColor Red
        $allChecksPass = $false
    }
}

# Final result
Write-Host "`n=== Verification Summary ===" -ForegroundColor Yellow
if ($allChecksPass) {
    Write-Host "✅ All checks passed! Cimian installation is working properly." -ForegroundColor Green
    exit 0
} else {
    Write-Host "❌ Some checks failed. Installation may not be working properly." -ForegroundColor Red
    Write-Host "Please review the failed checks above and fix any issues." -ForegroundColor Red
    exit 1
}
