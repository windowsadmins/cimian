<#
.SYNOPSIS
    Builds the Cimian project locally, replicating the CI/CD pipeline.

.DESCRIPTION
    This script automates the build and packaging process, including installing dependencies,
    building binaries, and packaging artifacts.
#>

#  ─Sign          … build + sign
#  ─Thumbprint XX … override auto-detection
param(
    [switch]$Sign,
    [string]$Thumbprint
)

# ──────────────────────────  GLOBALS  ──────────────────────────
# Friendly name (CN) of the enterprise code-signing certificate you push with Intune
$Global:EnterpriseCertCN = 'EmilyCarrU Intune Windows Enterprise Certificate'

# Exit immediately if a command exits with a non-zero status
$ErrorActionPreference = 'Stop'

# Ensure GO111MODULE is enabled for module-based builds
$env:GO111MODULE = "on"

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
function Ensure-SignTool {

    # helper to prepend path only once
    function Add-ToPath([string]$dir) {
        if (-not [string]::IsNullOrWhiteSpace($dir) -and
            -not ($env:Path -split ';' | Where-Object { $_ -ieq $dir })) {
            $env:Path = "$dir;$env:Path"
        }
    }

    # already reachable?
    if (Get-Command signtool.exe -EA SilentlyContinue) { return }

    # harvest possible SDK roots
    $roots = @(
        "${env:ProgramFiles}\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    )

    # add KitsRoot10 from the registry (covers non-standard installs)
    try {
        $kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' `
                     -EA Stop).KitsRoot10
        if ($kitsRoot) { $roots += (Join-Path $kitsRoot 'bin') }
    } catch { }

    $roots = $roots | Where-Object { Test-Path $_ } | Select-Object -Unique

    # scan every root for any architecture’s signtool.exe
    foreach ($root in $roots) {
        $exe = Get-ChildItem -Path $root -Recurse -Filter signtool.exe -EA SilentlyContinue |
               Sort-Object LastWriteTime -Desc | Select-Object -First 1
        if ($exe) {
            Add-ToPath $exe.Directory.FullName
            Write-Log "signtool discovered at $($exe.FullName)" "SUCCESS"
            return
        }
    }

    # graceful failure
    Write-Log @"
signtool.exe not found.

Install **any** Windows 10/11 SDK _or_ Visual Studio Build Tools  
(choose a workload that includes **Windows SDK Signing Tools**),  
then run the build again.
"@ "ERROR"
    exit 1
}

