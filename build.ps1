<#
.SYNOPSIS
    Builds the Cimian C# project with enterprise code signing and MSI packaging.
.DESCRIPTION
    This script automates the build and packaging process for the C# migration of Cimian,
    including building .NET binaries, signing them with enterprise certificates, and creating MSI installers.
    
    DEFAULT BEHAVIOR: Running .\build.ps1 with no parameters builds everything (binaries + MSI) with signing.
    
    Version Format: YYYY.MM.DD.HHMM (e.g., 2025.12.04.1430)
    MSI versions are automatically converted to compatible format (YY.MM.DDHH).
#>
param(
    [switch]$NoSign,     # Skip code signing (for development only)
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
    [switch]$MsiOnly       # Create MSI packages only using existing binaries (skip build and NUPKG)
)

# Load environment variables from .env file if it exists
function Import-DotEnv {
    param([string]$Path = ".env")
    
    if (Test-Path $Path) {
        Write-Host "Loading environment variables from $Path" -ForegroundColor Green
        Get-Content $Path | ForEach-Object {
            if ($_ -match '^\s*([^#][^=]*)\s*=\s*(.*)\s*$') {
                $name = $matches[1].Trim()
                $value = $matches[2].Trim()
                # Remove surrounding quotes if present
                if ($value -match '^"(.*)"$' -or $value -match "^'(.*)'$") {
                    $value = $matches[1]
                }
                [Environment]::SetEnvironmentVariable($name, $value, [EnvironmentVariableTarget]::Process)
                Write-Host "  $name = $value" -ForegroundColor Gray
            }
        }
    } else {
        Write-Host "No .env file found at $Path" -ForegroundColor Yellow
    }
}

# Load .env file first
Import-DotEnv

# Enterprise certificate configuration - loaded from environment or .env file
$Global:EnterpriseCertCN = $env:CIMIAN_CERT_CN ?? 'DefaultEnterprise'
$Global:EnterpriseCertSubject = $env:CIMIAN_CERT_SUBJECT ?? 'DefaultEnterprise'
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

function Test-Command {
    param ([string]$Command)
    return $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

function Get-SigningCertThumbprint {
    [OutputType([hashtable])]
    param()
    
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and $_.Subject -like "*$Global:EnterpriseCertSubject*" } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{ Thumbprint = $cert.Thumbprint; Store = "CurrentUser" }
    }
    
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.HasPrivateKey -and $_.Subject -like "*$Global:EnterpriseCertSubject*" } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{ Thumbprint = $cert.Thumbprint; Store = "LocalMachine" }
    }
    
    return $null
}

function Test-SignTool {
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) { return }
    
    $roots = @("$env:ProgramFiles(x86)\Windows Kits\10\bin") | Where-Object { Test-Path $_ }

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
    throw "signtool.exe not found. Install Windows 10/11 SDK."
}

function Invoke-SignArtifact {
    param(
        [string]$Path,
        [string]$Thumbprint,
        [string]$Store = "CurrentUser"
    )

    if (-not (Test-Path -LiteralPath $Path)) { 
        throw "File not found: $Path" 
    }

    $storeParam = if ($Store -eq "CurrentUser") { "/s", "My" } else { "/s", "My", "/sm" }

    try {
        Write-Log "Signing: $Path" "INFO"
        
        $signArgs = @(
            "sign"
            "/sha1", $Thumbprint
            "/tr", "http://timestamp.digicert.com"
            "/td", "sha256"
            "/fd", "sha256"
        ) + $storeParam + @($Path)
        
        & signtool.exe @signArgs
        if ($LASTEXITCODE -eq 0) {
            Write-Log "Successfully signed: $Path" "SUCCESS"
        } else {
            throw "Signing failed with exit code $LASTEXITCODE"
        }
    }
    catch {
        throw "Failed to sign $Path`: $_"
    }
}

