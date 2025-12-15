# Cimian Privilege Elevation Troubleshooting Guide

## Problem Description

When running Cimian on on-premises domain joined devices (vs. Entra Joined devices), CimianStatus may fail to properly elevate `managedsoftwareupdate.exe` to administrative privileges, causing installations to fail.

## Root Cause

The issue stems from differences in how UAC (User Account Control) and privilege elevation work between:

1. **Entra Joined devices** - Modern authentication with Azure AD integration
2. **On-premises domain joined devices** - Traditional domain authentication with different token types and security policies

## Symptoms

- CimianStatus opens but software updates fail
- `managedsoftwareupdate.exe` runs without admin privileges
- Installation failures due to insufficient permissions
- Different behavior compared to Entra Joined devices

## Solutions

### Solution 1: Use CimianWatcher Service (Recommended)

The CimianWatcher service runs as SYSTEM and can launch updates with proper privileges:

```cmd
# Check if CimianWatcher service is running
sc query CimianWatcher

# Start the service if it's stopped
sc start CimianWatcher

# Trigger updates via the service (instead of direct CimianStatus launch)
cimitrigger gui      # For GUI updates
cimitrigger headless # For background updates
```

### Solution 2: Use Enhanced CimiTrigger with Direct Elevation

For problematic domain environments, use the new direct elevation feature:

```cmd
# Try direct elevation methods (bypasses service)
cimitrigger --direct gui      # Direct GUI update with multiple elevation attempts
cimitrigger --direct headless # Direct headless update with multiple elevation attempts
```

### Solution 3: Manual PowerShell Elevation

If the above methods fail, manually elevate using PowerShell:

```powershell
# Open PowerShell as Administrator and run:
Start-Process -FilePath "C:\Program Files\Cimian\managedsoftwareupdate.exe" -ArgumentList "--auto","--show-status","-vv" -Verb RunAs
```

### Solution 4: Scheduled Task Method

Create a scheduled task that runs with SYSTEM privileges:

```cmd
# Create task (run as Administrator)
schtasks /Create /TN "CimianManualUpdate" /TR "\"C:\Program Files\Cimian\managedsoftwareupdate.exe\" --auto --show-status -vv" /SC ONCE /ST 23:59 /RU SYSTEM /F

# Run the task immediately
schtasks /Run /TN "CimianManualUpdate"

# Clean up after completion
schtasks /Delete /TN "CimianManualUpdate" /F
```

## Prevention and Long-term Fixes

### 1. Ensure CimianWatcher Service is Properly Configured

Verify the service is installed and configured to run as SYSTEM:

```cmd
# Check service configuration
sc qc CimianWatcher

# The service should show:
# START_TYPE: AUTO_START
# SERVICE_START_NAME: LocalSystem
```

### 2. Update Group Policy for UAC (if needed)

On domain-joined machines, you may need to adjust UAC policies:

1. Open `gpedit.msc` (Local Group Policy Editor)
2. Navigate to: `Computer Configuration > Windows Settings > Security Settings > Local Policies > Security Options`
3. Look for UAC-related policies, particularly:
   - "User Account Control: Run all administrators in Admin Approval Mode"
   - "User Account Control: Behavior of the elevation prompt for administrators in Admin Approval Mode"

### 3. Use CimianWatcher Service Consistently

Always trigger updates through the CimianWatcher service rather than direct CimianStatus launches:

- **Good**: `cimitrigger gui` (uses service)
- **Problematic**: Direct `cimistatus.exe` launch on domain machines

### 4. Deploy via Group Policy or SCCM

For enterprise deployments on domain-joined machines:

1. Deploy Cimian via Group Policy or SCCM
2. Ensure CimianWatcher service starts automatically
3. Configure scheduled tasks or startup scripts to use `cimitrigger` instead of direct launches

## Diagnostic Commands

### Check Current Status
```cmd
# Check if you're running as admin
whoami /groups | findstr "S-1-5-32-544"

# Check CimianWatcher service
sc query CimianWatcher

# Check for Cimian processes
tasklist | findstr cimi

# Check UAC settings
reg query "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v EnableLUA
```

### Test Elevation Methods
```cmd
# Test direct UAC elevation
runas /user:Administrator "C:\Program Files\Cimian\managedsoftwareupdate.exe --version"

# Test PowerShell elevation
powershell -Command "Start-Process -FilePath 'C:\Program Files\Cimian\managedsoftwareupdate.exe' -ArgumentList '--version' -Verb RunAs"
```

## Environment-Specific Notes

### Entra Joined Devices
- Standard UAC elevation works reliably
- `UseShellExecute = true` with `Verb = "runas"` functions as expected
- Modern authentication tokens handle elevation smoothly

### Domain Joined Devices  
- UAC behavior may vary based on domain policies
- Different token types (filtered vs unfiltered) can affect elevation
- Some environments require alternative elevation methods
- Group Policy may restrict certain elevation mechanisms

### Hybrid Environments
- Behavior may vary depending on how the device was joined
- Test both service-based and direct elevation methods
- Consider device-specific configurations

## Contact Information

If none of these solutions work in your environment:

1. Collect diagnostic information using the commands above
2. Note your specific domain configuration and policies
3. Test which elevation method (if any) works manually
4. Report the issue with environment details

## Version History

- **v1.0** - Initial troubleshooting guide
- **v1.1** - Added enhanced CimiTrigger direct elevation methods
- **v1.2** - Added scheduled task and PowerShell elevation alternatives
