# Bootstrap Provisioning Workflow - Complete Guide

**Date**: October 14, 2025  
**Status**: ✅ FULLY OPERATIONAL

## Overview

This document describes the **seamless bootstrap provisioning workflow** that enables zero-touch device enrollment with automatic manifest switching and continuous software deployment.

## The Complete Workflow

### Phase 1: Initial Bootstrap
1. Device boots with `ClientIdentifier: Bootstrap`
2. CimianWatcher service (or scheduled task) triggers managedsoftwareupdate
3. **Bootstrap manifest** runs, installing:
   - Core provisioning tools
   - CimianAuth for repo access
   - Inventory scripts
   - Enrollment packages

### Phase 2: Manifest Enrollment (Ω Items Run Last)
4. **`Ω ProvisioningManifestEnrollment`** (second-to-last):
   - Downloads enrollment CSV from repo
   - Matches device by serial number
   - Determines correct manifest based on usage/catalog/area/location/allocation hierarchy
   - Updates `Config.yaml` with:
     - New `ClientIdentifier` (e.g., `Shared/Production/Design/North123/COMP-Design-01`)
     - Appropriate `SoftwareRepoURL` (cloud/mars/ares)
   - Renames computer to allocation name
   - **Critical**: Updates config but does NOT trigger immediate run

5. **`Ω ProvisioningResetBootstrap`** (runs LAST):
   - Creates `.cimian.bootstrap` trigger file
   - Content:
     ```
     Bootstrap mode enabled at: 2025-10-14T06:40:00-07:00
     ```
   - This signals CimianWatcher to start a NEW run

### Phase 3: Seamless Transition (AUTOMATIC)
6. **CimianWatcher detects bootstrap file** (within 10 seconds):
   - Polls `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` every 10 seconds
   - Detects file modification timestamp change
   - Triggers dual-process launch

7. **Dual Process Launch**:
   ```
   CimianWatcher (SYSTEM, Session 0)
        ├─► managedsoftwareupdate.exe --auto --show-status -vvv
        │   (Runs as SYSTEM, reads NEW ClientIdentifier)
        │
        └─► cimistatus.exe
            (Launches in active user session via Win32 API)
   ```

8. **Production Manifest Runs**:
   - managedsoftwareupdate reads updated `ClientIdentifier`
   - Fetches production manifest (e.g., `Shared/Production/Design/North123/COMP-Design-01.yaml`)
   - Downloads and installs all production software
   - CimianStatus shows live progress with events from `events.jsonl`
   - Reports to external monitoring systems

9. **Bootstrap file cleanup**:
   - After managedsoftwareupdate launches, CimianWatcher deletes `.cimian.bootstrap`
   - System returns to normal state
   - Ready for future bootstrap triggers

### Phase 4: Continuous Operation
10. Device now operates with production manifest
11. Scheduled runs continue normally
12. If Bootstrap manifest is needed again, `Ω ProvisioningResetBootstrap` can trigger it anytime

## Key Components

### 1. CimianWatcher Service
**Location**: `C:\Program Files\Cimian\cimiwatcher.exe`

**Responsibilities**:
- Monitors bootstrap trigger files every 10 seconds
- Launches managedsoftwareupdate when triggered
- Launches CimianStatus in user session using Win32 `CreateProcessAsUser`
- Cleans up bootstrap file after successful launch

**Bootstrap File Detection**:
```go
// Polls every 10 seconds
if fileInfo, err := os.Stat(bootstrapFlagFile); err == nil {
    if m.lastSeenGUI.IsZero() || fileInfo.ModTime().After(m.lastSeenGUI) {
        go m.triggerBootstrapUpdate(true) // Launch with GUI
    }
}
```

**Process Launch**:
```go
// Launch managedsoftwareupdate
updateCmd := exec.Command(cimianExePath, "--auto", "--show-status", "-vvv")
updateCmd.Start()

// Launch CimianStatus in user session
launchGUIInUserSession(cimistatus, logger)
```

### 2. Omega (Ω) Items - Execution Order
The omega symbol (`Ω`) ensures packages run **after all other packages**:

