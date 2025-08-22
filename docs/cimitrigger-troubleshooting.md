# Troubleshooting: `cimitrigger gui` Not Working

## Issue Summary
When you ran `cimitrigger gui` on your test device, nothing happened - no CimianStatus window appeared and no updates were processed.

## Most Likely Causes

### 1. **CimianWatcher Service Not Running** (Most Common)
The `cimitrigger gui` command depends on the CimianWatcher Windows service to process the trigger files.

**Check this first:**
```cmd
sc query CimianWatcher
```

**Expected output if working:**
```
SERVICE_NAME: CimianWatcher
        TYPE               : 10  WIN32_OWN_PROCESS
        STATE              : 4  RUNNING
        ...
```

**If service is stopped:**
```cmd
sc start CimianWatcher
```

**If service doesn't exist:**
```cmd
# Reinstall/register the service (run from Cimian installation directory)
cimiwatcher.exe install
sc start CimianWatcher
```

### 2. **File System Permissions**
The service may not have permissions to read/delete the trigger files in `C:\ProgramData\ManagedInstalls\`.

### 3. **Service Not Monitoring Correctly**
The service might be running but not polling the trigger files properly.

### 4. **Session 0 Isolation (Common Issue)**
Windows services run in Session 0, which is isolated from user sessions. When CimianWatcher service starts CimianStatus.exe, it starts in Session 0 where users cannot see or interact with GUI applications.

**Check for this issue:**
```cmd
tasklist /fi "imagename eq cimistatus.exe" /fo table
```

If you see `Session#` as `0` (Services), the GUI is running but hidden from users.

**This is why `--force gui` method works** - it starts the GUI in the user session instead of the service session.

## Diagnostic Steps

### Step 1: Use the Enhanced Diagnostic Tools

I've created enhanced tools to help diagnose this exact issue:

```cmd
# Run the enhanced cimitrigger with debugging
cimitrigger debug

# Or use the dedicated diagnostic tool
cimitrigger-debug.exe

# Or use the PowerShell test script
PowerShell -ExecutionPolicy Bypass -File Test-CimianTrigger.ps1
```

### Step 2: Manual Service Test

Check if the service is actually monitoring:

```cmd
# 1. Create trigger file manually
echo "Test trigger" > "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"

# 2. Watch if the file gets deleted (indicates service processed it)
dir "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"

# Wait 15 seconds and check again
dir "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"
```

If the file is **deleted**, the service is working.
If the file **remains**, the service is not processing triggers.

### Step 3: Check Event Logs

Look for CimianWatcher service errors:

```powershell
Get-WinEvent -LogName Application | Where-Object {$_.ProviderName -eq 'CimianWatcher'} | Select-Object -First 10
```

## Solutions

### Solution 1: Service-Based Fix (Recommended)

```cmd
# Stop and restart the service
sc stop CimianWatcher
sc start CimianWatcher

# Test the trigger again
cimitrigger gui
```

### Solution 2: Direct Elevation (Recommended for Domain Environments)

Use the direct elevation method that bypasses the service and starts GUI in user session:

```cmd
cimitrigger --force gui
```

**This method is now recommended for domain-joined devices** because it avoids Session 0 isolation issues.

This will try multiple elevation methods:
1. Standard UAC elevation
2. PowerShell Start-Process with RunAs
3. Scheduled task with SYSTEM account

### Solution 3: Manual PowerShell Elevation

If the direct method fails, manually elevate:

```powershell
Start-Process -FilePath "C:\Program Files\Cimian\managedsoftwareupdate.exe" -ArgumentList "--auto","--show-status","-vv" -Verb RunAs
```

### Solution 4: Verify Installation

Check if all components are properly installed:

```cmd
# Check if executables exist
dir "C:\Program Files\Cimian\*.exe"

# Should show:
# - managedsoftwareupdate.exe
# - cimistatus.exe  
# - cimiwatcher.exe
# - cimitrigger.exe
```

## Testing the Fix

After applying any solution, test with the enhanced cimitrigger:

```cmd
# This will now provide detailed feedback
cimitrigger gui
```

You should see:
```
🚀 Triggering GUI update via CimianWatcher service...
✅ GUI update trigger file created successfully
📁 Trigger file location: C:\ProgramData\ManagedInstalls\.cimian.bootstrap
⏳ Waiting for CimianWatcher service to process the request...
✅ CimianWatcher service processed the trigger - update should be starting!
```

## Domain vs Entra Considerations

Since you mentioned this is for domain-joined devices (vs Entra-joined), the service approach is actually **more reliable** than direct elevation, so focus on getting the CimianWatcher service working properly.

## Quick Resolution Commands

Run these in order until one works:

```cmd
# 1. Check and start service
sc query CimianWatcher
sc start CimianWatcher
cimitrigger gui

# 2. If service issues persist, use direct method
cimitrigger --direct gui

# 3. If all else fails, manual elevation
PowerShell -Command "Start-Process -FilePath 'C:\Program Files\Cimian\managedsoftwareupdate.exe' -ArgumentList '--auto','--show-status','-vv' -Verb RunAs"
```

## Next Steps

1. **Deploy the enhanced tools** to your test device (rebuilt cimitrigger.exe, cimitrigger-debug.exe, Test-CimianTrigger.ps1)
2. **Run diagnostics** to identify the specific issue
3. **Fix the CimianWatcher service** if that's the problem
4. **Use direct elevation** as a reliable fallback for domain environments

The enhanced tools will give you much better visibility into what's failing, which will help determine if this is a service issue, permissions issue, or something else entirely.
