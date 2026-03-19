#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the Cimian C# project with enterprise code signing and MSI/NuGet packaging.

.DESCRIPTION
    This script automates the build and packaging process for the C# migration of Cimian,
    including building .NET binaries, signing them with enterprise certificates, and creating MSI installers.
    
    DEFAULT BEHAVIOR: Running .\build.ps1 with no parameters builds everything (binaries + MSI + NUPKG) with signing.
    
    Version Format: YYYY.MM.DD.HHMM (e.g., 2025.12.04.1430)
    MSI versions are automatically converted to compatible format (YY.MM.DDHH).

.PARAMETER Task
    Run specific task: build, package, all (default: all)

.PARAMETER Sign
    Sign binaries with code signing certificate (default if enterprise cert found)

.PARAMETER NoSign
    Skip code signing (for development only)

.PARAMETER Thumbprint
    Use specific certificate thumbprint for signing

.PARAMETER Binary
    Build only the specified binary (e.g., managedsoftwareupdate, cimistatus)

.PARAMETER Binaries
    Build all binaries only (skip packaging)

.PARAMETER Install
    Install MSI package after building (requires elevation)

.PARAMETER IntuneWin
    Create IntuneWin packages for Intune deployment

.PARAMETER Dev
    Development mode - stops services, faster iteration, skips signing

.PARAMETER SignMSI
    Sign existing MSI files in release directory (standalone operation)

.PARAMETER SkipMSI
    Skip MSI packaging, build only .nupkg packages

.PARAMETER PackageOnly
    Package existing binaries only (skip build), create both MSI and NUPKG

.PARAMETER NupkgOnly
    Create .nupkg packages only using existing binaries (skip build and MSI)

.PARAMETER MsiOnly
    Create MSI packages only using existing binaries (skip build and NUPKG)

.PARAMETER PkgOnly
    Create .pkg packages only using existing binaries (direct binary payload)

.PARAMETER Clean
    Clean all build artifacts before building

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release

.PARAMETER Architecture
    Target architecture (x64, arm64, or both). Default: both

.EXAMPLE
    .\build.ps1
    # Full build with auto-signing (binaries + MSI + NUPKG)

.EXAMPLE
    .\build.ps1 -Dev -Install
    # Development mode: fast rebuild and install

.EXAMPLE
    .\build.ps1 -Binaries
    # Build only binaries, skip packaging

.EXAMPLE
    .\build.ps1 -Binary cimistatus -Sign
    # Build and sign only cimistatus binary

.EXAMPLE
    .\build.ps1 -Sign -Thumbprint XX
    # Force sign with specific certificate

.EXAMPLE
    .\build.ps1 -SkipMSI
    # Build only .nupkg packages, skip MSI packaging

.EXAMPLE
    .\build.ps1 -PackageOnly
    # Package existing binaries (both MSI and NUPKG)

.EXAMPLE
    .\build.ps1 -NupkgOnly
    # Create only .nupkg packages from existing binaries

.EXAMPLE
    .\build.ps1 -MsiOnly
    # Create only MSI packages from existing binaries

.EXAMPLE
    .\build.ps1 -PkgOnly
    # Create only .pkg packages from existing binaries (direct payload)

.EXAMPLE
    .\build.ps1 -IntuneWin
    # Full build including .intunewin packages

.EXAMPLE
    .\build.ps1 -SignMSI
    # Sign existing MSI files in release directory
#>