function Install-Chocolatey {
    Write-Log "Checking if Chocolatey is installed..." "INFO"
    if (-not (Test-Command "choco")) {
        Write-Log "Chocolatey not found. Installing..." "INFO"
        Set-ExecutionPolicy Bypass -Scope Process -Force
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
        Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
        $env:PATH = [Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH", "User")
        Write-Log "Chocolatey installed successfully." "SUCCESS"
    }
    else {
        Write-Log "Chocolatey is already installed." "SUCCESS"
    }
}

function Convert-ToMsiVersion {
    <#
    .SYNOPSIS
        Converts YYYY.MM.DD.HHMM version to MSI-compatible format.
    .DESCRIPTION
        MSI requires versions with major < 256, minor < 256, build < 65536.
        We convert YYYY.MM.DD.HHMM to YY.MM.DDHH format.
    #>
    param([string]$Version)
    
    # Parse YYYY.MM.DD.HHMM
    if ($Version -match '^(\d{4})\.(\d{2})\.(\d{2})\.(\d{4})$') {
        $year = [int]$matches[1] - 2000  # 2025 -> 25
        $month = [int]$matches[2]
        $day = [int]$matches[3]
        $time = $matches[4]
        $hour = [int]$time.Substring(0, 2)
        
        # Format: YY.MM.DDHH (e.g., 25.12.0414 for 2025.12.04.1430)
        $msiVersion = "{0}.{1}.{2}{3:D2}" -f $year, $month, $day, $hour
        return $msiVersion
    }
    
    # Fallback - return as-is
    return $Version
}

function Build-MSI {
    param(
        [string]$Architecture,
        [string]$Version,          # Full version (YYYY.MM.DD.HHMM)
        [string]$BinDir,
        [switch]$Sign,
        [string]$Thumbprint,
        [string]$CertStore = "CurrentUser"
    )
    
    Write-Log "Building MSI for $Architecture..." "INFO"
    
    # Convert version to MSI-compatible format
    $msiVersion = Convert-ToMsiVersion -Version $Version
    Write-Log "MSI version: $msiVersion (from $Version)" "INFO"
    
    # Check for WiX
    $wixInstalled = $null
    try {
        $wixInstalled = & dotnet tool list -g 2>&1 | Select-String "wix"
    } catch {}
    
    if (-not $wixInstalled) {
        Write-Log "WiX toolset not found. Installing..." "INFO"
        & dotnet tool install --global wix 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install WiX toolset"
        }
    }
    
    $msiProjectPath = "build\msi\Cimian.wixproj"
    $outputPath = "release"
    
    # Build the MSI with MSI-compatible version
    & dotnet build $msiProjectPath `
        -p:Platform=$Architecture `
        -p:ProductVersion=$msiVersion `
        -p:BinDir=$BinDir `
        -o $outputPath
        
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build MSI for $Architecture"
    }
    
    # Find the newly built MSI (exclude already-versioned files)
    $msiFile = Get-ChildItem -Path $outputPath -Filter "Cimian*.msi" | 
        Where-Object { $_.Name -notmatch '^\d{4}\.\d{2}\.\d{2}\.' -and $_.Name -eq "Cimian.msi" } | 
        Select-Object -First 1
    if ($msiFile) {
        # Use full version in filename for clarity
        $finalName = "Cimian-$Version-$Architecture.msi"
        $finalPath = Join-Path $outputPath $finalName
        Move-Item -Path $msiFile.FullName -Destination $finalPath -Force
        Write-Log "Created MSI: $finalName" "SUCCESS"
        
        if ($Sign -and $Thumbprint) {
            Invoke-SignArtifact -Path $finalPath -Thumbprint $Thumbprint -Store $CertStore
            Write-Log "Signed MSI: $finalName" "SUCCESS"
        }
        
        return $finalPath
    }
    
    throw "MSI file not found after build"
}

function New-DirectoryIfNotExists {
    param ([string]$Path)
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        Write-Log "Created directory: $Path" "INFO"
    }
}

