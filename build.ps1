<#
.SYNOPSIS
    Builds the Cimian project locally, replicating the CI/CD pipeline.
.DESCRIPTION
    This script automates the build and packaging process, including installing dependencies,
    building binaries, and packaging artifacts.
#>
#  Sign           build + sign
#  NoSign         disable auto-signing even if enterprise cert is found
#  Thumbprint XX  override auto-detection
#  Task XX        run specific task: build, package, all (default: all)
#  Binaries       build and sign only the .exe binaries, skip all packaging
#  Binary XX      build and sign only a specific binary (e.g., managedsoftwareupdate)
#  Install        after building, install the .pkg package (requires elevation)
#  IntuneWin      create .intunewin packages (adds build time, only needed for deployment)
#  Dev            development mode: stops services, faster iteration
#  SignMSI        sign existing MSI files in release directory (standalone operation)
#  SkipMSI        skip MSI packaging, build only .nupkg packages
#  PackageOnly    package existing binaries only (skip build), create both MSI and NUPKG
#  NupkgOnly      create .nupkg packages only using existing binaries (skip build and MSI)
#  MsiOnly        create MSI packages only using existing binaries (skip build and NUPKG)
#  PkgOnly        create .pkg packages only using existing binaries (direct binary payload)
#
# Usage examples:
#   .\build.ps1                      # Full build with auto-signing (no .intunewin)
#   .\build.ps1 -Binaries -Sign      # Build only binaries with signing
#   .\build.ps1 -Binary managedsoftwareupdate -Sign # Build and sign only specific binary
#   .\build.ps1 -Sign -Thumbprint XX # Force sign with specific certificate
#   .\build.ps1 -Install             # Build and install the .pkg package
#   .\build.ps1 -IntuneWin           # Full build including .intunewin packages
#   .\build.ps1 -Dev -Install        # Development mode: fast rebuild and install
#   .\build.ps1 -SignMSI             # Sign existing MSI files in release directory
#   .\build.ps1 -SignMSI -Thumbprint XX # Sign existing MSI files with specific certificate
#   .\build.ps1 -SkipMSI             # Build only .nupkg packages, skip MSI packaging
#   .\build.ps1 -PackageOnly         # Package existing binaries (both MSI and NUPKG)
#   .\build.ps1 -NupkgOnly           # Create only .nupkg packages from existing binaries
#   .\build.ps1 -MsiOnly             # Create only MSI packages from existing binaries  
#   .\build.ps1 -PkgOnly             # Create only .pkg packages from existing binaries (direct payload)
param(
    [switch]$Sign,
    [switch]$NoSign,
    [string]$Thumbprint,
    [ValidateSet("build", "package", "all")]
    [string]$Task = "all",
    [switch]$Binaries,
    [string]$Binary,
    [switch]$Install,
    [switch]$IntuneWin,
    [switch]$Dev,  # Development mode - stops services, skips signing, faster iteration
    [switch]$SignMSI,  # Sign existing MSI files in release directory
    [switch]$SkipMSI,  # Skip MSI packaging, build only .nupkg packages
    [switch]$PackageOnly,  # Package existing binaries only (skip build), create both MSI and NUPKG
    [switch]$NupkgOnly,    # Create .nupkg packages only using existing binaries (skip build and MSI)
    [switch]$MsiOnly,      # Create MSI packages only using existing binaries (skip build and NUPKG)
    [switch]$PkgOnly       # Create .pkg packages only using existing binaries (direct binary payload)
)
#   GLOBALS  
# Friendly name (CN) of the enterprise code-signing certificate you push with Intune
$Global:EnterpriseCertCN = 'EmilyCarrU Intune Windows Enterprise Certificate'
# Exit immediately if a command exits with a non-zero status
$ErrorActionPreference = 'Stop'
# Ensure GO111MODULE is enabled for module-based builds
$env:GO111MODULE = "on"
# Disable CGO for pure Go builds (no GUI dependencies)
$env:CGO_ENABLED = "0"
# Function to display messages with different log levels
function Write-Log {
    param (
        [string]$Message,
        [ValidateSet("INFO", "SUCCESS", "WARNING", "ERROR")]
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    switch ($Level) {
        "INFO"    { Write-Host "[$timestamp] [INFO] $Message" -ForegroundColor Cyan }
        "SUCCESS" { Write-Host "[$timestamp] [SUCCESS] $Message" -ForegroundColor Green }
        "WARNING" { Write-Host "[$timestamp] [WARNING] $Message" -ForegroundColor Yellow }
        "ERROR"   { Write-Host "[$timestamp] [ERROR] $Message" -ForegroundColor Red }
    }
}
# Function to check if a command exists
function Test-Command {
    param (
        [string]$Command
    )
    # Compare with $null on the left
    return $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}
function Get-SigningCertThumbprint {
    [OutputType([hashtable])]
    param()
    # Check both CurrentUser and LocalMachine certificate stores
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and $_.Subject -like '*EmilyCarrU*' } |
       Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{
            Thumbprint = $cert.Thumbprint
            Store = "CurrentUser"
        }
    }
    
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.HasPrivateKey -and $_.Subject -like '*EmilyCarrU*' } |
       Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{
            Thumbprint = $cert.Thumbprint
            Store = "LocalMachine"
        }
    }
    
    return $null
}
function Test-SignTool {
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) { return }
    $roots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "$env:ProgramFiles(x86)\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    try {
        $kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -EA Stop).KitsRoot10
        if ($kitsRoot) { $roots += (Join-Path $kitsRoot 'bin') }
    } catch {}

    foreach ($root in $roots) {
        $cand = Get-ChildItem -Path (Join-Path $root '*\x64\signtool.exe') -EA SilentlyContinue |
                Sort-Object LastWriteTime -Desc | Select-Object -First 1
        if ($cand) {
            $env:Path = "$($cand.Directory.FullName);$env:Path"
            return
        }
    }
    throw "signtool.exe not found. Install Windows 10/11 SDK (Signing Tools)."
}
# Function to find the WiX Toolset bin directory (supports both v3 and v6)
function Find-WiXBinPath {
    # First check for WiX v5/v6 (.NET tool) - check using dotnet tool list to avoid permission issues
    try {
        $dotnetWixVersion = & dotnet tool list --global 2>$null | Select-String "^wix\s"
        if ($dotnetWixVersion) {
            Write-Log "Found WiX v5/v6 as .NET global tool: $($dotnetWixVersion.ToString().Trim())" "SUCCESS"
            return "dotnet-tool"
        }
    } catch {
        # Ignore errors from dotnet tool list
    }
    
    # Fallback to WiX v3 (legacy) - Common installation paths for WiX Toolset via Chocolatey
    $possiblePaths = @(
        "C:\Program Files (x86)\WiX Toolset*\bin\candle.exe",
        "C:\Program Files\WiX Toolset*\bin\candle.exe"
    )
    foreach ($path in $possiblePaths) {
        $found = Get-ChildItem -Path $path -ErrorAction SilentlyContinue
        if ($null -ne $found) {
            Write-Log "Found WiX v3 legacy installation: $($found[0].Directory.FullName)" "INFO"
            return $found[0].Directory.FullName
        }
    }
    return $null
}

# Function to clean up generated .syso files
function Remove-VersionResources {
    Write-Log "Cleaning up generated version resource files..." "INFO"
    Get-ChildItem -Path "cmd" -Recurse -Filter "resource.syso" | ForEach-Object {
        try {
            Remove-Item $_.FullName -Force
            Write-Log "Removed version resource: $($_.FullName)" "SUCCESS"
        }
        catch {
            Write-Log "Failed to remove version resource: $($_.FullName) - $_" "WARNING"
        }
    }
    
    # Also clean up any temporary version info JSON files
    Get-ChildItem -Path "." -Filter "versioninfo_*.json" | ForEach-Object {
        try {
            Remove-Item $_.FullName -Force
        }
        catch {
            # Ignore errors for temporary files
        }
    }
}

# Function to generate Windows version information for a binary
function New-VersionInfo {
    param (
        [Parameter(Mandatory)]
        [string]$BinaryName,
        [Parameter(Mandatory)]
        [string]$BinaryPath,
        [Parameter(Mandatory)]
        [string]$Version,
        [Parameter(Mandatory)]
        [string]$SemanticVersion
    )
    
    Write-Log "Preparing version information for $BinaryName..." "INFO"
    
    # For now, we'll rely on the ldflags to set the version information
    # Windows file properties require a .syso file which is being blocked by security software
    # The --version flag will work correctly with the ldflags approach
    
    Write-Log "Version information will be embedded via Go build flags for $BinaryName" "INFO"
    return $true
}
# Function to retry an action with delay
function Invoke-Retry {
    param (
        [scriptblock]$Action,
        [int]$MaxAttempts = 5,
        [int]$DelaySeconds = 2
    )
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            & $Action
            return $true
        }
        catch {
            if ($attempt -lt $MaxAttempts) {
                Write-Log "Attempt $attempt failed. Retrying in $DelaySeconds seconds..." "WARNING"
                Start-Sleep -Seconds $DelaySeconds
            }
            else {
                Write-Log "All $MaxAttempts attempts failed." "ERROR"
                return $false
            }
        }
    }
}

#   SIGNING DECISION  
# Auto-detect enterprise certificate if available
$autoDetectedThumbprint = $null
if (-not $Sign -and -not $NoSign -and -not $Thumbprint) {
    try {
        $certInfo = Get-SigningCertThumbprint
        if ($certInfo) {
            $autoDetectedThumbprint = $certInfo.Thumbprint
            Write-Log "Auto-detected enterprise certificate $autoDetectedThumbprint in $($certInfo.Store) store - will sign binaries for security." "INFO"
            $Sign = $true
            $Thumbprint = $autoDetectedThumbprint
        } else {
            Write-Log "No enterprise certificate found - binaries will be unsigned (may be blocked by Defender)." "WARNING"
        }
    }
    catch {
        Write-Log "Could not check for enterprise certificates: $_" "WARNING"
    }
}
# If Binaries flag is set, force Task to "build" and skip all packaging
if ($Binaries) {
    Write-Log "Binaries flag detected - will only build and sign .exe files, skipping all packaging." "INFO"
    $Task = "build"
}
# If Binary parameter is set, force Task to "build" and skip all packaging
if ($Binary) {
    Write-Log "Binary parameter detected - will only build and sign '$Binary' binary, skipping all packaging." "INFO"
    $Task = "build"
    # Validate that the specified binary exists in cmd directory
    $binaryPath = "cmd\$Binary"
    if (-not (Test-Path $binaryPath)) {
        Write-Log "Specified binary '$Binary' does not exist in cmd directory. Available binaries:" "ERROR"
        Get-ChildItem -Directory -Path "cmd" | ForEach-Object { Write-Log "  $($_.Name)" "INFO" }
        exit 1
    }
}
# If Install flag is set with Binaries or Binary, show error
if ($Install -and ($Binaries -or $Binary)) {
    Write-Log "Cannot use -Install with -Binaries or -Binary flag. .pkg packages are needed for installation." "ERROR"
    exit 1
}