[CmdletBinding()]
param(
    [ValidateSet("build", "package", "all")]
    [string]$Task = "all",
    [switch]$Sign,
    [switch]$NoSign,
    [string]$Thumbprint,
    [string]$Binary,
    [switch]$Binaries,
    [switch]$Install,
    [switch]$IntuneWin,
    [switch]$Dev,
    [switch]$SignMSI,
    [switch]$SkipMSI,
    [switch]$PackageOnly,
    [switch]$NupkgOnly,
    [switch]$MsiOnly,
    [switch]$PkgOnly,
    [switch]$Clean,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Architecture = 'both'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

#region Logging Functions

function Write-BuildLog {
    param(
        [string]$Message,
        [ValidateSet("INFO", "WARNING", "ERROR", "SUCCESS")]
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $color = switch ($Level) {
        "INFO"    { "Cyan" }
        "WARNING" { "Yellow" }
        "ERROR"   { "Red" }
        "SUCCESS" { "Green" }
    }
    Write-Host "[$timestamp] " -NoNewline -ForegroundColor DarkGray
    Write-Host "[$Level] " -NoNewline -ForegroundColor $color
    Write-Host $Message
}

#endregion

#region Chocolatey and Tool Installation Functions

function Install-Chocolatey {
    if (Test-Command "choco") {
        Write-BuildLog "Chocolatey is already installed" "SUCCESS"
        return $true
    }
    
    Write-BuildLog "Installing Chocolatey..." "INFO"
    try {
        Set-ExecutionPolicy Bypass -Scope Process -Force
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
        Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
        
        # Refresh PATH
        $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
        
        if (Test-Command "choco") {
            Write-BuildLog "Chocolatey installed successfully" "SUCCESS"
            return $true
        }
    }
    catch {
        Write-BuildLog "Failed to install Chocolatey: $_" "ERROR"
    }
    return $false
}

function Install-NuGetCli {
    if (Test-Command "nuget") {
        Write-BuildLog "nuget.exe is already installed" "SUCCESS"
        return $true
    }
    
    Write-BuildLog "Installing nuget.commandline via Chocolatey..." "INFO"
    
    if (-not (Install-Chocolatey)) {
        Write-BuildLog "Cannot install nuget.commandline - Chocolatey installation failed" "ERROR"
        return $false
    }
    
    try {
        & choco install nuget.commandline --yes --no-progress | Out-Null
        
        # Refresh PATH
        $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
        
        if (Test-Command "nuget") {
            Write-BuildLog "nuget.commandline installed successfully" "SUCCESS"
            return $true
        }
    }
    catch {
        Write-BuildLog "Failed to install nuget.commandline: $_" "ERROR"
    }
    return $false
}

#endregion

# Load environment variables from .env file if it exists
function Import-DotEnv {
    param([string]$Path = ".env")
    if (Test-Path $Path) {
        Write-BuildLog "Loading environment variables from $Path"
        Get-Content $Path | ForEach-Object {
            if ($_ -match '^\s*([^#][^=]*)\s*=\s*(.*)\s*$') {
                $name = $matches[1].Trim()
                $value = $matches[2].Trim()
                if ($value -match '^"(.*)"$' -or $value -match "^'(.*)'$") {
                    $value = $matches[1]
                }
                [Environment]::SetEnvironmentVariable($name, $value, [EnvironmentVariableTarget]::Process)
            }
        }
    }
}

Import-DotEnv

# Enterprise certificate configuration - loaded from environment or .env file
$Global:EnterpriseCertCN = $env:CIMIAN_CERT_CN ?? 'EmilyCarrU Intune Windows Enterprise Certificate'
$Global:EnterpriseCertSubject = $env:CIMIAN_CERT_SUBJECT ?? 'EmilyCarrU'

# Script constants
$script:RootDir = $PSScriptRoot
$script:OutputDir = Join-Path $RootDir 'release'
$script:BuildDir = Join-Path $RootDir 'build'

# C# CLI Tools mapping
$Global:CSharpTools = @{
    "managedsoftwareupdate" = @{ Project = "cli/managedsoftwareupdate"; Type = "CLI" }
    "cimiimport"            = @{ Project = "cli/cimiimport"; Type = "CLI" }
    "cimipkg"               = @{ Project = "cli/cimipkg"; Type = "CLI" }
    "makecatalogs"          = @{ Project = "cli/makecatalogs"; Type = "CLI" }
    "makepkginfo"           = @{ Project = "cli/makepkginfo"; Type = "CLI" }
    "cimitrigger"           = @{ Project = "cli/cimitrigger"; Type = "CLI" }
    "manifestutil"          = @{ Project = "cli/manifestutil"; Type = "CLI" }
    "repoclean"             = @{ Project = "cli/repoclean"; Type = "CLI" }
    "cimiwatcher"           = @{ Project = "cli/cimiwatcher"; Type = "CLI" }
    "cimistatus"            = @{ Project = "gui/CimianStatus"; Type = "GUI" }
}

# GUI Applications (WPF apps that need special handling)
$Global:GuiApps = @{
    "ManagedSoftwareCenter" = @{ Project = "gui/ManagedSoftwareCenter"; Type = "GUI" }
}

#region Certificate and Signing Functions

function Test-Command {
    param ([string]$Command)
    return $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

# Cleanup old package versions, keeping only the latest
function Clean-OldPackages {
    param(
        [string]$OutputDirectory = "release",
        [int]$KeepCount = 1  # Keep this many latest versions
    )
    
    if (-not (Test-Path $OutputDirectory)) {
        return
    }
    
    Write-BuildLog "Cleaning up old build artifacts (keeping $KeepCount most recent per architecture)..." "INFO"
    
    $totalBytesRemoved = 0L
    
    # Helper: remove old files matching a pattern, keeping $KeepCount newest
    function Remove-OldFiles {
        param([string]$Directory, [string]$Pattern)
        $files = Get-ChildItem -Path $Directory -Filter $Pattern -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending
        if ($files.Count -gt $KeepCount) {
            foreach ($file in ($files | Select-Object -Skip $KeepCount)) {
                try {
                    $sizeMB = ($file.Length / 1MB).ToString('F2')
                    Remove-Item -Path $file.FullName -Force
                    Write-BuildLog "Removed old artifact: $($file.Name) ($sizeMB MB)" "INFO"
                    $script:totalBytesRemoved += $file.Length
                }
                catch {
                    Write-BuildLog "Failed to remove $($file.Name): $_" "WARNING"
                }
            }
        }
    }
    
    foreach ($arch in @('x64', 'arm64')) {
        # IntuneWin packages
        Remove-OldFiles -Directory $OutputDirectory -Pattern "Cimian-*-$arch.intunewin"
        # MSI packages
        Remove-OldFiles -Directory $OutputDirectory -Pattern "Cimian-*-$arch.msi"
        # NuGet packages
        Remove-OldFiles -Directory $OutputDirectory -Pattern "CimianTools-$arch.*.nupkg"
        # PKG packages
        Remove-OldFiles -Directory $OutputDirectory -Pattern "CimianTools-$arch-*.pkg"
    }
    
    if ($totalBytesRemoved -gt 0) {
        $freedGB = ($totalBytesRemoved / 1GB).ToString('F2')
        Write-BuildLog "Freed $freedGB GB by removing old build artifacts" "SUCCESS"
    }
}

function Get-SigningCertThumbprint {
    [OutputType([hashtable])]
    param()
    
    # Check CurrentUser store first
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { 
        $_.HasPrivateKey -and $_.Subject -like "*$Global:EnterpriseCertSubject*" 
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{ Thumbprint = $cert.Thumbprint; Store = "CurrentUser" }
    }
    
    # Check LocalMachine store
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { 
        $_.HasPrivateKey -and $_.Subject -like "*$Global:EnterpriseCertSubject*" 
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{ Thumbprint = $cert.Thumbprint; Store = "LocalMachine" }
    }
    
    return $null
}

$Global:SignToolPath = $null

function Get-SignToolPath {
    if ($Global:SignToolPath -and (Test-Path $Global:SignToolPath)) {
        return $Global:SignToolPath
    }
    
    # Check PATH (prefer x64)
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c -and $c.Source -match '\\x64\\') { 
        $Global:SignToolPath = $c.Source
        return $Global:SignToolPath
    }
    
    # Search Windows SDK
    $programFilesx86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $searchRoot = Join-Path $programFilesx86 "Windows Kits\10\bin"
    
    if (Test-Path $searchRoot) {
        $candidates = Get-ChildItem -Path $searchRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object { $_.Directory.Parent.Name } -Descending
        
        if ($candidates -and $candidates.Count -gt 0) {
            $Global:SignToolPath = $candidates[0].FullName
            return $Global:SignToolPath
        }
    }
    
    # Check registry
    try {
        $kitsRoot = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" -Name KitsRoot10 -ErrorAction SilentlyContinue
        if ($kitsRoot) { 
            $regRoot = Join-Path $kitsRoot.KitsRoot10 'bin'
            if (Test-Path $regRoot) {
                $candidates = Get-ChildItem -Path $regRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -match '\\x64\\' } |
                    Sort-Object { $_.Directory.Parent.Name } -Descending
                
                if ($candidates -and $candidates.Count -gt 0) {
                    $Global:SignToolPath = $candidates[0].FullName
                    return $Global:SignToolPath
                }
            }
        }
    } catch {}
    
    return $null
}

function Test-SignTool {
    $path = Get-SignToolPath
    if (-not $path) {
        throw "signtool.exe not found. Install Windows 10/11 SDK (Signing Tools)."
    }
}

function Invoke-SignArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Thumbprint,
        [string]$Store = "CurrentUser",
        [int]$MaxAttempts = 4
    )

    if (-not (Test-Path -LiteralPath $Path)) { 
        throw "File not found: $Path" 
    }
    
    $signToolExe = Get-SignToolPath
    if (-not $signToolExe) {
        throw "signtool.exe not found. Install Windows 10/11 SDK."
    }

    $storeParam = if ($Store -eq "CurrentUser") { "/s", "My" } else { "/s", "My", "/sm" }
    
    $tsas = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS'
    )

    $attempt = 0
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        foreach ($tsa in $tsas) {
            try {
                Write-BuildLog "Signing (attempt $attempt): $Path" "INFO"
                
                $signArgs = @(
                    "sign"
                    "/sha1", $Thumbprint
                    "/tr", $tsa
                    "/td", "sha256"
                    "/fd", "sha256"
                ) + $storeParam + @($Path)
                
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName = $signToolExe
                $psi.Arguments = $signArgs -join ' '
                $psi.UseShellExecute = $false
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                $psi.CreateNoWindow = $true
                
                $process = [System.Diagnostics.Process]::Start($psi)
                $stdout = $process.StandardOutput.ReadToEnd()
                $stderr = $process.StandardError.ReadToEnd()
                $process.WaitForExit()
                
                if ($process.ExitCode -eq 0) {
                    Write-BuildLog "Successfully signed: $Path" "SUCCESS"
                    return
                }
            }
            catch {
                Write-BuildLog "Signing attempt failed: $_" "WARNING"
            }
            
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    throw "Signing failed after $MaxAttempts attempts: $Path"
}

