<#
.SYNOPSIS
    Builds the Cimian C# project locally, replicating the CI/CD pipeline.
.DESCRIPTION
    This script automates the build and packaging process for the C# migration of Cimian,
    including building .NET binaries, signing them, and packaging artifacts.
#>
#  Sign           build + sign (REQUIRED for enterprise deployment)
#  NoSign         disable auto-signing (NOT recommended for enterprise)
#  Thumbprint XX  override auto-detection
#  Task XX        run specific task: build, package, all (default: all)
#  Binaries       build and sign only the .exe binaries, skip all packaging
#  Binary XX      build and sign only a specific binary (e.g., managedsoftwareupdate)
#  Install        after building, install the MSI package (requires elevation)
#  IntuneWin      create .intunewin packages (adds build time, only needed for deployment)
#  Dev            development mode: stops services, faster iteration
#  SignMSI        sign existing MSI files in release directory (standalone operation)
#  SkipMSI        skip MSI packaging, build only .nupkg packages
#  PackageOnly    package existing binaries only (skip build), create both MSI and NUPKG
#  NupkgOnly      create .nupkg packages only using existing binaries (skip build and MSI)
#  MsiOnly        create MSI packages only using existing binaries (skip build and NUPKG)
#
# Usage examples:
#   .\build.ps1 -Sign                    # Full C# build with auto-signing
#   .\build.ps1 -Binaries -Sign         # Build only binaries with signing
#   .\build.ps1 -Binary managedsoftwareupdate -Sign # Build and sign only managedsoftwareupdate
#   .\build.ps1 -Sign -Thumbprint XX     # Force sign with specific certificate
#   .\build.ps1 -Install -Sign           # Build and install the MSI package
#   .\build.ps1 -IntuneWin -Sign         # Full build including .intunewin packages
#   .\build.ps1 -Dev -Install -Sign      # Development mode: fast rebuild and install
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
    [switch]$Dev,  # Development mode - stops services, faster iteration
    [switch]$SignMSI,  # Sign existing MSI files in release directory
    [switch]$SkipMSI,  # Skip MSI packaging, build only .nupkg packages
    [switch]$PackageOnly,  # Package existing binaries only (skip build)
    [switch]$NupkgOnly,    # Create .nupkg packages only using existing binaries
    [switch]$MsiOnly       # Create MSI packages only using existing binaries
)

#   GLOBALS  
# Friendly name (CN) of the enterprise code-signing certificate you push with Intune
$Global:EnterpriseCertCN = 'EmilyCarrU Intune Windows Enterprise Certificate'
# Exit immediately if a command exits with a non-zero status
$ErrorActionPreference = 'Stop'

# C# CLI Tools mapping
$Global:CSharpTools = @{
    "managedsoftwareupdate" = "Cimian.CLI.managedsoftwareupdate"
    "cimiimport" = "Cimian.CLI.cimiimport"
    "cimipkg" = "Cimian.CLI.cimipkg"
    "makecatalogs" = "Cimian.CLI.makecatalogs"
    "makepkginfo" = "Cimian.CLI.makepkginfo"
    "cimitrigger" = "Cimian.CLI.cimitrigger"
    "manifestutil" = "Cimian.CLI.manifestutil"
    "repoclean" = "Cimian.CLI.repoclean"
    "cimiwatcher" = "Cimian.CLI.cimiwatcher"
    "cimistatus" = "Cimian.GUI.CimianStatus"
}

# Function to display messages with different log levels
function Write-Log {
    param (
        [string]$Message,
        [ValidateSet("INFO", "WARNING", "ERROR", "SUCCESS")]
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    switch ($Level) {
        "INFO"    { Write-Host "[$timestamp] [INFO] $Message" -ForegroundColor White }
        "WARNING" { Write-Host "[$timestamp] [WARN] $Message" -ForegroundColor Yellow }
        "ERROR"   { Write-Host "[$timestamp] [ERROR] $Message" -ForegroundColor Red }
        "SUCCESS" { Write-Host "[$timestamp] [SUCCESS] $Message" -ForegroundColor Green }
    }
}

# Function to check if a command exists
function Test-Command {
    param (
        [string]$Command
    )
    return $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

function Get-SigningCertThumbprint {
    [OutputType([hashtable])]
    param()
    
    # Check both CurrentUser and LocalMachine certificate stores
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and $_.Subject -like '*EmilyCarrU*' } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{ Thumbprint = $cert.Thumbprint; Store = "CurrentUser" }
    }
    
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.HasPrivateKey -and $_.Subject -like '*EmilyCarrU*' } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{ Thumbprint = $cert.Thumbprint; Store = "LocalMachine" }
    }
    
    return $null
}