function New-PlaceholderProjects {
    Write-Log "Creating placeholder CLI projects..." "INFO"
    
    $placeholderProjects = @(
        @{ Name = "cimiimport"; Path = "src\Cimian.CLI.cimiimport\Cimian.CLI.cimiimport.csproj" }
        @{ Name = "cimipkg"; Path = "src\Cimian.CLI.cimipkg\Cimian.CLI.cimipkg.csproj" }
        @{ Name = "makecatalogs"; Path = "src\Cimian.CLI.makecatalogs\Cimian.CLI.makecatalogs.csproj" }
        @{ Name = "makepkginfo"; Path = "src\Cimian.CLI.makepkginfo\Cimian.CLI.makepkginfo.csproj" }
        @{ Name = "cimitrigger"; Path = "src\Cimian.CLI.cimitrigger\Cimian.CLI.cimitrigger.csproj" }
        @{ Name = "manifestutil"; Path = "src\Cimian.CLI.manifestutil\Cimian.CLI.manifestutil.csproj" }
        @{ Name = "repoclean"; Path = "src\Cimian.CLI.repoclean\Cimian.CLI.repoclean.csproj" }
        @{ Name = "cimistatus"; Path = "src\Cimian.GUI.CimianStatus\Cimian.GUI.CimianStatus.csproj" }
        @{ Name = "cimiwatcher"; Path = "src\Cimian.CLI.cimiwatcher\Cimian.CLI.cimiwatcher.csproj" }
    )
    
    foreach ($project in $placeholderProjects) {
        if (-not (Test-Path $project.Path)) {
            $projectDir = Split-Path $project.Path -Parent
            New-DirectoryIfNotExists $projectDir
            
            $projectContent = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>$($project.Name)</AssemblyName>
    <RootNamespace>Cimian.CLI.$($project.Name.Substring(0,1).ToUpper() + $project.Name.Substring(1))</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CommandLineParser" Version="2.9.1" />
    <PackageReference Include="Serilog" Version="4.1.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
  </ItemGroup>

</Project>
"@
            Set-Content -Path $project.Path -Value $projectContent -Encoding UTF8
            
            # Create basic Program.cs
            $programPath = Join-Path $projectDir "Program.cs"
            $programContent = @"
using System;
using System.Threading.Tasks;

namespace Cimian.CLI.$($project.Name.Substring(0,1).ToUpper() + $project.Name.Substring(1));

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("$($project.Name) - Placeholder implementation");
        Console.WriteLine("This tool is part of the Cimian C# migration and will be implemented in future phases.");
        
        if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("Usage: $($project.Name) [options]");
            Console.WriteLine("Options:");
            Console.WriteLine("  --help, -h    Show this help message");
            Console.WriteLine("  --version     Show version information");
        }
        
        Console.WriteLine("Placeholder execution completed successfully.");
        await Task.Delay(100); // Simulate async work
        return 0;
    }
}
"@
            Set-Content -Path $programPath -Value $programContent -Encoding UTF8
            
            Write-Log "Created placeholder project: $($project.Name)" "SUCCESS"
        }
    }
}