function Invoke-SignNuget {
    param(
        [Parameter(Mandatory)][string]$NupkgPath,
        [string]$Thumbprint
    )
    
    if (-not (Test-Path $NupkgPath)) {
        throw "NuGet package '$NupkgPath' not found."
    }
    
    if (-not $Thumbprint) {
        $certInfo = Get-SigningCertThumbprint
        $Thumbprint = if ($certInfo) { $certInfo.Thumbprint } else { $null }
    }
    
    if (-not $Thumbprint) {
        Write-BuildLog "No enterprise code-signing cert present - skipping NuGet signing." "WARNING"
        return $false
    }
    
    $tsa = 'http://timestamp.digicert.com'
    
    & nuget.exe sign $NupkgPath `
        -CertificateStoreName My `
        -CertificateSubjectName $Global:EnterpriseCertCN `
        -Timestamper $tsa
    
    if ($LASTEXITCODE) {
        Write-BuildLog "nuget sign failed ($LASTEXITCODE) for '$NupkgPath'" "WARNING"
        return $false
    }
    
    Write-BuildLog "NuGet package signed: $NupkgPath" "SUCCESS"
    return $true
}

#endregion

#region Version Functions

function Get-BuildVersion {
    $currentTime = Get-Date
    $fullVersion = $currentTime.ToString("yyyy.MM.dd.HHmm")
    $semanticVersion = "{0}.{1}.{2}.{3}" -f ($currentTime.Year - 2000), $currentTime.Month, $currentTime.Day, $currentTime.ToString("HHmm")
    
    return @{
        Full = $fullVersion
        Semantic = $semanticVersion
        MsiCompatible = "{0}.{1}.{2}{3:D2}" -f ($currentTime.Year - 2000), $currentTime.Month, $currentTime.Day, [int]$currentTime.ToString("HH")
    }
}

#endregion

#region Build Functions

function Initialize-BuildEnvironment {
    Write-BuildLog "Initializing build environment..."
    
    # Create output directories
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    
    foreach ($arch in $archs) {
        $archDir = Join-Path $OutputDir $arch
        if (-not (Test-Path $archDir)) {
            New-Item -ItemType Directory -Path $archDir -Force | Out-Null
        }
    }
    
    # Verify dotnet is available
    if (-not (Test-Command "dotnet")) {
        throw ".NET SDK not found. Please install .NET SDK."
    }
    
    $dotnetVersion = & dotnet --version
    Write-BuildLog "Using .NET SDK: $dotnetVersion" "SUCCESS"
}

function Invoke-Clean {
    Write-BuildLog "Cleaning build artifacts..."
    
    # Clean release directory
    if (Test-Path $OutputDir) {
        Remove-Item -Path "$OutputDir\*" -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    # Clean bin/obj folders in projects
    Get-ChildItem -Path $RootDir -Include 'bin', 'obj' -Recurse -Directory | ForEach-Object {
        Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    Write-BuildLog "Clean complete" -Level 'SUCCESS'
}

function Build-Solution {
    Write-BuildLog "Building solution..."
    
    $solutionPath = Join-Path $RootDir 'CimianTools.sln'
    $config = if ($Dev) { 'Debug' } else { $Configuration }
    
    $buildArgs = @(
        'build',
        $solutionPath,
        '--configuration', $config,
        '--verbosity', 'minimal'
    )
    
    Write-BuildLog "dotnet $($buildArgs -join ' ')"
    & dotnet @buildArgs
    
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed with exit code $LASTEXITCODE"
    }
    
    Write-BuildLog "Solution build complete" -Level 'SUCCESS'
}

function Publish-Binary {
    param(
        [string]$ToolName,
        [string]$ProjectPath,
        [string]$RuntimeIdentifier,
        [string]$OutputPath,
        [bool]$IsSelfContained = $true,
        [bool]$IsWinUI = $false,
        [string]$BuildVersion = ''
    )
    
    $config = if ($Dev) { 'Debug' } else { $Configuration }
    
    $publishArgs = @(
        'publish',
        $ProjectPath,
        '--configuration', $config,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', $IsSelfContained.ToString().ToLower(),
        '--output', $OutputPath,
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '--verbosity', 'minimal'
    )
    
    # WinUI 3 does not support PublishSingleFile without MSIX packaging
    if (-not $IsWinUI) {
        $publishArgs += '-p:PublishSingleFile=true'
        $publishArgs += '-p:EnableCompressionInSingleFile=true'
    }
    
    if ($BuildVersion) {
        $publishArgs += "-p:Version=$BuildVersion"
        $publishArgs += "-p:AssemblyVersion=$BuildVersion"
        $publishArgs += "-p:FileVersion=$BuildVersion"
    }
    
    Write-BuildLog "Publishing $ToolName for $RuntimeIdentifier..."
    & dotnet @publishArgs
    
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $ToolName for $RuntimeIdentifier"
    }
    
    # WinUI 3: dotnet publish --output misses .pri and .xbf files needed for XAML
    if ($IsWinUI) {
        $projectDir = Split-Path $ProjectPath -Parent
        $binDir = Join-Path $projectDir "bin\$config\net10.0-windows10.0.19041.0\$RuntimeIdentifier"
        if (Test-Path $binDir) {
            # Copy app PRI file (compiled XAML resource index)
            Get-ChildItem $binDir -Filter "*.pri" | Where-Object { $_.Name -notlike 'Microsoft.*' } | ForEach-Object {
                Copy-Item $_.FullName $OutputPath -Force
                Write-BuildLog "Copied PRI: $($_.Name)"
            }
            # Copy compiled XAML binaries (.xbf) preserving directory structure
            Get-ChildItem $binDir -Filter "*.xbf" -Recurse | ForEach-Object {
                $relativePath = $_.FullName.Substring($binDir.Length + 1)
                $destPath = Join-Path $OutputPath $relativePath
                $destDir = Split-Path $destPath -Parent
                if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
                Copy-Item $_.FullName $destPath -Force
            }
            Write-BuildLog "Copied WinUI XAML resources to output" -Level 'SUCCESS'
        }
    }
    
    Write-BuildLog "$ToolName ($RuntimeIdentifier) built successfully" -Level 'SUCCESS'
}

function Build-AllBinaries {
    param(
        [string]$SingleBinary,
        [string]$BuildVersion = ''
    )
    
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    $runtimeMap = @{ 'x64' = 'win-x64'; 'arm64' = 'win-arm64' }
    
    # Determine tools to build
    $toolsToBuild = @{}
    $guiAppsToBuild = @{}

    if ($SingleBinary) {
        if ($Global:CSharpTools.ContainsKey($SingleBinary)) {
            $toolsToBuild = @{ $SingleBinary = $Global:CSharpTools[$SingleBinary] }
        } elseif ($Global:GuiApps.ContainsKey($SingleBinary)) {
            $guiAppsToBuild = @{ $SingleBinary = $Global:GuiApps[$SingleBinary] }
        } else {
            $allNames = ($Global:CSharpTools.Keys + $Global:GuiApps.Keys) -join ', '
            throw "Unknown binary: $SingleBinary. Valid options: $allNames"
        }
    } else {
        $toolsToBuild = $Global:CSharpTools
        $guiAppsToBuild = $Global:GuiApps
    }
    
    Write-BuildLog "Building tools: $(($toolsToBuild.Keys + $guiAppsToBuild.Keys) -join ', ')"
    Write-BuildLog "Target architectures: $($archs -join ', ')"
    
    foreach ($arch in $archs) {
        $runtime = $runtimeMap[$arch]
        $outputPath = Join-Path $OutputDir $arch
        
        if (-not (Test-Path $outputPath)) {
            New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
        }
        
        foreach ($tool in $toolsToBuild.Keys) {
            $toolInfo = $toolsToBuild[$tool]
            $projectPath = Join-Path $RootDir $toolInfo.Project
            
            # Find .csproj file
            $csprojFiles = Get-ChildItem -Path $projectPath -Filter '*.csproj' -ErrorAction SilentlyContinue
            if (-not $csprojFiles -or $csprojFiles.Count -eq 0) {
                Write-BuildLog "No .csproj found in $projectPath" -Level 'WARNING'
                continue
            }
            
            $csproj = $csprojFiles[0].FullName
            $isSelfContained = ($toolInfo.Type -eq 'GUI')
            
            Publish-Binary -ToolName $tool -ProjectPath $csproj -RuntimeIdentifier $runtime -OutputPath $outputPath -IsSelfContained $isSelfContained -BuildVersion $BuildVersion
        }

        foreach ($appName in $guiAppsToBuild.Keys) {
            $appInfo = $guiAppsToBuild[$appName]
            $projectPath = Join-Path $RootDir $appInfo.Project
            
            $csprojFiles = Get-ChildItem -Path $projectPath -Filter '*.csproj' -ErrorAction SilentlyContinue
            if (-not $csprojFiles -or $csprojFiles.Count -eq 0) {
                Write-BuildLog "No .csproj found for $appName" -Level 'WARNING'
                continue
            }
            
            $csproj = $csprojFiles[0].FullName
            Publish-Binary -ToolName $appName -ProjectPath $csproj -RuntimeIdentifier $runtime -OutputPath $outputPath -IsSelfContained $true -IsWinUI $true -BuildVersion $BuildVersion
        }
    }
    
    Write-BuildLog "All binaries built successfully" -Level 'SUCCESS'
}

#endregion

#region Signing Functions

function Invoke-SignAllBinaries {
    param(
        [string]$Thumbprint,
        [string]$CertStore
    )
    
    Write-BuildLog "Signing all executables..."
    Test-SignTool
    
    # Force garbage collection to release file handles
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    Start-Sleep -Seconds 2
    
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    
    foreach ($arch in $archs) {
        $archDir = Join-Path $OutputDir $arch
        $exeFiles = Get-ChildItem -Path $archDir -Filter "*.exe" -File -ErrorAction SilentlyContinue
        
        foreach ($exe in $exeFiles) {
            try {
                Invoke-SignArtifact -Path $exe.FullName -Thumbprint $Thumbprint -Store $CertStore
            }
            catch {
                Write-BuildLog "Failed to sign $($exe.Name): $_" -Level 'WARNING'
            }
        }
    }
    
    Write-BuildLog "Binary signing complete" -Level 'SUCCESS'
}

#endregion

#region MSI Packaging Functions

function Build-MsiPackage {
    param(
        [string]$Architecture,
        [hashtable]$Version,
        [switch]$Sign,
        [string]$Thumbprint,
        [string]$CertStore
    )
    
    Write-BuildLog "Building MSI for $Architecture..." "INFO"
    
    # Check for WiX
    $wixInstalled = $null
    try {
        $wixInstalled = & dotnet tool list -g 2>&1 | Select-String "wix"
    } catch {}
    
    if (-not $wixInstalled) {
        Write-BuildLog "WiX toolset not found. Installing..." "INFO"
        & dotnet tool install --global wix 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install WiX toolset"
        }
    }
    
    $msiProjectPath = Join-Path $BuildDir "msi\Cimian.wixproj"
    $msiSourceDir = Join-Path $BuildDir "msi"
    $binDir = Join-Path $OutputDir $Architecture
    
    if (-not (Test-Path $msiProjectPath)) {
        Write-BuildLog "MSI project not found at $msiProjectPath - skipping MSI creation" "WARNING"
        return $null
    }
    
    # Copy support scripts to the binDir so WiX can find them
    $supportScripts = @(
        "install-tasks.ps1",
        "uninstall-tasks.ps1",
        "manage-service.ps1",
        "verify-installation.ps1",
        "diagnose-cimianwatcher.ps1"
    )
    foreach ($script in $supportScripts) {
        $srcPath = Join-Path $msiSourceDir $script
        if (Test-Path $srcPath) {
            Copy-Item $srcPath -Destination $binDir -Force
        }
    }
    
    $msiVersion = $Version.MsiCompatible
    Write-BuildLog "MSI version: $msiVersion (from $($Version.Full))" "INFO"
    
    # Build the MSI
    & dotnet build $msiProjectPath `
        -p:Platform=$Architecture `
        -p:ProductVersion=$msiVersion `
        -p:BinDir=$binDir `
        -o $OutputDir
        
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build MSI for $Architecture"
    }
    
    # Find and rename the MSI
    # Look for: Cimian-x64.msi, Cimian-arm64.msi, or generic Cimian.msi
    $msiFile = Get-ChildItem -Path $OutputDir -Filter "Cimian*.msi" | 
        Where-Object { $_.Name -match "^Cimian(-($Architecture|x64|arm64))?\.msi$" -and $_.Name -notmatch '^\d{4}\.\d{2}\.\d{2}\.' } | 
        Select-Object -First 1
        
    if ($msiFile) {
        $finalName = "Cimian-$($Version.Full)-$Architecture.msi"
        $finalPath = Join-Path $OutputDir $finalName
        Move-Item -Path $msiFile.FullName -Destination $finalPath -Force
        Write-BuildLog "Created MSI: $finalName" "SUCCESS"
        
        if ($Sign -and $Thumbprint) {
            Invoke-SignArtifact -Path $finalPath -Thumbprint $Thumbprint -Store $CertStore
            Write-BuildLog "Signed MSI: $finalName" "SUCCESS"
        }
        
        return $finalPath
    }
    
    Write-BuildLog "MSI file not found after build" "WARNING"
    return $null
}

#endregion

#region NuGet Packaging Functions

function Build-NuGetPackage {
    param(
        [string]$Architecture,
        [hashtable]$Version,
        [switch]$Sign,
        [string]$Thumbprint
    )
    
    Write-BuildLog "Creating NuGet package for $Architecture..." "INFO"
    
    # Ensure build directory structure
    $nuspecDir = Join-Path $BuildDir "nupkg"
    if (-not (Test-Path $nuspecDir)) {
        New-Item -ItemType Directory -Path $nuspecDir -Force | Out-Null
    }
    
    $releaseArchDir = Join-Path $OutputDir $Architecture
    
    # Verify binaries exist
    if (-not (Test-Path $releaseArchDir)) {
        Write-BuildLog "Binaries not found for $Architecture in $releaseArchDir" "WARNING"
        return $null
    }
    
    $nuspecPath = Join-Path $nuspecDir "nupkg.$Architecture.nuspec"
    
    # Create or update nuspec file
    $nuspecContent = @"
<?xml version="1.0"?>
<package>
  <metadata>
    <id>CimianTools-$Architecture</id>
    <version>$($Version.Semantic)</version>
    <title>Cimian Tools ($Architecture)</title>
    <authors>WindowsAdmins</authors>
    <owners>WindowsAdmins</owners>
    <description>Enterprise Software Deployment System for Windows - $Architecture binaries</description>
    <projectUrl>https://github.com/windowsadmins/cimian</projectUrl>
    <license type="expression">MIT</license>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <tags>deployment;software-management;windows;enterprise;intune</tags>
  </metadata>
  <files>
    <file src="..\..\..\release\$Architecture\*.exe" target="tools" />
  </files>
</package>
"@
    [System.IO.File]::WriteAllText($nuspecPath, $nuspecContent, [System.Text.Encoding]::UTF8)
    
    $nupkgOutput = Join-Path $OutputDir "CimianTools-$Architecture.$($Version.Semantic).nupkg"
    
    # Method 1: Try using nuget.exe directly (simplest, most reliable)
    if (Test-Command "nuget") {
        Write-BuildLog "Using nuget.exe to create package..." "INFO"
        try {
            Push-Location $nuspecDir
            & nuget pack "nupkg.$Architecture.nuspec" -OutputDirectory $OutputDir -NonInteractive -ForceEnglishOutput 2>&1 | Out-Null
            Pop-Location
            
            if ($LASTEXITCODE -eq 0 -and (Test-Path $nupkgOutput)) {
                Write-BuildLog "Created NuGet package: $(Split-Path $nupkgOutput -Leaf)" "SUCCESS"
                
                if ($Sign) {
                    Invoke-SignNuget -NupkgPath $nupkgOutput -Thumbprint $Thumbprint
                }
                
                return $nupkgOutput
            }
        }
        catch {
            Write-BuildLog "nuget.exe method failed: $_" "DEBUG"
        }
    }
    
    # Method 2: Create ZIP-based .nupkg manually (no external tools required)
    Write-BuildLog "Creating NuGet package using ZIP method..." "INFO"
    
    try {
        # Create temp directory structure
        $tempDir = Join-Path $nuspecDir "temp_nupkg_$Architecture"
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        
        # Create tools directory with binaries
        $toolsDir = Join-Path $tempDir "tools"
        New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
        Copy-Item "$releaseArchDir\*.exe" -Destination $toolsDir -Force -ErrorAction Stop
        
        # Create package metadata directory
        $metaDir = Join-Path $tempDir "_package"
        New-Item -ItemType Directory -Path $metaDir -Force | Out-Null
        
        # Create [Content_Types].xml (required for .nupkg)
        $contentTypes = @"
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="exe" ContentType="application/octet-stream" />
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Default Extension="xml" ContentType="application/xml" />
</Types>
"@
        [System.IO.File]::WriteAllText("$tempDir\[Content_Types].xml", $contentTypes, [System.Text.Encoding]::UTF8)
        
        # Create .rels file
        $relsDir = Join-Path $tempDir "_rels"
        New-Item -ItemType Directory -Path $relsDir -Force | Out-Null
        $relsContent = @"
<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Id="rel0" Target="package/services/metadata/core-properties/metadata.psmdcp" />
</Relationships>
"@
        [System.IO.File]::WriteAllText("$relsDir\.rels", $relsContent, [System.Text.Encoding]::UTF8)
        
        # Create manifest file
        $manifestContent = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/01/nuspec.xsd">
  <metadata>
    <id>CimianTools-$Architecture</id>
    <version>$($Version.Semantic)</version>
    <title>Cimian Tools ($Architecture)</title>
    <authors>WindowsAdmins</authors>
    <owners>WindowsAdmins</owners>
    <description>Enterprise Software Deployment System for Windows - $Architecture binaries</description>
    <projectUrl>https://github.com/windowsadmins/cimian</projectUrl>
    <license type="expression">MIT</license>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <tags>deployment;software-management;windows;enterprise;intune</tags>
  </metadata>
</package>
"@
        [System.IO.File]::WriteAllText("$metaDir\manifest.xml", $manifestContent, [System.Text.Encoding]::UTF8)
        
        # Create ZIP archive as .nupkg
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        
        # Remove old file if exists
        if (Test-Path $nupkgOutput) {
            Remove-Item $nupkgOutput -Force
        }
        
        # Create ZIP with proper compression
        [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $nupkgOutput, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        
        # Clean up temp directory
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        
        if (Test-Path $nupkgOutput) {
            Write-BuildLog "Created NuGet package: $(Split-Path $nupkgOutput -Leaf)" "SUCCESS"
            
            # Note: NuGet signing requires nuget.exe. For ZIP-based packages, signing is skipped.
            # To sign: install nuget.commandline and run: .\build.ps1 -NupkgOnly -Sign
            if ($Sign -and (Test-Command "nuget")) {
                Invoke-SignNuget -NupkgPath $nupkgOutput -Thumbprint $Thumbprint | Out-Null
            }
            
            return $nupkgOutput
        }
    }
    catch {
        Write-BuildLog "NuGet package creation failed: $_" "ERROR"
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    Write-BuildLog "NuGet package creation failed for $Architecture" "WARNING"
    return $null
}

#endregion

#region Pkg Packaging Functions

function Build-PkgPackage {
    param(
        [string]$Architecture,
        [hashtable]$Version,
        [switch]$Sign,
        [string]$Thumbprint
    )
    
    Write-BuildLog "Creating .pkg package for $Architecture..." "INFO"
    
    $binariesDir = "release\$Architecture"
    
    # Check if binaries directory exists
    if (-not (Test-Path $binariesDir)) {
        Write-BuildLog "Binaries directory not found for $Architecture architecture: $binariesDir" "WARNING"
        return $null
    }
    
    # cimipkg.exe must run on the host machine, so prefer the host-architecture binary.
    # Fall back to x64 since we are always building on x64 Windows.
    $hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' } else { 'x64' }
    $cimipkgPath = Join-Path "release\$hostArch" "cimipkg.exe"
    if (-not (Test-Path $cimipkgPath)) {
        # Last-resort fallback to whichever arch was built
        $cimipkgPath = Join-Path $binariesDir "cimipkg.exe"
    }
    
    if (-not (Test-Path $cimipkgPath)) {
        Write-BuildLog "cimipkg.exe not found (tried host arch '$hostArch' and '$Architecture')" "WARNING"
        Write-BuildLog "Build cimipkg first with: .\build.ps1 -Architecture x64 -Sign" "INFO"
        return $null
    }
    
    Write-BuildLog "Using cimipkg.exe from: $cimipkgPath" "INFO"
    
    # Create temporary .pkg build directory
    $pkgTempDir = "release\pkg_$Architecture"
    if (Test-Path $pkgTempDir) { Remove-Item $pkgTempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $pkgTempDir -Force | Out-Null
    
    # Create payload directory and copy all binaries
    $payloadDir = Join-Path $pkgTempDir "payload"
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    
    Write-BuildLog "Copying CimianTools binaries for $Architecture architecture to .pkg payload..." "INFO"
    $expectedExecutables = @(
        'cimiwatcher.exe', 'managedsoftwareupdate.exe', 'cimitrigger.exe', 'cimistatus.exe',
        'cimiimport.exe', 'cimipkg.exe', 'makecatalogs.exe', 'makepkginfo.exe', 'manifestutil.exe',
        'repoclean.exe', 'Managed Software Center.exe'
    )
    
    $missingExecutables = @()
    foreach ($exe in $expectedExecutables) {
        $sourcePath = Join-Path $binariesDir $exe
        $destPath = Join-Path $payloadDir $exe
        
        if (Test-Path $sourcePath) {
            Copy-Item $sourcePath $destPath -Force
            Write-BuildLog "Copied $exe to .pkg payload" "INFO"
        } else {
            $missingExecutables += $exe
        }
    }
    
    if ($missingExecutables.Count -gt 0) {
        Write-BuildLog "Missing executables for $Architecture architecture: $($missingExecutables -join ', ')" "WARNING"
        Write-BuildLog "Continuing with available executables..." "WARNING"
    }
    
    # Create scripts directory and copy install scripts
    $scriptsDir = Join-Path $pkgTempDir "scripts"
    New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null
    
    $releaseVersion = $Version.Full
    $semanticVersion = $Version.Semantic
    
    # Copy postinstall script
    $postinstallTemplatePath = "build\pkg\postinstall.ps1"
    $postinstallPath = Join-Path $scriptsDir "postinstall.ps1"
    if (Test-Path $postinstallTemplatePath) {
        $postinstallTemplate = Get-Content $postinstallTemplatePath -Raw
        $postinstallContent = $postinstallTemplate -replace '\{\{VERSION\}\}', $semanticVersion
        $postinstallContent | Set-Content $postinstallPath -Encoding UTF8
        Write-BuildLog "Created postinstall.ps1 script in scripts directory for .pkg" "INFO"
    } else {
        Write-BuildLog "Postinstall template not found: $postinstallTemplatePath" "WARNING"
    }
    
    # Copy preinstall script
    $preinstallTemplatePath = "build\pkg\preinstall.ps1"
    $preinstallPath = Join-Path $scriptsDir "preinstall.ps1"
    if (Test-Path $preinstallTemplatePath) {
        $preinstallTemplate = Get-Content $preinstallTemplatePath -Raw
        $preinstallContent = $preinstallTemplate -replace '\{\{VERSION\}\}', $semanticVersion
        $preinstallContent | Set-Content $preinstallPath -Encoding UTF8
        Write-BuildLog "Created preinstall.ps1 script in scripts directory for .pkg" "INFO"
    } else {
        Write-BuildLog "Preinstall template not found: $preinstallTemplatePath" "WARNING"
    }
    
    # Create build-info.yaml from template
    $buildInfoTemplatePath = "build\pkg\build-info.yaml"
    $buildInfoPath = Join-Path $pkgTempDir "build-info.yaml"
    if (Test-Path $buildInfoTemplatePath) {
        $buildInfoTemplate = Get-Content $buildInfoTemplatePath -Raw
        $buildInfoContent = $buildInfoTemplate -replace '\{\{VERSION\}\}', $releaseVersion
        $buildInfoContent | Set-Content $buildInfoPath -Encoding UTF8
        Write-BuildLog "Created build-info.yaml with version $releaseVersion" "INFO"
    } else {
        Write-BuildLog "build-info.yaml template not found: $buildInfoTemplatePath" "WARNING"
    }
    
    # Build the .pkg package using cimipkg
    try {
        $cimipkgArgs = @("--verbose", $pkgTempDir)
        
        Write-BuildLog "Running cimipkg.exe to create .pkg package..." "INFO"
        $process = Start-Process -FilePath $cimipkgPath -ArgumentList $cimipkgArgs -Wait -NoNewWindow -PassThru
        
        if ($process.ExitCode -eq 0) {
            Write-BuildLog ".pkg package created successfully for $Architecture" "SUCCESS"
            
            # Look for the created .pkg file in the build subdirectory
            $buildDir = Join-Path $pkgTempDir "build"
            if (Test-Path $buildDir) {
                $createdPkgFiles = Get-ChildItem -Path $buildDir -Filter "*.pkg"
                foreach ($pkgFile in $createdPkgFiles) {
                    $pkgSize = $pkgFile.Length
                    Write-BuildLog ".pkg package created: $($pkgFile.Name) ($([math]::Round($pkgSize / 1MB, 2)) MB)" "INFO"
                    
                    # Move to release directory with expected naming
                    $expectedName = "CimianTools-$Architecture-$releaseVersion.pkg"
                    $targetPath = "release\$expectedName"
                    Copy-Item $pkgFile.FullName $targetPath -Force
                    Write-BuildLog "Moved .pkg package to: $expectedName" "INFO"
                    
                    return $targetPath
                }
            } else {
                Write-BuildLog "Build directory not found after cimipkg execution: $buildDir" "WARNING"
            }
        } else {
            Write-BuildLog "Failed to create .pkg package for $Architecture (exit code: $($process.ExitCode))" "ERROR"
        }
    }
    catch {
        Write-BuildLog "Error creating .pkg package for $Architecture : $_" "ERROR"
    }
    finally {
        # Clean up temporary directory
        Remove-Item $pkgTempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    return $null
}

#endregion

#region IntuneWin Packaging Functions

function Build-IntuneWinPackage {
    param(
        [string]$Architecture,
        [hashtable]$Version
    )
    
    Write-BuildLog "Creating IntuneWin package for $Architecture..." "INFO"
    
    # Check for IntuneWinAppUtil
    $intuneUtil = Get-Command "IntuneWinAppUtil.exe" -ErrorAction SilentlyContinue
    if (-not $intuneUtil) {
        Write-BuildLog "IntuneWinAppUtil.exe not found - skipping IntuneWin package creation" "WARNING"
        return $null
    }
    
    $msiFile = Join-Path $OutputDir "Cimian-$($Version.Full)-$Architecture.msi"
    
    if (-not (Test-Path $msiFile)) {
        Write-BuildLog "MSI file not found for $Architecture - cannot create IntuneWin package" "WARNING"
        return $null
    }
    
    $intunewinOutput = Join-Path $OutputDir "Cimian-$($Version.Full)-$Architecture.intunewin"
    
    # Remove existing
    if (Test-Path $intunewinOutput) {
        Remove-Item $intunewinOutput -Force
    }
    
    # Create IntuneWin package
    $intuneProcess = Start-Process -FilePath "IntuneWinAppUtil.exe" `
        -ArgumentList "-c", "`"$OutputDir`"", "-s", "`"$msiFile`"", "-o", "`"$OutputDir`"", "-q" `
        -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$env:TEMP\intune_$Architecture.log" `
        -RedirectStandardError "$env:TEMP\intune_${Architecture}_err.log"
    
    if ($intuneProcess.ExitCode -eq 0) {
        # Find and rename the generated file
        $generatedFile = Get-ChildItem -Path $OutputDir -Filter "*.intunewin" |
            Where-Object { $_.Name -like "*Cimian*$Architecture*" } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
            
        if ($generatedFile -and $generatedFile.FullName -ne $intunewinOutput) {
            Move-Item $generatedFile.FullName $intunewinOutput -Force
        }
        
        if (Test-Path $intunewinOutput) {
            Write-BuildLog "Created IntuneWin package: $(Split-Path $intunewinOutput -Leaf)" "SUCCESS"
            return $intunewinOutput
        }
    }
    else {
        Write-BuildLog "IntuneWinAppUtil failed with exit code $($intuneProcess.ExitCode)" "WARNING"
    }
    
    # Cleanup temp files
    Remove-Item "$env:TEMP\intune_$Architecture.log" -ErrorAction SilentlyContinue
    Remove-Item "$env:TEMP\intune_${Architecture}_err.log" -ErrorAction SilentlyContinue
    
    return $null
}

