# CimianTools .pkg Preinstall Script
# This script runs BEFORE file operations to ensure no processes are blocking file access
$ErrorActionPreference = 'Stop'

Write-Host "CimianTools .pkg Preinstall: Preparing for file operations..." -ForegroundColor Green

try {
    # Stop CimianWatcher service if it's running (needed for upgrades)
    Write-Host "Checking for existing CimianWatcher service..."
    $existingService = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
    
    if ($existingService) {
        Write-Host "Found existing CimianWatcher service with status: $($existingService.Status)"
        if ($existingService.Status -eq "Running") {
            Write-Host "Stopping CimianWatcher service for upgrade..."
            try {
                Stop-Service -Name "CimianWatcher" -Force -ErrorAction Stop
                Start-Sleep -Seconds 3
                Write-Host "CimianWatcher service stopped successfully"
            } catch {
                Write-Warning "Failed to stop CimianWatcher service: $_"
                Write-Host "Attempting to forcefully terminate cimiwatcher.exe processes..."
                try {
                    Get-Process -Name "cimiwatcher" -ErrorAction SilentlyContinue | Stop-Process -Force
                    Start-Sleep -Seconds 2
                    Write-Host "Forcefully terminated cimiwatcher.exe processes"
                } catch {
                    Write-Warning "Failed to terminate cimiwatcher.exe processes: $_"
                }
            }
        }
    }
    
    # Check for legacy "Cimian Bootstrap File Watcher" service
    Write-Host "Checking for legacy bootstrap file watcher service..."
    $legacyService = Get-Service -Name "Cimian Bootstrap File Watcher" -ErrorAction SilentlyContinue
    if ($legacyService) {
        Write-Host "Found legacy service with status: $($legacyService.Status)"
        if ($legacyService.Status -eq "Running") {
            Write-Host "Stopping legacy bootstrap service..."
            try {
                Stop-Service -Name "Cimian Bootstrap File Watcher" -Force -ErrorAction Stop
                Start-Sleep -Seconds 3
                Write-Host "Legacy bootstrap service stopped successfully"
            } catch {
                Write-Warning "Failed to stop legacy bootstrap service: $_"
            }
        }
    }
    
    # Stop ALL Cimian processes before file operations (comprehensive approach)
    Write-Host "Stopping ALL Cimian processes for safe file operations..."
    try {
        # Get all Cimian processes using wildcard pattern
        $cimianProcesses = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil", "repoclean" -ErrorAction SilentlyContinue
        
        if ($cimianProcesses) {
            Write-Host "Found $($cimianProcesses.Count) Cimian process(es) to terminate:"
            foreach ($proc in $cimianProcesses) {
                Write-Host "  - $($proc.Name) (PID: $($proc.Id))"
            }
            
            # First attempt: graceful termination
            Write-Host "Attempting graceful termination of all Cimian processes..."
            $cimianProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 5
            
            # Verify all processes are terminated
            $remainingProcesses = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil", "repoclean" -ErrorAction SilentlyContinue
            if ($remainingProcesses) {
                Write-Host "Some Cimian processes still running, using taskkill for aggressive termination..."
                
                # Use taskkill for each known Cimian executable
                $cimianExes = @("cimiwatcher.exe", "managedsoftwareupdate.exe", "cimitrigger.exe", "cimistatus.exe", 
                               "cimiimport.exe", "cimipkg.exe", "makecatalogs.exe", "makepkginfo.exe", "manifestutil.exe", "repoclean.exe")
                
                foreach ($exeName in $cimianExes) {
                    try {
                        & taskkill /F /IM $exeName /T 2>$null
                    } catch {
                        # Ignore errors for processes that aren't running
                    }
                }
                
                Start-Sleep -Seconds 3
                
                # Final verification
                $finalCheck = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil", "repoclean" -ErrorAction SilentlyContinue
                if ($finalCheck) {
                    Write-Warning "Some Cimian processes may still be running after aggressive termination:"
                    foreach ($proc in $finalCheck) {
                        Write-Warning "  - $($proc.Name) (PID: $($proc.Id))"
                    }
                    Write-Warning "File operations may still fail due to locked files"
                } else {
                    Write-Host "All Cimian processes successfully terminated"
                }
            } else {
                Write-Host "All Cimian processes successfully terminated"
            }
        } else {
            Write-Host "No Cimian processes found running"
        }
    } catch {
        Write-Warning "Error during comprehensive Cimian process termination: $_"
        Write-Warning "File operations may fail due to locked files"
    }
    
    # Clean up any existing backup folders from previous installations/upgrades
    Write-Host "Cleaning up old backup folders..."
    $InstallDir = "C:\Program Files\Cimian"
    try {
        if (Test-Path $InstallDir) {
            $backupFolders = Get-ChildItem -Path $InstallDir -Directory | Where-Object { $_.Name -match "^backup-\d{8}-\d{6}$" }
            if ($backupFolders) {
                Write-Host "Found $($backupFolders.Count) backup folder(s) to remove:"
                foreach ($backupFolder in $backupFolders) {
                    Write-Host "  Removing: $($backupFolder.Name)"
                    try {
                        Remove-Item -Path $backupFolder.FullName -Recurse -Force -ErrorAction Stop
                        Write-Host "    ✅ Successfully removed $($backupFolder.Name)"
                    } catch {
                        Write-Warning "    ❌ Failed to remove $($backupFolder.Name): $_"
                    }
                }
            } else {
                Write-Host "No backup folders found to clean up"
            }
        }
    } catch {
        Write-Warning "Error during backup folder cleanup: $_"
        Write-Warning "Continuing with installation..."
    }
    
    # Wait a moment to ensure all file handles are released
    Write-Host "Waiting for file handles to be released..."
    Start-Sleep -Seconds 3

    Write-Host "CimianTools .pkg preinstall completed successfully!" -ForegroundColor Green
    Write-Host "File operations should now proceed safely." -ForegroundColor Green
}
catch {
    Write-Warning ".pkg preinstall encountered an error: $_"
    Write-Warning "Continuing with installation, but file operations may fail"
    exit 0  # Don't fail the entire installation for preinstall issues
}

exit 0