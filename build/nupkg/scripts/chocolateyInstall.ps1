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

    Write-Host "CimianTools installed successfully to ARM64-safe path!" -ForegroundColor Green
    Write-Host "Installation Directory: $InstallDir" -ForegroundColor Green
    Write-Host "Architecture: $arch" -ForegroundColor Green
    Write-Host "Added to system PATH" -ForegroundColor Green
}
catch {
    Write-Host "Installation failed: $_" -ForegroundColor Red
    throw
}