#endregion

#region Installation Functions

function Install-MsiPackage {
    param([string]$MsiPath)
    
    if (-not (Test-Path $MsiPath)) {
        Write-BuildLog "MSI package not found: $MsiPath" "ERROR"
        return $false
    }
    
    Write-BuildLog "Installing MSI package: $MsiPath" "INFO"
    
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]"Administrator")
    
    $absoluteMsiPath = (Resolve-Path $MsiPath).Path
    
    if ($isAdmin) {
        $installProcess = Start-Process -FilePath "msiexec.exe" `
            -ArgumentList "/i", "`"$absoluteMsiPath`"", "/qn", "/l*v", "`"$env:TEMP\cimian_install.log`"" `
            -Wait -PassThru
            
        if ($installProcess.ExitCode -eq 0) {
            Write-BuildLog "MSI installation completed successfully" "SUCCESS"
            return $true
        }
        else {
            Write-BuildLog "MSI installation failed with exit code $($installProcess.ExitCode)" "ERROR"
            return $false
        }
    }
    else {
        # Try sudo if available
        if (Get-Command "sudo" -ErrorAction SilentlyContinue) {
            Write-BuildLog "Using sudo for elevated installation..." "INFO"
            $sudoProcess = Start-Process -FilePath "sudo" `
                -ArgumentList "msiexec.exe", "/i", "`"$absoluteMsiPath`"", "/qn" `
                -Wait -PassThru
                
            if ($sudoProcess.ExitCode -eq 0) {
                Write-BuildLog "MSI installation completed via sudo" "SUCCESS"
                return $true
            }
        }
        
        Write-BuildLog "Administrator privileges required for installation" "ERROR"
        return $false
    }
}

