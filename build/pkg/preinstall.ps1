# CimianTools preinstall
#
# Fires only on upgrade/reinstall — cimipkg conditions this CA with
# `PREVIOUSVERSIONSINSTALLED OR (Installed AND NOT (REMOVE="ALL"))`, so it does
# NOT run on fresh install (no prior version to tear down) nor on uninstall
# (handled by uninstall.ps1). The phase guard below is defensive: if a human
# runs this script outside an MSI session, $env:CIMIAN_PHASE will be empty
# and we treat that as "force run" so dev work isn't gated on the env var.
$ErrorActionPreference = 'Stop'

Write-Host "CimianTools preinstall: phase=$($env:CIMIAN_PHASE) version=$($env:CIMIAN_VERSION)" -ForegroundColor Green

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

    # Wait for any in-progress managedsoftwareupdate run to finish naturally.
    # Killing MSU mid-install could leave packages in an inconsistent state, so
    # we never force it down. If it outlasts the timeout the MSI's standard
    # in-use file handling (Restart Manager) will queue replacement on reboot.
    $msuTimeout = New-TimeSpan -Minutes 30
    $msuDeadline = (Get-Date).Add($msuTimeout)
    $msuLastLog = Get-Date
    $msu = Get-Process -Name "managedsoftwareupdate" -ErrorAction SilentlyContinue
    if ($msu) {
        Write-Host "managedsoftwareupdate is running (PID: $($msu.Id)); waiting up to $($msuTimeout.TotalMinutes) min for it to finish..."
        while ($msu -and (Get-Date) -lt $msuDeadline) {
            if (((Get-Date) - $msuLastLog).TotalSeconds -ge 30) {
                Write-Host "  still waiting for managedsoftwareupdate (PID: $($msu.Id))"
                $msuLastLog = Get-Date
            }
            Start-Sleep -Seconds 5
            $msu = Get-Process -Name "managedsoftwareupdate" -ErrorAction SilentlyContinue
        }
        if ($msu) {
            Write-Warning "managedsoftwareupdate still running after $($msuTimeout.TotalMinutes) min; proceeding without killing it (MSI will queue file replacement if needed)"
        } else {
            Write-Host "managedsoftwareupdate finished — proceeding"
        }
    }

    # Aggressively terminate ancillary Cimian processes. Excludes
    # managedsoftwareupdate (handled above) — everything else is idempotent
    # and safe to hard-kill so the MSI can replace files cleanly.
    Write-Host "Stopping ancillary Cimian processes for safe file operations..."
    try {
        $cimianProcesses = Get-Process -Name "*cimi*", "makecatalogs", "makepkginfo", "manifestutil", "repoclean" -ErrorAction SilentlyContinue
        if ($cimianProcesses) {
            Write-Host "Found $($cimianProcesses.Count) ancillary process(es) to terminate:"
            foreach ($proc in $cimianProcesses) {
                Write-Host "  - $($proc.Name) (PID: $($proc.Id))"
            }
            $cimianProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3

            $cimianExes = @("cimiwatcher.exe", "cimitrigger.exe", "cimistatus.exe",
                           "cimiimport.exe", "cimipkg.exe", "makecatalogs.exe", "makepkginfo.exe",
                           "manifestutil.exe", "repoclean.exe", "Managed Software Center.exe")
            foreach ($exeName in $cimianExes) {
                try { & taskkill /F /IM $exeName /T 2>$null } catch { }
            }
            Start-Sleep -Seconds 2
        } else {
            Write-Host "No ancillary Cimian processes found running"
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