```yaml
# Runs second-to-last
name: Ω ProvisioningManifestEnrollment
# Sets ClientIdentifier to production manifest
# Updates SoftwareRepoURL
# Renames computer

# Runs LAST
name: Ω ProvisioningResetBootstrap  
# Creates bootstrap trigger file
# Signals CimianWatcher to start new run with new manifest
```

**Why this order matters**:
1. `Ω ProvisioningManifestEnrollment` must run BEFORE `Ω ProvisioningResetBootstrap`
2. Config must be updated BEFORE bootstrap trigger is created
3. Bootstrap trigger causes immediate new run with updated config
4. New run uses production manifest, not Bootstrap manifest

### 3. Self-Update with Cleanup
**Status**: ✅ RE-ENABLED with automatic cleanup

**Behavior**:
- If catalog contains `Cimian` or `CimianTools` packages:
  - Self-update is scheduled
  - `.cimian.selfupdate` flag created
  - Applied on next CimianWatcher service restart
  
- If catalog does NOT contain Cimian packages:
  - Any stale `.cimian.selfupdate` flag is automatically deleted
  - Prevents confusion when switching repos
  - Logged: `"Cleaned up stale selfupdate flag (no Cimian packages in catalog)"`

**Code Location**: `cmd/managedsoftwareupdate/main.go` lines ~1480-1502 and ~1613-1690

## Bootstrap File Format

### Created by ProvisioningResetBootstrap
```
Bootstrap mode enabled at: 2025-10-14T06:40:00-07:00
```

### Alternative Manual Creation
```powershell
# Create bootstrap trigger manually
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$content = "Bootstrap triggered at: $timestamp`nMode: GUI`nTriggered by: Manual test"
New-Item -Path "C:\ProgramData\ManagedInstalls\.cimian.bootstrap" -ItemType File -Force -Value $content
```

### Using cimitrigger.exe
```powershell
# Easiest method
.\cimitrigger.exe

# Creates bootstrap file with proper timestamp
# CimianWatcher detects within 10 seconds
```

## Testing the Workflow

### Test 1: Verify CimianWatcher Detection
```powershell
# Install and start service (if not already running)
sudo .\release\arm64\cimiwatcher.exe install
sudo Start-Service CimianWatcher

# Create bootstrap file
$timestamp = Get-Date -Format "o"
Set-Content -Path "C:\ProgramData\ManagedInstalls\.cimian.bootstrap" -Value "Bootstrap triggered at: $timestamp"

# Wait 10-15 seconds and check Event Viewer
Get-EventLog -LogName Application -Source CimianWatcher -Newest 5
```

**Expected Log Entries**:
```
Bootstrap flag file (GUI) detected - triggering update with GUI
Started managedsoftwareupdate process (PID: XXXX)
Successfully launched CimianStatus UI in user session
```

### Test 2: Verify Manifest Switching
```powershell
# Before: Check current manifest
Get-Content "C:\ProgramData\ManagedInstalls\Config.yaml" | Select-String "ClientIdentifier"
# Should show: ClientIdentifier: Bootstrap

# Simulate ProvisioningManifestEnrollment by manually updating config
$config = @"
SoftwareRepoURL: https://cimian.ecuad.ca/deployment
ClientIdentifier: Shared/Production/Design/North123/COMP-Design-01
"@
$config | Out-File "C:\ProgramData\ManagedInstalls\Config.yaml" -Encoding UTF8

# Create bootstrap trigger (simulating ProvisioningResetBootstrap)
$timestamp = Get-Date -Format "o"
Set-Content -Path "C:\ProgramData\ManagedInstalls\.cimian.bootstrap" -Value "Bootstrap triggered at: $timestamp"

# Wait for CimianWatcher to detect and launch
# Check logs to verify production manifest is loaded
$latestLog = Get-ChildItem "C:\ProgramData\ManagedInstalls\logs\" -Directory | 
    Sort-Object Name -Descending | Select-Object -First 1
Get-Content "$($latestLog.FullName)\install.log" | Select-String "ClientIdentifier|manifest"
```

### Test 3: End-to-End Provisioning
1. Reset device to Bootstrap manifest:
   ```powershell
   # Edit Config.yaml
   $config = @"
   SoftwareRepoURL: https://cimian.ecuad.ca/deployment
   ClientIdentifier: Bootstrap
   "@
   $config | Out-File "C:\ProgramData\ManagedInstalls\Config.yaml" -Encoding UTF8
   ```