#endregion

#region Development Mode Functions

function Enter-DevelopmentMode {
    Write-BuildLog "Development mode enabled - preparing for rapid iteration..." "INFO"
    
    # Stop Cimian services
    $services = @("CimianWatcher")
    foreach ($serviceName in $services) {
        try {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($service -and $service.Status -eq "Running") {
                Write-BuildLog "Stopping service: $serviceName" "INFO"
                Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            Write-BuildLog "Could not stop service $serviceName" "WARNING"
        }
    }
    
    # Kill running processes
    $processes = @("cimistatus", "cimiwatcher", "managedsoftwareupdate", "Managed Software Center")
    foreach ($processName in $processes) {
        try {
            Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Write-BuildLog "Stopped $processName process" "INFO"
        }
        catch {
            # Normal if process not running
        }
    }
    
    Write-BuildLog "Development mode preparation complete" "SUCCESS"
}

#endregion

#region Summary Functions

function Show-BuildSummary {
    param([hashtable]$Version)
    
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "                      BUILD SUMMARY                            " -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""
    
    Write-Host "Version:       " -NoNewline; Write-Host $Version.Full -ForegroundColor Yellow
    Write-Host "Configuration: " -NoNewline; Write-Host $(if ($Dev) { 'Debug' } else { $Configuration }) -ForegroundColor Yellow
    Write-Host "Architecture:  " -NoNewline; Write-Host $Architecture -ForegroundColor Yellow
    Write-Host "Output:        " -NoNewline; Write-Host $OutputDir -ForegroundColor Yellow
    Write-Host ""
    
    # List built files
    if (Test-Path $OutputDir) {
        $cwdRelease = "." + $OutputDir.Substring($RootDir.Length)
        Write-Host "Built Artifacts:" -ForegroundColor Green
        Get-ChildItem -Path $OutputDir -Recurse -Include "*.msi","*.nupkg","*.pkg","*.intunewin" | Sort-Object Name | ForEach-Object {
            $relativePath = $_.FullName.Replace($OutputDir, '').TrimStart('\')
            $size = [math]::Round($_.Length / 1MB, 1)
            Write-Host "  • $cwdRelease\$relativePath ($size MB)" -ForegroundColor White
        }
    }
    
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
}

#endregion

#region Main Execution

try {
    $startTime = Get-Date
    $version = Get-BuildVersion
    
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "║          CIMIAN TOOLS BUILD SYSTEM                            ║" -ForegroundColor Cyan
    Write-Host "║          Version: $($version.Full)                             ║" -ForegroundColor Cyan
    Write-Host "╚═══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host ""
    
    # Handle development mode
    if ($Dev) {
        Enter-DevelopmentMode
        $NoSign = $true  # Development mode skips signing
    }
    
    # Handle signing configuration
    $shouldSign = -not $NoSign
    $actualThumbprint = ""
    $certStore = "CurrentUser"
    
    if ($Thumbprint) {
        $actualThumbprint = $Thumbprint
        Write-BuildLog "Using provided certificate thumbprint" "INFO"
    }
    elseif ($shouldSign) {
        $certInfo = Get-SigningCertThumbprint
        if ($certInfo) {
            $actualThumbprint = $certInfo.Thumbprint
            $certStore = $certInfo.Store
            Write-BuildLog "Enterprise certificate auto-detected: $actualThumbprint" "SUCCESS"
        }
        else {
            Write-BuildLog "No enterprise certificate found - binaries will be unsigned" "WARNING"
            $shouldSign = $false
        }
    }
    else {
        Write-BuildLog "Signing disabled - binaries will be unsigned" "WARNING"
    }
    
    # Handle SignMSI mode
    if ($SignMSI) {
        Write-BuildLog "SignMSI mode - signing existing MSI files..." "INFO"
        
        if (-not $shouldSign) {
            throw "Cannot sign MSI files without a valid certificate."
        }
        
        Test-SignTool
        
        $msiFiles = Get-ChildItem -Path $OutputDir -Filter "*.msi" -File -ErrorAction SilentlyContinue
        
        if ($msiFiles.Count -eq 0) {
            Write-BuildLog "No MSI files found in release directory." "WARNING"
            exit 0
        }
        
        foreach ($msi in $msiFiles) {
            Invoke-SignArtifact -Path $msi.FullName -Thumbprint $actualThumbprint -Store $certStore
        }
        
        Write-BuildLog "SignMSI completed." "SUCCESS"
        exit 0
    }
    
    # Initialize build environment
    Initialize-BuildEnvironment
    
    # Clean up stale artifacts from previous builds
    Clean-OldPackages -OutputDirectory $OutputDir -KeepCount 1
    
    # Clean if requested
    if ($Clean) {
        Invoke-Clean
    }
    
    # Handle Binaries/Binary flags - force Task to build only
    if ($Binaries -or $Binary) {
        $Task = "build"
        Write-BuildLog "Binaries flag detected - will only build and sign .exe files, skipping all packaging." "INFO"
    }
    
    # Handle package-only modes - force Task to package
    if ($PackageOnly -or $NupkgOnly -or $MsiOnly -or $PkgOnly) {
        $Task = "package"
        if ($PkgOnly) {
            Write-BuildLog ".pkg-only mode: Will create .pkg packages using cimipkg" "INFO"
        } elseif ($NupkgOnly) {
            Write-BuildLog "NuGet-only mode: Will create NuGet packages" "INFO"
        } elseif ($MsiOnly) {
            Write-BuildLog "MSI-only mode: Will create MSI packages" "INFO"
        } else {
            Write-BuildLog "Package-only mode: Will create MSI, NuGet, and .pkg packages" "INFO"
        }
    }
    
    # Build phase
    if (-not ($PackageOnly -or $NupkgOnly -or $MsiOnly -or $PkgOnly) -and ($Task -eq "build" -or $Task -eq "all")) {
        # Build solution first
        Build-Solution
        
        # Build binaries (pass consistent version to all builds)
        if ($Binary) {
            Build-AllBinaries -SingleBinary $Binary -BuildVersion $version.Full
        }
        elseif ($Binaries) {
            Build-AllBinaries -BuildVersion $version.Full
        }
        else {
            Build-AllBinaries -BuildVersion $version.Full
        }
    }
    
    # Signing phase
    if ($shouldSign -and -not ($PackageOnly -or $NupkgOnly -or $MsiOnly -or $PkgOnly)) {
        Invoke-SignAllBinaries -Thumbprint $actualThumbprint -CertStore $certStore
    }
    
    # Early exit for binaries-only mode or build-only task
    if ($Binaries -or $Binary -or $Task -eq "build") {
        Show-BuildSummary -Version $version
        $elapsed = (Get-Date) - $startTime
        Write-BuildLog "Build completed in $($elapsed.TotalSeconds.ToString('F1')) seconds" -Level 'SUCCESS'
        exit 0
    }
    
    # Packaging phase (Task = package or all)
    if ($Task -eq "package" -or $Task -eq "all") {
        # Clean up old packages before creating new ones
        Clean-OldPackages -OutputDirectory $OutputDir -KeepCount 1
        
        $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
        
        # MSI packages (unless skipped)
        if (-not $SkipMSI -and -not $NupkgOnly -and -not $PkgOnly) {
            foreach ($arch in $archs) {
                $msiPath = Build-MsiPackage -Architecture $arch -Version $version -Sign:$shouldSign -Thumbprint $actualThumbprint -CertStore $certStore
            }
        }
        
        # NuGet packages (unless skipped)
        if (-not $MsiOnly -and -not $PkgOnly) {
            foreach ($arch in $archs) {
                $nupkgPath = Build-NuGetPackage -Architecture $arch -Version $version -Sign:$shouldSign -Thumbprint $actualThumbprint
            }
        }
        
        # .pkg packages (always created in full builds)
        if (-not $MsiOnly -and -not $NupkgOnly) {
            foreach ($arch in $archs) {
                $pkgPath = Build-PkgPackage -Architecture $arch -Version $version -Sign:$shouldSign -Thumbprint $actualThumbprint
            }
        }
        
        # IntuneWin packages (if requested)
        if ($IntuneWin) {
            foreach ($arch in $archs) {
                $intunewinPath = Build-IntuneWinPackage -Architecture $arch -Version $version
            }
        }
    }
    
    # Installation (if requested)
    if ($Install) {
        Write-BuildLog "Install flag detected - installing MSI package..." "INFO"
        
        # Stop services before installation
        Enter-DevelopmentMode
        
        $currentArch = if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64") { "x64" } else { "arm64" }
        $msiToInstall = Join-Path $OutputDir "Cimian-$($version.Full)-$currentArch.msi"
        
        if (-not (Test-Path $msiToInstall)) {
            $msiToInstall = Get-ChildItem -Path $OutputDir -Filter "Cimian-*-$currentArch.msi" | Select-Object -First 1
            if ($msiToInstall) { $msiToInstall = $msiToInstall.FullName }
        }
        
        if ($msiToInstall -and (Test-Path $msiToInstall)) {
            $installSuccess = Install-MsiPackage -MsiPath $msiToInstall
            if ($installSuccess) {
                Write-BuildLog "Cimian has been successfully installed!" "SUCCESS"
            }
        }
        else {
            Write-BuildLog "No MSI package found for installation" "ERROR"
        }
    }
    
    # Final cleanup - ensure only latest version is kept
    if ($Task -eq "package" -or $Task -eq "all") {
        Clean-OldPackages -OutputDirectory $OutputDir -KeepCount 1
    }
    
    # Summary
    Show-BuildSummary -Version $version
    
    $elapsed = (Get-Date) - $startTime
    Write-BuildLog "Build completed in $($elapsed.TotalSeconds.ToString('F1')) seconds" -Level 'SUCCESS'
}
catch {
    Write-BuildLog "Build failed: $_" -Level 'ERROR'
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}

#endregion
