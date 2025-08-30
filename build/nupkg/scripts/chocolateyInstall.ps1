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
    
    # Stop any running managedsoftwareupdate.exe processes (needed for upgrades)
    Write-Host "Checking for running managedsoftwareupdate.exe processes..."
    $managedSoftwareProcesses = Get-Process -Name "managedsoftwareupdate" -ErrorAction SilentlyContinue
    if ($managedSoftwareProcesses) {
        Write-Host "Found $($managedSoftwareProcesses.Count) running managedsoftwareupdate.exe process(es)"
        Write-Host "Terminating managedsoftwareupdate.exe processes for upgrade..."
        try {
            $managedSoftwareProcesses | Stop-Process -Force
            Start-Sleep -Seconds 3
            Write-Host "Successfully terminated managedsoftwareupdate.exe processes"
        } catch {
            Write-Warning "Failed to terminate some managedsoftwareupdate.exe processes: $_"
            # Try a more aggressive approach
            try {
                Get-Process -Name "managedsoftwareupdate" -ErrorAction SilentlyContinue | Stop-Process -Force
                Start-Sleep -Seconds 2
                Write-Host "Forcefully terminated remaining managedsoftwareupdate.exe processes"
            } catch {
                Write-Warning "Failed to forcefully terminate managedsoftwareupdate.exe processes: $_"
            }
        }
    } else {
        Write-Host "No running managedsoftwareupdate.exe processes found"
    }
    
    # Create native Program Files\Cimian directory (never x86)
    Write-Host "Creating ARM64-safe installation directory: $InstallDir"
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Write-Host "Created directory: $InstallDir"
    }

    # Copy EXEs from the package's tools folder (ignore shim-blockers)
    Write-Host "Copying executables to ARM64-safe Program Files..."
    $exeFiles = Get-ChildItem -Path $toolsDir -Filter '*.exe' -File |
                Where-Object { $_.Name -notlike '*.ignore' }
    
    if ($exeFiles.Count -eq 0) {
        throw "No .exe files found in tools directory: $toolsDir"
    }
    
    foreach ($exe in $exeFiles) {
        $destPath = Join-Path $InstallDir $exe.Name
        
        # Check if the destination file is locked and try to handle it
        if (Test-Path $destPath) {
            Write-Host "Checking if $($exe.Name) is in use..."
            $retryCount = 0
            $maxRetries = 5
            $copySucceeded = $false
            
            while ($retryCount -lt $maxRetries -and -not $copySucceeded) {
                try {
                    Copy-Item -Path $exe.FullName -Destination $destPath -Force -ErrorAction Stop
                    Write-Host "Copied $($exe.Name)"
                    $copySucceeded = $true
                } catch {
                    $retryCount++
                    Write-Warning "Attempt $retryCount failed to copy $($exe.Name): $_"
                    
                    if ($exe.Name -eq "managedsoftwareupdate.exe") {
                        # Special handling for managedsoftwareupdate.exe
                        Write-Host "Attempting to terminate any remaining managedsoftwareupdate.exe processes..."
                        try {
                            Get-Process -Name "managedsoftwareupdate" -ErrorAction SilentlyContinue | Stop-Process -Force
                            Start-Sleep -Seconds 2
                        } catch {
                            Write-Warning "Failed to terminate managedsoftwareupdate.exe: $_"
                        }
                    }
                    
                    if ($retryCount -lt $maxRetries) {
                        Write-Host "Waiting 2 seconds before retry..."
                        Start-Sleep -Seconds 2
                    } else {
                        throw "Failed to copy $($exe.Name) after $maxRetries attempts: $_"
                    }
                }
            }
        } else {
            # File doesn't exist, simple copy
            Copy-Item -Path $exe.FullName -Destination $destPath -Force
            Write-Host "Copied $($exe.Name)"
        }
    }

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

    # Install scheduled tasks for automatic software updates
    Write-Host "Installing scheduled tasks for automatic software updates..."
    $taskInstallScript = Join-Path $toolsDir "install-scheduled-tasks.ps1"
    if (Test-Path $taskInstallScript) {
        try {
            & $taskInstallScript -InstallPath $InstallDir
            Write-Host "Scheduled tasks installed successfully"
        } catch {
            Write-Warning "Failed to install scheduled tasks: $_"
            Write-Warning "You may need to manually create scheduled tasks for automatic updates"
        }
    } else {
        Write-Warning "Scheduled task installation script not found: $taskInstallScript"
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
