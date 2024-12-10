<#
.SYNOPSIS
    Builds the Gorilla project locally, replicating the CI/CD pipeline.

.DESCRIPTION
    This script automates the build and packaging process, including installing dependencies,
    building binaries, and packaging artifacts.
#>

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
        "INFO" { Write-Host "[$timestamp] [INFO] $Message" -ForegroundColor Cyan }
        "SUCCESS" { Write-Host "[$timestamp] [SUCCESS] $Message" -ForegroundColor Green }
        "WARNING" { Write-Host "[$timestamp] [WARNING] $Message" -ForegroundColor Yellow }
        "ERROR" { Write-Host "[$timestamp] [ERROR] $Message" -ForegroundColor Red }
    }
}

# Function to check if a command exists
function Test-Command {
    param (
        [string]$Command
    )
    return (Get-Command $Command -ErrorAction SilentlyContinue) -ne $null
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
        if ($found) {
            return $found[0].Directory.FullName
        }
    }
    return $null
}

# Function to retry an action with delay
function Retry-Action {
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

# Step 1: Install Required Tools via Chocolatey (if not already available)
Write-Log "Checking and installing required tools..." "INFO"

$tools = @(
    @{ Name = "nuget.commandline"; Command = "nuget" },
    @{ Name = "intunewinapputil"; Command = "intunewinapputil" },
    @{ Name = "wixtoolset"; Command = "candle.exe" },  # Check for WiX via `candle.exe`
    @{ Name = "go"; Command = "go" }
)

foreach ($tool in $tools) {
    $toolName = $tool.Name
    $toolCommand = $tool.Command

    Write-Log "Checking if $toolName is already installed..." "INFO"

    if (Test-Command $toolCommand) {
        Write-Log "$toolName is already installed and available via command '$toolCommand'." "SUCCESS"
        continue
    }

    # If the tool is not installed, install it via Chocolatey
    Write-Log "$toolName is not installed. Installing via Chocolatey..." "INFO"
    try {
        choco install $toolName --no-progress --yes | Out-Null
        Write-Log "$toolName installed successfully." "SUCCESS"
    }
    catch {
        Write-Log "Failed to install $toolName. Error: $_" "ERROR"
        exit 1
    }
}

Write-Log "Required tools check and installation completed." "SUCCESS"

# Step 1.1: Refresh Environment Variables to Update PATH
Write-Log "Refreshing environment variables to include newly installed tools..." "INFO"

# Retrieve the updated PATH from the system and user environment variables
$machinePath = [System.Environment]::GetEnvironmentVariable("PATH", "Machine")
$userPath = [System.Environment]::GetEnvironmentVariable("PATH", "User")
$env:PATH = "$machinePath;$userPath"

Write-Log "Environment variables refreshed." "SUCCESS"

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

if ($wixBinPath) {
    Write-Log "WiX Toolset bin directory found at $wixBinPath" "INFO"
    # Check if WiX bin path is already in PATH to prevent duplication
    $wixPathNormalized = [System.IO.Path]::GetFullPath($wixBinPath).TrimEnd('\')
    $pathEntries = $env:PATH -split ";" | ForEach-Object { $_.Trim() }
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

# Removed setting GOPATH to a local directory and adding $PSScriptRoot\go\bin to PATH
# Assuming GOPATH is correctly set at the user level (C:\Users\rchristiansen\go)
# Ensure that Go binaries are already in PATH via system installation

Write-Log "Go environment variables set." "SUCCESS"

# Step 6: Prepare Release Version
Write-Log "Preparing release version..." "INFO"

$fullVersion = Get-Date -Format "yyyy.MM.dd"
$year = (Get-Date).Year - 2000
$month = (Get-Date).Month
$day = (Get-Date).Day
$semanticVersion = "{0}.{1}.{2}" -f $year, $month, $day

$env:RELEASE_VERSION = $fullVersion
$env:SEMANTIC_VERSION = $semanticVersion

Write-Log "RELEASE_VERSION set to $fullVersion" "INFO"
Write-Log "SEMANTIC_VERSION set to $semanticVersion" "INFO"

# Step 7: Tidy and Download Go Modules
Write-Log "Tidying and downloading Go modules..." "INFO"

go mod tidy
go mod download

Write-Log "Go modules tidied and downloaded." "SUCCESS"

# Step 8: Build All Binaries
Write-Log "Building all binaries..." "INFO"

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

    $ldflags = "-X github.com/windowsadmins/gorilla/pkg/version.appName=$binaryName " +
        "-X github.com/windowsadmins/gorilla/pkg/version.version=$env:RELEASE_VERSION " +
        "-X github.com/windowsadmins/gorilla/pkg/version.branch=$branchName " +
        "-X github.com/windowsadmins/gorilla/pkg/version.buildDate=$buildDate " +
        "-X github.com/windowsadmins/gorilla/pkg/version.revision=$revision"

    # Build command with error handling
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

Write-Log "All binaries built." "SUCCESS"

# Step 9: Package Binaries
Write-Log "Packaging binaries..." "INFO"

# Copy binaries to release
Get-ChildItem -Path "bin\*.exe" | ForEach-Object {
    Copy-Item $_.FullName "release\"
    Write-Log "Copied $($_.Name) to release directory." "INFO"
}

# Compress release directory with retry mechanism
Write-Log "Compressing release directory into release.zip..." "INFO"

$compressAction = {
    Compress-Archive -Path "release\*" -DestinationPath "release.zip" -Force
}

$compressSuccess = Retry-Action -Action $compressAction -MaxAttempts 5 -DelaySeconds 2

if ($compressSuccess) {
    Write-Log "Compressed binaries into release.zip." "SUCCESS"
}
else {
    Write-Log "Failed to compress release directory after multiple attempts." "ERROR"
    exit 1
}

# Step 10: Build MSI Package with WiX
Write-Log "Building MSI package with WiX..." "INFO"

$msiOutput = "release\Gorilla-$env:RELEASE_VERSION.msi"

# Compile WiX source
try {
    candle.exe -ext WixUtilExtension.dll -out "build\msi.wixobj" "build\msi.wxs"
    light.exe -sice:ICE61 -ext WixUtilExtension.dll -out $msiOutput "build\msi.wixobj"
    Write-Log "MSI package built at $msiOutput." "SUCCESS"
}
catch {
    Write-Log "Failed to build MSI package. Error: $_" "ERROR"
    exit 1
}

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

# Pack NuGet package
try {
    nuget pack "build\nupkg.nuspec" -OutputDirectory "release" -BasePath "$PSScriptRoot" | Out-Null
    Write-Log "NuGet package created in release directory." "SUCCESS"
}
catch {
    Write-Log "Failed to pack NuGet package. Error: $_" "ERROR"
    exit 1
}

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
$setupFile = "release\Gorilla-$env:RELEASE_VERSION.msi"
$outputFolder = "release"

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
Write-Log "Cleaning up temporary files..." "INFO"

$releaseZip = "release.zip"

if (Test-Path $releaseZip) {
    try {
        Remove-Item -Path $releaseZip -Force
        Write-Log "Temporary file '$releaseZip' deleted successfully." "SUCCESS"
    }
    catch {
        Write-Log "Failed to delete '$releaseZip'. Error: $_" "WARNING"
    }
}
else {
    Write-Log "'$releaseZip' does not exist. Skipping deletion." "INFO"
}

Write-Log "Clean-up process completed." "SUCCESS"

# Step 15: Clean Up MSI-Related Temporary Files
Write-Log "Cleaning up MSI-related temporary files..." "INFO"

$msiFiles = @("build\msi.msi", "build\msi.wixobj", "build\msi.wixpdb")

foreach ($file in $msiFiles) {
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

Write-Log "MSI-related temporary files clean-up completed." "SUCCESS"