# If SignMSI flag is set, validate it's not used with conflicting flags
if ($SignMSI) {
    $conflictingFlags = @()
    if ($Binaries) { $conflictingFlags += "-Binaries" }
    if ($Binary) { $conflictingFlags += "-Binary" }
    if ($Install) { $conflictingFlags += "-Install" }
    if ($IntuneWin) { $conflictingFlags += "-IntuneWin" }
    if ($Dev) { $conflictingFlags += "-Dev" }
    if ($PackageOnly) { $conflictingFlags += "-PackageOnly" }
    if ($NupkgOnly) { $conflictingFlags += "-NupkgOnly" }
    if ($MsiOnly) { $conflictingFlags += "-MsiOnly" }
    if ($PkgOnly) { $conflictingFlags += "-PkgOnly" }
    
    if ($conflictingFlags.Count -gt 0) {
        Write-Log "Cannot use -SignMSI with the following flags: $($conflictingFlags -join ', ')" "ERROR"
        Write-Log "-SignMSI is designed to sign existing MSI files only." "ERROR"
        exit 1
    }
}
# If Install flag is set, ensure Task includes packaging
if ($Install -and $Task -eq "build") {
    Write-Log "Install flag detected - forcing Task to 'all' to ensure .pkg packages are built." "INFO"
    $Task = "all"
}
if ($NoSign) {
    Write-Log "NoSign parameter specified - skipping all signing." "INFO"
    $Sign = $false
}
if ($Sign) {
    Test-SignTool
    if (-not $Thumbprint) {
        $certInfo = Get-SigningCertThumbprint
        if (-not $certInfo) {
            Write-Log "No valid EmilyCarrU certificate with a private key found - aborting." "ERROR"
            exit 1
        }
        $Thumbprint = $certInfo.Thumbprint
        Write-Log "Auto-selected signing cert $Thumbprint from $($certInfo.Store) store" "INFO"
    } else {
        Write-Log "Using signing certificate $Thumbprint" "INFO"
    }
    $env:SIGN_THUMB = $Thumbprint   # used by the two sign* functions
} else {
    Write-Log "Build will be unsigned." "INFO"
}
#   DEVELOPMENT MODE HANDLING  
if ($Dev) {
    Write-Log "Development mode enabled - preparing for rapid iteration..." "INFO"
    # Stop and remove Cimian services that might lock files
    $services = @("CimianWatcher")
    foreach ($serviceName in $services) {
        try {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($service) {
                if ($service.Status -eq "Running") {
                    Write-Log "Stopping $serviceName service for development build..." "INFO"
                    Stop-Service -Name $serviceName -Force
                }
                Write-Log "Removing $serviceName service for clean rebuild..." "INFO"
                $exe = "C:\Program Files\Cimian\cimiwatcher.exe"
                if (Test-Path $exe) {
                    & $exe remove 2>$null
                }
            }
        }
        catch {
            Write-Log "Could not manage $serviceName service (this is normal): $_" "WARNING"
        }
    }
    # Kill any running Cimian processes that might lock files
    $processes = @("cimiwatcher", "managedsoftwareupdate")
    foreach ($processName in $processes) {
        try {
            Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force
            Write-Log "Stopped $processName process for development build" "INFO"
        }
        catch {
            # Normal if process not running
        }
    }
    # Disable signing in development mode to avoid certificate lock issues
    if ($Sign) {
        Write-Log "Development mode: Disabling signing to avoid certificate conflicts" "WARNING"
        $Sign = $false
        $env:SIGN_THUMB = $null
    }
    Write-Log "Development mode preparation complete - files unlocked for rebuild" "SUCCESS"
}

#   SIGNING HELPERS  
function Invoke-SignArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Thumbprint,
        [string]$Store = "LocalMachine",
        [int]$MaxAttempts = 4
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }

    $tsas = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS'
    )

    # Set store parameter based on which store the certificate is in
    $storeParam = if ($Store -eq "CurrentUser") { "/s", "My" } else { "/s", "My", "/sm" }

    $attempt = 0
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        foreach ($tsa in $tsas) {
            $signArgs = @("sign") + $storeParam + @(
                "/sha1", $Thumbprint,
                "/fd", "SHA256",
                "/td", "SHA256",
                "/tr", $tsa,
                "/v",
                $Path
            )
            
            & signtool.exe @signArgs
            $code = $LASTEXITCODE

            if ($code -eq 0) {
                # Optional append of legacy timestamp for old verifiers; harmless if TSA rejects.
                & signtool.exe timestamp /t http://timestamp.digicert.com /v "$Path" 2>$null
                return
            }

            Start-Sleep -Seconds (4 * $attempt)
        }
    }

    throw "Signing failed after $MaxAttempts attempts across TSAs: $Path"
}

# Legacy signPackage function wrapper for backwards compatibility
function signPackage {
    <#
      .SYNOPSIS  Authenticode-signs an MSI/EXE/ with our enterprise cert.
      .PARAMETER FilePath      the file you want to sign
      .PARAMETER Thumbprint    SHA-1 thumbprint of the cert (defaults to $env:SIGN_THUMB)
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string]$Thumbprint = $env:SIGN_THUMB
    )
    # Verify file exists and is accessible
    if (-not (Test-Path $FilePath)) {
        Write-Log "File not found for signing: $FilePath" "WARNING"
        return $false
    }
    # Check if file is locked by trying to open it exclusively
    try {
        $fileStream = [System.IO.File]::Open($FilePath, 'Open', 'Read', 'None')
        $fileStream.Close()
    }
    catch {
        Write-Log "File appears to be locked: $FilePath. Attempting advanced unlock..." "WARNING"
        
        # Try to identify and terminate processes locking this file
        try {
            # Use handle.exe if available to identify locking processes
            if (Get-Command "handle.exe" -ErrorAction SilentlyContinue) {
                $handleOutput = & handle.exe $FilePath 2>$null
                if ($handleOutput -and $handleOutput -match "pid: (\d+)") {
                    $lockingPids = [regex]::Matches($handleOutput, "pid: (\d+)") | ForEach-Object { $_.Groups[1].Value }
                    foreach ($procId in $lockingPids) {
                        try {
                            $process = Get-Process -Id $procId -ErrorAction SilentlyContinue
                            if ($process) {
                                Write-Log "Terminating process $($process.Name) (PID: $procId) that may be locking $FilePath" "INFO"
                                $process | Stop-Process -Force -ErrorAction SilentlyContinue
                                Start-Sleep -Seconds 1
                            }
                        }
                        catch {
                            # Ignore errors when killing processes
                        }
                    }
                }
            }
        }
        catch {
            # Ignore handle.exe errors
        }
        
        # Multiple attempts with increasing delays
        $unlockAttempts = 3
        for ($attempt = 1; $attempt -le $unlockAttempts; $attempt++) {
            Start-Sleep -Seconds ($attempt * 2)
            
            # Force garbage collection
            [System.GC]::Collect()
            [System.GC]::WaitForPendingFinalizers()
            [System.GC]::Collect()
            
            try {
                $fileStream = [System.IO.File]::Open($FilePath, 'Open', 'Read', 'None')
                $fileStream.Close()
                Write-Log "File unlocked after $attempt attempts: $FilePath" "SUCCESS"
                break
            }
            catch {
                if ($attempt -eq $unlockAttempts) {
                    Write-Log "File still locked after $unlockAttempts attempts: $FilePath. Skipping signing." "WARNING"
                    return $false
                }
            }
        }
    }
    
    # Use the new hardened signing function
    try {
        # Get certificate store information
        $certInfo = Get-SigningCertThumbprint
        $store = if ($certInfo) { $certInfo.Store } else { "LocalMachine" }
        
        Invoke-SignArtifact -Path $FilePath -Thumbprint $Thumbprint -Store $store
        Write-Log "Signed '$FilePath' successfully" "SUCCESS"
        return $true
    }
    catch {
        Write-Log "Signing failed for '$FilePath': $($_.Exception.Message)" "WARNING"
        return $false
    }
}
function signNuget {
    param(
        [Parameter(Mandatory)][string]$Nupkg,
        [string]$Thumbprint            #  optional override (matches existing caller)
    )
    if (-not (Test-Path $Nupkg)) {
        throw "NuGet package '$Nupkg' not found - cannot sign."
    }
    $tsa = 'http://timestamp.digicert.com'
    if (-not $Thumbprint) {
        $certInfo = Get-SigningCertThumbprint
        $Thumbprint = if ($certInfo) { $certInfo.Thumbprint } else { $null }
    }
    if (-not $Thumbprint) {
        Write-Log "No enterprise code-signing cert present - skipping NuGet repo sign." "WARNING"
        return $false
    }
    & nuget.exe sign `
    $Nupkg `
    -CertificateStoreName   My `
    -CertificateSubjectName 'EmilyCarrU Intune Windows Enterprise Certificate' `
    -Timestamper            $tsa
    if ($LASTEXITCODE) {
        Write-Log "nuget sign failed ($LASTEXITCODE) for '$Nupkg' - continuing build" "WARNING"
        return $false
    }
    Write-Log "NuGet repo signature complete." "SUCCESS"
    return $true
}
# Function to install MSI package with elevation
function Install-MsiPackage {
    param (
        [Parameter(Mandatory)]
        [string]$MsiPath
    )
    if (-not (Test-Path $MsiPath)) {
        Write-Log "MSI package not found at '$MsiPath'" "ERROR"
        return $false
    }
    Write-Log "Installing MSI package: $MsiPath" "INFO"
    # Check if we're already running as administrator
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")
    if ($isAdmin) {
        Write-Log "Already running with administrator privileges. Installing directly..." "INFO"
        try {
            # Ensure we use absolute path for MSI installation
            $absoluteMsiPath = (Resolve-Path $MsiPath).Path
            Write-Log "Installing MSI from absolute path: $absoluteMsiPath" "INFO"
            $installProcess = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i", "`"$absoluteMsiPath`"", "/qn", "/l*v", "`"$env:TEMP\cimian_install.log`"" -Wait -PassThru
            if ($installProcess.ExitCode -eq 0) {
                Write-Log "MSI installation completed successfully. Exit code: $($installProcess.ExitCode)" "SUCCESS"
                return $true
            } else {
                Write-Log "MSI installation failed with exit code $($installProcess.ExitCode). Check log: $env:TEMP\cimian_install.log" "ERROR"
                return $false
            }
        }
        catch {
            Write-Log "Failed to install MSI: $_" "ERROR"
            return $false
        }
    }
    # Not running as admin - try elevation methods
    Write-Log "Administrator privileges required for installation. Attempting elevation..." "INFO"
    # Method 1: Try sudo (if available)
    if (Get-Command "sudo" -ErrorAction SilentlyContinue) {
        Write-Log "Using 'sudo' for elevation..." "INFO"
        try {
            # Ensure we use absolute path for sudo as well
            $absoluteMsiPath = (Resolve-Path $MsiPath).Path
            Write-Log "Installing MSI from absolute path via sudo: $absoluteMsiPath" "INFO"
            $sudoArgs = @("msiexec.exe", "/i", "`"$absoluteMsiPath`"", "/qn", "/l*v", "`"$env:TEMP\cimian_install.log`"")
            $sudoProcess = Start-Process -FilePath "sudo" -ArgumentList $sudoArgs -Wait -PassThru
            if ($sudoProcess.ExitCode -eq 0) {
                Write-Log "MSI installation completed successfully via sudo." "SUCCESS"
                return $true
            } else {
                Write-Log "MSI installation via sudo failed with exit code $($sudoProcess.ExitCode)" "WARNING"
            }
        }
        catch {
            Write-Log "Failed to use sudo for installation: $_" "WARNING"
        }
    }
    # Method 2: Launch elevated PowerShell session
    Write-Log "Launching elevated PowerShell session for installation..." "INFO"
    try {
        # Ensure we use absolute path for MSI installation
        $absoluteMsiPath = (Resolve-Path $MsiPath).Path
        Write-Log "Installing MSI from absolute path via elevation: $absoluteMsiPath" "INFO"
        $installCommand = "Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i', '`"$absoluteMsiPath`"', '/qn', '/l*v', '`"$env:TEMP\cimian_install.log`"' -Wait -PassThru | ForEach-Object { if (`$_.ExitCode -eq 0) { Write-Host 'Installation completed successfully. Exit code:' `$_.ExitCode } else { Write-Host 'Installation failed with exit code' `$_.ExitCode; exit `$_.ExitCode } }"
        $elevatedProcess = Start-Process -FilePath "powershell.exe" -ArgumentList "-Command", $installCommand -Verb RunAs -Wait -PassThru
        if ($elevatedProcess.ExitCode -eq 0) {
            Write-Log "MSI installation completed successfully via elevated session. Exit code: $($elevatedProcess.ExitCode)" "SUCCESS"
            return $true
        } else {
            Write-Log "MSI installation via elevated session failed with exit code $($elevatedProcess.ExitCode). Check log: $env:TEMP\cimian_install.log" "ERROR"
            return $false
        }
    }
    catch {
        Write-Log "Failed to launch elevated session for installation: $_" "ERROR"
        return $false
    }
}
# Function to test if a file is locked
function Test-FileLocked {
    param (
        [Parameter(Mandatory)]
        [string]$FilePath
    )
    if (-not (Test-Path $FilePath)) {
        return $false
    }
    try {
        $fileStream = [System.IO.File]::Open($FilePath, 'Open', 'Read', 'None')
        $fileStream.Close()
        return $false  # File is not locked
    }
    catch {
        return $true   # File is locked
    }
}

