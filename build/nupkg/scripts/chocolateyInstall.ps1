# CimianTools Installation Script
# Prevents Chocolatey shim creation by using .ignore files and manually managing installation
$ErrorActionPreference = 'Stop'

Write-Host "Installing CimianTools..." -ForegroundColor Green

$toolsDir = "$(Split-Path -parent $MyInvocation.MyCommand.Definition)"

# ARM64-safe installation path - NEVER use Program Files (x86)
# This works correctly on both x64 and ARM64 architectures
if ($env:ProgramW6432) {
    # On 64-bit systems, ProgramW6432 points to the native Program Files
    $InstallDir = "$env:ProgramW6432\Cimian"
} else {
    # Fallback to ProgramFiles (should be the same on modern systems)
    $InstallDir = "$env:ProgramFiles\Cimian"
}

# Verify we're NOT installing to the legacy x86 folder
if ($InstallDir -like "*Program Files (x86)*") {
    throw "CRITICAL ERROR: Attempted installation to legacy x86 folder: $InstallDir. This is not allowed for ARM64-safe deployment."
}

Write-Host "ARM64-safe installation path: $InstallDir" -ForegroundColor Green
$arch = $env:PROCESSOR_ARCHITECTURE
Write-Host "Target architecture: $arch" -ForegroundColor Green

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
    
    # Stop ALL Cimian processes before file operations (comprehensive approach)
    Write-Host "Stopping ALL Cimian processes for upgrade..."
    try {
        # Get all Cimian processes using wildcard pattern (matches your manual command)
        $cimianProcesses = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil" -ErrorAction SilentlyContinue
        
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
            $remainingProcesses = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil" -ErrorAction SilentlyContinue
            if ($remainingProcesses) {
                Write-Host "Some Cimian processes still running, using taskkill for aggressive termination..."
                
                # Use taskkill for each known Cimian executable
                $cimianExes = @("cimiwatcher.exe", "managedsoftwareupdate.exe", "cimitrigger.exe", "cimistatus.exe", 
                               "cimiimport.exe", "cimipkg.exe", "makecatalogs.exe", "makepkginfo.exe", "manifestutil.exe")
                
                foreach ($exeName in $cimianExes) {
                    try {
                        & taskkill /F /IM $exeName /T 2>$null
                    } catch {
                        # Ignore errors for processes that aren't running
                    }
                }
                
                Start-Sleep -Seconds 3
                
                # Final verification
                $finalCheck = Get-Process -Name "*cimi*", "managedsoftwareupdate", "makecatalogs", "makepkginfo", "manifestutil" -ErrorAction SilentlyContinue
                if ($finalCheck) {
                    Write-Warning "Some Cimian processes may still be running after aggressive termination:"
                    foreach ($proc in $finalCheck) {
                        Write-Warning "  - $($proc.Name) (PID: $($proc.Id))"
                    }
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
        Write-Warning "Some file operations may fail due to locked files"
    }
    
    # Create native Program Files\Cimian directory (never x86)
    Write-Host "Creating ARM64-safe installation directory: $InstallDir"
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Write-Host "Created directory: $InstallDir"
    }

    # Copy EXEs from the package's tools folder (ignore shim-blockers)
    Write-Host "Copying executables to ARM64-safe Program Files..."
    $expected = @(
      'cimiwatcher.exe','managedsoftwareupdate.exe','cimitrigger.exe','cimistatus.exe',
      'cimiimport.exe','cimipkg.exe','makecatalogs.exe','makepkginfo.exe','manifestutil.exe'
    )
    $missing = @()
    foreach ($name in $expected) {
      $src = Get-ChildItem -Path $toolsDir -Recurse -Filter $name -File | Select-Object -First 1
      if (-not $src) { $missing += $name; continue }
      $dest = Join-Path $InstallDir $name
      $ok = $false
      for ($i=1; $i -le 5 -and -not $ok; $i++) {
        try {
          Copy-Item -LiteralPath $src.FullName -Destination $dest -Force -ErrorAction Stop
          Write-Host "Copied $name"
          $ok = $true
        } catch {
          try {
            if (Test-Path $dest) { Move-Item -LiteralPath $dest -Destination "$dest.old.$i" -Force -ErrorAction SilentlyContinue }
          } catch {}
          Start-Sleep -Seconds ([Math]::Min($i*2,5))
          if ($i -eq 5 -and -not $ok) { throw "Failed to copy $name" }
        }
      }
    }
    if ($missing.Count -gt 0) { throw "Missing from package payload: $($missing -join ', ')" }

    # Add to PATH
    Write-Host "Adding Cimian to system PATH..."
    $currentPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
    if ($currentPath -split ';' -notcontains $InstallDir) {
        $newPath = "$InstallDir;$currentPath"
        [Environment]::SetEnvironmentVariable("PATH", $newPath, [EnvironmentVariableTarget]::Machine)
        $env:PATH = "$InstallDir;$env:PATH"
        Write-Host "Added to system PATH"
    }

    # Install and start CimianWatcher service for responsive bootstrap monitoring
    Write-Host "Installing CimianWatcher service for responsive bootstrap monitoring..."
    $cimiwatcherExe = Join-Path $InstallDir "cimiwatcher.exe"
    if (Test-Path $cimiwatcherExe) {
        try {
            # If service was already installed, we may just need to start it
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
        # Get version from the chocolatey package environment variable
        $packageVersion = $env:ChocolateyPackageVersion
        if ([string]::IsNullOrEmpty($packageVersion)) {
            # Fallback: try to get version from the executable
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
        }
        
        # Final fallback
        if ([string]::IsNullOrEmpty($packageVersion)) {
            $packageVersion = "unknown"
            Write-Warning "Could not determine Cimian version, using 'unknown'"
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

    Write-Host "CimianTools installed successfully to ARM64-safe path!" -ForegroundColor Green
    Write-Host "Installation Directory: $InstallDir" -ForegroundColor Green
    Write-Host "Architecture: $arch" -ForegroundColor Green
    Write-Host "Added to system PATH" -ForegroundColor Green
    Write-Host "CimianWatcher service installed and started for responsive bootstrap monitoring" -ForegroundColor Green
    Write-Host "Scheduled task created for automatic hourly updates" -ForegroundColor Green
    Write-Host "Version information written to registry: HKLM\SOFTWARE\Cimian\Version" -ForegroundColor Green
}
catch {
    Write-Host "Installation failed: $_" -ForegroundColor Red
    throw
}
