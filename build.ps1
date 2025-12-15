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
    .\build.ps1 -IntuneWin
    # Full build including .intunewin packages

.EXAMPLE
    .\build.ps1 -SignMSI
    # Sign existing MSI files in release directory
#>

[CmdletBinding()]
param(
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
    "cimistatus"            = @{ Project = "apps/CimianStatus"; Type = "GUI" }
}

# GUI Applications (WPF apps that need special handling)
$Global:GuiApps = @{
    "ManagedSoftwareCenter" = @{ Project = "apps/ManagedSoftwareCenter"; Type = "GUI" }
}

#region Certificate and Signing Functions

function Test-Command {
    param ([string]$Command)
    return $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
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
        [bool]$IsSelfContained = $true
    )
    
    $config = if ($Dev) { 'Debug' } else { $Configuration }
    
    $publishArgs = @(
        'publish',
        $ProjectPath,
        '--configuration', $config,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', $IsSelfContained.ToString().ToLower(),
        '--output', $OutputPath,
        '-p:PublishSingleFile=true',
        '-p:PublishReadyToRun=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '--verbosity', 'minimal'
    )
    
    Write-BuildLog "Publishing $ToolName for $RuntimeIdentifier..."
    & dotnet @publishArgs
    
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $ToolName for $RuntimeIdentifier"
    }
    
    Write-BuildLog "$ToolName ($RuntimeIdentifier) built successfully" -Level 'SUCCESS'
}