# Function to diagnose file locking issues
function Get-FileLockInfo {
    param (
        [Parameter(Mandatory)]
        [string]$FilePath
    )
    
    if (-not (Test-Path $FilePath)) {
        Write-Log "File does not exist: $FilePath" "INFO"
        return
    }
    
    Write-Log "Diagnosing file lock for: $FilePath" "INFO"
    
    # Check basic file properties
    try {
        $fileInfo = Get-Item $FilePath
        Write-Log "File size: $($fileInfo.Length) bytes, Last write: $($fileInfo.LastWriteTime)" "INFO"
    }
    catch {
        Write-Log "Cannot access file properties: $_" "WARNING"
    }
    
    # Try to identify locking processes using built-in tools
    try {
        # Use openfiles command if available (requires elevated permissions)
        if (Get-Command "openfiles" -ErrorAction SilentlyContinue) {
            $openFiles = & openfiles /query /fo csv 2>$null | ConvertFrom-Csv
            $matchingFiles = $openFiles | Where-Object { $_."Open File (Path\executable)" -like "*$([System.IO.Path]::GetFileName($FilePath))*" }
            if ($matchingFiles) {
                Write-Log "Processes with open handles to this file:" "INFO"
                $matchingFiles | ForEach-Object {
                    Write-Log "  Process: $($_."Accessed By") - $($_."Open File (Path\executable)")" "INFO"
                }
            }
        }
    }
    catch {
        # Ignore openfiles errors (may not be available or require elevation)
    }
    
    # Check if Windows Defender or antivirus might be scanning
    try {
        $recentProcesses = Get-Process | Where-Object { 
            $_.ProcessName -match "(MsMpEng|WinDefend|av|anti|scan)" -and 
            $_.StartTime -gt (Get-Date).AddMinutes(-5) 
        }
        if ($recentProcesses) {
            Write-Log "Recent antivirus/security processes detected:" "INFO"
            $recentProcesses | ForEach-Object {
                Write-Log "  $($_.ProcessName) (PID: $($_.Id))" "INFO"
            }
        }
    }
    catch {
        # Ignore process enumeration errors
    }
}
# 
#  SIGNMSI MODE HANDLING
# 
if ($SignMSI) {
    Write-Log "SignMSI mode - will sign existing MSI files in release directory" "INFO"
    
    # Force signing to be enabled
    if ($NoSign) {
        Write-Log "Cannot use -NoSign with -SignMSI flag. Removing NoSign restriction." "WARNING"
        $NoSign = $false
    }
    $Sign = $true
    
    # Ensure signing tools and certificate are available
    Test-SignTool
    if (-not $Thumbprint) {
        $certInfo = Get-SigningCertThumbprint
        if (-not $certInfo) {
            Write-Log "No valid EmilyCarrU certificate with a private key found - aborting." "ERROR"
            exit 1
        }
        $Thumbprint = $certInfo.Thumbprint
        Write-Log "Auto-selected signing cert $Thumbprint from $($certInfo.Store) store for MSI signing" "INFO"
    } else {
        Write-Log "Using signing certificate $Thumbprint for MSI signing" "INFO"
    }
    $env:SIGN_THUMB = $Thumbprint
    
    # Find MSI files in release directory
    $msiFiles = Get-ChildItem -Path "release" -Filter "*.msi" -File -ErrorAction SilentlyContinue
    if ($msiFiles.Count -eq 0) {
        Write-Log "No MSI files found in release directory to sign." "ERROR"
        exit 1
    }
    
    Write-Log "Found $($msiFiles.Count) MSI file(s) to sign:" "INFO"
    foreach ($msi in $msiFiles) {
        Write-Log "  $($msi.Name)" "INFO"
    }
    
    # Sign each MSI file
    $signedCount = 0
    foreach ($msi in $msiFiles) {
        Write-Log "Signing MSI: $($msi.Name)" "INFO"
        $success = signPackage -FilePath $msi.FullName
        if ($success) {
            $signedCount++
            Write-Log "Successfully signed: $($msi.Name)" "SUCCESS"
        } else {
            Write-Log "Failed to sign: $($msi.Name)" "ERROR"
        }
    }
    
    Write-Log "SignMSI completed: $signedCount of $($msiFiles.Count) MSI files signed successfully." "SUCCESS"
    exit 0
}

# 
#  BUILD PROCESS STARTS
# 
# Early exit for binaries-only mode or single binary mode after basic setup
if ($Binaries -or $Binary) {
    if ($Binary) {
        Write-Log "Single binary mode: Starting minimal build process for '$Binary'..." "INFO"
    } else {
        Write-Log "Binaries-only mode: Starting minimal build process..." "INFO"
    }
    # Only do essential checks for binaries mode
    if (-not (Test-Command "go")) {
        Write-Log "Go is not installed or not in PATH. Please install Go manually." "ERROR"
        Write-Log "Download Go from: https://go.dev/dl/" "INFO"
        exit 1
    }
    # Set version for binaries (inline to avoid function dependency)
    $currentTime = Get-Date
    $fullVersion     = $currentTime.ToString("yyyy.MM.dd.HHmm")
    $semanticVersion = "{0}.{1}.{2}.{3}" -f $($currentTime.Year - 2000), $currentTime.Month, $currentTime.Day, $currentTime.ToString("HHmm")
    $env:RELEASE_VERSION   = $fullVersion
    $env:SEMANTIC_VERSION  = $semanticVersion
    Write-Log "RELEASE_VERSION set to $fullVersion" "INFO"
    Write-Log "SEMANTIC_VERSION set to $semanticVersion" "INFO"
    # Tidy modules
    go mod tidy
    go mod download
    # Build binaries
    Write-Log "Building binaries for x64 and arm64..." "INFO"
    
    $binaryDirs = Get-ChildItem -Directory -Path "./cmd"
    # Filter to specific binary if -Binary parameter is specified
    if ($Binary) {
        $binaryDirs = $binaryDirs | Where-Object { $_.Name -eq $Binary }
        if (-not $binaryDirs) {
            Write-Log "Specified binary '$Binary' not found in cmd directory." "ERROR"
            exit 1
        }
        Write-Log "Building only binary: $Binary" "INFO"
        # For single binary mode, only remove the specific binary files, not all release files
        foreach ($arch in @("x64", "arm64")) {
            $targetFile = "release\$arch\$Binary.exe"
            if (Test-Path $targetFile) {
                Remove-Item -Path $targetFile -Force -ErrorAction SilentlyContinue
            }
        }
    } else {
        Write-Log "Building all binaries" "INFO"
        # Create and clean release directories only when building all
        if (Test-Path "release") {
            Remove-Item -Path "release\*" -Recurse -Force
        } else {
            New-Item -ItemType Directory -Path "release" -Force | Out-Null
        }
    }
    # Ensure release directories exist
    if (-not (Test-Path "release")) {
        New-Item -ItemType Directory -Path "release" -Force | Out-Null
    }
    
    $archs = @("x64", "arm64")
    $goarchMap = @{
        "x64"   = "amd64"
        "arm64" = "arm64"
    }

    foreach ($arch in $archs) {
        $releaseArchDir = "release\$arch"
        if (-not (Test-Path $releaseArchDir)) {
            New-Item -ItemType Directory -Path $releaseArchDir | Out-Null
        }
        foreach ($dir in $binaryDirs) {
            $binaryName = $dir.Name
            
            # Detect Go sources for this binary (skip non-Go projects in cmd/)
            $goFiles = Get-ChildItem -Path $dir.FullName -Recurse -Filter "*.go" -File -ErrorAction SilentlyContinue
            $hasGoFiles = ($goFiles -and $goFiles.Count -gt 0)
            $submoduleGoMod = Join-Path $dir.FullName "go.mod"
            if (-not $hasGoFiles -and -not (Test-Path $submoduleGoMod)) {
                Write-Log "Skipping $binaryName for ${arch}: no Go sources found in $($dir.FullName)." "WARNING"
                continue
            }

            Write-Log "Building $binaryName for $arch..." "INFO"
            
            # Generate Windows version information for this binary
            New-VersionInfo -BinaryName $binaryName -BinaryPath $dir.FullName -Version $env:RELEASE_VERSION -SemanticVersion $env:SEMANTIC_VERSION
            
            # Check if this is a Go project
            $outputPath = "release\$arch\$binaryName.exe"
            if (Test-Path $submoduleGoMod) {
                # This is a Go submodule project
                Write-Log "Detected Go submodule for $binaryName" "INFO"
                # Get build info for Go projects
                try {
                    $branchName = (git rev-parse --abbrev-ref HEAD)
                } catch {
                    $branchName = "main"
                }
                try {
                    $revision = (git rev-parse HEAD)
                } catch {
                    $revision = "unknown"
                }
                $buildDate = Get-Date -Format s
                $ldflags = "-X github.com/windowsadmins/cimian/pkg/version.appName=$binaryName " +
                    "-X github.com/windowsadmins/cimian/pkg/version.version=$env:RELEASE_VERSION " +
                    "-X github.com/windowsadmins/cimian/pkg/version.branch=$branchName " +
                    "-X github.com/windowsadmins/cimian/pkg/version.buildDate=$buildDate " +
                    "-X github.com/windowsadmins/cimian/pkg/version.revision=$revision " +
                    "-X main.version=$env:RELEASE_VERSION"
                $env:GOARCH = $goarchMap[$arch]
                $env:GOOS = "windows"
                Push-Location $dir.FullName
                try {
                    go mod tidy
                    go mod download
                    go build -v -o "..\..\$outputPath" -ldflags="$ldflags" .
                    if ($LASTEXITCODE -ne 0) {
                        throw "Build failed for Go submodule $binaryName ($arch) with exit code $LASTEXITCODE."
                    }
                } catch {
                    Write-Log "Failed to build Go submodule $binaryName ($arch). Error: $_" "ERROR"
                    Pop-Location
                    exit 1
                }
                Pop-Location
            } else {
                # This is a standard Go project
                Write-Log "Detected standard Go project for $binaryName" "INFO"
                # Get build info for Go projects
                try {
                    $branchName = (git rev-parse --abbrev-ref HEAD)
                } catch {
                    $branchName = "main"
                }
                try {
                    $revision = (git rev-parse HEAD)
                } catch {
                    $revision = "unknown"
                }
                $buildDate = Get-Date -Format s
                $ldflags = "-X github.com/windowsadmins/cimian/pkg/version.appName=$binaryName " +
                    "-X github.com/windowsadmins/cimian/pkg/version.version=$env:RELEASE_VERSION " +
                    "-X github.com/windowsadmins/cimian/pkg/version.branch=$branchName " +
                    "-X github.com/windowsadmins/cimian/pkg/version.buildDate=$buildDate " +
                    "-X github.com/windowsadmins/cimian/pkg/version.revision=$revision " +
                    "-X main.version=$env:RELEASE_VERSION"
                $env:GOARCH = $goarchMap[$arch]
                $env:GOOS = "windows"
                try {
                    go build -v -o "$outputPath" -ldflags="$ldflags" "./cmd/$binaryName"
                    if ($LASTEXITCODE -ne 0) {
                        throw "Build failed for Go project $binaryName ($arch) with exit code $LASTEXITCODE."
                    }
                } catch {
                    Write-Log "Failed to build Go project $binaryName ($arch). Error: $_" "ERROR"
                    exit 1
                }
            }
            Write-Log "$binaryName ($arch) built successfully." "SUCCESS"
        }
    }
    # Sign binaries if signing is enabled
    if ($Sign) {
        Test-SignTool
        if ($Binary) {
            Write-Log "Signing '$Binary' binary..." "INFO"
        } else {
            Write-Log "Signing all EXEs in release folders..." "INFO"
        }
        foreach ($arch in $archs) {
            Get-ChildItem -Path "release\$arch\*.exe" | ForEach-Object {
                try {
                    $signResult = signPackage -FilePath $_.FullName
                    if ($signResult) {
                        Write-Log "Signed $($_.FullName) " "SUCCESS"
                    } else {
                        Write-Log "Failed to sign $($_.FullName) - continuing build" "WARNING"
                    }
                } catch {
                    Write-Log "Failed to sign $($_.FullName). $_ - continuing build" "WARNING"
                }
            }
        }
    }
    if ($Binary) {
        Write-Log "Single binary build completed successfully for '$Binary'." "SUCCESS"
    } else {
        Write-Log "Binaries-only build completed successfully." "SUCCESS"
    }
    
    # Clean up generated version resource files
    Remove-VersionResources
    
    Write-Log "Built binaries are available in:" "INFO"
    Get-ChildItem -Path "release" -Recurse -Filter "*.exe" | ForEach-Object {
        Write-Host "  $($_.FullName)"
    }
    exit 0
}