function Test-SignTool {
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) { return }
    
    $roots = @(
        "$env:ProgramFiles(x86)\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    try {
        $kitsRoot = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" -Name KitsRoot10 -ErrorAction SilentlyContinue
        if ($kitsRoot) { $roots += (Join-Path $kitsRoot.KitsRoot10 'bin') }
    } catch {}

    foreach ($root in $roots) {
        $candidates = Get-ChildItem -Path $root -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue
        if ($candidates) {
            $env:PATH += ";$($candidates[0].Directory.FullName)"
            return
        }
    }
    throw "signtool.exe not found. Install Windows 10/11 SDK (Signing Tools)."
}

# Function to sign artifacts with enterprise certificate
function Invoke-SignArtifact {
    param(
        [string]$Path,
        [string]$Thumbprint,
        [string]$Store = "CurrentUser",
        [int]$MaxAttempts = 3
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }

    $tsas = @(
        "http://timestamp.digicert.com",
        "http://timestamp.sectigo.com",
        "http://timestamp.globalsign.com"
    )

    # Set store parameter based on which store the certificate is in
    $storeParam = if ($Store -eq "CurrentUser") { "/s", "My" } else { "/s", "My", "/sm" }

    $attempt = 0
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        foreach ($tsa in $tsas) {
            try {
                Write-Log "Signing attempt $attempt with TSA $tsa for: $Path" "INFO"
                
                $signArgs = @(
                    "sign"
                    "/sha1", $Thumbprint
                    "/tr", $tsa
                    "/td", "sha256"
                    "/fd", "sha256"
                ) + $storeParam + @($Path)
                
                & signtool.exe @signArgs
                if ($LASTEXITCODE -eq 0) {
                    Write-Log "Successfully signed: $Path" "SUCCESS"
                    return
                }
                Write-Log "Signing failed with exit code $LASTEXITCODE, trying next TSA..." "WARNING"
            }
            catch {
                Write-Log "Signing attempt failed: $($_.Exception.Message)" "WARNING"
            }
        }
        if ($attempt -lt $MaxAttempts) {
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    throw "Signing failed after $MaxAttempts attempts across TSAs: $Path"
}

# Function to ensure Chocolatey is installed
function Install-Chocolatey {
    Write-Log "Checking if Chocolatey is installed..." "INFO"
    if (-not (Test-Command "choco")) {
        Write-Log "Chocolatey not found. Installing..." "INFO"
        Set-ExecutionPolicy Bypass -Scope Process -Force
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
        iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
        # Refresh environment variables
        $env:PATH = [Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH", "User")
        Write-Log "Chocolatey installed successfully." "SUCCESS"
    }
    else {
        Write-Log "Chocolatey is already installed." "SUCCESS"
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
            Write-Log "Enterprise certificate auto-detected: $autoDetectedThumbprint" "SUCCESS"
            $Sign = $true
            $Thumbprint = $autoDetectedThumbprint
        }
        else {
            Write-Log "No enterprise certificate found. Build will be unsigned (NOT RECOMMENDED for enterprise deployment)." "WARNING"
        }
    }
    catch {
        Write-Log "Certificate auto-detection failed: $($_.Exception.Message)" "WARNING"
    }
}

# Validate binary parameter if specified
if ($Binary) {
    Write-Log "Binary parameter detected - will only build '$Binary' binary" "INFO"
    $Task = "build"
    
    if (-not $Global:CSharpTools.ContainsKey($Binary)) {
        Write-Log "Invalid binary name: $Binary. Valid options are: $($Global:CSharpTools.Keys -join ', ')" "ERROR"
        exit 1
    }
}

# Force signing validation for enterprise environment
if (-not $Sign -and -not $NoSign) {
    Write-Log "WARNING: No signing specified. Enterprise environments require signed binaries!" "WARNING"
    Write-Log "Use -Sign parameter to enable signing, or -NoSign to explicitly disable (not recommended)" "WARNING"
}

if ($Sign) {
    Test-SignTool
    if (-not $Thumbprint) {
        $certInfo = Get-SigningCertThumbprint
        if ($certInfo) {
            $Thumbprint = $certInfo.Thumbprint
            Write-Log "Auto-selected signing cert $Thumbprint from $($certInfo.Store) store" "INFO"
        } else {
            Write-Log "No enterprise certificate found for signing!" "ERROR"
            exit 1
        }
    }
    $env:SIGN_THUMB = $Thumbprint
} else {
    Write-Log "Build will be unsigned - NOT RECOMMENDED for enterprise deployment." "WARNING"
}

#   DEVELOPMENT MODE HANDLING  
if ($Dev) {
    Write-Log "Development mode enabled - preparing for rapid iteration..." "INFO"
    # Stop and remove Cimian services that might lock files
    $services = @("CimianWatcher")
    foreach ($serviceName in $services) {
        try {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($service -and $service.Status -eq 'Running') {
                Write-Log "Stopping service: $serviceName" "INFO"
                sudo Stop-Service -Name $serviceName -Force
            }
        }
        catch {
            Write-Log "Could not stop service $serviceName : $($_.Exception.Message)" "WARNING"
        }
    }
    
    # Kill any running Cimian processes
    $processes = @("cimistatus", "cimiwatcher", "managedsoftwareupdate")
    foreach ($processName in $processes) {
        Get-Process -Name $processName -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Log "Terminating process: $processName (PID: $($_.Id))" "INFO"
            $_ | Stop-Process -Force
        }
    }
    
    Write-Log "Development mode preparation complete" "SUCCESS"
}

