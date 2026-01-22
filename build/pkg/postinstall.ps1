# CimianTools .pkg Postinstall Script
# Adapted from chocolateyInstall.ps1 for .pkg package format
# Process termination is handled by preinstall.ps1 BEFORE file operations
$ErrorActionPreference = 'Stop'

Write-Host "CimianTools .pkg Postinstall: Setting up services and scheduled tasks..." -ForegroundColor Green

# The binaries are already copied to the install location by the .pkg installer
$InstallDir = "C:\Program Files\Cimian"

# Verify we're in the correct location
if (-not (Test-Path $InstallDir)) {
    throw "Installation directory not found: $InstallDir"
}

Write-Host "Installation directory confirmed: $InstallDir" -ForegroundColor Green
$arch = $env:PROCESSOR_ARCHITECTURE
Write-Host "Target architecture: $arch" -ForegroundColor Green

try {
    Write-Host "Verifying CimianTools executables..."
    $expected = @(
        'cimiwatcher.exe','managedsoftwareupdate.exe','cimitrigger.exe',
        'cimiimport.exe','cimipkg.exe','makecatalogs.exe','makepkginfo.exe','manifestutil.exe'
    )
    $missing = @()
    foreach ($name in $expected) {
        $execPath = Join-Path $InstallDir $name
        if (-not (Test-Path $execPath)) {
            $missing += $name
        } else {
            Write-Host "✅ Found $name"
        }
    }
    
    if ($missing.Count -gt 0) {
        throw "Missing executables from .pkg payload: $($missing -join ', ')"
    }

    # Add to PATH
    Write-Host "Adding Cimian to system PATH..."
    $currentPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
    if ($currentPath -split ';' -notcontains $InstallDir) {
        $newPath = "$InstallDir;$currentPath"
        [Environment]::SetEnvironmentVariable("PATH", $newPath, [EnvironmentVariableTarget]::Machine)
        $env:PATH = "$InstallDir;$env:PATH"
        Write-Host "Added to system PATH"
    } else {
        Write-Host "Already in system PATH"
    }

    # Install and start CimianWatcher service for responsive bootstrap monitoring
    Write-Host "Installing CimianWatcher service for responsive bootstrap monitoring..."
    $cimiwatcherExe = Join-Path $InstallDir "cimiwatcher.exe"
    if (Test-Path $cimiwatcherExe) {
        try {
            # Check if service already exists
            $existingService = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
            
            if ($existingService) {
                Write-Host "CimianWatcher service already installed, restarting it..."
                try {
                    Start-Service -Name "CimianWatcher" -ErrorAction Stop
                    Start-Sleep -Seconds 2
                    Write-Host "CimianWatcher service restarted successfully"
                } catch {
                    Write-Warning "Failed to restart existing service, attempting full reinstall: $_"
                    # Uninstall and reinstall the service
                    try {
                        & $cimiwatcherExe uninstall
                        Start-Sleep -Seconds 2
                    } catch {
                        Write-Warning "Failed to uninstall existing service: $_"
                    }
                    
                    # Install fresh
                    Write-Host "Installing CimianWatcher service..."
                    & $cimiwatcherExe install
                    Start-Sleep -Seconds 2
                    
                    # Start the service
                    Write-Host "Starting CimianWatcher service..."
                    & $cimiwatcherExe start
                    Start-Sleep -Seconds 2
                }
            } else {
                # Fresh installation
                Write-Host "Installing CimianWatcher service..."
                & $cimiwatcherExe install
                Start-Sleep -Seconds 2
                
                # Start the service
                Write-Host "Starting CimianWatcher service..."
                & $cimiwatcherExe start
                Start-Sleep -Seconds 2
            }
            
            # Verify service is running
            $service = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
            if ($service -and $service.Status -eq "Running") {
                Write-Host "CimianWatcher service installed and started successfully" -ForegroundColor Green
            } else {
                Write-Warning "CimianWatcher service was installed but may not be running properly"
            }
        } catch {
            Write-Warning "Failed to install or start CimianWatcher service: $_"
            Write-Warning "Bootstrap monitoring may not work optimally without the service"
        }
    } else {
        Write-Warning "cimiwatcher.exe not found: $cimiwatcherExe"
        Write-Warning "Bootstrap monitoring service will not be available"
    }

    # Install scheduled tasks for automatic software updates (CRITICAL - MUST WORK)
    Write-Host "Installing critical hourly scheduled task for automatic software updates..."
    try {
        $managedSoftwareUpdateExe = Join-Path $InstallDir "managedsoftwareupdate.exe"
        
        # Verify the executable exists
        if (-not (Test-Path $managedSoftwareUpdateExe)) {
            throw "managedsoftwareupdate.exe not found at $managedSoftwareUpdateExe"
        }

        # Create hourly automatic software update task
        Write-Host "Creating Cimian automatic software update task..."
        
        # Task action: run managedsoftwareupdate.exe --auto
        $action = New-ScheduledTaskAction -Execute $managedSoftwareUpdateExe -Argument "--auto" -WorkingDirectory $InstallDir
        
        # Task trigger: start immediately and repeat every hour indefinitely (adds randomization across deployments)
        $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Hours 1)
        
        # Task settings: 30 minute timeout, restart on failure, hidden, run on battery
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
        
        # Task principal: run as SYSTEM with highest privileges
        $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
        
        # Register the scheduled task
        try {
            Register-ScheduledTask `
                -TaskName "Cimian Managed Software Update Hourly" `
                -Action $action `
                -Trigger $trigger `
                -Settings $settings `
                -Principal $principal `
                -Description "Automatically checks for and installs software updates every hour using Cimian managed software system" `
                -Force `
                -ErrorAction Stop
            
            # Verify the task was actually created
            $createdTask = Get-ScheduledTask -TaskName "Cimian Managed Software Update Hourly" -ErrorAction Stop
            if ($createdTask.State -eq "Ready") {
                Write-Host "✅ CRITICAL scheduled task created successfully" -ForegroundColor Green
                Write-Host "   Task Name: Cimian Managed Software Update Hourly" -ForegroundColor Green
                Write-Host "   Schedule: Every hour starting 5 minutes after installation" -ForegroundColor Green
                Write-Host "   Command: $managedSoftwareUpdateExe --auto" -ForegroundColor Green
                Write-Host "   Runs as: SYSTEM (highest privileges)" -ForegroundColor Green
            } else {
                throw "Task was created but is not in Ready state. Current state: $($createdTask.State)"
            }
        } catch [Microsoft.PowerShell.Cmdletization.Cim.CimJobException] {
            # Handle the specific case where task already exists with "Access is denied"
            if ($_.Exception.Message -like "*Access is denied*") {
                Write-Host "Task registration encountered access denied, checking if task already exists and is functional..."
                try {
                    $existingTask = Get-ScheduledTask -TaskName "Cimian Managed Software Update Hourly" -ErrorAction Stop
                    if ($existingTask.State -eq "Ready") {
                        Write-Host "✅ CRITICAL scheduled task already exists and is functional" -ForegroundColor Green
                        Write-Host "   Task Name: Cimian Managed Software Update Hourly" -ForegroundColor Green
                        Write-Host "   Current State: $($existingTask.State)" -ForegroundColor Green
                    } else {
                        throw "Existing task found but is not functional. State: $($existingTask.State)"
                    }
                } catch {
                    throw "Access denied during task creation and no functional existing task found: $($_.Exception.Message)"
                }
            } else {
                throw "Failed to register scheduled task: $($_.Exception.Message)"
            }
        }
    } catch {
        Write-Error "CRITICAL: Failed to install scheduled tasks: $_"
        Write-Error "Installation cannot continue without scheduled tasks"
        throw "Scheduled task installation failed: $_"
    }

    # Write Cimian version to registry
    Write-Host "Writing Cimian version to registry..."
    try {
        # For .pkg format, we need to determine the version differently
        # Try to get version from one of the executables
        $packageVersion = "unknown"
        $cimiwatcherExe = Join-Path $InstallDir "cimiwatcher.exe"
        if (Test-Path $cimiwatcherExe) {
            try {
                $versionInfo = (Get-ItemProperty $cimiwatcherExe).VersionInfo
                $packageVersion = $versionInfo.ProductVersion
                if ([string]::IsNullOrEmpty($packageVersion)) {
                    $packageVersion = $versionInfo.FileVersion
                }
            } catch {
                Write-Warning "Could not extract version from executable: $_"
            }
        }
        
        # Final fallback
        if ([string]::IsNullOrEmpty($packageVersion) -or $packageVersion -eq "unknown") {
            $packageVersion = "{{VERSION}}"  # This will be replaced by build script
            Write-Warning "Using template version placeholder: $packageVersion"
        }
        
        # Create registry key and write version
        $registryPath = "HKLM:\SOFTWARE\Cimian"
        if (-not (Test-Path $registryPath)) {
            New-Item -Path $registryPath -Force | Out-Null
        }
        Set-ItemProperty -Path $registryPath -Name "Version" -Value $packageVersion -Type String
        Set-ItemProperty -Path $registryPath -Name "InstallPath" -Value $InstallDir -Type String
        
        Write-Host "Successfully wrote Cimian version '$packageVersion' to registry" -ForegroundColor Green
    } catch {
        Write-Warning "Failed to write version to registry: $_"
        Write-Warning "Version information will not be available in registry"
    }

    Write-Host "CimianTools .pkg postinstall completed successfully!" -ForegroundColor Green
    Write-Host "Installation Directory: $InstallDir" -ForegroundColor Green
    Write-Host "Architecture: $arch" -ForegroundColor Green
    Write-Host "Added to system PATH" -ForegroundColor Green
    Write-Host "CimianWatcher service installed and started for responsive bootstrap monitoring" -ForegroundColor Green
    Write-Host "Scheduled task created for automatic hourly updates" -ForegroundColor Green
    Write-Host "Version information written to registry: HKLM\SOFTWARE\Cimian\Version" -ForegroundColor Green
}
catch {
    Write-Host ".pkg postinstall failed: $_" -ForegroundColor Red
    exit 1
}

exit 0