# 
#  PACKAGE-ONLY MODE EARLY EXIT
# 
# Early exit for package-only modes - skip all build steps and go directly to packaging
if ($PackageOnly -or $NupkgOnly -or $MsiOnly -or $PkgOnly) {
    Write-Log "Package-only mode detected - skipping build steps and going directly to packaging..." "INFO"
    
    # Validate that binaries exist before attempting to package
    $binaryDirs = @("release\x64", "release\arm64")
    $missingBinaries = @()
    foreach ($dir in $binaryDirs) {
        if (-not (Test-Path $dir)) {
            $missingBinaries += $dir
        }
    }
    
    if ($missingBinaries.Count -gt 0) {
        Write-Log "Cannot package - missing binary directories: $($missingBinaries -join ', ')" "ERROR"
        Write-Log "Run '.\build.ps1 -Binaries' first to build the binaries." "INFO"
        exit 1
    }
    
    # Basic tool validation for packaging
    $requiredTools = @()
    if (-not $MsiOnly -and -not $PkgOnly) {
        $requiredTools += @{ Name = "nuget.commandline"; Command = "nuget" }
    }
    if (-not $NupkgOnly -and -not $PkgOnly) {
        # Check for WiX
        $wixBin = Find-WiXBinPath
        if (-not $wixBin) {
            Write-Log "WiX Toolset is required for MSI packaging but not found." "ERROR"
            exit 1
        }
    }
    # For .pkg packaging, we need cimipkg which should be in release directories
    if ($PkgOnly -or $PackageOnly) {
        $cimipkgFound = $false
        foreach ($arch in @("x64", "arm64")) {
            if (Test-Path "release\$arch\cimipkg.exe") {
                $cimipkgFound = $true
                break
            }
        }
        if (-not $cimipkgFound) {
            Write-Log "cimipkg.exe is required for .pkg packaging but not found in release directories." "ERROR"
            Write-Log "Build cimipkg first with: .\build.ps1 -Binary cimipkg -Sign" "INFO"
            exit 1
        }
    }
    
    # Check for required tools
    foreach ($tool in $requiredTools) {
        if (-not (Test-Command $tool.Command)) {
            Write-Log "$($tool.Name) is required for packaging but not found." "ERROR"
            Write-Log "Please install $($tool.Name) manually before running package-only mode." "INFO"
            exit 1
        }
    }
    
    # Set up version environment variables
    $currentTime = Get-Date
    $fullVersion     = $currentTime.ToString("yyyy.MM.dd.HHmm")
    $semanticVersion = "{0}.{1}.{2}.{3}" -f $($currentTime.Year - 2000), $currentTime.Month, $currentTime.Day, $currentTime.ToString("HHmm")
    $env:RELEASE_VERSION   = $fullVersion
    $env:SEMANTIC_VERSION  = $semanticVersion
    Write-Log "RELEASE_VERSION set to $fullVersion" "INFO"
    Write-Log "SEMANTIC_VERSION set to $semanticVersion" "INFO"
    
    # Set mode-specific flags
    if ($NupkgOnly) {
        $SkipMSI = $true
        Write-Log "NUPKG-only mode: Will skip MSI and .pkg packaging" "INFO"
    } elseif ($MsiOnly) {
        # No special flags needed for MSI-only, NuGet packaging conditional is already set
        Write-Log "MSI-only mode: Will skip NuGet and .pkg packaging" "INFO"
    } elseif ($PkgOnly) {
        # PkgOnly uses direct binary payload, no MSI dependency needed
        $SkipMSI = $true
        Write-Log ".pkg-only mode: Will use direct binary payload in .pkg packages" "INFO"
    } elseif ($PackageOnly) {
        Write-Log "Package-only mode: Will create MSI, NuGet, and .pkg packages" "INFO"
    }
    
    # Jump directly to packaging section - we'll handle the specific mode logic there
    Write-Log "Package-only setup complete. Proceeding to packaging..." "SUCCESS"
    # Don't exit here - let it continue to the packaging section
}

# Define architecture arrays for both build and packaging modes
$archs = @("x64", "arm64")
$goarchMap = @{
    "x64"   = "amd64"
    "arm64" = "arm64"
}

# Function to clean up generated .syso files
function Remove-VersionResources {
    Write-Log "Cleaning up generated version resource files..." "INFO"
    Get-ChildItem -Path "cmd" -Recurse -Filter "resource.syso" | ForEach-Object {
        try {
            Remove-Item $_.FullName -Force
            Write-Log "Removed version resource: $($_.FullName)" "SUCCESS"
        }
        catch {
            Write-Log "Failed to remove version resource: $($_.FullName) - $_" "WARNING"
        }
    }
    
    # Also clean up any temporary version info JSON files
    Get-ChildItem -Path "." -Filter "versioninfo_*.json" | ForEach-Object {
        try {
            Remove-Item $_.FullName -Force
        }
        catch {
            # Ignore errors for temporary files
        }
    }
}