# Early exit for SignMSI mode
if ($SignMSI) {
    Write-Log "SignMSI mode - will sign existing MSI files in release directory" "INFO"
    
    if ($NoSign) {
        Write-Log "Cannot use -SignMSI with -NoSign" "ERROR"
        exit 1
    }
    $Sign = $true
    
    Test-SignTool
    if (-not $Thumbprint) {
        $certInfo = Get-SigningCertThumbprint
        if ($certInfo) {
            $Thumbprint = $certInfo.Thumbprint
            Write-Log "Auto-selected signing cert $Thumbprint for MSI signing" "INFO"
        } else {
            Write-Log "No enterprise certificate found for MSI signing!" "ERROR"
            exit 1
        }
    }
    
    $msiFiles = Get-ChildItem -Path "release" -Filter "*.msi" -File -ErrorAction SilentlyContinue
    if ($msiFiles.Count -eq 0) {
        Write-Log "No MSI files found in release directory to sign." "WARNING"
        exit 0
    }
    
    foreach ($msi in $msiFiles) {
        try {
            Invoke-SignArtifact -Path $msi.FullName -Thumbprint $Thumbprint
            Write-Log "Successfully signed: $($msi.Name)" "SUCCESS"
        }
        catch {
            Write-Log "Failed to sign MSI $($msi.Name): $($_.Exception.Message)" "ERROR"
        }
    }
    
    Write-Log "SignMSI completed." "SUCCESS"
    exit 0
}

# 
#  C# BUILD PROCESS STARTS
# 

Write-Log "Starting Cimian C# build process..." "INFO"

# Step 1: Ensure required tools are installed
Install-Chocolatey