# Function to find the WiX Toolset bin directory
function Find-WiXBinPath {
    # Common installation paths for WiX Toolset via Chocolatey
    $possiblePaths = @(
        "C:\Program Files (x86)\WiX Toolset*\bin\candle.exe",
        "C:\Program Files\WiX Toolset*\bin\candle.exe"
    )

    foreach ($path in $possiblePaths) {
        $found = Get-ChildItem -Path $path -ErrorAction SilentlyContinue
        if ($null -ne $found) {
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
if ($Sign) {
    Ensure-SignTool
    if (-not $Thumbprint) {
        $Thumbprint = Get-SigningCertThumbprint
        if (-not $Thumbprint) {
            Write-Log "No valid '$Global:EnterpriseCertCN' certificate with a private key found – aborting." "ERROR"
            exit 1
        }
        Write-Log "Auto-selected signing cert $Thumbprint" "INFO"
    } else {
        Write-Log "Using user-supplied thumbprint $Thumbprint" "INFO"
    }
    $env:SIGN_THUMB = $Thumbprint   # used by the two sign* functions
} else {
    Write-Log "Sign switch not present – build will be unsigned." "INFO"
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

    $tsaList = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS'
    )

    foreach ($tsa in $tsaList) {
        Write-Log "Signing '$FilePath' using $tsa …" "INFO"
        & signtool.exe sign `
            /sha1  $Thumbprint `
            /fd    SHA256 `
            /tr    $tsa `
            /td    SHA256 `
            /v `
            "$FilePath"

        if ($LASTEXITCODE -eq 0) {
            Write-Log  "signtool succeeded with $tsa" "SUCCESS"
            return
        }
        Write-Log "signtool failed with $tsa (exit $LASTEXITCODE)" "WARNING"
    }

    throw "signtool failed with all timestamp authorities."
}

function signNuget {
    param(
        [Parameter(Mandatory)][string]$Nupkg,
        [string]$Thumbprint            # ← optional override (matches existing caller)
    )

    if (-not (Test-Path $Nupkg)) {
        throw "NuGet package '$Nupkg' not found – cannot sign."
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
        return
    }

    & nuget.exe sign `
    $Nupkg `
    -CertificateStoreName   My `
    -CertificateSubjectName $Global:EnterpriseCertCN `
    -Timestamper            $tsa

    if ($LASTEXITCODE) { throw "nuget sign failed ($LASTEXITCODE)" }
    Write-Log "NuGet repo signature complete." "SUCCESS"
}


# ───────────────────────────────────────────────────
#  BUILD PROCESS STARTS
# ───────────────────────────────────────────────────

# Step 0: Clean Release Directory Before Build
Write-Log "Cleaning existing release directory..." "INFO"

if (Test-Path "release") {
    try {
        Remove-Item -Path "release\*" -Recurse -Force
        Write-Log "Existing release directory cleaned." "SUCCESS"
    }
    catch {
        Write-Log "Failed to clean release directory. Error: $_" "ERROR"
        exit 1
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
    @{ Name = "intunewinapputil"; Command = "intunewinapputil" },
    @{ Name = "go"; Command = "go" }
)

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
    Write-Log "WiX is already installed: $wixBin" "SUCCESS"
} else {
    Write-Log "WiX is not installed. Installing via Chocolatey..." "INFO"
    choco install wixtoolset --yes --no-progress --force | Out-Null
    Write-Log "WiX installed successfully." "SUCCESS"
}

Write-Log "Required tools check and installation completed." "SUCCESS"

# Force environment reload via Chocolatey's refreshenv
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
if (-not (Test-Command "candle.exe")) {
    Write-Log "WiX Toolset is not installed correctly or not in PATH. Exiting..." "ERROR"
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
    
    # Update the WiX XML with the new version
    Write-Log "Updating WiX product version to $semanticVersion in msi.wxs..." "INFO"
    $wxsPath = "build\msi.wxs"
    $wxsContent = Get-Content $wxsPath -Raw
    $updatedWxsContent = [regex]::Replace($wxsContent, 
                                       '(<Product Id="\*"\s+UpgradeCode="[^"]+"\s+Name="[^"]+"\s+Version=")([^"]+)(")', 
                                       "`${1}$semanticVersion`${3}")
    Set-Content -Path $wxsPath -Value $updatedWxsContent
    Write-Log "Updated WiX product version in $wxsPath" "SUCCESS"
}

Set-Version

# Step 7: Tidy and Download Go Modules
Write-Log "Tidying and downloading Go modules..." "INFO"

go mod tidy
go mod download

Write-Log "Go modules tidied and downloaded." "SUCCESS"

# Step 8: Build All Binaries
Write-Log "Building all binaries..." "INFO"

# Clean existing binaries first
Write-Log "Cleaning existing binaries..." "INFO"
if (Test-Path "bin") {
    Remove-Item -Path "bin\*.exe" -Force
    Write-Log "Cleaned existing binaries from bin directory." "SUCCESS"
}

$binaryDirs = Get-ChildItem -Directory -Path "./cmd"

foreach ($dir in $binaryDirs) {
    $binaryName = $dir.Name
    Write-Log "Building $binaryName..." "INFO"

    # Retrieve the current Git branch name
    try {
        $branchName = (git rev-parse --abbrev-ref HEAD)
        Write-Log "Current Git branch: $branchName" "INFO"
    }
    catch {
        Write-Log "Unable to retrieve Git branch name. Defaulting to 'main'." "WARNING"
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

    # Check if this cmd/<binaryName> folder is a separate module (has its own go.mod)
    $submoduleGoMod = Join-Path $dir.FullName "go.mod"
    if (Test-Path $submoduleGoMod) {
        Write-Log "Detected submodule for $binaryName (go.mod found). Building from submodule..." "INFO"
        Push-Location $dir.FullName
        try {
            go mod tidy
            go mod download
            go build -v -o "../../bin/$binaryName.exe" -ldflags="$ldflags" .
            if ($LASTEXITCODE -ne 0) {
                throw "Build failed for submodule $binaryName with exit code $LASTEXITCODE."
            }
            Write-Log "$binaryName (submodule) built successfully." "SUCCESS"
        }
        catch {
            Write-Log "Failed to build submodule $binaryName. Error: $_" "ERROR"
            Pop-Location
            exit 1
        }
        Pop-Location
    }
    else {
        Write-Log "Building $binaryName from main module..." "INFO"
        try {
            go build -v -o "bin\$binaryName.exe" -ldflags="$ldflags" "./cmd/$binaryName"
            if ($LASTEXITCODE -ne 0) {
                throw "Build failed for $binaryName with exit code $LASTEXITCODE."
            }
            Write-Log "$binaryName built successfully." "SUCCESS"
        }
        catch {
            Write-Log "Failed to build $binaryName. Error: $_" "ERROR"
            exit 1
        }
    }
}

Write-Log "All binaries built." "SUCCESS"

# Step 9: Package Binaries
Write-Log "Packaging binaries..." "INFO"

# Copy binaries to release
Get-ChildItem -Path "bin\*.exe" | ForEach-Object {
    Copy-Item $_.FullName "release\"
    Write-Log "Copied $($_.Name) to release directory." "INFO"
}

if ($Sign)
{
    Write-Log "Signing all EXEs in release directory..." "INFO"

    Get-ChildItem -Path "release\*.exe" | ForEach-Object {
        try {
            signPackage -FilePath $_.FullName                      # uses $env:SIGN_THUMB
            # Quick verification – make sure Status = Valid
            $sig = Get-AuthenticodeSignature $_.FullName
            if ($sig.Status -eq 'Valid') {
                if (-not $sig.SignerCertificate.NotBefore -or -not $sig.TimeStamperCertificate) {
                    Write-Log "Signed $($_.Name) ✔ but no timestamp was embedded. Signature may expire with certificate." "WARNING"
                } else {
                    Write-Log "Signed $($_.Name) ✔" "SUCCESS"
                }
            } else {
                Write-Log "Signature on $($_.Name) is $($sig.Status)" "WARNING"
            }
        }
        catch {
            Write-Log "Failed to sign $($_.Name). Error: $_" "ERROR"
            exit 1
        }
    }
}

# Compress release directory with retry mechanism
Write-Log "Compressing release directory into release.zip..." "INFO"

$compressAction = {
    Compress-Archive -Path "release\*" -DestinationPath "release.zip" -Force
}

$compressSuccess = Invoke-Retry -Action $compressAction -MaxAttempts 5 -DelaySeconds 2

if ($compressSuccess) {
    Write-Log "Compressed binaries into release.zip." "SUCCESS"
}
else {
    Write-Log "Failed to compress release directory after multiple attempts." "ERROR"
    exit 1
}

# Step 10: Build MSI Package with WiX
Write-Log "Building MSI package with WiX..." "INFO"

# Define WiX Toolset Path
$wixToolsetPath   = "C:\Program Files (x86)\WiX Toolset v3.14\bin"
$candlePath       = Join-Path $wixToolsetPath "candle.exe"
$lightPath        = Join-Path $wixToolsetPath "light.exe"
$wixUtilExtension = Join-Path $wixToolsetPath "WixUtilExtension.dll"

# Validate WiX Toolset path
if (-not (Test-Path $wixToolsetPath)) {
    Write-Log "WiX Toolset path '$wixToolsetPath' not found. Exiting..." "ERROR"
    exit 1
}

# Define output paths
$msiOutput = "release\Cimian-$env:RELEASE_VERSION.msi"

# Compile WiX source
try {
    Write-Log "Compiling WiX source with candle..." "INFO"
    & $candlePath -ext $wixUtilExtension -out "build\msi.wixobj" "build\msi.wxs"

    Write-Log "Linking and creating MSI with light..." "INFO"
    & $lightPath -sice:ICE* -ext $wixUtilExtension -out $msiOutput "build\msi.wixobj"

    Write-Log "MSI package built at $msiOutput." "SUCCESS"
}
catch {
    Write-Log "Failed to build MSI package. Error: $_" "ERROR"
    exit 1
}

if ($Sign) { signPackage $msiOutput $env:SIGN_THUMB }

# Step 11: Prepare NuGet Package
Write-Log "Preparing NuGet package..." "INFO"

# Replace SEMANTIC_VERSION in nuspec
try {
    (Get-Content "build\nupkg.nuspec") -replace '\$\{\{ env\.SEMANTIC_VERSION \}\}', $env:SEMANTIC_VERSION | Set-Content "build\nupkg.nuspec"
    Write-Log "Updated nuspec with SEMANTIC_VERSION." "INFO"
}
catch {
    Write-Log "Failed to update nuspec. Error: $_" "ERROR"
    exit 1
}

if (-not (Test-Path "build\install.ps1")) { '' | Out-File "build\install.ps1" -Encoding ASCII }

# Pack NuGet package
try {
    nuget pack "build\nupkg.nuspec" -OutputDirectory "release" -BasePath "$PSScriptRoot" | Out-Null
    Write-Log "NuGet package created in release directory." "SUCCESS"
}
catch {
    Write-Log "Failed to pack NuGet package. Error: $_" "ERROR"
    exit 1
}

# pick up the freshly created .nupkg (whatever it’s called)
$nupkgPath = Get-ChildItem -Path "release\*.nupkg" |
             Sort-Object LastWriteTime -Desc | Select-Object -First 1

if (-not $nupkgPath) {
    throw "No .nupkg produced – cannot sign."
}

if ($Sign) { signNuget $nupkgPath.FullName $Thumbprint }

# Step 11.1: Revert `nupkg.nuspec` to its dynamic state
Write-Log "Reverting build/nupkg.nuspec to dynamic state..." "INFO"

try {
    # Replace hardcoded version with placeholder
    (Get-Content "build\nupkg.nuspec") -replace "$env:SEMANTIC_VERSION", '${{ env.SEMANTIC_VERSION }}' | Set-Content "build\nupkg.nuspec"
    Write-Log "Reverted build/nupkg.nuspec to use dynamic placeholder." "SUCCESS"
}
catch {
    Write-Log "Failed to revert build/nupkg.nuspec. Error: $_" "ERROR"
    exit 1
}

Write-Log "Build process completed successfully with cleanup." "SUCCESS"

# Step 12: Prepare IntuneWin Package
Write-Log "Preparing IntuneWin package..." "INFO"

# Define variables for IntuneWin conversion
$setupFolder = "release"
$setupFile   = "release\Cimian-$env:RELEASE_VERSION.msi"
$outputFolder= "release"

# Check if the setup file exists before attempting conversion
if (-not (Test-Path $setupFile)) {
    Write-Log "Setup file '$setupFile' does not exist. Skipping IntuneWin package preparation." "WARNING"
}
else {
    # Run intunewin.ps1 and capture any errors
    try {
        powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "build\intunewin.ps1" -SetupFolder $setupFolder -SetupFile $setupFile -OutputFolder $outputFolder
        Write-Log "IntuneWin package prepared." "SUCCESS"
    }
    catch {
        Write-Log "IntuneWin package preparation failed. Error: $_" "ERROR"
        exit 1
    }
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
Write-Log "Temporary files cleanup completed." "SUCCESS"