# 
#  SKIP BUILD STEPS FOR PACKAGE-ONLY MODES
# 
# Only run build steps if not in package-only mode
if (-not ($PackageOnly -or $NupkgOnly -or $MsiOnly -or $PkgOnly)) {
    Write-Log "Starting full build process..." "INFO"

    # Step 0: Clean Release Directory Before Build
    Write-Log "Cleaning existing release directory..." "INFO"
    if (Test-Path "release") {
        try {
            Remove-Item -Path "release\*" -Recurse -Force
            Write-Log "Existing release directory cleaned." "SUCCESS"
        }
        catch {
            Write-Log "Standard cleanup failed: $($_.Exception.Message)" "WARNING"
            Write-Log "Attempting cleanup with elevated permissions..." "INFO"
            
            # Try using sudo if available
            if (Get-Command "sudo" -ErrorAction SilentlyContinue) {
                try {
                    Write-Log "Using 'sudo' for elevated directory cleanup..." "INFO"
                    sudo powershell -Command "Remove-Item -Path '$(Get-Location)\release\*' -Recurse -Force"
                    Write-Log "Release directory cleaned successfully using sudo." "SUCCESS"
                }
                catch {
                    Write-Log "Sudo cleanup failed: $_" "WARNING"
                    # Continue to try elevation method
                    $sudoFailed = $true
                }
            } else {
                $sudoFailed = $true
            }
            
            # If sudo failed or isn't available, try elevated PowerShell
            if ($sudoFailed -ne $false) {
                try {
                    Write-Log "Launching elevated PowerShell session for directory cleanup..." "INFO"
                    $cleanupScript = "Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; Remove-Item -Path '$(Get-Location)\release\*' -Recurse -Force; Write-Host 'Cleanup completed successfully'"
                    $elevatedProcess = Start-Process -FilePath "powershell.exe" -ArgumentList "-Command", $cleanupScript -Verb RunAs -Wait -PassThru
                    if ($elevatedProcess.ExitCode -eq 0) {
                        Write-Log "Release directory cleaned successfully via elevated session." "SUCCESS"
                    } else {
                        Write-Log "Elevated cleanup failed with exit code $($elevatedProcess.ExitCode)" "ERROR"
                        Write-Log "You may need to manually delete locked files in the release directory." "WARNING"
                        Write-Log "Continuing with build - some files may remain..." "INFO"
                    }
                }
                catch {
                    Write-Log "Failed to launch elevated session: $_" "ERROR"
                    Write-Log "Continuing with build - some files may remain in release directory..." "WARNING"
                }
            }
        }
    }
    else {
        Write-Log "Release directory does not exist. Creating it..." "INFO"
        try {
            New-Item -ItemType Directory -Path "release" -Force | Out-Null
            Write-Log "Release directory created." "SUCCESS"
        }
        catch {
            Write-Log "Failed to create release directory. Error: $_" "ERROR"
            exit 1
        }
    }

# Step 1: Check for required tools
Write-Log "Checking for required tools..." "INFO"
$tools = @(
    @{ Name = "nuget.commandline"; Command = "nuget" },
    @{ Name = "go"; Command = "go" },
    @{ Name = "dotnet"; Command = "dotnet" }
)
# Add IntuneWinAppUtil only if IntuneWin flag is specified
if ($IntuneWin) {
    $tools += @{ Name = "intunewinapputil"; Command = "intunewinapputil" }
}
foreach ($tool in $tools) {
    $toolName    = $tool.Name
    $toolCommand = $tool.Command
    Write-Log "Checking if $toolName is installed..." "INFO"
    if (Test-Command $toolCommand) {
        Write-Log "$toolName is already installed and available via command '$toolCommand'." "SUCCESS"
    } else {
        Write-Log "$toolName is not installed or not in PATH." "ERROR"
        Write-Log "Please install $toolName manually before running the build." "INFO"
        exit 1
    }
}
Write-Log "Checking if WiX is installed..." "INFO"
$wixBin = Find-WiXBinPath
if ($wixBin) {
    if ($wixBin -eq "dotnet-tool") {
        Write-Log "WiX v6 is already installed as .NET global tool" "SUCCESS"
    } else {
        Write-Log "WiX v3 is already installed: $wixBin" "SUCCESS"
    }
} else {
    Write-Log "WiX is not installed. Installing WiX v6 via .NET tool..." "INFO"
    sudo dotnet tool install --global wix --version 6.0.1
    if ($LASTEXITCODE -eq 0) {
        Write-Log "WiX v6 installed successfully." "SUCCESS"
    } else {
        Write-Log "Failed to install WiX v6. Please install WiX manually." "ERROR"
        Write-Log "Install WiX v6: dotnet tool install --global wix" "INFO"
        Write-Log "Or install WiX v3 from: https://wixtoolset.org/" "INFO"
        exit 1
    }
}
Write-Log "Required tools check completed." "SUCCESS"
# Step 2: Ensure Go is available
Write-Log "Verifying Go installation..." "INFO"
if (-not (Test-Command "go")) {
    Write-Log "Go is not installed or not in PATH. Exiting..." "ERROR"
    exit 1
}
Write-Log "Go is available." "SUCCESS"
# Step 3: Locate and Add WiX Toolset bin to PATH
Write-Log "Locating WiX Toolset binaries..." "INFO"
$wixBinPath = Find-WiXBinPath
if ($null -ne $wixBinPath) {
    Write-Log "WiX Toolset bin directory found at $wixBinPath" "INFO"
    # Check if WiX bin path is already in PATH to prevent duplication
    $wixPathNormalized = [System.IO.Path]::GetFullPath($wixBinPath).TrimEnd('\')
    $pathEntries       = $env:PATH -split ";" | ForEach-Object { $_.Trim() }
    if (-not ($pathEntries -contains $wixPathNormalized)) {
        $env:PATH = "$wixBinPath;$env:PATH"
        Write-Log "Added WiX Toolset bin directory to PATH." "SUCCESS"
    }
    else {
        Write-Log "WiX Toolset bin directory already in PATH. Skipping addition." "INFO"
    }
}
else {
    Write-Log "WiX Toolset binaries not found. Ensure WiX is installed correctly." "ERROR"
    exit 1
}
# Step 4: Verify WiX Toolset installation
Write-Log "Verifying WiX Toolset installation..." "INFO"
$wixVerified = $false

# Check WiX version
$wixBinPath = Find-WiXBinPath
if ($wixBinPath -eq "dotnet-tool") {
    # For WiX v6, verify using dotnet wix
    try {
        $wixVersion = & dotnet tool list --global 2>$null | Select-String "^wix\s"
        if ($wixVersion) {
            Write-Log "WiX v6 (.NET tool) verified: $($wixVersion.ToString().Trim())" "SUCCESS"
            $wixVerified = $true
        }
    } catch {
        Write-Log "Failed to verify WiX v6 installation" "ERROR"
    }
} elseif (-not (Test-Command "candle.exe")) {
    Write-Log "WiX v3 candle.exe not found in PATH" "ERROR"
} else {
    Write-Log "WiX v3 verified" "SUCCESS"
    $wixVerified = $true
}

if (-not $wixVerified) {
    Write-Log "WiX Toolset is not installed correctly or not accessible. Exiting..." "ERROR"
    exit 1
}
Write-Log "WiX Toolset is available." "SUCCESS"
# Step 5: Set Up Go Environment Variables
Write-Log "Setting up Go environment variables..." "INFO"
Write-Log "Go environment variables set." "SUCCESS"
# Step 6: Prepare Release Version
function Set-Version {
    $currentTime = Get-Date
    $fullVersion     = $currentTime.ToString("yyyy.MM.dd.HHmm")
    $semanticVersion = "{0}.{1}.{2}.{3}" -f $($currentTime.Year - 2000), $currentTime.Month, $currentTime.Day, $currentTime.ToString("HHmm")
    $env:RELEASE_VERSION   = $fullVersion
    $env:SEMANTIC_VERSION  = $semanticVersion
    Write-Log "RELEASE_VERSION set to $fullVersion" "INFO"
    Write-Log "SEMANTIC_VERSION set to $semanticVersion" "INFO"
    # No longer update msi.wxs file here
}
Set-Version
# Step 7: Tidy and Download Go Modules
Write-Log "Tidying and downloading Go modules..." "INFO"
go mod tidy
go mod download
Write-Log "Go modules tidied and downloaded." "SUCCESS"
# Step 8: Build All Binaries
Write-Log "Building all binaries for x64 and arm64..." "INFO"
# Clean existing binaries first
$binaryDirs = Get-ChildItem -Directory -Path "./cmd"
foreach ($arch in $archs) {
    $releaseArchDir = "release\$arch"
    if (-not (Test-Path $releaseArchDir)) {
        New-Item -ItemType Directory -Path $releaseArchDir | Out-Null
    }
    foreach ($dir in $binaryDirs) {
        $binaryName = $dir.Name
        Write-Log "Building $binaryName for $arch..." "INFO"
        
        # Generate Windows version information for this binary
        New-VersionInfo -BinaryName $binaryName -BinaryPath $dir.FullName -Version $env:RELEASE_VERSION -SemanticVersion $env:SEMANTIC_VERSION
        
        # Check if this is a Go project
        $submoduleGoMod = Join-Path $dir.FullName "go.mod"
        $outputPath = "release\$arch\$binaryName.exe"
        if (Test-Path $submoduleGoMod) {
            # This is a Go submodule project
            Write-Log "Detected Go submodule for $binaryName (go.mod found). Building from submodule..." "INFO"
            # Determine which project file to use
            $projectFile = $null
            if (Test-Path $csharpAltProject) { 
                $projectFile = $csharpAltProject 
            } elseif (Test-Path $csharpRepoCleanProject) { 
                $projectFile = $csharpRepoCleanProject 
            } elseif (Test-Path $csharpCimiRepoCleanProject) { 
                $projectFile = $csharpCimiRepoCleanProject 
            } else { 
                $projectFile = $csharpProject 
            }
            # Map architecture for .NET runtime identifiers
            $dotnetRid = switch ($arch) {
                "x64" { "win-x64" }
                "arm64" { "win-arm64" }
            }
            Push-Location $dir.FullName
            try {
                # Detect target framework from project file
                $projectContent = Get-Content $projectFile -Raw
                $targetFrameworkMatch = [regex]::Match($projectContent, '<TargetFramework>(.*?)</TargetFramework>')
                $targetFramework = if ($targetFrameworkMatch.Success) { $targetFrameworkMatch.Groups[1].Value } else { "net8.0-windows" }
                Write-Log "Detected target framework: $targetFramework for $binaryName" "INFO"
                
                # Publish the C# project for specific architecture using hardcoded system dotnet path
                $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
                if (-not (Test-Path $dotnetPath)) {
                    # Fallback to PATH-based dotnet if system path doesn't exist
                    $dotnetPath = "dotnet"
                }
                & $dotnetPath publish $projectFile --configuration Release --runtime $dotnetRid --self-contained true --output "bin\Release\$targetFramework\$dotnetRid" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true --verbosity minimal
                if ($LASTEXITCODE -ne 0) {
                    throw "Publish failed for C# project $binaryName ($arch) with exit code $LASTEXITCODE."
                }
                # Find the built executable - look for the binary name first, then fallback
                $builtExePath = "bin\Release\$targetFramework\$dotnetRid\$binaryName.exe"
                if (-not (Test-Path $builtExePath)) {
                    # Fallback: look for cimistatus.exe for the special case
                    $builtExePath = "bin\Release\$targetFramework\$dotnetRid\cimistatus.exe"
                }
                if (-not (Test-Path $builtExePath)) {
                    # Final fallback: look for any .exe in the output directory
                    $builtExePath = Get-ChildItem "bin\Release\$targetFramework\$dotnetRid\*.exe" | Select-Object -First 1 -ExpandProperty FullName
                }
                if (-not $builtExePath -or -not (Test-Path $builtExePath)) {
                    throw "Could not find built executable for $binaryName ($arch)"
                }
                $builtExe = Get-Item $builtExePath
                
                # Copy to the expected output location
                # For C# projects, we need to copy ALL files from the output directory, not just the .exe
                $sourceDir = Split-Path $builtExe.FullName
                $destDir = Split-Path "..\..\$outputPath"
                
                try {
                    # Use robocopy to copy the entire directory for C# projects, excluding language packs
                    & robocopy $sourceDir $destDir /E /R:3 /W:1 /NP /NDL /NJH /NJS /XD "af-ZA" "am-ET" "ar-SA" "az-Latn-AZ" "be-BY" "bg-BG" "bn-BD" "bs-Latn-BA" "ca-ES" "cs" "cs-CZ" "da-DK" "de" "de-DE" "el-GR" "en-GB" "es" "es-ES" "es-MX" "et-EE" "eu-ES" "fa-IR" "fi-FI" "fil-PH" "fr" "fr-CA" "fr-FR" "gl-ES" "he-IL" "hi-IN" "hr-HR" "hu-HU" "id-ID" "is-IS" "it" "it-IT" "ja" "ja-JP" "ka-GE" "kk-KZ" "km-KH" "kn-IN" "ko" "ko-KR" "lo-LA" "lt-LT" "lv-LV" "mk-MK" "ml-IN" "ms-MY" "nb-NO" "nl-NL" "nn-NO" "pl" "pl-PL" "pt-BR" "pt-PT" "ro-RO" "ru" "ru-RU" "sk-SK" "sl-SI" "sq-AL" "sr-Latn-RS" "sv-SE" "sw-KE" "ta-IN" "te-IN" "th-TH" "tr" "tr-TR" "uk-UA" "uz-Latn-UZ" "vi-VN" "zh-Hans" "zh-Hant" | Out-Null
                    # Robocopy exit codes 0-7 are success
                    if ($LASTEXITCODE -le 7) {
                        Write-Log "$binaryName ($arch, C#) built successfully with all dependencies." "SUCCESS"
                    } else {
                        throw "Robocopy failed with exit code $LASTEXITCODE"
                    }
                } catch {
                    Write-Log "Robocopy failed, falling back to standard copy: $_" "WARNING"
                    Copy-Item -Path "$sourceDir\*" -Destination $destDir -Recurse -Force
                    Write-Log "$binaryName ($arch, C#) built successfully with all dependencies." "SUCCESS"
                }
            } catch {
                Write-Log "Failed to build C# project $binaryName ($arch). Error: $_" "ERROR"
                Pop-Location
                exit 1
            }
            Pop-Location
        } elseif (Test-Path $submoduleGoMod) {
            # This is a Go submodule project
            Write-Log "Detected Go submodule for $binaryName (go.mod found). Building from submodule..." "INFO"
            # Retrieve the current Git branch name
            try {
                $branchName = (git rev-parse --abbrev-ref HEAD)
            }
            catch {
                $branchName = "main"
            }
            $revision = "unknown"
            try {
                $revision = (git rev-parse HEAD)
            }
            catch {
                Write-Log "Unable to retrieve Git revision. Using 'unknown'." "WARNING"
            }
            $buildDate = Get-Date -Format s
            $ldflags = "-X github.com/windowsadmins/cimian/pkg/version.appName=$binaryName " +
                "-X github.com/windowsadmins/cimian/pkg/version.version=$env:RELEASE_VERSION " +
                "-X github.com/windowsadmins/cimian/pkg/version.branch=$branchName " +
                "-X github.com/windowsadmins/cimian/pkg/version.buildDate=$buildDate " +
                "-X github.com/windowsadmins/cimian/pkg/version.revision=$revision " +
                "-X main.version=$env:RELEASE_VERSION"
            $env:GOARCH = $goarchMap[$arch]
            $env:GOOS = "windows"
            Push-Location $dir.FullName
            try {
                go mod tidy
                go mod download
                # Special handling for GUI applications
                if ($binaryName -eq "cimistatus") {
                    # GUI application - enable CGO and use windowsgui flag to hide console
                    $env:CGO_ENABLED = "1"
                    go build -v -o "..\..\$outputPath" -ldflags="$ldflags -H windowsgui" .
                    $env:CGO_ENABLED = "0"  # Reset to disabled for other builds
                } else {
                    # Standard console application
                    go build -v -o "..\..\$outputPath" -ldflags="$ldflags" .
                }
                if ($LASTEXITCODE -ne 0) {
                    throw "Build failed for submodule $binaryName with exit code $LASTEXITCODE."
                }
                Write-Log "$binaryName ($arch, Go submodule) built successfully." "SUCCESS"
            }
            catch {
                Write-Log "Failed to build Go submodule $binaryName ($arch). Error: $_" "ERROR"
                Pop-Location
                exit 1
            }
            Pop-Location
        } else {
            # This is a standard Go project
            Write-Log "Building $binaryName ($arch) from main Go module..." "INFO"
            # Retrieve the current Git branch name
            try {
                $branchName = (git rev-parse --abbrev-ref HEAD)
            }
            catch {
                $branchName = "main"
            }
            $revision = "unknown"
            try {
                $revision = (git rev-parse HEAD)
            }
            catch {
                Write-Log "Unable to retrieve Git revision. Using 'unknown'." "WARNING"
            }
            $buildDate = Get-Date -Format s
            $ldflags = "-X github.com/windowsadmins/cimian/pkg/version.appName=$binaryName " +
                "-X github.com/windowsadmins/cimian/pkg/version.version=$env:RELEASE_VERSION " +
                "-X github.com/windowsadmins/cimian/pkg/version.branch=$branchName " +
                "-X github.com/windowsadmins/cimian/pkg/version.buildDate=$buildDate " +
                "-X github.com/windowsadmins/cimian/pkg/version.revision=$revision " +
                "-X main.version=$env:RELEASE_VERSION"
            $env:GOARCH = $goarchMap[$arch]
            $env:GOOS = "windows"
            try {
                go build -v -o "$outputPath" -ldflags="$ldflags" "./cmd/$binaryName"
                if ($LASTEXITCODE -ne 0) {
                    throw "Build failed for $binaryName ($arch) with exit code $LASTEXITCODE."
                }
                Write-Log "$binaryName ($arch, Go) built successfully." "SUCCESS"
            }
            catch {
                Write-Log "Failed to build Go project $binaryName ($arch). Error: $_" "ERROR"
                exit 1
            }
        }
    }
}
Write-Log "All binaries built for all architectures." "SUCCESS"

# Clean up generated version resource files
Remove-VersionResources
#  SIGN EVERY EXE (once) IN ITS OWN ARCH FOLDER 
if ($Sign) {
    Write-Log "Signing all EXEs in each release\<arch>\ folder..." "INFO"
    # Force a comprehensive garbage collection before signing to release any lingering file handles
    Write-Log "Performing garbage collection to release file handles before signing..." "INFO"
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    # Give the system a moment to fully release handles
    Start-Sleep -Seconds 3
    foreach ($arch in $archs) {
        Get-ChildItem -Path ("release\{0}\*.exe" -f $arch) | ForEach-Object {
            try {
                # Additional check: Skip if file doesn't exist (shouldn't happen but defensive)
                if (-not (Test-Path $_.FullName)) {
                    Write-Log "File not found for signing: $($_.FullName)" "WARNING"
                    return
                }
                # Force garbage collection and wait for finalizers to release file handles
                [System.GC]::Collect()
                [System.GC]::WaitForPendingFinalizers()
                [System.GC]::Collect()
                # Add a small delay to ensure file handles are fully released
                Start-Sleep -Milliseconds 500
                $signResult = signPackage -FilePath $_.FullName   #  uses $env:SIGN_THUMB, adds RFC 3161 timestamp
                if ($signResult) {
                    Write-Log "Signed $($_.FullName) " "SUCCESS"
                } else {
                    Write-Log "Failed to sign $($_.FullName) - continuing build" "WARNING"
                }
            }
            catch {
                Write-Log "Failed to sign $($_.FullName). $_ - continuing build" "WARNING"
            }
        }
    }
}
# Compress release directory with retry mechanism
Write-Log "Compressing release directory into release.zip..." "INFO"
try {
    # Force garbage collection to release any file handles before compression
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    [System.GC]::Collect()
    # Add a delay to ensure all file handles are released
    Start-Sleep -Seconds 2
    Compress-Archive -Path "release\*" -DestinationPath "release.zip" -Force
    Write-Log "Compressed all binaries into release.zip successfully." "SUCCESS"
}
catch {
    Write-Log "Failed to compress release directory: $($_.Exception.Message)" "ERROR"
    Write-Log "Attempting compression with temporary copy to avoid file locks..." "WARNING"
    try {
        # Create temporary directory for compression without file locks
        $tempCompressDir = "release_temp_compress"
        if (Test-Path $tempCompressDir) { Remove-Item $tempCompressDir -Recurse -Force }
        # Copy all files to temporary location
        Copy-Item "release" $tempCompressDir -Recurse
        Compress-Archive -Path "$tempCompressDir\*" -DestinationPath "release.zip" -Force
        Remove-Item $tempCompressDir -Recurse -Force
        Write-Log "Compressed binaries into release.zip using temporary copy method." "SUCCESS"
    }
    catch {
        Write-Log "All compression attempts failed: $($_.Exception.Message)" "ERROR"
        Write-Log "Continuing without zip archive..." "WARNING"
    }
}

} # End of build steps conditional for package-only modes

# Step 10: Build MSI Packages with WiX for both x64 and arm64 (unless -SkipMSI is specified)
if (-not $SkipMSI) {
    Write-Log "Building MSI packages with WiX for x64 and arm64..." "INFO"

    # Detect WiX version and set up paths
    $wixBinPath = Find-WiXBinPath
    if ($wixBinPath -eq "dotnet-tool") {
        Write-Log "Using WiX v6 (.NET tool)" "INFO"
        $useWixV5 = $true
    } elseif ($wixBinPath) {
        Write-Log "Using WiX v3 legacy: $wixBinPath" "INFO"
        $useWixV5 = $false
    $wixToolsetPath = $wixBinPath
    $candlePath = Join-Path $wixToolsetPath "candle.exe"
    $lightPath = Join-Path $wixToolsetPath "light.exe"
    $wixUtilExtension = Join-Path $wixToolsetPath "WixUtilExtension.dll"
    
    if (-not (Test-Path $wixToolsetPath)) {
        Write-Log "WiX Toolset path '$wixToolsetPath' not found. Exiting..." "ERROR"
        exit 1
    }
} else {
    Write-Log "No WiX installation found. Please install WiX v6 (.NET tool) or WiX v3 (legacy)." "ERROR"
    Write-Log "To install WiX v6: dotnet tool install --global wix" "INFO"
    Write-Log "To install WiX v3: choco install wixtoolset" "INFO"
    exit 1
}

$msiArchs = @("x64", "arm64")
foreach ($msiArch in $msiArchs) {
    $msiTempDir = "release\msi_$msiArch"
    if (Test-Path $msiTempDir) { Remove-Item -Path "$msiTempDir\*" -Recurse -Force }
    else { New-Item -ItemType Directory -Path $msiTempDir | Out-Null }
    
    # Copy correct binaries for this arch
    Write-Log "Preparing $msiArch binaries for MSI..." "INFO"
    Get-ChildItem -Path "release\$msiArch\*.exe" | ForEach-Object {
        Copy-Item $_.FullName $msiTempDir -Force
    }
    
    # Copy any other required files (e.g., config.yaml) if needed by WiX
    if (Test-Path "build\msi\config.yaml") {
        Copy-Item "build\msi\config.yaml" $msiTempDir -Force
    }
    
    # Copy scheduled task scripts for this architecture
    if (Test-Path "build\msi\install-tasks.ps1") {
        Copy-Item "build\msi\install-tasks.ps1" $msiTempDir -Force
    }
    if (Test-Path "build\msi\uninstall-tasks.ps1") {
        Copy-Item "build\msi\uninstall-tasks.ps1" $msiTempDir -Force
    }
    
    # Copy service management script for this architecture
    if (Test-Path "build\msi\manage-service.ps1") {
        Copy-Item "build\msi\manage-service.ps1" $msiTempDir -Force
    }
    
    # Copy diagnostic and verification scripts for this architecture
    if (Test-Path "build\msi\verify-installation.ps1") {
        Copy-Item "build\msi\verify-installation.ps1" $msiTempDir -Force
    }
    if (Test-Path "build\msi\diagnose-cimianwatcher.ps1") {
        Copy-Item "build\msi\diagnose-cimianwatcher.ps1" $msiTempDir -Force
    }
    
    # Build MSI for this arch
    $msiOutput = "release\Cimian-$msiArch-$env:RELEASE_VERSION.msi"
    
    try {
        if ($useWixV5) {
            # WiX v6 (.NET tool) build process
            Write-Log "Building MSI with WiX v6 for $msiArch..." "INFO"
            
            # Use the existing WiX project and build directly
            $wixProjPath = "build\msi\Cimian.wixproj"
            
            # Build with dotnet using the updated project
            $buildArgs = @(
                "build"
                $wixProjPath
                "-p:Platform=$msiArch"
                "-p:InstallerPlatform=$msiArch"
                "-p:ProductVersion=$env:SEMANTIC_VERSION"
                "-p:BinDir=../../release/msi_$msiArch"
                "-p:OutputName=Cimian-$msiArch"
                "--configuration", "Release"
                "--nologo"
                "--verbosity", "minimal"
            )
            
            Write-Log "Running: dotnet $($buildArgs -join ' ')" "INFO"
            & dotnet @buildArgs
            if ($LASTEXITCODE -ne 0) {
                throw "WiX v6 build failed for $msiArch with exit code $LASTEXITCODE"
            }
            
            # Find the output MSI in the build output (architecture-specific path)
            $builtMsi = "build\msi\bin\$msiArch\Release\Cimian-$msiArch.msi"
            if (Test-Path $builtMsi) {
                Move-Item $builtMsi $msiOutput -Force
                Write-Log "MSI package built with WiX v6 at $msiOutput." "SUCCESS"
            } else {
                # Try alternate paths with architecture awareness
                $altPaths = @(
                    "build\msi\bin\$msiArch\Release\Cimian-$msiArch.msi",
                    "build\msi\bin\$msiArch\Debug\Cimian-$msiArch.msi",
                    "build\msi\bin\x64\Release\Cimian-$msiArch.msi",  # fallback for x64
                    "build\msi\bin\Release\Cimian-$msiArch.msi",
                    "build\msi\bin\Debug\Cimian-$msiArch.msi",
                    "build\bin\$msiArch\Release\Cimian-$msiArch.msi",
                    "build\bin\x64\Release\Cimian-$msiArch.msi",
                    "build\bin\Release\Cimian-$msiArch.msi",
                    "build\bin\Debug\Cimian-$msiArch.msi",
                    "build\Cimian-$msiArch.msi"
                )
                $found = $false
                foreach ($altPath in $altPaths) {
                    if (Test-Path $altPath) {
                        Move-Item $altPath $msiOutput -Force
                        Write-Log "MSI package built with WiX v6 at $msiOutput (found at $altPath)." "SUCCESS"
                        $found = $true
                        break
                    }
                }
                if (-not $found) {
                    throw "WiX v6 build completed but output MSI not found. Expected at $builtMsi"
                }
            }
        } else {
            # WiX v3 legacy build process
            Write-Log "Compiling WiX source with candle for $msiArch..." "INFO"
            # Use argument splatting for candle
            $candleArgs = @(
                "-dBIN_DIR=$msiTempDir"
                "-dProductVersion=$env:SEMANTIC_VERSION"
                "-ext", $wixUtilExtension
                "-out", "build\msi.$msiArch.wixobj"
                "build\msi.wxs"
            )
            & $candlePath @candleArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Candle compilation failed for $msiArch with exit code $LASTEXITCODE"
            }
            Write-Log "Candle compilation completed successfully for $msiArch" "SUCCESS"
            
            Write-Log "Linking and creating MSI with light for $msiArch..." "INFO"
            # Use argument splatting for light
            $lightArgs = @(
                "-dBIN_DIR=$msiTempDir"
                "-dProductVersion=$env:SEMANTIC_VERSION"
                "-sice:ICE*"
                "-ext", $wixUtilExtension
                "-out", $msiOutput
                "build\msi.$msiArch.wixobj"
            )
            & $lightPath @lightArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Light linking failed for $msiArch with exit code $LASTEXITCODE"
            }
            Write-Log "Light linking completed successfully for $msiArch" "SUCCESS"
            Write-Log "MSI package built with WiX v3 at $msiOutput." "SUCCESS"
        }
        
        # Force garbage collection and wait for WiX processes to fully release file handles
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        [System.GC]::Collect()
        
        # Give WiX processes additional time to release file handles
        Write-Log "Waiting for WiX processes to release file handles..." "INFO"
        Start-Sleep -Seconds 2
    }
    catch {
        Write-Log "Failed to build MSI package for $msiArch. Error: $_" "ERROR"
        exit 1
    }
    
    # MSI package created - signing will be done at the end to avoid file locking issues
    Write-Log "MSI package created: $msiOutput" "SUCCESS"
    Write-Log "MSI signing will be performed after all packaging is complete to avoid file locks." "INFO"
    
    # Clean up temp folder
    Remove-Item -Path "$msiTempDir\*" -Recurse -Force
}
} else {
    Write-Log "Skipping MSI package building due to -SkipMSI flag" "INFO"
}