2. Run managedsoftwareupdate:
   ```powershell
   sudo .\release\arm64\managedsoftwareupdate.exe --auto -vv
   ```

3. Verify Bootstrap manifest runs
4. Verify `Ω ProvisioningManifestEnrollment` updates config
5. Verify `Ω ProvisioningResetBootstrap` creates bootstrap file
6. Verify CimianWatcher detects and launches new run
7. Verify production manifest runs with correct software

## Troubleshooting

### Bootstrap File Not Detected
**Symptoms**: File created but nothing happens

**Checks**:
```powershell
# 1. Verify service is running
Get-Service CimianWatcher

# 2. Check service logs
Get-EventLog -LogName Application -Source CimianWatcher -Newest 20 | Format-List

# 3. Verify file exists and has recent timestamp
Get-Item "C:\ProgramData\ManagedInstalls\.cimian.bootstrap" | Format-List LastWriteTime

# 4. Manually test file detection
Stop-Service CimianWatcher
Start-Service CimianWatcher
# Service checks for existing files on startup
```

### CimianStatus Not Launching
**Symptoms**: managedsoftwareupdate runs but no UI appears

**Causes**:
- No active user session (service runs in Session 0)
- User not logged in
- cimistatus.exe not found

**Checks**:
```powershell
# Verify user is logged in
query user

# Verify cimistatus.exe exists
Test-Path "C:\Program Files\Cimian\cimistatus.exe"

# Check Event Viewer for launch errors
Get-EventLog -LogName Application -Source CimianWatcher -Newest 20 | 
    Where-Object { $_.Message -like "*CimianStatus*" }
```

### Manifest Not Switching
**Symptoms**: Still running Bootstrap manifest after enrollment

**Causes**:
- `Ω ProvisioningManifestEnrollment` failed
- Config.yaml not updated
- Bootstrap file not created

**Checks**:
```powershell
# 1. Check current ClientIdentifier
Get-Content "C:\ProgramData\ManagedInstalls\Config.yaml" | Select-String "ClientIdentifier"

# 2. Check enrollment logs
$latestLog = Get-ChildItem "C:\ProgramData\ManagedInstalls\logs\" -Directory | 
    Sort-Object Name -Descending | Select-Object -First 1
Get-Content "$($latestLog.FullName)\install.log" | 
    Select-String "ProvisioningManifestEnrollment|ClientIdentifier|manifest"

# 3. Verify bootstrap file was created
Get-Item "C:\ProgramData\ManagedInstalls\.cimian.bootstrap" -ErrorAction SilentlyContinue
```

### Self-Update Issues
**Symptoms**: Cimian package detected but not updating

**Checks**:
```powershell
# Check for selfupdate flag
Test-Path "C:\ProgramData\ManagedInstalls\.cimian.selfupdate"

# View flag contents
Get-Content "C:\ProgramData\ManagedInstalls\.cimian.selfupdate"

# Check if Cimian is in catalog
sudo .\release\arm64\managedsoftwareupdate.exe --checkonly -vv 2>&1 | 
    Select-String -Pattern "Cimian|selfupdate"

# Manually trigger selfupdate application
sudo Restart-Service CimianWatcher
```

## System Requirements

### For Bootstrap Workflow
- ✅ CimianWatcher service installed and running
- ✅ `C:\Program Files\Cimian\managedsoftwareupdate.exe`
- ✅ `C:\Program Files\Cimian\cimistatus.exe`
- ✅ Bootstrap manifest with Ω provisioning items
- ✅ Production manifests in correct hierarchy
- ✅ Enrollment CSV accessible from repo

### For Self-Update
- ✅ Cimian/CimianTools package in catalog
- ✅ CimianWatcher service installed
- ✅ Automatic cleanup enabled (built-in)

## Command Reference

### Bootstrap File Management
```powershell
# Create bootstrap trigger
New-Item -Path "C:\ProgramData\ManagedInstalls\.cimian.bootstrap" -ItemType File -Force

# Check if exists
Test-Path "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"

# View contents
Get-Content "C:\ProgramData\ManagedInstalls\.cimian.bootstrap"

# Delete (cleanup)
Remove-Item "C:\ProgramData\ManagedInstalls\.cimian.bootstrap" -Force
```

