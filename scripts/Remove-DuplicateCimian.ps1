# Cleanup Script for Duplicate Cimian Installations
# This script removes all existing Cimian installations that were created with wildcard Product IDs
# Run this before deploying the new MSI with fixed Product ID

[CmdletBinding()]
param(
    [switch]$WhatIf = $false
)

$ErrorActionPreference = "Continue"

Write-Host "=== Cimian Duplicate Installation Cleanup ===" -ForegroundColor Magenta
Write-Host "This script will remove all existing Cimian installations" -ForegroundColor Yellow
Write-Host "to prepare for the new MSI with consistent Product ID." -ForegroundColor Yellow
Write-Host ""

if ($WhatIf) {
    Write-Host "[WHAT-IF MODE] No actual changes will be made" -ForegroundColor Cyan
    Write-Host ""
}

# Function to get all Cimian installations from registry
function Get-CimianInstallations {
    $installations = @()
    
    # Check both 32-bit and 64-bit uninstall keys
    $uninstallKeys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    
    foreach ($keyPath in $uninstallKeys) {
        try {
            Get-ItemProperty $keyPath -ErrorAction SilentlyContinue | 
                Where-Object { 
                    $_.DisplayName -like "*Cimian*" -and 
                    $_.UninstallString -like "*msiexec*" 
                } | 
                ForEach-Object {
                    $installations += [PSCustomObject]@{
                        DisplayName = $_.DisplayName
                        DisplayVersion = $_.DisplayVersion
                        InstallDate = $_.InstallDate
                        Publisher = $_.Publisher
                        UninstallString = $_.UninstallString
                        ProductCode = $_.PSChildName
                        RegistryPath = $_.PSPath
                    }
                }
        }
        catch {
            Write-Warning "Could not access registry path: $keyPath"
        }
    }
    
    return $installations
}

# Function to uninstall via Product Code
function Remove-CimianInstallation {
    param(
        [Parameter(Mandatory)]
        [object]$Installation
    )
    
    $productCode = $Installation.ProductCode
    $displayName = $Installation.DisplayName
    $version = $Installation.DisplayVersion
    
    Write-Host "  Removing: $displayName ($version)" -ForegroundColor Yellow
    Write-Host "    Product Code: $productCode" -ForegroundColor Gray
    
    if ($WhatIf) {
        Write-Host "    [WHAT-IF] Would run: msiexec /x $productCode /qn" -ForegroundColor Cyan
        return $true
    }
    
    try {
        $process = Start-Process -FilePath "msiexec.exe" -ArgumentList "/x", $productCode, "/qn", "/l*v", "$env:TEMP\cimian_cleanup_$productCode.log" -Wait -PassThru -NoNewWindow
        
        if ($process.ExitCode -eq 0) {
            Write-Host "    ✓ Successfully removed" -ForegroundColor Green
            return $true
        } elseif ($process.ExitCode -eq 1605) {
            Write-Host "    ⚠ Product not found (already removed)" -ForegroundColor Yellow
            return $true
        } else {
            Write-Host "    ✗ Failed with exit code: $($process.ExitCode)" -ForegroundColor Red
            Write-Host "      Check log: $env:TEMP\cimian_cleanup_$productCode.log" -ForegroundColor Gray
            return $false
        }
    }
    catch {
        Write-Host "    ✗ Error during removal: $_" -ForegroundColor Red
        return $false
    }
}

