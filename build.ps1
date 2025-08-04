<#
.SYNOPSIS
    Builds the Cimian project locally, replicating the CI/CD pipeline.
.DESCRIPTION
    This script automates the build and packaging process, including installing dependencies,
    building binaries, and packaging artifacts.
#>
#  ─Sign          … build + sign
#  ─NoSign        … disable auto-signing even if enterprise cert is found
#  ─Thumbprint XX … override auto-detection
#  ─Task XX       … run specific task: build, package, all (default: all)
#  ─Binaries      … build and sign only the .exe binaries, skip all packaging
#  ─Binary XX     … build and sign only a specific binary (e.g., cimistatus)
#  ─Install       … after building, install the MSI package (requires elevation)
#  ─IntuneWin     … create .intunewin packages (adds build time, only needed for deployment)
#  ─Dev           … development mode: stops services, faster iteration
#
# Usage examples:
#   .\build.ps1                      # Full build with auto-signing (no .intunewin)
#   .\build.ps1 -Binaries -Sign      # Build only binaries with signing
#   .\build.ps1 -Binary cimistatus -Sign # Build and sign only cimistatus binary
#   .\build.ps1 -Sign -Thumbprint XX # Force sign with specific certificate
#   .\build.ps1 -Install             # Build and install the MSI package
#   .\build.ps1 -IntuneWin           # Full build including .intunewin packages
#   .\build.ps1 -Dev -Install        # Development mode: fast rebuild and install
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
    [switch]$Dev  # Development mode - stops services, skips signing, faster iteration
)
# ──────────────────────────  GLOBALS  ──────────────────────────
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
    [OutputType([string])]
    param()
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -like "*CN=$Global:EnterpriseCertCN*" -and
            $_.NotAfter -gt (Get-Date) -and
            $_.HasPrivateKey
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1 -ExpandProperty Thumbprint
}
# Function to ensure signtool is available
function Test-SignTool {
    param(
        [string[]]$PreferredArchOrder = @(
            # Automatically pick the host arch first
            $(if ($Env:PROCESSOR_ARCHITECTURE -eq 'AMD64') { 'x64' }
              elseif ($Env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' }
              else { 'x86' }),
            # Fallbacks in case the host arch build is missing
            'x86', 'x64', 'arm64'
        )
    )
    function Add-ToPath([string]$dir) {
        if (-not [string]::IsNullOrWhiteSpace($dir) -and
            -not ($env:Path -split ';' | Where-Object { $_ -ieq $dir })) {
            $env:Path = "$dir;$env:Path"
        }
    }
    if (Get-Command signtool.exe -ErrorAction SilentlyContinue) { return }
    $roots = @(
        "${env:ProgramFiles}\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    )
    try {
        $kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -EA Stop).KitsRoot10
        if ($kitsRoot) { $roots += (Join-Path $kitsRoot 'bin') }
    } catch { }
    $roots = $roots | Where-Object { Test-Path $_ } | Select-Object -Unique
    foreach ($root in $roots) {
        foreach ($arch in $PreferredArchOrder) {
            $candidate = Get-ChildItem -Path (Join-Path $root "*\$arch\signtool.exe") -EA SilentlyContinue |
                         Sort-Object LastWriteTime -Desc | Select-Object -First 1
            if ($candidate) {
                Add-ToPath $candidate.Directory.FullName
                Write-Log "signtool discovered at $($candidate.FullName)" "SUCCESS"
                return
            }
        }
    }
    Write-Log @"
signtool.exe not found.
Install **any** Windows 10/11 SDK _or_ Visual Studio Build Tools
(ensure the **Windows SDK Signing Tools** workload is included),
then run the build again.
"@ "ERROR"
    exit 1
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
# ──────────────────────────  SIGNING DECISION  ─────────────────
# ──────────────────────────  SIGNING DECISION  ─────────────────
# Auto-detect enterprise certificate if available
$autoDetectedThumbprint = $null
if (-not $Sign -and -not $NoSign -and -not $Thumbprint) {
    try {
        $autoDetectedThumbprint = Get-SigningCertThumbprint
        if ($autoDetectedThumbprint) {
            Write-Log "Auto-detected enterprise certificate $autoDetectedThumbprint - will sign binaries for security." "INFO"
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
    Write-Log "Cannot use -Install with -Binaries or -Binary flag. MSI packages are needed for installation." "ERROR"
    exit 1
}
# If Install flag is set, ensure Task includes packaging
if ($Install -and $Task -eq "build") {
    Write-Log "Install flag detected - forcing Task to 'all' to ensure MSI packages are built." "INFO"
    $Task = "all"
}
if ($NoSign) {
    Write-Log "NoSign parameter specified - skipping all signing." "INFO"
    $Sign = $false
}
if ($Sign) {
    Test-SignTool
    if (-not $Thumbprint) {
        $Thumbprint = Get-SigningCertThumbprint
        if (-not $Thumbprint) {
            Write-Log "No valid '$Global:EnterpriseCertCN' certificate with a private key found – aborting." "ERROR"
            exit 1
        }
        Write-Log "Auto-selected signing cert $Thumbprint" "INFO"
    } else {
        Write-Log "Using signing certificate $Thumbprint" "INFO"
    }
    $env:SIGN_THUMB = $Thumbprint   # used by the two sign* functions
} else {
    Write-Log "Build will be unsigned." "INFO"
}
# ────────────────  DEVELOPMENT MODE HANDLING  ────────────────
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
    $processes = @("cimistatus", "cimiwatcher", "managedsoftwareupdate")
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
# ────────────────  SIGNING HELPERS  ────────────────
function signPackage {
    <#
      .SYNOPSIS  Authenticode-signs an MSI/EXE/… with our enterprise cert.
      .PARAMETER FilePath     – the file you want to sign
      .PARAMETER Thumbprint   – SHA-1 thumbprint of the cert (defaults to $env:SIGN_THUMB)
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
    $tsaList = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS'
    )
    foreach ($tsa in $tsaList) {
        Write-Log "Signing '$FilePath' using $tsa …" "INFO"
        # Force garbage collection before signing to release any handles
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        & signtool.exe sign `
            /sha1  $Thumbprint `
            /fd    SHA256 `
            /tr    $tsa `
            /td    SHA256 `
            /v `
            "$FilePath"
        if ($LASTEXITCODE -eq 0) {
            Write-Log  "signtool succeeded with $tsa" "SUCCESS"
            return $true
        }
        Write-Log "signtool failed with $tsa (exit $LASTEXITCODE)" "WARNING"
    }
    Write-Log "signtool failed with all timestamp authorities for '$FilePath' - continuing build without signature" "WARNING"
    return $false
}
function signNuget {
    param(
        [Parameter(Mandatory)][string]$Nupkg,
        [string]$Thumbprint            # ← optional override (matches existing caller)
    )
    if (-not (Test-Path $Nupkg)) {
        throw "NuGet package '$Nupkg' not found - cannot sign."
    }
    $tsa = 'http://timestamp.digicert.com'
    if (-not $Thumbprint) {
        $Thumbprint = (Get-ChildItem Cert:\CurrentUser\My |
                       Where-Object { $_.Subject -like "*CN=$Global:EnterpriseCertCN*" -and $_.HasPrivateKey } |
                       Sort-Object NotAfter -Descending |
                       Select-Object -First 1 -ExpandProperty Thumbprint)
    }
    if (-not $Thumbprint) {
        Write-Log "No enterprise code-signing cert present – skipping NuGet repo sign." "WARNING"
        return $false
    }
    & nuget.exe sign `
    $Nupkg `
    -CertificateStoreName   My `
    -CertificateSubjectName $Global:EnterpriseCertCN `
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
            $sudoArgs = @("msiexec.exe", "/i", "`"$MsiPath`"", "/qn", "/l*v", "`"$env:TEMP\cimian_install.log`"")
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
# ───────────────────────────────────────────────────
#  BUILD PROCESS STARTS
# ───────────────────────────────────────────────────
# Early exit for binaries-only mode or single binary mode after basic setup
if ($Binaries -or $Binary) {
    if ($Binary) {
        Write-Log "Single binary mode: Starting minimal build process for '$Binary'..." "INFO"
    } else {
        Write-Log "Binaries-only mode: Starting minimal build process..." "INFO"
    }
    # Only do essential checks for binaries mode
    if (-not (Test-Command "go")) {
        Write-Log "Go is not installed or not in PATH. Installing..." "INFO"
        Install-Chocolatey
        choco install go --no-progress --yes --force | Out-Null
        # Force environment reload
        if ($env:ChocolateyInstall -and (Test-Path "$env:ChocolateyInstall\helpers\refreshenv.cmd")) {
            & "$env:ChocolateyInstall\helpers\refreshenv.cmd"
        }
        # Check common Go paths
        $possibleGoPaths = @(
            "C:\Program Files\Go\bin",
            "C:\Go\bin",
            "C:\ProgramData\chocolatey\bin"
        )
        foreach ($p in $possibleGoPaths) {
            if (Test-Path (Join-Path $p "go.exe")) {
                $env:Path = "$p;$env:Path"
                break
            }
        }
        if (-not (Test-Command "go")) {
            Write-Log "Go installation failed. Exiting..." "ERROR"
            exit 1
        }
    }
    # Set version for binaries (inline to avoid function dependency)
    $fullVersion     = Get-Date -Format "yyyy.MM.dd"
    $semanticVersion = "{0}.{1}.{2}" -f $((Get-Date).Year - 2000), $((Get-Date).Month), $((Get-Date).Day)
    $env:RELEASE_VERSION   = $fullVersion
    $env:SEMANTIC_VERSION  = $semanticVersion
    Write-Log "RELEASE_VERSION set to $fullVersion" "INFO"
    Write-Log "SEMANTIC_VERSION set to $semanticVersion" "INFO"
    # Tidy modules
    go mod tidy
    go mod download
    # Build binaries
    Write-Log "Building binaries for x64 and arm64..." "INFO"
    # Clean and create bin directories
    if (Test-Path "bin") {
        Remove-Item -Path "bin\*" -Recurse -Force
    }
    # Create release directories
    if (Test-Path "release") {
        Remove-Item -Path "release\*" -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path "release" -Force | Out-Null
    }
    $binaryDirs = Get-ChildItem -Directory -Path "./cmd"
    # Filter to specific binary if -Binary parameter is specified
    if ($Binary) {
        $binaryDirs = $binaryDirs | Where-Object { $_.Name -eq $Binary }
        if (-not $binaryDirs) {
            Write-Log "Specified binary '$Binary' not found in cmd directory." "ERROR"
            exit 1
        }
        Write-Log "Building only binary: $Binary" "INFO"
    } else {
        Write-Log "Building all binaries" "INFO"
    }
    $archs = @("x64", "arm64")
    $goarchMap = @{
        "x64"   = "amd64"
        "arm64" = "arm64"
    }
    foreach ($arch in $archs) {
        $binArchDir = "bin\$arch"
        $releaseArchDir = "release\$arch"
        if (-not (Test-Path $binArchDir)) {
            New-Item -ItemType Directory -Path $binArchDir | Out-Null
        }
        if (-not (Test-Path $releaseArchDir)) {
            New-Item -ItemType Directory -Path $releaseArchDir | Out-Null
        }
        foreach ($dir in $binaryDirs) {
            $binaryName = $dir.Name
            Write-Log "Building $binaryName for $arch..." "INFO"
            # Check if this is a C# project
            $csharpProject = Join-Path $dir.FullName "$binaryName.csproj"
            $csharpAltProject = Join-Path $dir.FullName "CimianStatus.csproj"  # Special case for cimistatus
            $submoduleGoMod = Join-Path $dir.FullName "go.mod"
            $outputPath = "bin\$arch\$binaryName.exe"
            if ((Test-Path $csharpProject) -or (Test-Path $csharpAltProject)) {
                # This is a C# project - build with dotnet
                Write-Log "Detected C# project for $binaryName" "INFO"
                # Determine which project file to use
                $projectFile = if (Test-Path $csharpAltProject) { $csharpAltProject } else { $csharpProject }
                # Map architecture for .NET runtime identifiers
                $dotnetRid = switch ($arch) {
                    "x64" { "win-x64" }
                    "arm64" { "win-arm64" }
                }
                Push-Location $dir.FullName
                try {
                    # Publish the C# project for specific architecture (self-contained single file) using hardcoded system dotnet path
                    $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
                    if (-not (Test-Path $dotnetPath)) {
                        # Fallback to PATH-based dotnet if system path doesn't exist
                        $dotnetPath = "dotnet"
                    }
                    & $dotnetPath publish $projectFile --configuration Release --runtime $dotnetRid --self-contained true --output "bin\Release\net8.0-windows\$dotnetRid" -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true --verbosity minimal
                    if ($LASTEXITCODE -ne 0) {
                        throw "Publish failed for C# project $binaryName ($arch) with exit code $LASTEXITCODE."
                    }
                    # Find the built executable (should now be named cimistatus.exe)
                    $builtExePath = "bin\Release\net8.0-windows\$dotnetRid\cimistatus.exe"
                    if (-not (Test-Path $builtExePath)) {
                        # Fallback: look for any .exe in the output directory
                        $builtExePath = Get-ChildItem "bin\Release\net8.0-windows\$dotnetRid\*.exe" | Select-Object -First 1 -ExpandProperty FullName
                    }
                    $builtExe = Get-ChildItem $builtExePath | Select-Object -First 1
                    if (-not $builtExe) {
                        throw "Could not find built executable for $binaryName ($arch)"
                    }
                    # Copy to the expected output location with retry mechanism for file locking issues
                    $copySuccess = $false
                    $maxRetries = 5
                    $retryDelay = 2
                    for ($retry = 1; $retry -le $maxRetries; $retry++) {
                        try {
                            # Force garbage collection to release any file handles
                            [System.GC]::Collect()
                            [System.GC]::WaitForPendingFinalizers()
                            # Use robocopy for more reliable file copying with locked files
                            & robocopy (Split-Path $builtExe.FullName) (Split-Path "..\..\$outputPath") (Split-Path $builtExe.FullName -Leaf) /R:3 /W:1 /NP /NDL /NJH /NJS | Out-Null
                            # Robocopy exit codes 0-7 are success, 8+ are errors
                            if ($LASTEXITCODE -le 7 -and (Test-Path "..\..\$outputPath")) {
                                $copySuccess = $true
                                Write-Log "Successfully copied $binaryName ($arch) on attempt $retry" "SUCCESS"
                                break
                            } else {
                                throw "Robocopy succeeded but file not found at destination"
                            }
                        } catch {
                            if ($retry -lt $maxRetries) {
                                Write-Log "Copy attempt $retry failed for $binaryName ($arch): $_. Retrying in $retryDelay seconds..." "WARNING"
                                Start-Sleep -Seconds $retryDelay
                                $retryDelay += 1  # Exponential backoff
                            } else {
                                Write-Log "All copy attempts failed for $binaryName ($arch). Falling back to standard copy..." "WARNING"
                                try {
                                    Copy-Item $builtExe.FullName "..\..\$outputPath" -Force
                                    $copySuccess = $true
                                } catch {
                                    throw "Final fallback copy also failed: $_"
                                }
                            }
                        }
                    }
                    if (-not $copySuccess) {
                        throw "Failed to copy built executable after all retry attempts"
                    }
                } catch {
                    Write-Log "Failed to build C# project $binaryName ($arch). Error: $_" "ERROR"
                    Pop-Location
                    exit 1
                }
                Pop-Location
            } elseif (Test-Path $submoduleGoMod) {
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
                    if ($binaryName -eq "cimistatus") {
                        $env:CGO_ENABLED = "1"
                        go build -v -o "..\..\$outputPath" -ldflags="$ldflags -H windowsgui" .
                        $env:CGO_ENABLED = "0"
                    } else {
                        go build -v -o "..\..\$outputPath" -ldflags="$ldflags" .
                    }
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
            # Copy to release directory
            Copy-Item "bin\$arch\$binaryName.exe" "release\$arch\$binaryName.exe"
            Write-Log "$binaryName ($arch) built and copied to release folder." "SUCCESS"
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
                        Write-Log "Signed $($_.FullName) ✔" "SUCCESS"
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
    Write-Log "Built binaries are available in:" "INFO"
    Get-ChildItem -Path "release" -Recurse -Filter "*.exe" | ForEach-Object {
        Write-Host "  $($_.FullName)"
    }
    exit 0
}
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
# Function to ensure Chocolatey is installed
function Install-Chocolatey {
    Write-Log "Checking if Chocolatey is installed..." "INFO"
    if (-not (Test-Command "choco")) {
        Write-Log "Chocolatey is not installed. Installing now..." "INFO"
        try {
            # Bypass Execution Policy and install Chocolatey
            Set-ExecutionPolicy Bypass -Scope Process -Force
            [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
            Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
            Write-Log "Chocolatey installed successfully." "SUCCESS"
            $chocoBinPath = "C:\ProgramData\chocolatey\bin"
            if (Test-Path $chocoBinPath) {
                $env:Path = "$chocoBinPath;$env:Path"
                Write-Log "Added '$chocoBinPath' to PATH to ensure 'choco' is recognized in this session." "INFO"
            }
            else {
                Write-Log "'$chocoBinPath' does not exist; cannot add to PATH." "WARNING"
            }
        }
        catch {
            Write-Log "Failed to install Chocolatey. Error: $_" "ERROR"
            exit 1
        }
    }
    else {
        Write-Log "Chocolatey is already installed." "SUCCESS"
    }
}
# Step 1: Ensure Chocolatey is installed
Install-Chocolatey
# Step 2: Install required tools via Chocolatey
Write-Log "Checking and installing required tools..." "INFO"
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
    Write-Log "Checking if $toolName is already installed..." "INFO"
    if (Test-Command $toolCommand) {
        Write-Log "$toolName is already installed and available via command '$toolCommand'." "SUCCESS"
        continue
    }
    Write-Log "$toolName is not installed. Installing via Chocolatey..." "INFO"
    try {
        choco install $toolName --no-progress --yes --force | Out-Null
        Write-Log "$toolName installed successfully." "SUCCESS"
    }
    catch {
        Write-Log "Failed to install $toolName. Error: $_" "ERROR"
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
        Write-Log "Failed to install WiX v6. Falling back to WiX v3..." "WARNING"
        choco install wixtoolset --yes --no-progress --force | Out-Null
        Write-Log "WiX v3 installed successfully." "SUCCESS"
    }
}
Write-Log "Required tools check and installation completed." "SUCCESS"
# Force environment reload via Chocolateley's refreshenv
if ($env:ChocolateyInstall -and (Test-Path "$env:ChocolateyInstall\helpers\refreshenv.cmd")) {
    Write-Log "Forcibly reloading environment with refreshenv.cmd..." "INFO"
    & "$env:ChocolateyInstall\helpers\refreshenv.cmd"
}
# Check if 'go' is now recognized
if (-not (Test-Command "go")) {
    Write-Log "Go still not recognized; appending common install paths manually..." "WARNING"
    $possibleGoPaths = @(
        "C:\Program Files\Go\bin",
        "C:\Go\bin",
        "C:\ProgramData\chocolatey\bin",
        "C:\ProgramData\chocolatey\lib\go\bin"
    )
    foreach ($p in $possibleGoPaths) {
        if (Test-Path (Join-Path $p "go.exe")) {
            $env:Path = "$p;$env:Path"
            Write-Log "Added '$p' to PATH. Checking 'go' again..." "INFO"
            if (Test-Command "go") {
                Write-Log "'go' is now recognized." "SUCCESS"
                break
            }
        }
    }
}
if (-not (Test-Command "go")) {
    Write-Log "Go is still not recognized. Installation may have failed or PATH is wonky." "ERROR"
    exit 1
}
else {
    Write-Log "Go is recognized in this session." "SUCCESS"
}
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
    $fullVersion     = Get-Date -Format "yyyy.MM.dd"
    $semanticVersion = "{0}.{1}.{2}" -f $((Get-Date).Year - 2000), $((Get-Date).Month), $((Get-Date).Day)
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
Write-Log "Cleaning existing binaries from bin directory..." "INFO"
if (Test-Path "bin") {
    Remove-Item -Path "bin\*" -Recurse -Force
    Write-Log "Cleaned existing binaries from bin directory." "SUCCESS"
}
$binaryDirs = Get-ChildItem -Directory -Path "./cmd"
$archs = @("x64", "arm64")
$goarchMap = @{
    "x64"   = "amd64"
    "arm64" = "arm64"
}
foreach ($arch in $archs) {
    $binArchDir = "bin\$arch"
    if (-not (Test-Path $binArchDir)) {
        New-Item -ItemType Directory -Path $binArchDir | Out-Null
    }
    foreach ($dir in $binaryDirs) {
        $binaryName = $dir.Name
        Write-Log "Building $binaryName for $arch..." "INFO"
        # Check if this is a C# project
        $csharpProject = Join-Path $dir.FullName "$binaryName.csproj"
        $csharpAltProject = Join-Path $dir.FullName "CimianStatus.csproj"  # Special case for cimistatus
        $submoduleGoMod = Join-Path $dir.FullName "go.mod"
        $outputPath = "bin\$arch\$binaryName.exe"
        if ((Test-Path $csharpProject) -or (Test-Path $csharpAltProject)) {
            # This is a C# project - build with dotnet
            Write-Log "Detected C# project for $binaryName" "INFO"
            # Determine which project file to use
            $projectFile = if (Test-Path $csharpAltProject) { $csharpAltProject } else { $csharpProject }
            # Map architecture for .NET runtime identifiers
            $dotnetRid = switch ($arch) {
                "x64" { "win-x64" }
                "arm64" { "win-arm64" }
            }
            Push-Location $dir.FullName
            try {
                # Build the C# project for specific architecture using hardcoded system dotnet path
                $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
                if (-not (Test-Path $dotnetPath)) {
                    # Fallback to PATH-based dotnet if system path doesn't exist
                    $dotnetPath = "dotnet"
                }
                & $dotnetPath build $projectFile --configuration Release --runtime $dotnetRid --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false --verbosity minimal
                if ($LASTEXITCODE -ne 0) {
                    throw "Build failed for C# project $binaryName ($arch) with exit code $LASTEXITCODE."
                }
                # Find the built executable (should now be named cimistatus.exe)
                $builtExePath = "bin\Release\net8.0-windows\$dotnetRid\cimistatus.exe"
                if (-not (Test-Path $builtExePath)) {
                    # Fallback: look for any .exe in the output directory
                    $builtExePath = "bin\Release\net8.0-windows*\$dotnetRid\*.exe"
                    $builtExe = Get-ChildItem $builtExePath | Select-Object -First 1
                } else {
                    $builtExe = Get-Item $builtExePath
                }
                if (-not $builtExe) {
                    throw "Could not find built executable for $binaryName ($arch)"
                }
                # Copy to the expected output location
                Copy-Item $builtExe.FullName "..\..\$outputPath" -Force
                Write-Log "$binaryName ($arch, C#) built successfully." "SUCCESS"
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
# Step 9: Package Binaries
Write-Log "Packaging binaries for all architectures..." "INFO"
foreach ($arch in $archs) {
    $releaseArchDir = "release\$arch"
    if (-not (Test-Path $releaseArchDir)) {
        New-Item -ItemType Directory -Path $releaseArchDir | Out-Null
    }
    Get-ChildItem -Path "bin\$arch\*.exe" | ForEach-Object {
        Copy-Item $_.FullName $releaseArchDir
        Write-Log "Copied $($_.Name) to $releaseArchDir." "INFO"
    }
}
# ───────────── SIGN EVERY EXE (once) IN ITS OWN ARCH FOLDER ─────────────
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
                $signResult = signPackage -FilePath $_.FullName   # ← uses $env:SIGN_THUMB, adds RFC 3161 timestamp
                if ($signResult) {
                    Write-Log "Signed $($_.FullName) ✔" "SUCCESS"
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
# Step 10: Build MSI Packages with WiX for both x64 and arm64
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
            
            # Find the output MSI in the build output
            $builtMsi = "build\msi\bin\x64\Release\Cimian-$msiArch.msi"
            if (Test-Path $builtMsi) {
                Move-Item $builtMsi $msiOutput -Force
                Write-Log "MSI package built with WiX v6 at $msiOutput." "SUCCESS"
            } else {
                # Try alternate paths
                $altPaths = @(
                    "build\msi\bin\Release\Cimian-$msiArch.msi",
                    "build\msi\bin\Debug\Cimian-$msiArch.msi",
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
    
    if ($Sign) {
        # Wait longer for WiX to fully release file handles
        Write-Log "Preparing MSI for signing..." "INFO"
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        Start-Sleep -Seconds 5
        
        # Attempt MSI signing with retry logic
        $signAttempts = 3
        $signSuccess = $false
        
        for ($attempt = 1; $attempt -le $signAttempts; $attempt++) {
            Write-Log "Attempting MSI signing (attempt $attempt of $signAttempts)..." "INFO"
            
            try {
                # Check if file is still locked
                if (Test-FileLocked -FilePath $msiOutput) {
                    Write-Log "MSI file is locked, waiting..." "WARNING"
                    Start-Sleep -Seconds (3 * $attempt)
                    [System.GC]::Collect()
                    [System.GC]::WaitForPendingFinalizers()
                }
                
                $signResult = signPackage -FilePath $msiOutput
                if ($signResult) {
                    Write-Log "MSI package signed successfully: $msiOutput" "SUCCESS"
                    $signSuccess = $true
                    break
                } else {
                    Write-Log "MSI signing attempt $attempt failed" "WARNING"
                }
            }
            catch {
                Write-Log "MSI signing attempt $attempt failed: $_" "WARNING"
            }
            
            if ($attempt -lt $signAttempts) {
                Write-Log "Retrying MSI signing in $($attempt * 2) seconds..." "INFO"
                Start-Sleep -Seconds ($attempt * 2)
            }
        }
        
        if (-not $signSuccess) {
            Write-Log "All MSI signing attempts failed. MSI package created unsigned: $msiOutput" "WARNING"
            Write-Log "Note: Individual executables inside the MSI are still signed for security." "INFO"
        }
    } else {
        Write-Log "MSI package created (unsigned): $msiOutput" "SUCCESS"
    }
    # Clean up temp folder
    Remove-Item -Path "$msiTempDir\*" -Recurse -Force
}
# Step 11: Prepare NuGet Packages for both x64 and arm64
Write-Log "Preparing NuGet packages for x64 and arm64..." "INFO"
foreach ($arch in $archs) {
    $pkgTempDir  = "release\nupkg_$arch"
    $archBinDst  = Join-Path $pkgTempDir $arch
    $nuspecPath  = "build\nupkg.$arch.nuspec"
    $nupkgOut    = "release\Cimian-$arch-$env:SEMANTIC_VERSION.nupkg"
    # workspace
    if (Test-Path $pkgTempDir) { Remove-Item $pkgTempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $archBinDst -Force | Out-Null
    # binaries
    Copy-Item "release\$arch\*.exe" $archBinDst
    # common payload
    Copy-Item "build\nupkg\config.yaml"   $pkgTempDir        -EA SilentlyContinue
    if (Test-Path "build\install.ps1") { Copy-Item "build\install.ps1" $pkgTempDir }
    if (Test-Path "README.md")      { Copy-Item "README.md" $pkgTempDir }
    else { 'Cimian command-line tools.' | Set-Content (Join-Path $pkgTempDir 'README.md') }
    # materialise nuspec (add <file> line for install.ps1 only if present)
    $nuspecText = Get-Content "build\nupkg\nupkg.nuspec"
    if (-not (Test-Path "$pkgTempDir\install.ps1")) {
        $nuspecText = $nuspecText -replace '<file src="install.ps1".*?/>', ''
    }
    $nuspecText `
        -replace '\$\{\{ARCH\}\}',        $arch `
        -replace '\$\{\{VERSION\}\}',     $env:SEMANTIC_VERSION |
        Set-Content $nuspecPath
    # pack
    nuget pack $nuspecPath -OutputDirectory "release" `
                -BasePath $pkgTempDir -NoDefaultExcludes | Out-Null
    $built = Get-ChildItem "release" -Filter '*.nupkg' |
             Sort-Object LastWriteTime -Desc | Select-Object -First 1
    Move-Item $built.FullName $nupkgOut -Force
    if ($Sign) {
        $signResult = signNuget $nupkgOut
        if ($signResult) {
            Write-Log "NuGet package signed successfully: $nupkgOut" "SUCCESS"
        } else {
            Write-Log "Failed to sign NuGet package: $nupkgOut - continuing build" "WARNING"
        }
    }
    # cleanup
    Remove-Item $pkgTempDir -Recurse -Force
    Remove-Item $nuspecPath -Force
    Write-Log "$arch NuGet ready → $nupkgOut" "SUCCESS"
}
# Step 11.1: Revert `nupkg.nuspec` to its dynamic state
Write-Log "Reverting build/nupkg/nupkg.nuspec to dynamic state..." "INFO"
try {
    (Get-Content "build\nupkg\nupkg.nuspec") -replace "$env:SEMANTIC_VERSION", '{{VERSION}}' | Set-Content "build\nupkg\nupkg.nuspec"
    Write-Log "Reverted build/nupkg/nupkg.nuspec to use dynamic placeholder." "SUCCESS"
}
catch {
    Write-Log "Failed to revert build/nupkg/nupkg.nuspec. Error: $_" "ERROR"
    exit 1
}
Write-Log "NuGet packaging for all architectures completed." "SUCCESS"
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
Write-Log "Build and packaging process completed successfully." "SUCCESS"
# Step 15: Install MSI Package if requested
if ($Install) {
    Write-Log "Install flag detected. Attempting to install MSI package..." "INFO"
    # Determine the current architecture for installation
    $currentArch = if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64") { "x64" } else { "arm64" }
    $msiToInstall = "release\Cimian-$currentArch-$env:RELEASE_VERSION.msi"
    # Check if the MSI for current architecture exists
    if (-not (Test-Path $msiToInstall)) {
        Write-Log "MSI package for current architecture ($currentArch) not found at '$msiToInstall'" "WARNING"
        # Try the other architecture as fallback
        $fallbackArch = if ($currentArch -eq "x64") { "arm64" } else { "x64" }
        $fallbackMsi = "release\Cimian-$fallbackArch-$env:RELEASE_VERSION.msi"
        if (Test-Path $fallbackMsi) {
            Write-Log "Using fallback MSI for $fallbackArch architecture: $fallbackMsi" "INFO"
            $msiToInstall = $fallbackMsi
        } else {
            Write-Log "No MSI packages found for installation. Available files in release:" "ERROR"
            Get-ChildItem -Path "release" -Filter "*.msi" | ForEach-Object {
                Write-Log "  $($_.Name)" "INFO"
            }
            Write-Log "Installation aborted." "ERROR"
            exit 1
        }
    }
    # Attempt to install the MSI
    $installSuccess = Install-MsiPackage -MsiPath $msiToInstall
    if ($installSuccess) {
        Write-Log "Cimian has been successfully installed!" "SUCCESS"
        Write-Log "You can now use the Cimian tools from the command line." "INFO"
    } else {
        Write-Log "Installation failed. Please check the installation log at $env:TEMP\cimian_install.log for details." "ERROR"
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