### CimianWatcher Service
```powershell
# Install service
sudo .\release\arm64\cimiwatcher.exe install

# Start service
sudo Start-Service CimianWatcher

# Stop service
sudo Stop-Service CimianWatcher

# Check status
Get-Service CimianWatcher

# View logs
Get-EventLog -LogName Application -Source CimianWatcher -Newest 20

# Restart to apply selfupdate
sudo Restart-Service CimianWatcher
```

### Config Management
```powershell
# View current config
Get-Content "C:\ProgramData\ManagedInstalls\Config.yaml"

# View ClientIdentifier
Get-Content "C:\ProgramData\ManagedInstalls\Config.yaml" | Select-String "ClientIdentifier"

# View SoftwareRepoURL
Get-Content "C:\ProgramData\ManagedInstalls\Config.yaml" | Select-String "SoftwareRepoURL"

# Edit config
notepad "C:\ProgramData\ManagedInstalls\Config.yaml"
```

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    BOOTSTRAP PROVISIONING FLOW                   │
└─────────────────────────────────────────────────────────────────┘

[1] Initial State
    ClientIdentifier: Bootstrap
    ↓

[2] Bootstrap Manifest Runs
    - Core tools installed
    - ProvisioningManifestEnrollment (Ω)
    - ProvisioningResetBootstrap (Ω)
    ↓

[3] Ω ProvisioningManifestEnrollment
    - Downloads enrollment CSV
    - Matches device by serial
    - Updates ClientIdentifier → "Shared/Production/Design/..."
    - Updates SoftwareRepoURL
    - Renames computer
    ↓

[4] Ω ProvisioningResetBootstrap
    - Creates .cimian.bootstrap file
    ↓

[5] CimianWatcher (polling every 10s)
    - Detects .cimian.bootstrap
    - Triggers dual launch:
      ├─ managedsoftwareupdate (SYSTEM)
      └─ cimistatus (User Session)
    ↓

[6] New Run with Production Manifest
    - Reads updated ClientIdentifier
    - Fetches production manifest
    - Installs production software
    - Shows progress in CimianStatus
    ↓

[7] Bootstrap file deleted
    ↓

[8] Normal Operation
    - Scheduled runs with production manifest
    - Can re-trigger bootstrap anytime

┌─────────────────────────────────────────────────────────────────┐
│                      SELF-UPDATE WITH CLEANUP                    │
└─────────────────────────────────────────────────────────────────┘

Every managedsoftwareupdate run:
    ↓
[1] Load catalog
    ↓
[2] Check for Cimian/CimianTools packages
    ↓
    ├─ YES: Cimian found
    │   ├─ Separate from regular items
    │   ├─ Download Cimian package
    │   ├─ Create .cimian.selfupdate flag
    │   ├─ Schedule for service restart
    │   └─ Install other items normally
    │
    └─ NO: Cimian not found
        ├─ Check if .cimian.selfupdate exists
        ├─ If YES: Delete it (cleanup)
        ├─ Log: "Cleaned up stale selfupdate flag"
        └─ Continue with regular items
```

## Best Practices

### 1. Always Use Omega (Ω) for Provisioning Items
- Ensures enrollment runs AFTER all software installed
- Bootstrap trigger created LAST
- Prevents race conditions

### 2. Test in Development Environment First
- Use test repo/catalog
- Verify manifest hierarchy
- Confirm CSV enrollment data
- Test bootstrap file detection

### 3. Monitor CimianWatcher Logs
- Regular checks of Event Viewer
- Alert on errors
- Verify bootstrap triggers working

### 4. Keep Binaries Updated
- Rebuild after changes
- Sign all binaries
- Deploy to production systematically

### 5. Document Custom Manifests
- Maintain manifest hierarchy documentation
- Document usage/catalog/area/location conventions
- Keep enrollment CSV up to date

## Summary

✅ **Self-update RE-ENABLED** with automatic cleanup  
✅ **Bootstrap workflow FULLY OPERATIONAL**  
✅ **Seamless manifest switching WORKING**  
✅ **CimianWatcher detection VERIFIED**  
✅ **CimianStatus live progress ENABLED**  

Your provisioning workflow is production-ready! 🚀
