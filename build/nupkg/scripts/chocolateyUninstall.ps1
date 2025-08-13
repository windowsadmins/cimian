# CimianTools Chocolatey Uninstallation Script
# Simplified version to fix syntax issues

$ErrorActionPreference = 'Continue'

$packageName = 'CimianTools'
$InstallDir = Join-Path $env:ProgramFiles "Cimian"
$ServiceName = "CimianWatcher"

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logEntry = "[$timestamp] [$Level] $Message"
    Write-Host $logEntry -ForegroundColor $(switch($Level) {
        "ERROR" { "Red" }; "WARNING" { "Yellow" }; "SUCCESS" { "Green" }; default { "White" }
    })
}

function Remove-CimianService {
    Write-Log "Removing CimianWatcher service..." "INFO"
    
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        
        if ($service) {
            if ($service.Status -eq "Running") {
                Write-Log "Stopping service $ServiceName..." "INFO"
                Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
            }
            
            $serviceExe = Join-Path $InstallDir "cimiwatcher.exe"
            if (Test-Path $serviceExe) {
                Write-Log "Removing service $ServiceName..." "INFO"
                & sc.exe delete $ServiceName
            }
            Write-Log "Service $ServiceName removed successfully" "SUCCESS"
        } else {
            Write-Log "Service $ServiceName not found" "INFO"
        }
    }
    catch {
        Write-Log "Failed to remove service: $($_.Exception.Message)" "WARNING"
    }
}

function Stop-CimianProcesses {
    Write-Log "Stopping Cimian processes..." "INFO"
    
    $processNames = @("cimiwatcher", "cimistatus", "managedsoftwareupdate")
    
    foreach ($processName in $processNames) {
        try {
            $processes = Get-Process -Name $processName -ErrorAction SilentlyContinue
            if ($processes) {
                Write-Log "Stopping process: $processName" "INFO"
                $processes | Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 1
            }
        }
        catch {
            Write-Log "Warning during process cleanup: $($_.Exception.Message)" "WARNING"
        }
    }
}

function Remove-FromPath {
    Write-Log "Removing Cimian from system PATH..." "INFO"
    
    try {
        $currentPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
        
        if ($currentPath) {
            $pathEntries = $currentPath -split ';' | Where-Object { $_ -ne $InstallDir -and $_ -ne "" }
            $newPath = $pathEntries -join ';'
            
            if ($newPath -ne $currentPath) {
                [Environment]::SetEnvironmentVariable("PATH", $newPath, [EnvironmentVariableTarget]::Machine)
                Write-Log "Successfully removed Cimian from system PATH" "SUCCESS"
            } else {
                Write-Log "Cimian was not found in system PATH" "INFO"
            }
        }
    }
    catch {
        Write-Log "Failed to update system PATH: $($_.Exception.Message)" "WARNING"
    }
}

function Remove-InstallationFiles {
    Write-Log "Removing installation files..." "INFO"
    Write-Log "InstallDir value: '$InstallDir'" "INFO"
    
    try {
        if ([string]::IsNullOrEmpty($InstallDir)) {
            Write-Log "InstallDir is null or empty, skipping file removal" "WARNING"
            return
        }
        
        if (Test-Path $InstallDir) {
            Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction Continue
            Write-Log "Removed installation directory: $InstallDir" "SUCCESS"
        } else {
            Write-Log "Installation directory not found: $InstallDir" "INFO"
        }
    }
    catch {
        Write-Log "Failed to remove installation files: $($_.Exception.Message)" "WARNING"
    }
}

function Remove-RegistryKeys {
    Write-Log "Removing Cimian registry keys..." "INFO"
    
    try {
        $registryPath = "HKLM:\SOFTWARE\Cimian"
        
        if (Test-Path $registryPath) {
            Remove-Item -Path $registryPath -Recurse -Force -ErrorAction Stop
            Write-Log "Successfully removed registry key: $registryPath" "SUCCESS"
        } else {
            Write-Log "Registry key not found: $registryPath" "INFO"
        }
    }
    catch {
        Write-Log "Failed to remove registry keys: $($_.Exception.Message)" "WARNING"
    }
}

function Uninstall-CimianTools {
    Write-Log "Starting CimianTools uninstallation..." "INFO"
    
    try {
        # Step 1: Stop Cimian processes
        Stop-CimianProcesses
        
        # Step 2: Remove service
        Remove-CimianService
        
        # Step 3: Remove from PATH
        Remove-FromPath
        
        # Step 4: Remove installation files
        Remove-InstallationFiles
        
        # Step 5: Remove registry keys
        Remove-RegistryKeys
        
        Write-Log "CimianTools uninstallation completed successfully" "SUCCESS"
        Write-Host ""
        Write-Host "   ============================================" -ForegroundColor Green
        Write-Host "   CimianTools Uninstallation Summary" -ForegroundColor Green
        Write-Host "   ============================================" -ForegroundColor Green
        Write-Host "   Service Removed:         $ServiceName" -ForegroundColor Green
        Write-Host "   Processes Stopped:       cimiwatcher, cimistatus, managedsoftwareupdate" -ForegroundColor Green
        Write-Host "   PATH Updated:            Cimian removed from system PATH" -ForegroundColor Green
        Write-Host "   Files Removed:           $InstallDir" -ForegroundColor Green
        Write-Host "   Registry Cleaned:        HKLM:\SOFTWARE\Cimian" -ForegroundColor Green
        Write-Host "   ============================================" -ForegroundColor Green
        Write-Host ""
    }
    catch {
        Write-Log "Uninstallation failed: $($_.Exception.Message)" "ERROR"
        Write-Host "Uninstallation encountered errors. Some cleanup may be incomplete." -ForegroundColor Red
    }
}

# Execute uninstallation
try {
    Write-Log "CimianTools uninstallation starting..." "INFO"
    Uninstall-CimianTools
    Write-Log "Uninstallation script completed" "SUCCESS"
}
catch {
    Write-Log "Uninstallation script failed: $($_.Exception.Message)" "ERROR"
}
