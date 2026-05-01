#Requires -Version 5.1
<#
.SYNOPSIS
    Deploy showcase InstallInfo.yaml and icons for MSC screenshot sessions.
.DESCRIPTION
    Stops MSC, deploys the curated showcase data and icons, then restarts MSC.
    Run this whenever you need to set up MSC for screenshots/demos.
.EXAMPLE
    .\deploy-showcase.ps1
    .\deploy-showcase.ps1 -NoRestart   # Deploy without restarting MSC
#>
param(
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path "$PSScriptRoot\showcase-installinfo.yaml")) {
    $repoRoot = $PSScriptRoot  # handle running from deployment/ directly
}
$showcaseYaml = Join-Path $PSScriptRoot 'showcase-installinfo.yaml'
$iconsSource = Join-Path $PSScriptRoot 'icons'
$managedInstalls = 'C:\ProgramData\ManagedInstalls'
$iconsDest = Join-Path $managedInstalls 'icons'
$installInfoDest = Join-Path $managedInstalls 'InstallInfo.yaml'
$mscExe = 'C:\Program Files\Cimian\Managed Software Center.exe'

# Validate
if (-not (Test-Path $showcaseYaml)) {
    Write-Error "showcase-installinfo.yaml not found at: $showcaseYaml"
    return
}

# Stop MSC
Write-Host '[1/4] Stopping Managed Software Center...'
Stop-Process -Name 'Managed Software Center' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# Stop cimiwatcher to prevent managedsoftwareupdate from overwriting
Write-Host '[2/4] Pausing cimiwatcher service...'
Stop-Service cimiwatcher -ErrorAction SilentlyContinue

# Deploy
Write-Host '[3/4] Deploying showcase data...'
New-Item -ItemType Directory -Path $iconsDest -Force | Out-Null
Copy-Item -Path $showcaseYaml -Destination $installInfoDest -Force
Copy-Item -Path "$iconsSource\*" -Destination $iconsDest -Force -ErrorAction SilentlyContinue
Write-Host "       InstallInfo.yaml deployed"
Write-Host "       $((@(Get-ChildItem $iconsDest -Filter '*.png')).Count) icons deployed"

# Restart
if (-not $NoRestart) {
    Write-Host '[4/4] Starting Managed Software Center...'
    Start-Process $mscExe
    Start-Sleep -Seconds 3
    $proc = Get-Process -Name 'Managed Software Center' -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Host "       MSC running (PID: $($proc.Id))"
    } else {
        Write-Warning 'MSC did not start'
    }
} else {
    Write-Host '[4/4] Skipping restart (-NoRestart specified)'
}

Write-Host ''
Write-Host 'Showcase deployed. Remember to restart cimiwatcher when done:'
Write-Host '  Start-Service cimiwatcher'
