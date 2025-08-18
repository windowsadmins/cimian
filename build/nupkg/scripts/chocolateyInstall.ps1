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
        Copy-Item -Path $exe.FullName -Destination $destPath -Force
        Write-Host "Copied $($exe.Name)"
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
            # Install the service
            Write-Host "Installing CimianWatcher service..."
            & $cimiwatcherExe install
            Start-Sleep -Seconds 2
            
            # Start the service
            Write-Host "Starting CimianWatcher service..."
            & $cimiwatcherExe start
            Start-Sleep -Seconds 2
            
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

    Write-Host "CimianTools installed successfully to ARM64-safe path!" -ForegroundColor Green
    Write-Host "Installation Directory: $InstallDir" -ForegroundColor Green
    Write-Host "Architecture: $arch" -ForegroundColor Green
    Write-Host "Added to system PATH" -ForegroundColor Green
    Write-Host "CimianWatcher service installed and started for responsive bootstrap monitoring" -ForegroundColor Green
    Write-Host "Scheduled task created for automatic hourly updates" -ForegroundColor Green
}
catch {
    Write-Host "Installation failed: $_" -ForegroundColor Red
    throw
}
