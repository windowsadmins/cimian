# MSI Service Management Script for CimianWatcher
# This script provides robust service installation, removal, and management for MSI installations

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("install", "remove", "start", "stop")]
    [string]$Action,
    
    [string]$ServiceExePath = "",
    [string]$InstallPath = "C:\Program Files\Cimian"
)

$ErrorActionPreference = 'Stop'

function Write-ServiceLog {
    param([string]$Message, [string]$Level = "INFO")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host "[$timestamp] [$Level] $Message"
}

function Remove-ExistingService {
    param([string]$ServiceName)
    
    Write-ServiceLog "Attempting to remove existing service: $ServiceName"
    $serviceRemoved = $false
    
    # First, try to stop the service if it's running
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq "Running") {
            Write-ServiceLog "Stopping service $ServiceName..."
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            Start-Sleep -Seconds 3
            Write-ServiceLog "Service stopped successfully"
        }
    } catch {
        Write-ServiceLog "Failed to stop service gracefully: $_" "WARNING"
        # Force kill any processes
        try {
            Get-Process -Name "cimiwatcher" -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Seconds 2
            Write-ServiceLog "Force killed cimiwatcher processes"
        } catch {
            Write-ServiceLog "No cimiwatcher processes found to kill"
        }
    }
    
    # Method 1: Try using cimiwatcher.exe remove command if available
    if ($ServiceExePath -and (Test-Path $ServiceExePath)) {
        try {
            Write-ServiceLog "Attempting service removal using $ServiceExePath remove..."
            & $ServiceExePath remove
            Start-Sleep -Seconds 3
            
            # Check if service still exists
            $checkService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if (-not $checkService) {
                $serviceRemoved = $true
                Write-ServiceLog "Service removed successfully using executable"
            }
        } catch {
            Write-ServiceLog "Failed to remove service using executable: $_" "WARNING"
        }
    }
    
    # Method 2: Try using sc.exe delete
    if (-not $serviceRemoved) {
        try {
            Write-ServiceLog "Attempting service removal using sc.exe delete..."
            & sc.exe delete $ServiceName
            Start-Sleep -Seconds 3
            
            # Check if service still exists
            $checkService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if (-not $checkService) {
                $serviceRemoved = $true
                Write-ServiceLog "Service removed successfully using sc.exe"
            }
        } catch {
            Write-ServiceLog "Failed to remove service using sc.exe: $_" "WARNING"
        }
    }
    
    # Method 3: Try WMI deletion as last resort
    if (-not $serviceRemoved) {
        try {
            Write-ServiceLog "Attempting service removal using WMI..."
            $wmiService = Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
            if ($wmiService) {
                $wmiService.Delete()
                Start-Sleep -Seconds 3
                
                # Check if service still exists
                $checkService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
                if (-not $checkService) {
                    $serviceRemoved = $true
                    Write-ServiceLog "Service removed successfully using WMI"
                }
            } else {
                Write-ServiceLog "Service not found in WMI, may already be removed"
                $serviceRemoved = $true
            }
        } catch {
            Write-ServiceLog "Failed to remove service using WMI: $_" "WARNING"
        }
    }
    
    # Method 4: Registry cleanup as last resort
    if (-not $serviceRemoved) {
        try {
            Write-ServiceLog "Attempting registry cleanup as last resort..."
            $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
            if (Test-Path $regPath) {
                Remove-Item -Path $regPath -Recurse -Force -ErrorAction Stop
                Start-Sleep -Seconds 2
                Write-ServiceLog "Registry cleanup completed"
                $serviceRemoved = $true
            } else {
                Write-ServiceLog "Service registry key not found, may already be removed"
                $serviceRemoved = $true
            }
        } catch {
            Write-ServiceLog "Registry cleanup failed: $_" "ERROR"
        }
    }
    
    return $serviceRemoved
}

function Install-CimianWatcherService {
    param([string]$ExePath)
    
    Write-ServiceLog "Installing CimianWatcher service using: $ExePath"
    
    if (-not (Test-Path $ExePath)) {
        throw "Service executable not found: $ExePath"
    }
    
    # Install the service
    & $ExePath install
    if ($LASTEXITCODE -ne 0) {
        throw "Service installation failed with exit code: $LASTEXITCODE"
    }
    
    Start-Sleep -Seconds 3
    Write-ServiceLog "Service installation completed successfully"
}

function Start-CimianWatcherService {
    param([string]$ExePath)
    
    Write-ServiceLog "Starting CimianWatcher service"
    
    # Start the service
    & $ExePath start
    if ($LASTEXITCODE -ne 0) {
        throw "Service start failed with exit code: $LASTEXITCODE"
    }
    
    Start-Sleep -Seconds 5
    
    # Verify service is running
    $service = Get-Service -Name "CimianWatcher" -ErrorAction Stop
    if ($service.Status -ne "Running") {
        throw "Service was started but is not running - status: $($service.Status)"
    }
    
    Write-ServiceLog "Service started successfully and is running"
}

# Main execution
try {
    $cimiwatcherExe = if ($ServiceExePath) { $ServiceExePath } else { Join-Path $InstallPath "cimiwatcher.exe" }
    
    switch ($Action) {
        "remove" {
            $removed = Remove-ExistingService -ServiceName "CimianWatcher"
            if ($removed) {
                Write-ServiceLog "Service removal completed successfully"
                exit 0
            } else {
                Write-ServiceLog "Service removal failed" "ERROR"
                exit 1
            }
        }
        
        "install" {
            # First remove any existing service
            $existing = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
            if ($existing) {
                Write-ServiceLog "Existing service found, removing first..."
                $removed = Remove-ExistingService -ServiceName "CimianWatcher"
                if (-not $removed) {
                    Write-ServiceLog "Failed to remove existing service" "WARNING"
                }
            }
            
            Install-CimianWatcherService -ExePath $cimiwatcherExe
            Write-ServiceLog "Service installation completed successfully"
            exit 0
        }
        
        "start" {
            Start-CimianWatcherService -ExePath $cimiwatcherExe
            Write-ServiceLog "Service start completed successfully"
            exit 0
        }
        
        "stop" {
            Write-ServiceLog "Stopping CimianWatcher service"
            try {
                Stop-Service -Name "CimianWatcher" -Force -ErrorAction Stop
                Write-ServiceLog "Service stopped successfully"
                exit 0
            } catch {
                Write-ServiceLog "Failed to stop service: $_" "ERROR"
                exit 1
            }
        }
    }
    
} catch {
    Write-ServiceLog "Service management failed: $_" "ERROR"
    exit 1
}