# Function to stop Cimian services and scheduled tasks
function Stop-CimianServices {
    Write-Host "Stopping Cimian services and scheduled tasks..." -ForegroundColor Blue
    
    # Stop CimianWatcher service
    $service = Get-Service -Name "CimianWatcher" -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -eq "Running") {
            if ($WhatIf) {
                Write-Host "  [WHAT-IF] Would stop CimianWatcher service" -ForegroundColor Cyan
            } else {
                try {
                    Stop-Service -Name "CimianWatcher" -Force
                    Write-Host "  ✓ CimianWatcher service stopped" -ForegroundColor Green
                }
                catch {
                    Write-Host "  ⚠ Could not stop CimianWatcher service: $_" -ForegroundColor Yellow
                }
            }
        } else {
            Write-Host "  ✓ CimianWatcher service already stopped" -ForegroundColor Green
        }
    } else {
        Write-Host "  ✓ CimianWatcher service not found" -ForegroundColor Green
    }
    
    # Stop scheduled tasks
    $tasks = @("CimianHourlyRun", "CimianBootstrapCheck")
    foreach ($taskName in $tasks) {
        if ($WhatIf) {
            Write-Host "  [WHAT-IF] Would check and stop task: $taskName" -ForegroundColor Cyan
        } else {
            try {
                $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
                if ($task -and $task.State -eq "Running") {
                    Stop-ScheduledTask -TaskName $taskName
                    Write-Host "  ✓ Stopped scheduled task: $taskName" -ForegroundColor Green
                } elseif ($task) {
                    Write-Host "  ✓ Scheduled task not running: $taskName" -ForegroundColor Green
                } else {
                    Write-Host "  ✓ Scheduled task not found: $taskName" -ForegroundColor Green
                }
            }
            catch {
                Write-Host "  ⚠ Could not manage scheduled task $taskName`: $_" -ForegroundColor Yellow
            }
        }
    }
}

# Main execution
try {
    # Check if running as administrator
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")
    
    if (-not $isAdmin) {
        Write-Host "❌ This script requires administrator privileges!" -ForegroundColor Red
        Write-Host "Please run PowerShell as Administrator and try again." -ForegroundColor Yellow
        exit 1
    }
    
    # Find all Cimian installations
    Write-Host "Scanning for Cimian installations..." -ForegroundColor Blue
    $installations = Get-CimianInstallations
    
    if ($installations.Count -eq 0) {
        Write-Host "✓ No Cimian installations found in registry" -ForegroundColor Green
        Write-Host "System is ready for new MSI deployment." -ForegroundColor Green
        exit 0
    }
    
    Write-Host "Found $($installations.Count) Cimian installation(s):" -ForegroundColor Yellow
    foreach ($install in $installations) {
        Write-Host "  • $($install.DisplayName) $($install.DisplayVersion) (Product: $($install.ProductCode))" -ForegroundColor White
    }
    Write-Host ""
    
    if (-not $WhatIf) {
        $confirmation = Read-Host "Do you want to remove all these installations? (y/N)"
        if ($confirmation -notmatch '^[Yy]') {
            Write-Host "Operation cancelled by user." -ForegroundColor Yellow
            exit 0
        }
        Write-Host ""
    }
    
    # Stop services first
    Stop-CimianServices
    Write-Host ""
    
    # Remove installations
    Write-Host "Removing Cimian installations..." -ForegroundColor Blue
    $removedCount = 0
    $failedCount = 0
    
    foreach ($installation in $installations) {
        if (Remove-CimianInstallation -Installation $installation) {
            $removedCount++
        } else {
            $failedCount++
        }
    }
    
    Write-Host ""
    Write-Host "=== Cleanup Summary ===" -ForegroundColor Magenta
    Write-Host "Installations processed: $($installations.Count)" -ForegroundColor White
    Write-Host "Successfully removed: $removedCount" -ForegroundColor Green
    Write-Host "Failed to remove: $failedCount" -ForegroundColor Red
    
    if ($failedCount -eq 0) {
        Write-Host ""
        Write-Host "✅ Cleanup completed successfully!" -ForegroundColor Green
        Write-Host "System is now ready for the new Cimian MSI with fixed Product ID." -ForegroundColor Green
        Write-Host ""
        Write-Host "Next steps:" -ForegroundColor Yellow
        Write-Host "1. Build the new MSI with fixed Product ID" -ForegroundColor White
        Write-Host "2. Deploy via Intune or install manually" -ForegroundColor White
        Write-Host "3. Future upgrades will work correctly" -ForegroundColor White
    } else {
        Write-Host ""
        Write-Host "⚠️  Cleanup completed with some failures" -ForegroundColor Yellow
        Write-Host "Check the log files in $env:TEMP for details on failed removals." -ForegroundColor Yellow
        Write-Host "You may need to manually remove the failed installations." -ForegroundColor Yellow
    }
    
}
catch {
    Write-Host ""
    Write-Host "❌ Cleanup failed with error: $_" -ForegroundColor Red
    exit 1
}