# Step 11: Prepare NuGet Packages for both x64 and arm64 (unless -MsiOnly or -PkgOnly is specified)
if (-not $MsiOnly -and -not $PkgOnly) {
    Write-Log "Preparing NuGet packages for x64 and arm64..." "INFO"
foreach ($arch in $archs) {
    $pkgTempDir  = "release\nupkg_$arch"
    $nuspecPath  = "build\nupkg\nupkg.$arch.nuspec"
    $nupkgOut    = "release\CimianTools-$arch-$env:RELEASE_VERSION.nupkg"
    
    # Verify the architecture-specific nuspec exists
    if (-not (Test-Path $nuspecPath)) {
        Write-Log "Architecture-specific nuspec not found: $nuspecPath" "ERROR"
        exit 1
    }
    
    # workspace
    if (Test-Path $pkgTempDir) { Remove-Item $pkgTempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $pkgTempDir -Force | Out-Null
    
    # payload (Program Files and ProgramData structure) - not needed for direct reference
    # scripts - not needed for direct reference
    
    # common payload - only copy README if needed
    if (-not (Test-Path "README.md")) { 
        'Cimian command-line tools.' | Set-Content (Join-Path $pkgTempDir 'README.md') 
    }
    
    # Use the pre-created architecture-specific nuspec directly with version substitution
    $nuspecContent = Get-Content $nuspecPath -Raw
    $nuspecWithVersion = $nuspecContent -replace '\{\{VERSION\}\}', $env:SEMANTIC_VERSION
    $tempNuspecPath = "build\nupkg\temp_nupkg_$arch.nuspec"
    $nuspecWithVersion | Set-Content $tempNuspecPath
    
    # pack - use the build/nupkg directory as base path since the nuspec paths are relative to build/nupkg/
    nuget pack $tempNuspecPath -OutputDirectory "release" -BasePath "build\nupkg" -NoDefaultExcludes | Out-Null
    $built = Get-ChildItem "release" -Filter '*.nupkg' |
             Sort-Object LastWriteTime -Desc | Select-Object -First 1
    
    # Retry Move-Item to handle file locking issues
    $retryCount = 3
    $moved = $false
    for ($i = 0; $i -lt $retryCount; $i++) {
        try {
            Move-Item $built.FullName $nupkgOut -Force -ErrorAction Stop
            $moved = $true
            break
        } catch {
            Write-Log "Attempt $($i+1): Failed to move package file - $_" "WARNING"
            Start-Sleep -Seconds 2
        }
    }
    if (-not $moved) {
        Write-Log "Failed to move $($built.FullName) to $nupkgOut after $retryCount attempts" "ERROR"
        throw "Unable to complete package build due to file locking"
    }
    if ($Sign) {
        $signResult = signNuget $nupkgOut
        if ($signResult) {
            Write-Log "NuGet package signed successfully: $nupkgOut" "SUCCESS"
        } else {
            Write-Log "Failed to sign NuGet package: $nupkgOut - continuing build" "WARNING"
        }
    }
    # cleanup
    Remove-Item $pkgTempDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $tempNuspecPath -Force -ErrorAction SilentlyContinue
    Write-Log "$arch NuGet ready  $nupkgOut" "SUCCESS"
}
# Step 11.1: No need to revert nuspec files since we use architecture-specific ones
Write-Log "NuGet packaging for all architectures completed." "SUCCESS"

} else {
    Write-Log "Skipping NuGet package creation due to -MsiOnly or -PkgOnly flag" "INFO"
}

# Step 11.5: Create .pkg Packages for both x64 and arm64 (Modern package format)
if (-not $MsiOnly -and -not $NupkgOnly) {
    Write-Log "Creating .pkg packages for x64 and arm64..." "INFO"
    
    # Ensure cimipkg tool is available
    if (-not (Test-Path "release\x64\cimipkg.exe") -and -not (Test-Path "release\arm64\cimipkg.exe")) {
        Write-Log "cimipkg.exe not found in release directories. .pkg packages cannot be created." "WARNING"
        Write-Log "Build cimipkg first with: .\build.ps1 -Binary cimipkg -Sign" "INFO"
    } else {
        foreach ($arch in $archs) {
            $binariesDir = "release\$arch"
            $cimipkgPath = "release\$arch\cimipkg.exe"
            
            # Skip if binaries directory doesn't exist for this architecture
            if (-not (Test-Path $binariesDir)) {
                Write-Log "Binaries directory not found for $arch architecture: $binariesDir" "WARNING"
                continue
            }
            
            # Skip if cimipkg doesn't exist for this architecture
            if (-not (Test-Path $cimipkgPath)) {
                Write-Log "cimipkg.exe not found for $arch architecture: $cimipkgPath" "WARNING"
                continue
            }
            
            # Create temporary .pkg build directory
            $pkgTempDir = "release\pkg_$arch"
            if (Test-Path $pkgTempDir) { Remove-Item $pkgTempDir -Recurse -Force }
            New-Item -ItemType Directory -Path $pkgTempDir -Force | Out-Null
            
            # Create payload directory and copy all binaries
            $payloadDir = Join-Path $pkgTempDir "payload"
            New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
            
            Write-Log "Copying CimianTools binaries for $arch architecture to .pkg payload..." "INFO"
            $expectedExecutables = @(
                'cimiwatcher.exe','managedsoftwareupdate.exe','cimitrigger.exe','cimistatus.exe',
                'cimiimport.exe','cimipkg.exe','makecatalogs.exe','makepkginfo.exe','manifestutil.exe','repoclean.exe'
            )
            
            $missingExecutables = @()
            foreach ($exe in $expectedExecutables) {
                $sourcePath = Join-Path $binariesDir $exe
                $destPath = Join-Path $payloadDir $exe
                
                if (Test-Path $sourcePath) {
                    Copy-Item $sourcePath $destPath -Force
                    Write-Log "Copied $exe to .pkg payload" "INFO"
                } else {
                    $missingExecutables += $exe
                }
            }
            
            if ($missingExecutables.Count -gt 0) {
                Write-Log "Missing executables for $arch architecture: $($missingExecutables -join ', ')" "WARNING"
                Write-Log "Continuing with available executables..." "WARNING"
            }
            
            # Create scripts directory and copy postinstall script
            $scriptsDir = Join-Path $pkgTempDir "scripts"
            New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null
            
            $postinstallTemplatePath = "build\pkg\postinstall.ps1"
            $postinstallPath = Join-Path $scriptsDir "postinstall.ps1"
            if (Test-Path $postinstallTemplatePath) {
                $postinstallTemplate = Get-Content $postinstallTemplatePath -Raw
                $postinstallContent = $postinstallTemplate -replace '\{\{VERSION\}\}', $env:SEMANTIC_VERSION
                $postinstallContent | Set-Content $postinstallPath -Encoding UTF8
                Write-Log "Created postinstall.ps1 script in scripts directory for .pkg" "INFO"
            } else {
                Write-Log "Postinstall template not found: $postinstallTemplatePath" "WARNING"
            }
            
            # Copy preinstall script
            $preinstallTemplatePath = "build\pkg\preinstall.ps1"
            $preinstallPath = Join-Path $scriptsDir "preinstall.ps1"
            if (Test-Path $preinstallTemplatePath) {
                $preinstallTemplate = Get-Content $preinstallTemplatePath -Raw
                $preinstallContent = $preinstallTemplate -replace '\{\{VERSION\}\}', $env:SEMANTIC_VERSION
                $preinstallContent | Set-Content $preinstallPath -Encoding UTF8
                Write-Log "Created preinstall.ps1 script in scripts directory for .pkg" "INFO"
            } else {
                Write-Log "Preinstall template not found: $preinstallTemplatePath" "WARNING"
            }
            
            # Create build-info.yaml from template
            # Use RELEASE_VERSION (YYYY.MM.DD.HHMM format) for build-info.yaml
            # This ensures cimiimport extracts the correct version when importing to the repo
            $buildInfoTemplate = Get-Content "build\pkg\build-info.yaml" -Raw
            $buildInfoContent = $buildInfoTemplate -replace '\{\{VERSION\}\}', $env:RELEASE_VERSION
            $buildInfoContent = $buildInfoContent -replace '\{\{ARCHITECTURE\}\}', $arch
            $buildInfoPath = Join-Path $pkgTempDir "build-info.yaml"
            $buildInfoContent | Set-Content $buildInfoPath -Encoding UTF8
            
            Write-Log "Creating .pkg package for $arch architecture..." "INFO"
            
            # Build the .pkg package using cimipkg
            try {
                # Use the cimipkg tool to create the package (pass the build directory)
                $cimipkgArgs = @()
                
                # Add verbose flag if needed
                $cimipkgArgs += "--verbose"
                
                # Add the project directory as the last argument
                $cimipkgArgs += $pkgTempDir
                
                # Execute cimipkg from the parent directory
                $process = Start-Process -FilePath $cimipkgPath -ArgumentList $cimipkgArgs -Wait -NoNewWindow -PassThru
                
                if ($process.ExitCode -eq 0) {
                    Write-Log ".pkg package created successfully for ${arch}" "SUCCESS"
                    
                    # Look for the created .pkg file in the build subdirectory
                    $buildDir = Join-Path $pkgTempDir "build"
                    if (Test-Path $buildDir) {
                        $createdPkgFiles = Get-ChildItem -Path $buildDir -Filter "*.pkg"
                        foreach ($pkgFile in $createdPkgFiles) {
                            $pkgSize = $pkgFile.Length
                            Write-Log ".pkg package created: $($pkgFile.Name) ($([math]::Round($pkgSize / 1MB, 2)) MB)" "INFO"
                            
                            # Move to release directory with expected naming
                            $expectedName = "CimianTools-$arch-$env:RELEASE_VERSION.pkg"
                            $targetPath = "release\$expectedName"
                            Copy-Item $pkgFile.FullName $targetPath -Force
                            Write-Log "Moved .pkg package to: $expectedName" "INFO"
                        }
                    } else {
                        Write-Log "Build directory not found after cimipkg execution: $buildDir" "WARNING"
                    }
                } else {
                    Write-Log "Failed to create .pkg package for ${arch} (exit code: $($process.ExitCode))" "ERROR"
                }
                
            } catch {
                Write-Log "Error creating .pkg package for ${arch}: $_" "ERROR"
            } finally {
                # Clean up temporary directory
                Remove-Item $pkgTempDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        Write-Log ".pkg packaging for all architectures completed." "SUCCESS"
    }
} else {
    Write-Log "Skipping .pkg package creation due to -MsiOnly or -NupkgOnly flag" "INFO"
}

# Step 12: Prepare IntuneWin Packages for both x64 and arm64 (Optional)
if ($IntuneWin) {
    Write-Log "IntuneWin flag detected - preparing IntuneWin packages for x64 and arm64..." "INFO"
    foreach ($arch in $archs) {
        $msiFile = "release\Cimian-$arch-$env:RELEASE_VERSION.msi"
        $outputFolder = "release"
        $intunewinOutput = "release\Cimian-$arch-$env:RELEASE_VERSION.intunewin"
        if (-not (Test-Path $msiFile)) {
            Write-Log "Setup file '$msiFile' does not exist. Skipping IntuneWin package preparation for $arch." "WARNING"
            continue
        }
        Write-Log "Creating IntuneWin package for $arch architecture..." "INFO"
        # Check if IntuneWinAppUtil is available
        if (-not (Get-Command "IntuneWinAppUtil.exe" -ErrorAction SilentlyContinue)) {
            Write-Log "IntuneWinAppUtil.exe not found. Ensure it's installed via Chocolatey." "ERROR"
            exit 1
        }
        # Remove any existing .intunewin files that might conflict
        $existingIntunewin = Get-ChildItem -Path $outputFolder -Filter "Cimian-$arch-*.intunewin" -ErrorAction SilentlyContinue
        if ($existingIntunewin) {
            Write-Log "Removing existing .intunewin files for $arch to prevent conflicts..." "INFO"
            $existingIntunewin | Remove-Item -Force
        }
        try {
            # Create the IntuneWin package directly with proper output handling
            Write-Log "Running IntuneWinAppUtil for $arch MSI..." "INFO"
            # Use Start-Process with proper output redirection to avoid terminal issues
            $intuneArgs = @(
                "-c", "`"$outputFolder`""
                "-s", "`"$msiFile`""
                "-o", "`"$outputFolder`""
                "-q"  # Quiet mode
            )
            $intuneProcess = Start-Process -FilePath "IntuneWinAppUtil.exe" -ArgumentList $intuneArgs -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\intune_$arch.log" -RedirectStandardError "$env:TEMP\intune_$arch_err.log"
            if ($intuneProcess.ExitCode -eq 0) {
                Write-Log "IntuneWinAppUtil completed successfully for $arch." "SUCCESS"
                # Find the generated .intunewin file and rename it if needed
                $generatedFile = Get-ChildItem -Path $outputFolder -Filter "*.intunewin" |
                                 Where-Object { $_.Name -like "*$([System.IO.Path]::GetFileNameWithoutExtension($msiFile))*" } |
                                 Sort-Object LastWriteTime -Descending |
                                 Select-Object -First 1
                if ($generatedFile -and $generatedFile.FullName -ne $intunewinOutput) {
                    Write-Log "Renaming generated file to match expected naming convention..." "INFO"
                    Move-Item $generatedFile.FullName $intunewinOutput -Force
                }
                if (Test-Path $intunewinOutput) {
                    Write-Log "IntuneWin package created successfully: $intunewinOutput" "SUCCESS"
                } else {
                    Write-Log "IntuneWin package was created but not found at expected location." "WARNING"
                }
            } else {
                Write-Log "IntuneWinAppUtil failed for $arch with exit code $($intuneProcess.ExitCode)" "ERROR"
                # Show error details if available
                if (Test-Path "$env:TEMP\intune_$arch_err.log") {
                    $errorContent = Get-Content "$env:TEMP\intune_$arch_err.log" -Raw
                    if ($errorContent.Trim()) {
                        Write-Log "Error details: $errorContent" "ERROR"
                    }
                }
                exit 1
            }
        }
        catch {
            Write-Log "IntuneWin package preparation failed for $arch. Error: $_" "ERROR"
            exit 1
        }
        finally {
            # Clean up temporary log files
            Remove-Item "$env:TEMP\intune_$arch.log" -ErrorAction SilentlyContinue
            Remove-Item "$env:TEMP\intune_$arch_err.log" -ErrorAction SilentlyContinue
        }
    }
    Write-Log "IntuneWin packaging completed." "SUCCESS"
} else {
    Write-Log "Skipping IntuneWin package creation. Use -IntuneWin flag to create .intunewin packages." "INFO"
}
# Step 13: Verify Generated Files
Write-Log "Verifying generated files..." "INFO"
$generatedFiles = Get-ChildItem -Path "release\*"
if ($generatedFiles.Count -eq 0) {
    Write-Log "No files generated in release folder! Exiting..." "ERROR"
    exit 1
}
else {
    Write-Log "Generated files:" "INFO"
    $generatedFiles | ForEach-Object { Write-Host $_.FullName }
}
Write-Log "Verification complete." "SUCCESS"

# Step 14.5: Sign MSI packages (moved to end to avoid file locking issues)
if ($Sign -and -not $SkipMSI) {
    Write-Log "Signing MSI packages after all build processes are complete..." "INFO"
    
    # Find MSI files in release directory
    $msiFiles = Get-ChildItem -Path "release" -Filter "*.msi" -File -ErrorAction SilentlyContinue
    if ($msiFiles.Count -eq 0) {
        Write-Log "No MSI files found to sign in release directory." "INFO"
    } else {
        Write-Log "Found $($msiFiles.Count) MSI file(s) to sign:" "INFO"
        foreach ($msi in $msiFiles) {
            Write-Log "  $($msi.Name)" "INFO"
        }
        
        # Wait for any remaining file handles to be released
        Write-Log "Waiting for all file handles to be released before signing..." "INFO"
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        Start-Sleep -Seconds 10
        
        # Sign each MSI file
        $signedCount = 0
        foreach ($msi in $msiFiles) {
            Write-Log "Signing MSI: $($msi.Name)" "INFO"
            $success = signPackage -FilePath $msi.FullName
            if ($success) {
                $signedCount++
                Write-Log "Successfully signed: $($msi.Name)" "SUCCESS"
            } else {
                Write-Log "Failed to sign: $($msi.Name)" "WARNING"
            }
        }
        
        Write-Log "MSI signing completed: $signedCount of $($msiFiles.Count) MSI files signed successfully." "SUCCESS"
    }
}

Write-Log "Build and packaging process completed successfully." "SUCCESS"
# Step 15: Install .pkg Package if requested
if ($Install) {
    Write-Log "Install flag detected. Attempting to install .pkg package..." "INFO"
    # Determine the current architecture for installation
    $currentArch = if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64") { "x64" } else { "arm64" }
    
    # Check if sudo installer is available
    if (-not (Get-Command "sudo" -ErrorAction SilentlyContinue)) {
        Write-Log "sudo command not found. Installing gsudo for package installation..." "INFO"
        try {
            Install-Chocolatey
            choco install gsudo --no-progress --yes --force | Out-Null
            # Refresh PATH to ensure sudo is available
            if ($env:ChocolateyInstall -and (Test-Path "$env:ChocolateyInstall\helpers\refreshenv.cmd")) {
                & "$env:ChocolateyInstall\helpers\refreshenv.cmd"
            }
            # Add common gsudo paths
            $possibleSudoPaths = @(
                "C:\ProgramData\chocolatey\bin",
                "C:\Program Files\gsudo\Current",
                "$env:ProgramFiles\gsudo\Current"
            )
            foreach ($p in $possibleSudoPaths) {
                if (Test-Path (Join-Path $p "sudo.exe")) {
                    $env:Path = "$p;$env:Path"
                    break
                }
            }
            if (-not (Get-Command "sudo" -ErrorAction SilentlyContinue)) {
                Write-Log "Failed to install or locate sudo command. Cannot proceed with .pkg installation." "ERROR"
                exit 1
            }
            Write-Log "sudo (gsudo) installed successfully." "SUCCESS"
        }
        catch {
            Write-Log "Failed to install sudo (gsudo): $_" "ERROR"
            exit 1
        }
    }
    
    # Check if installer command is available (sbin-installer)
    if (-not (Get-Command "installer" -ErrorAction SilentlyContinue)) {
        Write-Log "installer command not found. Checking for sbin-installer..." "INFO"
        # Check common installation paths for sbin-installer
        $possibleInstallerPaths = @(
            "C:\Program Files\sbin-installer\installer.exe",
            "C:\Program Files (x86)\sbin-installer\installer.exe",
            "C:\sbin-installer\installer.exe",
            ".\installer.exe"
        )
        $installerFound = $false
        foreach ($path in $possibleInstallerPaths) {
            if (Test-Path $path) {
                # Add to PATH temporarily
                $installerDir = Split-Path $path -Parent
                $env:Path = "$installerDir;$env:Path"
                $installerFound = $true
                Write-Log "Found sbin-installer at: $path" "SUCCESS"
                break
            }
        }
        if (-not $installerFound) {
            Write-Log "sbin-installer not found. Please install sbin-installer to use .pkg package installation." "ERROR"
            Write-Log "You can install it from: https://github.com/microsoft/sbin-installer" "INFO"
            Write-Log "Fallback: You can manually extract and install the .pkg file (it's a ZIP archive)." "INFO"
            exit 1
        }
    }
    
    # If RELEASE_VERSION is not set, try to detect it from existing .pkg files
    if (-not $env:RELEASE_VERSION) {
        $existingPkg = Get-ChildItem -Path "release" -Filter "CimianTools-$currentArch-*.pkg" | Select-Object -First 1
        if ($existingPkg) {
            # Extract version from filename: CimianTools-arm64-2025.08.22.1948.pkg -> 2025.08.22.1948
            if ($existingPkg.Name -match "CimianTools-$currentArch-(.+)\.pkg") {
                $env:RELEASE_VERSION = $matches[1]
                Write-Log "Detected RELEASE_VERSION from existing .pkg: $env:RELEASE_VERSION" "INFO"
            }
        }
    }
    
    $pkgToInstall = "release\CimianTools-$currentArch-$env:RELEASE_VERSION.pkg"
    # Check if the .pkg for current architecture exists
    if (-not (Test-Path $pkgToInstall)) {
        Write-Log ".pkg package for current architecture ($currentArch) not found at '$pkgToInstall'" "WARNING"
        
        # Try to find any .pkg for current architecture with any version
        $currentArchPkgs = Get-ChildItem -Path "release" -Filter "CimianTools-$currentArch-*.pkg"
        if ($currentArchPkgs.Count -gt 0) {
            $pkgToInstall = $currentArchPkgs[0].FullName
            Write-Log "Found .pkg for current architecture: $($currentArchPkgs[0].Name)" "INFO"
        } else {
            # Try the other architecture as fallback
            $fallbackArch = if ($currentArch -eq "x64") { "arm64" } else { "x64" }
            $fallbackPkgs = Get-ChildItem -Path "release" -Filter "CimianTools-$fallbackArch-*.pkg"
            if ($fallbackPkgs.Count -gt 0) {
                $pkgToInstall = $fallbackPkgs[0].FullName
                Write-Log "Using fallback .pkg for $fallbackArch architecture: $($fallbackPkgs[0].Name)" "INFO"
            } else {
                Write-Log "No .pkg packages found for installation. Available files in release:" "ERROR"
                Get-ChildItem -Path "release" -Filter "*.pkg" | ForEach-Object {
                    Write-Log "  $($_.Name)" "INFO"
                }
                Write-Log "Installation aborted." "ERROR"
                exit 1
            }
        }
    }
    
    # Pre-installation: Stop any running Cimian services to prevent conflicts
    Write-Log "Pre-installation: Stopping existing Cimian services..." "INFO"
    try {
        # Stop the service using multiple methods
        $services = @("CimianWatcher", "Cimian Bootstrap File Watcher")
        foreach ($serviceName in $services) {
            try {
                $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                if ($service -and $service.Status -eq "Running") {
                    Write-Log "Stopping service: $serviceName" "INFO"
                    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
                    # Wait a moment for service to stop
                    Start-Sleep -Seconds 2
                }
            }
            catch {
                # Ignore service stop errors
            }
        }
        
        # Force kill any remaining processes
        $processes = @("cimiwatcher", "cimistatus", "managedsoftwareupdate")
        foreach ($processName in $processes) {
            try {
                Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            }
            catch {
                # Ignore process kill errors
            }
        }
        
        Write-Log "Pre-installation cleanup completed." "SUCCESS"
        Start-Sleep -Seconds 3  # Give Windows time to release file handles
    }
    catch {
        Write-Log "Pre-installation cleanup had some issues, but continuing: $_" "WARNING"
    }
    
    # Attempt to install the .pkg package using sudo installer
    Write-Log "Installing .pkg package: $pkgToInstall" "INFO"
    try {
        # Convert to absolute path for installer
        $absolutePkgPath = (Resolve-Path $pkgToInstall).Path
        Write-Log "Installing .pkg from absolute path: $absolutePkgPath" "INFO"
        
        # Use sudo installer to install the .pkg package
        $installArgs = @("installer", "--pkg", $absolutePkgPath)
        $installProcess = Start-Process -FilePath "sudo" -ArgumentList $installArgs -Wait -PassThru -NoNewWindow
        
        if ($installProcess.ExitCode -eq 0) {
            Write-Log "Cimian has been successfully installed from .pkg package!" "SUCCESS"
            Write-Log "You can now use the Cimian tools from C:\Program Files\Cimian\" "INFO"
            
            # Verify installation by checking if binaries exist
            $installDir = "C:\Program Files\Cimian"
            if (Test-Path $installDir) {
                $installedFiles = Get-ChildItem $installDir -Filter "*.exe" | Select-Object -First 5
                Write-Log "Installed binaries:" "INFO"
                $installedFiles | ForEach-Object { Write-Log "  $($_.Name)" "INFO" }
                if ($installedFiles.Count -gt 5) {
                    Write-Log "  ... and $($installedFiles.Count - 5) more binaries" "INFO"
                }
            }
        } else {
            Write-Log "Installation failed with exit code $($installProcess.ExitCode)" "ERROR"
            Write-Log "Check the installer output above for details." "ERROR"
            exit 1
        }
    }
    catch {
        Write-Log "Failed to install .pkg package: $_" "ERROR"
        Write-Log "You can manually extract and install the .pkg file (it's a ZIP archive with payload and scripts directories)." "INFO"
        exit 1
    }
}
# Step 14: Clean Up Temporary Files
function Remove-TempFiles {
    param ([string[]]$Files)
    foreach ($file in $Files) {
        if (Test-Path $file) {
            try {
                Remove-Item -Path $file -Force
                Write-Log "Temporary file '$file' deleted successfully." "SUCCESS"
            }
            catch {
                Write-Log "Failed to delete '$file'. Error: $_" "WARNING"
            }
        }
        else {
            Write-Log "'$file' does not exist. Skipping deletion." "INFO"
        }
    }
}
# Use Remove-TempFiles for cleanup
Write-Log "Cleaning up temporary files..." "INFO"
$temporaryFiles = @("release.zip", "build\msi.msi", "build\msi.wixobj", "build\msi.wixpdb")
Remove-TempFiles -Files $temporaryFiles

# Clean up temporary MSI staging directories
$tempDirectories = @("release\msi_x64", "release\msi_arm64")
foreach ($dir in $tempDirectories) {
    if (Test-Path $dir) {
        try {
            Remove-Item $dir -Recurse -Force
            Write-Log "Temporary directory '$dir' deleted successfully." "SUCCESS"
        }
        catch {
            Write-Log "Failed to delete temporary directory '$dir'. Error: $_" "WARNING"
        }
    }
    else {
        Write-Log "Temporary directory '$dir' does not exist. Skipping deletion." "INFO"
    }
}

# Clean up .wixpdb files in release\
Get-ChildItem -Path "release" -Filter "*.wixpdb" -File | ForEach-Object {
    try {
        Remove-Item $_.FullName -Force
        Write-Log "Temporary file '$($_.FullName)' deleted successfully." "SUCCESS"
    }
    catch {
        Write-Log "Failed to delete '$($_.FullName)'. Error: $_" "WARNING"
    }
}
Write-Log "Temporary files cleanup completed." "SUCCESS"
# Step 2.1: MinGW is no longer needed since CGO is disabled
# Write-Log "Checking if mingw is already installed..." "INFO"
Write-Log "Skipping MinGW check - CGO disabled, no GCC dependencies required." "INFO"
$mingwInstalled = $false
# First check if gcc is available in PATH
if (Test-Command "gcc") {
    Write-Log "mingw is already installed and gcc is available in PATH." "SUCCESS"
    $mingwInstalled = $true
}
else {
    # Check common MinGW installation paths first
    $mingwPaths = @(
        "C:\ProgramData\chocolatey\lib\mingw\tools\install\mingw64\bin",
        "C:\tools\mingw64\bin",
        "C:\mingw64\bin",
        "C:\msys64\mingw64\bin",
        "C:\TDM-GCC-64\bin"
    )
    foreach ($path in $mingwPaths) {
        if (Test-Path (Join-Path $path "gcc.exe")) {
            Write-Log "Found mingw at '$path'. Adding to PATH." "INFO"
            $env:Path = "$path;$env:Path"
            $mingwInstalled = $true
            break
        }
    }
    # If not found in common paths, check Chocolatey registry
    if (-not $mingwInstalled) {
        try {
            $chocoList = choco list --local-only mingw 2>$null
            if ($chocoList -match "mingw.*(\d+).*package") {
                Write-Log "mingw is installed via Chocolatey. Searching for binaries..." "INFO"
                $mingwInstalled = $true
            }
        }
        catch {
            Write-Log "Could not check Chocolatey package list for mingw." "WARNING"
        }
    }
}
if (-not $mingwInstalled) {
    Write-Log "mingw is not installed. Installing via Chocolatey..." "INFO"
    try {
        choco install mingw --no-progress --yes --force | Out-Null
        Write-Log "mingw installed successfully." "SUCCESS"
        # After installation, try to add to PATH
        $mingwPaths = @(
            "C:\ProgramData\chocolatey\lib\mingw\tools\install\mingw64\bin",
            "C:\tools\mingw64\bin"
        )
        foreach ($path in $mingwPaths) {
            if (Test-Path (Join-Path $path "gcc.exe")) {
                $env:Path = "$path;$env:Path"
                Write-Log "Added newly installed mingw path '$path' to PATH." "INFO"
                break
            }
        }
    }
    catch {
        Write-Log "Failed to install mingw. Error: $_" "ERROR"
        exit 1
    }
}
else {
    Write-Log "mingw is already available." "SUCCESS"
}

