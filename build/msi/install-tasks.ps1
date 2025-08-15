# Cimian MSI - Install Scheduled Tasks
# This script creates Windows scheduled tasks for Cimian automatic software management

param(
    [string]$InstallPath = "C:\Program Files\Cimian"
)

Write-Host "Installing Cimian scheduled tasks..."

try {
    # First, remove any existing Cimian tasks to prevent duplicates
    Write-Host "Removing any existing Cimian tasks..."
    Get-ScheduledTask | Where-Object {
        $_.TaskName -like "*Cimian*" -or
        $_.Description -like "*Cimian*" -or
        $_.TaskName -like "*Automatic Software Update*"
    } | ForEach-Object {
        Write-Host "  Removing existing task: $($_.TaskName)"
        Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }

    $managedSoftwareUpdateExe = Join-Path $InstallPath "managedsoftwareupdate.exe"
    
    # Verify the executable exists
    if (-not (Test-Path $managedSoftwareUpdateExe)) {
        Write-Error "managedsoftwareupdate.exe not found at $managedSoftwareUpdateExe"
        exit 1
    }

    # Create hourly automatic software update task
    Write-Host "Creating Cimian automatic software update task..."
    
    # Task action: run managedsoftwareupdate.exe --auto
    $action = New-ScheduledTaskAction -Execute $managedSoftwareUpdateExe -Argument "--auto" -WorkingDirectory $InstallPath
    
    # Task trigger: start immediately and repeat every hour indefinitely (adds randomization across deployments)
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Hours 1)
    
    # Task settings: 30 minute timeout, restart on failure, hidden, run on battery
    $settings = New-ScheduledTaskSettingsSet `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 10) `
        -RunOnlyIfNetworkAvailable `
        -Hidden `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -WakeToRun `
        -StartWhenAvailable
    
    # Task principal: run as SYSTEM with highest privileges
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    
    # Register the scheduled task
    Register-ScheduledTask `
        -TaskName "Cimian Managed Software Update Hourly" `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description "Automatically checks for and installs software updates every hour using Cimian managed software system" `
        -Force `
        -ErrorAction Stop

    Write-Host "✅ Cimian scheduled task created successfully"
    Write-Host "   Task Name: Cimian Managed Software Update Hourly"
    Write-Host "   Schedule: Every hour starting 5 minutes after installation"
    Write-Host "   Command: $managedSoftwareUpdateExe --auto"
    Write-Host "   Runs as: SYSTEM (highest privileges)"

} catch {
    Write-Error "Failed to create Cimian scheduled task: $_"
    exit 1
}