# Main execution starts here
try {
    Write-Log "=== Cimian C# Build Script ===" "INFO"
    $signMode = if ($NoSign) { "Disabled" } else { "Enabled (default)" }
    $buildMode = if ($Binaries) { "Binaries Only" } elseif ($Binary) { "Single Binary: $Binary" } else { "Full (Binaries + MSI)" }
    Write-Log "Build Mode: $buildMode | Signing: $signMode" "INFO"

    # Validate binary parameter if specified
    if ($Binary) {
        Write-Log "Building only '$Binary' binary" "INFO"
        $Task = "build"
        
        if (-not $Global:CSharpTools.ContainsKey($Binary)) {
            Write-Log "Invalid binary name: $Binary. Valid options are: $($Global:CSharpTools.Keys -join ', ')" "ERROR"
            exit 1
        }
    }

    # Handle development mode
    if ($Dev) {
        Write-Log "Development mode enabled - stopping services..." "INFO"
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
                Write-Log "Could not stop service $serviceName`: $_" "WARNING"
            }
        }
    }

    # Handle signing configuration - DEFAULT is to sign unless -NoSign is specified
    $shouldSign = -not $NoSign
    $actualThumbprint = ""
    $certStore = "CurrentUser"

    if ($Thumbprint) {
        $actualThumbprint = $Thumbprint
        Write-Log "Using provided certificate thumbprint" "INFO"
    }
    elseif ($shouldSign) {
        $certInfo = Get-SigningCertThumbprint
        if ($certInfo) {
            $actualThumbprint = $certInfo.Thumbprint
            $certStore = $certInfo.Store
            Write-Log "Enterprise certificate auto-detected: $actualThumbprint" "SUCCESS"
        }
        else {
            Write-Log "No enterprise certificate found. Use -NoSign to build without signing." "ERROR"
            throw "Signing is enabled by default but no valid certificate found. Use -NoSign for development builds."
        }
    }
    else {
        Write-Log "Signing disabled (-NoSign). Build will be unsigned." "WARNING"
    }

    # Handle SignMSI mode early
    if ($SignMSI) {
        Write-Log "SignMSI mode - signing existing MSI files..." "INFO"
        
        if (-not $shouldSign) {
            throw "Cannot sign MSI files without a valid certificate."
        }

        Test-SignTool
        $msiFiles = Get-ChildItem -Path "release" -Filter "*.msi" -File -ErrorAction SilentlyContinue
        
        if ($msiFiles.Count -eq 0) {
            Write-Log "No MSI files found in release directory." "WARNING"
            exit 0
        }
        
        foreach ($msi in $msiFiles) {
            Invoke-SignArtifact -Path $msi.FullName -Thumbprint $actualThumbprint -Store $certStore
        }
        
        Write-Log "SignMSI completed." "SUCCESS"
        exit 0
    }

    # Ensure required tools
    if (-not (Test-Command "dotnet")) {
        Write-Log ".NET SDK not found. Installing via Chocolatey..." "INFO"
        Install-Chocolatey
        choco install dotnet -y
        $env:PATH = [Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH", "User")
    }

    if (-not (Test-Command "dotnet")) {
        throw ".NET SDK is still not available after installation!"
    }

    $dotnetVersion = & dotnet --version
    Write-Log "Using .NET SDK version: $dotnetVersion" "SUCCESS"

    # Set version information
    $currentTime = Get-Date
    $semanticVersion = "{0}.{1:D2}.{2:D2}.{3}" -f $currentTime.Year, $currentTime.Month, $currentTime.Day, $currentTime.ToString("HHmm")
    Write-Log "Version set to $semanticVersion" "INFO"
    
    # Clean and prepare release directories
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

    # Create placeholder projects if needed
    New-PlaceholderProjects

    # Restore packages
    Write-Log "Restoring NuGet packages..." "INFO"
    dotnet restore Cimian.sln
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restore NuGet packages"
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
                throw "Project not found: $projectPath"
            }
            
            $outputPath = "release\$arch"
            
            dotnet publish $projectPath `
                --configuration Release `
                --runtime $runtimeId `
                --self-contained false `
                --output $outputPath `
                -p:PublishSingleFile=true `
                -p:IncludeSourceRevisionInInformationalVersion=false
                
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to build $Binary for $arch"
            }
            
            Write-Log "Successfully built $Binary for $arch" "SUCCESS"
        }
        elseif ($Binaries) {
            # Build all available CLI tools
            foreach ($tool in $Global:CSharpTools.Keys) {
                $projectName = $Global:CSharpTools[$tool]
                $projectPath = "src\$projectName\$projectName.csproj"
                
                if (Test-Path $projectPath) {
                    $outputPath = "release\$arch"
                    
                    # GUI needs self-contained build
                    $isSelfContained = ($tool -eq "cimistatus")
                    $selfContainedArg = if ($isSelfContained) { "true" } else { "false" }
                    
                    dotnet publish $projectPath `
                        --configuration Release `
                        --runtime $runtimeId `
                        --self-contained $selfContainedArg `
                        --output $outputPath `
                        -p:PublishSingleFile=true `
                        -p:IncludeSourceRevisionInInformationalVersion=false
                        
                    if ($LASTEXITCODE -ne 0) {
                        throw "Failed to build $tool for $arch"
                    }
                    
                    Write-Log "Successfully built $tool for $arch" "SUCCESS"
                }
                else {
                    Write-Log "Project not found: $projectPath (skipping)" "WARNING"
                }
            }
            
            # Copy MSI support scripts
            $msiScripts = @(
                "install-tasks.ps1",
                "uninstall-tasks.ps1",
                "manage-service.ps1",
                "verify-installation.ps1",
                "diagnose-cimianwatcher.ps1"
            )
            foreach ($script in $msiScripts) {
                $srcPath = "build\msi\$script"
                $dstPath = "release\$arch\$script"
                if (Test-Path $srcPath) {
                    Copy-Item -Path $srcPath -Destination $dstPath -Force
                    Write-Log "Copied MSI script: $script to $arch" "INFO"
                }
            }
        }
        else {
            # Default: Build all available CLI tools (same as -Binaries)
            foreach ($tool in $Global:CSharpTools.Keys) {
                $projectName = $Global:CSharpTools[$tool]
                $projectPath = "src\$projectName\$projectName.csproj"
                
                if (Test-Path $projectPath) {
                    $outputPath = "release\$arch"
                    
                    # GUI needs self-contained build
                    $isSelfContained = ($tool -eq "cimistatus")
                    $selfContainedArg = if ($isSelfContained) { "true" } else { "false" }
                    
                    dotnet publish $projectPath `
                        --configuration Release `
                        --runtime $runtimeId `
                        --self-contained $selfContainedArg `
                        --output $outputPath `
                        -p:PublishSingleFile=true `
                        -p:IncludeSourceRevisionInInformationalVersion=false
                        
                    if ($LASTEXITCODE -ne 0) {
                        throw "Failed to build $tool for $arch"
                    }
                    
                    Write-Log "Successfully built $tool for $arch" "SUCCESS"
                }
                else {
                    Write-Log "Project not found: $projectPath (skipping)" "WARNING"
                }
            }
            
            # Copy MSI support scripts
            $msiScripts = @(
                "install-tasks.ps1",
                "uninstall-tasks.ps1",
                "manage-service.ps1",
                "verify-installation.ps1",
                "diagnose-cimianwatcher.ps1"
            )
            foreach ($script in $msiScripts) {
                $srcPath = "build\msi\$script"
                $dstPath = "release\$arch\$script"
                if (Test-Path $srcPath) {
                    Copy-Item -Path $srcPath -Destination $dstPath -Force
                    Write-Log "Copied MSI script: $script to $arch" "INFO"
                }
            }
        }
    }

    # Sign all executables if signing is enabled
    if ($shouldSign) {
        Write-Log "Signing all executables..." "INFO"
        Test-SignTool
        
        # Force garbage collection to release file handles
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        Start-Sleep -Seconds 2
        
        foreach ($arch in $archs) {
            $releaseDir = "release\$arch"
            $exeFiles = Get-ChildItem -Path $releaseDir -Filter "*.exe" -File -ErrorAction SilentlyContinue
            
            foreach ($exe in $exeFiles) {
                Invoke-SignArtifact -Path $exe.FullName -Thumbprint $actualThumbprint -Store $certStore
                Write-Log "Successfully signed: $($exe.Name) ($arch)" "SUCCESS"
            }
        }
    }
    else {
        Write-Log "WARNING: Executables are unsigned. Enterprise environments require signed binaries!" "WARNING"
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

    # Build MSI packages (default behavior unless -SkipMSI or -NupkgOnly)
    if (-not $SkipMSI -and -not $NupkgOnly) {
        Write-Log "Building MSI installers..." "INFO"
        
        foreach ($arch in $archs) {
            $binDir = Join-Path (Get-Location) "release\$arch"
            $msiPath = Build-MSI -Architecture $arch `
                -Version $semanticVersion `
                -BinDir $binDir `
                -Sign:$shouldSign `
                -Thumbprint $actualThumbprint `
                -CertStore $certStore
        }
        
        Write-Log "MSI installers created successfully." "SUCCESS"
    }

    # Summary
    Write-Log "========================================" "SUCCESS"
    Write-Log "BUILD COMPLETE - Version $semanticVersion" "SUCCESS"
    Write-Log "========================================" "SUCCESS"
    
    Write-Log "Built artifacts:" "INFO"
    Get-ChildItem -Path "release" -Recurse -Include "*.exe","*.msi" | ForEach-Object {
        $size = [math]::Round($_.Length / 1MB, 1)
        Write-Log "  $($_.Name) ($size MB)" "INFO"
    }

    if ($Install) {
        Write-Log "Install flag specified - MSI installers available for deployment." "INFO"
    }

} catch {
    Write-Log "Build script failed: $_" "ERROR"
    exit 1
}