# Install .NET SDK if not available
if (-not (Test-Command "dotnet")) {
    Write-Log ".NET SDK not found. Installing via Chocolatey..." "INFO"
    choco install dotnet -y
    # Refresh PATH
    $env:PATH = [Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH", "User")
}

if (-not (Test-Command "dotnet")) {
    Write-Log ".NET SDK is still not available after installation!" "ERROR"
    exit 1
}

Write-Log ".NET SDK is available" "SUCCESS"

# Step 2: Set up version information
function Set-Version {
    $currentTime = Get-Date
    $fullVersion = $currentTime.ToString("yyyy.MM.dd.HHmm")
    $semanticVersion = "{0}.{1}.{2}.{3}" -f $($currentTime.Year - 2000), $currentTime.Month, $currentTime.Day, $currentTime.ToString("HHmm")
    $env:RELEASE_VERSION = $fullVersion
    $env:SEMANTIC_VERSION = $semanticVersion
    Write-Log "RELEASE_VERSION set to $fullVersion" "INFO"
    Write-Log "SEMANTIC_VERSION set to $semanticVersion" "INFO"
}

Set-Version

# Step 3: Clean and prepare release directories
Write-Log "Cleaning release directories..." "INFO"
if (Test-Path "release") {
    Remove-Item -Path "release\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path "release" | Out-Null
}

$archs = @("x64", "arm64")
foreach ($arch in $archs) {
    $releaseArchDir = "release\$arch"
    New-Item -ItemType Directory -Path $releaseArchDir -Force | Out-Null
}

# Step 4: Build C# projects
Write-Log "Building C# solution..." "INFO"

# Restore packages
dotnet restore Cimian.sln
if ($LASTEXITCODE -ne 0) {
    Write-Log "Failed to restore NuGet packages" "ERROR"
    exit 1
}

# Build for each architecture
foreach ($arch in $archs) {
    $runtimeId = if ($arch -eq "x64") { "win-x64" } else { "win-arm64" }
    
    Write-Log "Building for $arch ($runtimeId)..." "INFO"
    
    if ($Binary) {
        # Build only the specified binary
        $projectName = $Global:CSharpTools[$Binary]
        $projectPath = "src\$projectName\$projectName.csproj"
        
        if (-not (Test-Path $projectPath)) {
            Write-Log "Project not found: $projectPath" "ERROR"
            exit 1
        }
        
        $outputPath = "release\$arch"
        $assemblyName = if ($Binary -eq "cimistatus") { "cimistatus" } else { $Binary }
        
        dotnet publish $projectPath `
            --configuration Release `
            --runtime $runtimeId `
            --self-contained false `
            --output $outputPath `
            -p:PublishSingleFile=true `
            -p:AssemblyName=$assemblyName `
            -p:Version=$env:SEMANTIC_VERSION
            
        if ($LASTEXITCODE -ne 0) {
            Write-Log "Failed to build $Binary for $arch" "ERROR"
            exit 1
        }
        
        Write-Log "Successfully built $Binary for $arch" "SUCCESS"
    }
    elseif ($Binaries) {
        # Build all CLI tools
        foreach ($tool in $Global:CSharpTools.Keys) {
            if ($tool -eq "cimistatus") { continue } # Skip GUI for binaries mode
            
            $projectName = $Global:CSharpTools[$tool]
            $projectPath = "src\$projectName\$projectName.csproj"
            
            if (Test-Path $projectPath) {
                $outputPath = "release\$arch"
                
                dotnet publish $projectPath `
                    --configuration Release `
                    --runtime $runtimeId `
                    --self-contained false `
                    --output $outputPath `
                    -p:PublishSingleFile=true `
                    -p:AssemblyName=$tool `
                    -p:Version=$env:SEMANTIC_VERSION
                    
                if ($LASTEXITCODE -ne 0) {
                    Write-Log "Failed to build $tool for $arch" "ERROR"
                    exit 1
                }
                
                Write-Log "Successfully built $tool for $arch" "SUCCESS"
            }
        }
    }
    else {
        # Build the main CLI tool for now (managedsoftwareupdate)
        $projectPath = "src\Cimian.CLI.managedsoftwareupdate\Cimian.CLI.managedsoftwareupdate.csproj"
        $outputPath = "release\$arch"
        
        dotnet publish $projectPath `
            --configuration Release `
            --runtime $runtimeId `
            --self-contained false `
            --output $outputPath `
            -p:PublishSingleFile=true `
            -p:AssemblyName=managedsoftwareupdate `
            -p:Version=$env:SEMANTIC_VERSION
            
        if ($LASTEXITCODE -ne 0) {
            Write-Log "Failed to build managedsoftwareupdate for $arch" "ERROR"
            exit 1
        }
        
        Write-Log "Successfully built managedsoftwareupdate for $arch" "SUCCESS"
    }
}

# Step 5: Sign all executables if signing is enabled
if ($Sign) {
    Write-Log "Signing all executables..." "INFO"
    
    # Force garbage collection to release file handles
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    [System.GC]::Collect()
    Start-Sleep -Seconds 2
    
    foreach ($arch in $archs) {
        $releaseDir = "release\$arch"
        $exeFiles = Get-ChildItem -Path $releaseDir -Filter "*.exe" -File
        
        foreach ($exe in $exeFiles) {
            try {
                Invoke-SignArtifact -Path $exe.FullName -Thumbprint $Thumbprint
                Write-Log "Successfully signed: $($exe.Name) ($arch)" "SUCCESS"
            }
            catch {
                Write-Log "Failed to sign $($exe.Name) ($arch): $($_.Exception.Message)" "ERROR"
                exit 1
            }
        }
    }
}

# Early exit for binaries-only mode
if ($Binaries -or $Binary) {
    Write-Log "Binaries build completed successfully." "SUCCESS"
    Write-Log "Built binaries are available in:" "INFO"
    Get-ChildItem -Path "release" -Recurse -Filter "*.exe" | ForEach-Object {
        Write-Log "  $($_.FullName)" "INFO"
    }
    exit 0
}

# Step 6: Package creation (MSI/NuGet) would go here
# For now, we'll skip packaging in the initial implementation

Write-Log "Build process completed successfully." "SUCCESS"
Write-Log "Built and signed executables are available in release\x64\ and release\arm64\" "SUCCESS"

# Step 7: Install if requested
if ($Install) {
    Write-Log "Install flag specified, but MSI packaging is not yet implemented in C# build." "WARNING"
    Write-Log "Built executables are available for manual testing." "INFO"
}
