# intunewin.ps1 - Automate conversion of MSI to Intune .intunewin format

param (
    [string]$SetupFolder = "release",
    [string]$SetupFile = "release\gorilla.msi",
    [string]$OutputFolder = "release\intunewin"
)

# Ensure Microsoft-Win32-Content-Prep-Tool is installed
if (-not (Get-Command "IntuneWinAppUtil.exe" -ErrorAction SilentlyContinue)) {
    Write-Error "Microsoft-Win32-Content-Prep-Tool not found. Please ensure it is installed."
    exit 1
}

# Create output directory if it doesn't exist
if (-not (Test-Path -Path $OutputFolder)) {
    New-Item -ItemType Directory -Path $OutputFolder | Out-Null
}

# Run the IntuneWinAppUtil to generate .intunewin package
Write-Host "Converting MSI to .intunewin format..."
Start-Process "IntuneWinAppUtil.exe" -ArgumentList `
    "-c `"$SetupFolder`" -s `"$SetupFile`" -o `"$OutputFolder`" -q" -Wait -NoNewWindow

# Check if the conversion was successful
if (Test-Path "$OutputFolder\gorilla.intunewin") {
    Write-Host "Conversion successful! File saved at: $OutputFolder\gorilla.intunewin"
} else {
    Write-Error "Conversion failed. Please check the logs for more details."
}
