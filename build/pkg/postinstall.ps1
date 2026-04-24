# CimianTools postinstall
#
# Fires on fresh install and on upgrade/reinstall — cimipkg conditions this CA
# with `NOT (REMOVE="ALL")`. Must be idempotent: the same operations may run
# after a fresh install OR after an upgrade replaces the binaries in place.
$ErrorActionPreference = 'Stop'

$InstallDir = "C:\Program Files\Cimian"
$arch = $env:PROCESSOR_ARCHITECTURE
$phase = $env:CIMIAN_PHASE

Write-Host "CimianTools postinstall: phase=$phase version=$($env:CIMIAN_VERSION) arch=$arch" -ForegroundColor Green

if (-not (Test-Path $InstallDir)) {
    throw "Installation directory not found: $InstallDir"
}

try {
    Write-Host "Verifying CimianTools executables..."
    $expected = @(
        'cimiwatcher.exe','managedsoftwareupdate.exe','cimitrigger.exe','cimistatus.exe',
        'cimiimport.exe','cimipkg.exe','makecatalogs.exe','makepkginfo.exe','manifestutil.exe','repoclean.exe',
        'Managed Software Center.exe'
    )
    $missing = @()
    foreach ($name in $expected) {
        $execPath = Join-Path $InstallDir $name
        if (-not (Test-Path $execPath)) { $missing += $name }
    }
    if ($missing.Count -gt 0) {
        throw "Missing executables from .pkg payload: $($missing -join ', ')"
    }

    # Add to system PATH (idempotent)
    Write-Host "Adding Cimian to system PATH..."
    $currentPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
    if ($currentPath -split ';' -notcontains $InstallDir) {
        $newPath = "$InstallDir;$currentPath"
        [Environment]::SetEnvironmentVariable("PATH", $newPath, [EnvironmentVariableTarget]::Machine)
        $env:PATH = "$InstallDir;$env:PATH"
        Write-Host "Added to system PATH"
    }

    # Install or restart CimianWatcher service
    $cimiwatcherExe = Join-Path $InstallDir "cimiwatcher.exe"
    if (Test-Path $cimiwatcherExe) {
        try {
            $existingService = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
            if ($existingService) {
                Write-Host "CimianWatcher service present; starting..."
                try {
                    Start-Service -Name "CimianWatcher" -ErrorAction Stop
                    Start-Sleep -Seconds 2
                } catch {
                    Write-Warning "Failed to start existing service, reinstalling: $_"
                    try { & $cimiwatcherExe uninstall; Start-Sleep -Seconds 2 } catch { }
                    & $cimiwatcherExe install; Start-Sleep -Seconds 2
                    & $cimiwatcherExe start; Start-Sleep -Seconds 2
                }
            } else {
                Write-Host "Installing CimianWatcher service..."
                & $cimiwatcherExe install; Start-Sleep -Seconds 2
                & $cimiwatcherExe start; Start-Sleep -Seconds 2
            }

            $service = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
            if (-not $service -or $service.Status -ne "Running") {
                Write-Warning "CimianWatcher service did not reach Running state"
            }
        } catch {
            Write-Warning "Failed to install or start CimianWatcher service: $_"
        }
    }

    # Register hourly scheduled task (idempotent — -Force overwrites)
    $managedSoftwareUpdateExe = Join-Path $InstallDir "managedsoftwareupdate.exe"
    if (-not (Test-Path $managedSoftwareUpdateExe)) {
        throw "managedsoftwareupdate.exe not found at $managedSoftwareUpdateExe"
    }
    $action = New-ScheduledTaskAction -Execute $managedSoftwareUpdateExe -Argument "--auto" -WorkingDirectory $InstallDir
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Hours 1)
    $settings = New-ScheduledTaskSettingsSet `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 10) `
        -RunOnlyIfNetworkAvailable `
        -Hidden `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -WakeToRun `
        -StartWhenAvailable
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    try {
        Register-ScheduledTask `
            -TaskName "Cimian Managed Software Update Hourly" `
            -Action $action `
            -Trigger $trigger `
            -Settings $settings `
            -Principal $principal `
            -Description "Automatically checks for and installs software updates every hour" `
            -Force `
            -ErrorAction Stop | Out-Null
        Write-Host "Scheduled task registered"
    } catch {
        $existing = Get-ScheduledTask -TaskName "Cimian Managed Software Update Hourly" -ErrorAction SilentlyContinue
        if (-not $existing) {
            throw "Failed to register scheduled task: $_"
        }
    }

    # Write version to registry
    try {
        $packageVersion = $env:CIMIAN_VERSION
        if ([string]::IsNullOrEmpty($packageVersion)) {
            $versionInfo = (Get-ItemProperty $cimiwatcherExe).VersionInfo
            $packageVersion = $versionInfo.ProductVersion
            if ([string]::IsNullOrEmpty($packageVersion)) { $packageVersion = $versionInfo.FileVersion }
        }
        if ([string]::IsNullOrEmpty($packageVersion)) { $packageVersion = "{{VERSION}}" }

        $registryPath = "HKLM:\SOFTWARE\Cimian"
        if (-not (Test-Path $registryPath)) { New-Item -Path $registryPath -Force | Out-Null }
        Set-ItemProperty -Path $registryPath -Name "Version" -Value $packageVersion -Type String
        Set-ItemProperty -Path $registryPath -Name "InstallPath" -Value $InstallDir -Type String
    } catch {
        Write-Warning "Failed to write version to registry: $_"
    }

    # Start Menu shortcut (idempotent)
    try {
        $startMenuPath = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Cimian"
        if (-not (Test-Path $startMenuPath)) {
            New-Item -ItemType Directory -Path $startMenuPath -Force | Out-Null
        }
        $shortcutPath = Join-Path $startMenuPath "Managed Software Center.lnk"
        $mscExe = Join-Path $InstallDir "Managed Software Center.exe"
        $wshell = New-Object -ComObject WScript.Shell
        $shortcut = $wshell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $mscExe
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.Description = "Self-service software installation for managed Windows devices"
        $shortcut.IconLocation = "$mscExe,0"
        $shortcut.Save()
    } catch {
        Write-Warning "Failed to create Start Menu shortcut: $_"
    }

    Write-Host "CimianTools postinstall completed ($phase)" -ForegroundColor Green
}
catch {
    Write-Host "postinstall failed: $_" -ForegroundColor Red
    exit 1
}

exit 0
