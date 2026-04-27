# CimianTools uninstall
#
# Fires only on standalone uninstall — cimipkg conditions this CA with
# `(REMOVE="ALL") AND NOT UPGRADINGPRODUCTCODE`, so it does NOT run during
# the previous-version removal pass of a major upgrade (postinstall.ps1 will
# re-create whatever this tears down; running it would waste work and risk
# racing with the new product's install).
$ErrorActionPreference = 'Continue'

$InstallDir = "C:\Program Files\Cimian"
Write-Host "CimianTools uninstall: phase=$($env:CIMIAN_PHASE) version=$($env:CIMIAN_VERSION)" -ForegroundColor Yellow

# 0. Unregister the CimianStatusProvider PLAP first, while the DLL still exists.
# We have to do this BEFORE RemoveFiles removes CimianStatusProvider.dll,
# otherwise regsvr32 /u has nothing to call DllUnregisterServer on and the
# HKLM\...\PLAP Providers\{GUID} entry would be orphaned, leaving a dead
# CLSID reference that LogonUI keeps trying to load.
try {
    $plapDll = Join-Path $InstallDir 'CimianStatusProvider.dll'
    $plapGuid = '{C1819A88-7E61-4C0E-9D77-3F0E4B3C1A55}'
    $plapKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\PLAP Providers\$plapGuid"
    $clsidKey = "HKLM:\SOFTWARE\Classes\CLSID\$plapGuid"

    if (Test-Path $plapDll) {
        $regsvr = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
        & $regsvr /s /u "`"$plapDll`""
    }
    # Belt-and-suspenders cleanup in case the DLL was already gone or
    # DllUnregisterServer didn't run. RegDeleteKey is harmless if the key
    # doesn't exist.
    if (Test-Path $plapKey)  { Remove-Item -Path $plapKey  -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $clsidKey) { Remove-Item -Path $clsidKey -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "Unregistered CimianStatusProvider PLAP"
} catch {
    Write-Warning "Failed to unregister CimianStatusProvider PLAP: $_"
}

# 1. Remove scheduled task. Harmless if it's already gone.
try {
    $task = Get-ScheduledTask -TaskName "Cimian Managed Software Update Hourly" -ErrorAction SilentlyContinue
    if ($task) {
        Unregister-ScheduledTask -TaskName "Cimian Managed Software Update Hourly" -Confirm:$false -ErrorAction Stop
        Write-Host "Removed scheduled task"
    }
} catch {
    Write-Warning "Failed to remove scheduled task: $_"
}

# 2. Stop and remove CimianWatcher service. The service binary is about to be
# deleted by RemoveFiles; uninstalling the service first prevents SCM holding
# a handle that blocks the file delete.
try {
    $service = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -eq "Running") {
            try { Stop-Service -Name "CimianWatcher" -Force -ErrorAction Stop; Start-Sleep -Seconds 3 }
            catch { Write-Warning "Stop-Service failed: $_" }
        }
        $cimiwatcherExe = Join-Path $InstallDir "cimiwatcher.exe"
        if (Test-Path $cimiwatcherExe) {
            try { & $cimiwatcherExe uninstall; Start-Sleep -Seconds 2 }
            catch { Write-Warning "cimiwatcher uninstall failed: $_" }
        } else {
            # Fall back to sc.exe if the exe is already gone.
            try { & sc.exe delete CimianWatcher | Out-Null } catch { }
        }
        Write-Host "Removed CimianWatcher service"
    }
} catch {
    Write-Warning "Failed to remove CimianWatcher service: $_"
}

# 3. Terminate any stray Cimian processes so RemoveFiles isn't blocked.
try {
    $procs = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil", "repoclean" -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    # Managed Software Center runs as the interactive user; taskkill catches it
    # regardless of session.
    try { & taskkill /F /IM "Managed Software Center.exe" /T 2>$null } catch { }
} catch {
    Write-Warning "Process termination had non-fatal errors: $_"
}

# 4. Remove Start Menu shortcut folder (idempotent)
try {
    $startMenuPath = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Cimian"
    if (Test-Path $startMenuPath) {
        Remove-Item -Path $startMenuPath -Recurse -Force -ErrorAction Stop
        Write-Host "Removed Start Menu shortcut"
    }
} catch {
    Write-Warning "Failed to remove Start Menu shortcut: $_"
}

# 5. Remove Cimian from system PATH
try {
    $currentPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
    if ($currentPath) {
        $entries = $currentPath -split ';' | Where-Object { $_ -and ($_ -ne $InstallDir) }
        $newPath = ($entries -join ';')
        if ($newPath -ne $currentPath) {
            [Environment]::SetEnvironmentVariable("PATH", $newPath, [EnvironmentVariableTarget]::Machine)
            Write-Host "Removed Cimian from system PATH"
        }
    }
} catch {
    Write-Warning "Failed to remove Cimian from PATH: $_"
}

# 6. Remove the HKLM\SOFTWARE\Cimian registry stamp
try {
    $registryPath = "HKLM:\SOFTWARE\Cimian"
    if (Test-Path $registryPath) {
        Remove-Item -Path $registryPath -Recurse -Force -ErrorAction Stop
        Write-Host "Removed HKLM\SOFTWARE\Cimian"
    }
} catch {
    Write-Warning "Failed to remove registry key: $_"
}

Write-Host "CimianTools uninstall completed" -ForegroundColor Yellow
exit 0