function Build-AllBinaries {
    param([string]$SingleBinary)
    
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    $runtimeMap = @{ 'x64' = 'win-x64'; 'arm64' = 'win-arm64' }
    
    # Determine tools to build
    $toolsToBuild = if ($SingleBinary) {
        if (-not $Global:CSharpTools.ContainsKey($SingleBinary)) {
            throw "Unknown binary: $SingleBinary. Valid options: $($Global:CSharpTools.Keys -join ', ')"
        }
        @{ $SingleBinary = $Global:CSharpTools[$SingleBinary] }
    } else {
        $Global:CSharpTools
    }
    
    Write-BuildLog "Building tools: $($toolsToBuild.Keys -join ', ')"
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
            
            Publish-Binary -ToolName $tool -ProjectPath $csproj -RuntimeIdentifier $runtime -OutputPath $outputPath -IsSelfContained $isSelfContained
        }
    }
    
    # Also build GUI apps if not building single binary
    if (-not $SingleBinary) {
        foreach ($appName in $Global:GuiApps.Keys) {
            $appInfo = $Global:GuiApps[$appName]
            $projectPath = Join-Path $RootDir $appInfo.Project
            
            $csprojFiles = Get-ChildItem -Path $projectPath -Filter '*.csproj' -ErrorAction SilentlyContinue
            if (-not $csprojFiles -or $csprojFiles.Count -eq 0) {
                Write-BuildLog "No .csproj found for $appName" -Level 'WARNING'
                continue
            }
            
            $csproj = $csprojFiles[0].FullName
            
            foreach ($arch in $archs) {
                $runtime = $runtimeMap[$arch]
                $outputPath = Join-Path $OutputDir $arch
                Publish-Binary -ToolName $appName -ProjectPath $csproj -RuntimeIdentifier $runtime -OutputPath $outputPath -IsSelfContained $true
            }
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
    $binDir = Join-Path $OutputDir $Architecture
    
    if (-not (Test-Path $msiProjectPath)) {
        Write-BuildLog "MSI project not found at $msiProjectPath - skipping MSI creation" "WARNING"
        return $null
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
    $msiFile = Get-ChildItem -Path $OutputDir -Filter "Cimian*.msi" | 
        Where-Object { $_.Name -eq "Cimian.msi" -or $_.Name -notmatch '^\d{4}\.\d{2}\.\d{2}\.' } | 
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
    
    # Check for nuget
    if (-not (Test-Command "nuget")) {
        Write-BuildLog "nuget.exe not found - skipping NuGet package creation" "WARNING"
        return $null
    }
    
    $nuspecDir = Join-Path $BuildDir "nupkg"
    $nuspecPath = Join-Path $nuspecDir "nupkg.$Architecture.nuspec"
    
    # If architecture-specific nuspec doesn't exist, create a generic one
    if (-not (Test-Path $nuspecPath)) {
        Write-BuildLog "Creating NuGet spec for $Architecture..." "INFO"
        
        if (-not (Test-Path $nuspecDir)) {
            New-Item -ItemType Directory -Path $nuspecDir -Force | Out-Null
        }
        
        $nuspecContent = @"
<?xml version="1.0"?>
<package>
  <metadata>
    <id>CimianTools-$Architecture</id>
    <version>{{VERSION}}</version>
    <title>Cimian Tools ($Architecture)</title>
    <authors>WindowsAdmins</authors>
    <owners>WindowsAdmins</owners>
    <description>Enterprise Software Deployment System for Windows - $Architecture binaries</description>
    <projectUrl>https://github.com/windowsadmins/cimian</projectUrl>
    <license type="expression">MIT</license>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <tags>deployment software-management windows enterprise intune</tags>
  </metadata>
  <files>
    <file src="..\..\release\$Architecture\*.exe" target="tools" />
  </files>
</package>
"@
        $nuspecContent | Set-Content -Path $nuspecPath -Encoding UTF8
    }
    
    # Create temp nuspec with version substitution
    $nuspecContent = Get-Content $nuspecPath -Raw
    $nuspecWithVersion = $nuspecContent -replace '\{\{VERSION\}\}', $Version.Semantic
    $tempNuspecPath = Join-Path $nuspecDir "temp_nupkg_$Architecture.nuspec"
    $nuspecWithVersion | Set-Content $tempNuspecPath -Encoding UTF8
    
    $nupkgOutput = Join-Path $OutputDir "CimianTools-$Architecture-$($Version.Semantic).nupkg"
    
    # Pack
    & nuget pack $tempNuspecPath -OutputDirectory $OutputDir -BasePath $nuspecDir -NoDefaultExcludes
    
    if ($LASTEXITCODE -ne 0) {
        Write-BuildLog "NuGet pack failed for $Architecture" "WARNING"
        Remove-Item $tempNuspecPath -Force -ErrorAction SilentlyContinue
        return $null
    }
    
    # Find and rename the package
    $builtPkg = Get-ChildItem $OutputDir -Filter "CimianTools-$Architecture*.nupkg" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    
    if ($builtPkg -and $builtPkg.FullName -ne $nupkgOutput) {
        Move-Item $builtPkg.FullName $nupkgOutput -Force
    }
    
    # Clean up temp file
    Remove-Item $tempNuspecPath -Force -ErrorAction SilentlyContinue
    
    if (Test-Path $nupkgOutput) {
        Write-BuildLog "Created NuGet package: $(Split-Path $nupkgOutput -Leaf)" "SUCCESS"
        
        if ($Sign) {
            Invoke-SignNuget -NupkgPath $nupkgOutput -Thumbprint $Thumbprint
        }
        
        return $nupkgOutput
    }
    
    Write-BuildLog "NuGet package not found after build" "WARNING"
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
    $processes = @("cimistatus", "cimiwatcher", "managedsoftwareupdate", "ManagedSoftwareCenter")
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
        Write-Host "Built Artifacts:" -ForegroundColor Green
        Get-ChildItem -Path $OutputDir -Recurse -Include "*.exe","*.msi","*.nupkg","*.intunewin" | ForEach-Object {
            $relativePath = $_.FullName.Replace($OutputDir, '').TrimStart('\')
            $size = [math]::Round($_.Length / 1MB, 1)
            Write-Host "  • $relativePath ($size MB)" -ForegroundColor White
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
    
    # Clean if requested
    if ($Clean) {
        Invoke-Clean
    }
    
    # Build phase
    if (-not ($PackageOnly -or $NupkgOnly -or $MsiOnly)) {
        # Build solution first
        Build-Solution
        
        # Build binaries
        if ($Binary) {
            Build-AllBinaries -SingleBinary $Binary
        }
        elseif ($Binaries) {
            Build-AllBinaries
        }
        else {
            Build-AllBinaries
        }
    }
    
    # Signing phase
    if ($shouldSign -and -not ($PackageOnly -or $NupkgOnly -or $MsiOnly)) {
        Invoke-SignAllBinaries -Thumbprint $actualThumbprint -CertStore $certStore
    }
    
    # Early exit for binaries-only mode
    if ($Binaries -or $Binary) {
        Show-BuildSummary -Version $version
        $elapsed = (Get-Date) - $startTime
        Write-BuildLog "Build completed in $($elapsed.TotalSeconds.ToString('F1')) seconds" -Level 'SUCCESS'
        exit 0
    }
    
    # Packaging phase
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    
    # MSI packages (unless skipped)
    if (-not $SkipMSI -and -not $NupkgOnly) {
        foreach ($arch in $archs) {
            $msiPath = Build-MsiPackage -Architecture $arch -Version $version -Sign:$shouldSign -Thumbprint $actualThumbprint -CertStore $certStore
        }
    }
    
    # NuGet packages (unless skipped)
    if (-not $MsiOnly) {
        foreach ($arch in $archs) {
            $nupkgPath = Build-NuGetPackage -Architecture $arch -Version $version -Sign:$shouldSign -Thumbprint $actualThumbprint
        }
    }
    
    # IntuneWin packages (if requested)
    if ($IntuneWin) {
        foreach ($arch in $archs) {
            $intunewinPath = Build-IntuneWinPackage -Architecture $arch -Version $version
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
