@echo off
REM cleanup-legacy-bootstrap.bat - Removes legacy bootstrap scheduled tasks
REM This script should be run during upgrades to clean up old scheduled task systems

echo Cimian Bootstrap System Cleanup
echo ================================
echo.

echo Checking for legacy bootstrap scheduled tasks...

REM Check for the old CimianBootstrapCheck task
schtasks /query /tn "CimianBootstrapCheck" >nul 2>&1
if %errorlevel% equ 0 (
    echo Found legacy CimianBootstrapCheck task - removing...
    schtasks /delete /f /tn "CimianBootstrapCheck" >nul 2>&1
    if %errorlevel% equ 0 (
        echo ✓ Removed CimianBootstrapCheck scheduled task
    ) else (
        echo ✗ Failed to remove CimianBootstrapCheck scheduled task
    )
) else (
    echo ✓ No CimianBootstrapCheck task found
)

REM Check for the hourly scheduled task as well (it's being kept but might need updating)
schtasks /query /tn "CimianHourlyRun" >nul 2>&1
if %errorlevel% equ 0 (
    echo ✓ CimianHourlyRun task found (this is expected to remain)
) else (
    echo ℹ CimianHourlyRun task not found (will be created if needed)
)

echo.
echo Checking CimianWatcher service status...
sc query CimianWatcher >nul 2>&1
if %errorlevel% equ 0 (
    echo ✓ CimianWatcher service is installed
    for /f "tokens=4" %%i in ('sc query CimianWatcher ^| find "STATE"') do set SERVICE_STATE=%%i
    echo   Status: !SERVICE_STATE!
) else (
    echo ⚠ CimianWatcher service not found - please reinstall Cimian
)

echo.
echo Cleanup completed. The system now uses CimianWatcher service for bootstrap monitoring.
echo Bootstrap functionality: Create flag file at C:\ProgramData\ManagedInstalls\.cimian.bootstrap
echo.
pause
