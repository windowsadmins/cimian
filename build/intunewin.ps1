# intunewin.ps1 - Automate conversion of MSI to Intune .intunewin format

param (
    [Parameter(Mandatory = $true)]
    [string]$SetupFolder = "release",
    
    [Parameter(Mandatory = $true)]
    [string]$SetupFile = "release\gorilla.msi",
    
    [Parameter(Mandatory = $true)]
    [string]$OutputFolder = "release\intunewin"
)

# Function to display messages with color coding
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
    return (Get-Command $Command -ErrorAction SilentlyContinue) -ne $null
}

# Verify IntuneWinAppUtil.exe is available
$intuneWinAppUtilPath = "IntuneWinAppUtil.exe"

Write-Log "Verifying IntuneWinAppUtil.exe is available..." "INFO"

if (-not (Test-Command $intuneWinAppUtilPath)) {
    Write-Log "IntuneWinAppUtil.exe not found. Please ensure it is installed and in the PATH." "ERROR"
    exit 1
}

# Resolve absolute paths
try {
    $SetupFolder = (Resolve-Path $SetupFolder).Path
    Write-Log "Resolved SetupFolder path: $SetupFolder" "INFO"
}
catch {
    Write-Log "SetupFolder path '$SetupFolder' is invalid. Exiting." "ERROR"
    exit 1
}

try {
    $SetupFile = (Resolve-Path $SetupFile).Path
    Write-Log "Resolved SetupFile path: $SetupFile" "INFO"
}
catch {
    Write-Log "SetupFile path '$SetupFile' is invalid. Exiting." "ERROR"
    exit 1
}

try {
    $OutputFolder = (Resolve-Path $OutputFolder).Path
    Write-Log "Resolved OutputFolder path: $OutputFolder" "INFO"
}
catch {
    Write-Log "OutputFolder path '$OutputFolder' is invalid. Exiting." "ERROR"
    exit 1
}

# Verify SetupFile exists
Write-Log "Checking if SetupFile '$SetupFile' exists..." "INFO"
if (-not (Test-Path $SetupFile)) {
    Write-Log "SetupFile '$SetupFile' does not exist. Exiting." "ERROR"
    exit 1
}

# Create OutputFolder if it doesn't exist
if (-not (Test-Path -Path $OutputFolder)) {
    Write-Log "OutputFolder '$OutputFolder' does not exist. Creating it..." "INFO"
    try {
        New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
        Write-Log "OutputFolder '$OutputFolder' created successfully." "SUCCESS"
    }
    catch {
        Write-Log "Failed to create OutputFolder '$OutputFolder'. Error: $_" "ERROR"
        exit 1
    }
}

# Remove existing .intunewin files to prevent conflicts
Write-Log "Checking for existing .intunewin files in '$OutputFolder'..." "INFO"
$existingIntunewin = Get-ChildItem -Path $OutputFolder -Filter "*.intunewin" -ErrorAction SilentlyContinue
if ($existingIntunewin.Count -gt 0) {
    Write-Log "Removing existing .intunewin files to prevent conflicts." "INFO"
    try {
        Remove-Item -Path $existingIntunewin.FullName -Force
        Write-Log "Existing .intunewin files removed." "SUCCESS"
    }
    catch {
        Write-Log "Failed to remove existing .intunewin files. Error: $_" "ERROR"
        exit 1
    }
}
else {
    Write-Log "No existing .intunewin files found." "INFO"
}

# Run the IntuneWinAppUtil to generate .intunewin package
Write-Log "Starting conversion of '$SetupFile' to IntuneWin format..." "INFO"

# Determine the expected .intunewin filename based on SetupFile
$setupFileName = [System.IO.Path]::GetFileNameWithoutExtension($SetupFile)
$intunewinFilePath = [System.IO.Path]::Combine($OutputFolder, "$setupFileName.intunewin")

# Start the conversion process and capture the exit code
$process = Start-Process "IntuneWinAppUtil.exe" -ArgumentList "-c `"$SetupFolder`" -s `"$SetupFile`" -o `"$OutputFolder`" -q" -Wait -NoNewWindow -PassThru

# Check the exit code of IntuneWinAppUtil.exe
if ($process.ExitCode -ne 0) {
    Write-Log "IntuneWinAppUtil.exe failed with exit code $($process.ExitCode)." "ERROR"
    exit $process.ExitCode
}
else {
    Write-Log "IntuneWinAppUtil.exe completed successfully with exit code $($process.ExitCode)." "SUCCESS"
}

# Verify that the .intunewin file was created
Write-Log "Verifying the creation of the .intunewin file at '$intunewinFilePath'..." "INFO"

if (Test-Path $intunewinFilePath) {
    Write-Log "Conversion successful! File saved at: $intunewinFilePath" "SUCCESS"
    exit 0
}
else {
    Write-Log "Conversion failed. .intunewin file not found at '$intunewinFilePath'." "ERROR"
    exit 1
}
