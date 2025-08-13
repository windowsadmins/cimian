# CimianTools Installation Script
# Prevents Chocolatey shim creation by using .ignore files and manually managing installation
$ErrorActionPreference = 'Stop'

Write-Host "Installing CimianTools..." -ForegroundColor Green

$toolsDir = "$(Split-Path -parent $MyInvocation.MyCommand.Definition)"
# Use ProgramData for enterprise environments that may have stricter Program Files policies
$InstallDir = "${env:ProgramData}\Cimian"

try {
    # Create Program Files\Cimian directory
    Write-Host "Creating installation directory: $InstallDir"
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Write-Host "Created directory: $InstallDir"
    }

    # Copy all .exe files to Program Files\Cimian (excluding any in subdirectories to avoid duplicates)
    Write-Host "Copying executables to Program Files..."
    $exeFiles = Get-ChildItem -Path $toolsDir -Filter "*.exe" | Where-Object { $_.Directory.Name -eq "scripts" }
    
    if ($exeFiles.Count -eq 0) {
        # Fallback: look in parent directories for exe files
        $packageDir = Split-Path -Parent $toolsDir
        $exeFiles = Get-ChildItem -Path $packageDir -Filter "*.exe" -Recurse | Where-Object { $_.Name -notlike "*.ignore" }
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

    Write-Host "CimianTools installed successfully!" -ForegroundColor Green
    Write-Host "Installation Directory: $InstallDir" -ForegroundColor Green
    Write-Host "Added to system PATH" -ForegroundColor Green
}
catch {
    Write-Host "Installation failed: $_" -ForegroundColor Red
    throw
}
