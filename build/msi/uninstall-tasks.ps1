# Cimian MSI - Uninstall Scheduled Tasks
# This script removes Windows scheduled tasks for Cimian

Write-Host "Removing Cimian scheduled tasks..."

try {
    $taskNames = @(
        "Cimian Managed Software Update Hourly"
    )

    foreach ($taskName in $taskNames) {
        try {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if ($task) {
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
                Write-Host "✅ Removed scheduled task: $taskName"
            } else {
                Write-Host "ℹ️  Task not found (already removed): $taskName"
            }
        } catch {
            Write-Warning "Failed to remove task '$taskName': $_"
        }
    }

    Write-Host "✅ Cimian scheduled tasks cleanup completed"

} catch {
    Write-Error "Failed to remove scheduled tasks: $_"
    # Don't exit with error during uninstall to avoid blocking removal
}
