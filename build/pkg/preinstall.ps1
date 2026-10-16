# CimianTools preinstall
#
# cimipkg runs this as the immediate CimianPreinstall custom action on every
# install, before InstallValidate and before any payload file is laid down; it
# does not run on uninstall (handled by uninstall.ps1). A non-zero exit fails
# the install, so the free-space guard below stops an install that would
# otherwise die part-way through with msiexec 112. If a human runs this script
# outside an MSI session, $env:CIMIAN_PHASE is empty and everything runs.
$ErrorActionPreference = 'Stop'

Write-Host "CimianTools preinstall: phase=$($env:CIMIAN_PHASE) version=$($env:CIMIAN_VERSION)" -ForegroundColor Green

# Refuse to install with less than 1.5 GB free on the system drive. The payload
# alone is a few hundred MB, and a drive that fills mid-install leaves a
# half-copied tree and a rollback that can itself fail. A probe error is not a
# reason to block, so only a successful reading below the floor stops us.
$minFreeMB = 1500
$systemDrive = if ($env:SystemDrive) { $env:SystemDrive } else { 'C:' }
try {
    $drive = [System.IO.DriveInfo]::new($systemDrive)
    if (-not $drive.IsReady) { throw "drive is not ready" }
    $freeMB = [math]::Floor($drive.AvailableFreeSpace / 1MB)
    Write-Host "Free space on ${systemDrive}: $freeMB MB (minimum $minFreeMB MB)"
    if ($freeMB -lt $minFreeMB) {
        Write-Host "Insufficient disk space on ${systemDrive}: $freeMB MB free. CimianTools requires at least $minFreeMB MB free. Free some space and run the installer again."
        exit 112
    }
} catch {
    Write-Warning "Could not read free space on ${systemDrive}, continuing: $_"
}

if ($env:CIMIAN_PHASE -eq 'fresh') {
    Write-Host "Fresh install detected — nothing to tear down, exiting."
    exit 0
}

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
    $legacyService = Get-Service -Name "Cimian Bootstrap File Watcher" -ErrorAction SilentlyContinue
    if ($legacyService -and $legacyService.Status -eq "Running") {
        Write-Host "Stopping legacy bootstrap service..."
        try {
            Stop-Service -Name "Cimian Bootstrap File Watcher" -Force -ErrorAction Stop
            Start-Sleep -Seconds 3
        } catch {
            Write-Warning "Failed to stop legacy bootstrap service: $_"
        }
    }

    # Stop ALL Cimian processes before file operations
    Write-Host "Stopping ALL Cimian processes for safe file operations..."
    try {
        $cimianProcesses = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil", "repoclean" -ErrorAction SilentlyContinue
        if ($cimianProcesses) {
            Write-Host "Found $($cimianProcesses.Count) Cimian process(es) to terminate:"
            foreach ($proc in $cimianProcesses) {
                Write-Host "  - $($proc.Name) (PID: $($proc.Id))"
            }
            $cimianProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 5

            $remainingProcesses = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil", "repoclean" -ErrorAction SilentlyContinue
            if ($remainingProcesses) {
                $cimianExes = @("cimiwatcher.exe", "managedsoftwareupdate.exe", "cimitrigger.exe", "cimistatus.exe",
                               "cimiimport.exe", "cimipkg.exe", "makecatalogs.exe", "makepkginfo.exe", "manifestutil.exe", "repoclean.exe",
                               "Managed Software Center.exe")
                foreach ($exeName in $cimianExes) {
                    try { & taskkill /F /IM $exeName /T 2>$null } catch { }
                }
                Start-Sleep -Seconds 3
            }
        } else {
            Write-Host "No Cimian processes found running"
        }
    } catch {
        Write-Warning "Error during Cimian process termination: $_"
    }

    # Clean up any existing backup folders from previous installations/upgrades
    $InstallDir = "C:\Program Files\Cimian"
    if (Test-Path $InstallDir) {
        try {
            $backupFolders = Get-ChildItem -Path $InstallDir -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match "^backup-\d{8}-\d{6}$" }
            foreach ($backupFolder in $backupFolders) {
                try {
                    Remove-Item -Path $backupFolder.FullName -Recurse -Force -ErrorAction Stop
                    Write-Host "Removed backup folder: $($backupFolder.Name)"
                } catch {
                    Write-Warning "Failed to remove $($backupFolder.Name): $_"
                }
            }
        } catch {
            Write-Warning "Error during backup folder cleanup: $_"
        }
    }

    Start-Sleep -Seconds 3
    Write-Host "CimianTools preinstall completed" -ForegroundColor Green
}
catch {
    Write-Warning "preinstall encountered an error: $_"
    # Don't fail the entire installation for preinstall issues — files can often
    # still be replaced even if process termination was partial.
    exit 0
}

exit 0
