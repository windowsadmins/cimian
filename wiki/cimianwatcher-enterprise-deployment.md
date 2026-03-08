# CimianWatcher Service Installation - Enterprise Deployment Guide

## Executive Summary

✅ **Service installation is fully automated** - no manual steps required  
✅ **MSI custom actions handle service registration** - uses native Windows service management  
✅ **Enterprise deployment ready** - supports Intune, GPO, and automated installations  

## Service Installation Architecture

### Automated Service Registration Process
1. **MSI Installation Phase:** Files deployed to `C:\Program Files\Cimian\`
2. **Custom Action Phase:** `InstallCimianWatcherService` executes `cimiwatcher.exe install`
3. **Service Start Phase:** `StartCimianWatcherService` executes `cimiwatcher.exe start`
4. **Verification Phase:** Service appears in Windows Service Manager

### Custom Action Configuration
```xml
<!-- Service installation with error checking -->
<CustomAction Id="InstallCimianWatcherService"
              Execute="deferred"
              Impersonate="no"
              Return="check"
              Directory="INSTALLDIR"
              ExeCommand="&quot;[INSTALLDIR]cimiwatcher.exe&quot; install" />

<!-- Service startup (allows installation to complete even if service doesn't start) -->
<CustomAction Id="StartCimianWatcherService"
              Execute="deferred"
              Impersonate="no"
              Return="ignore"
              Directory="INSTALLDIR"
              ExeCommand="&quot;[INSTALLDIR]cimiwatcher.exe&quot; start" />
```

## Enterprise Deployment Scenarios

### 1. Microsoft Intune Deployment ✅ AUTOMATED
**Context:** System-level deployment with automatic elevation
```powershell
# Intune Win32 App Configuration
Install Command: msiexec /i "Cimian-x64-2025.07.12.msi" /qn
Uninstall Command: msiexec /x "Cimian-x64-2025.07.12.msi" /qn
Install Context: System
Detection Rule: File exists "C:\Program Files\Cimian\cimiwatcher.exe"
```
**Result:** Service automatically installed and started

### 2. Group Policy Software Installation ✅ AUTOMATED
**Context:** Computer-level GPO with system privileges
```
Computer Configuration > Policies > Software Settings > Software Installation
Deployment Method: Assigned
Installation UI Level: Maximum
Advanced > Deployment Options > Install this application at logon
```
**Result:** Service automatically installed during computer startup/logon

### 3. SCCM/ConfigMgr Deployment ✅ AUTOMATED
**Context:** System Center deployment with administrative privileges
```powershell
# Application Deployment Type
Installation Program: msiexec /i "Cimian-x64-2025.07.12.msi" /qn
Install for System: Yes
User Experience: Install for system if resource allows
```
**Result:** Service automatically installed in system context

### 4. PowerShell DSC ✅ AUTOMATED
**Context:** Desired State Configuration with system privileges
```powershell
Configuration CimianInstallation {
    Package CimianSoftware {
        Ensure = "Present"
        Path = "C:\Software\Cimian-x64-2025.07.12.msi"
        ProductId = "{9127064A-3536-42A1-BDE4-131AA5DBE458}"
        Arguments = "/qn"
    }
}
```
**Result:** Service automatically managed by DSC

## Manual Installation (Development/Testing)

### For Administrator Testing
```powershell
# Run PowerShell as Administrator, then:
msiexec /i "Cimian-x64-2025.07.12.msi" /qn

# Verify installation
Get-Service CimianWatcher
```

### For Standard User Testing (Requires UAC Prompt)
```powershell
# Triggers UAC elevation prompt
Start-Process -FilePath "msiexec.exe" -ArgumentList "/i", "Cimian-x64-2025.07.12.msi", "/qn" -Verb RunAs -Wait
```

## Validation and Testing

### Automated Health Check
```powershell
# Run comprehensive tests
.\test_watcher_enhanced.ps1 -RunAllTests

# Quick status check
.\test_watcher_enhanced.ps1 -CheckStatus
```

### Service Verification Commands
```powershell
# Check service registration
Get-Service CimianWatcher

# Check service configuration
Get-WmiObject Win32_Service | Where-Object { $_.Name -eq "CimianWatcher" }

# Check service startup
Test-Path "C:\Program Files\Cimian\cimiwatcher.exe"

# Test bootstrap functionality
Set-Content -Path "C:\ProgramData\ManagedInstalls\.cimian.bootstrap" -Value "Test - $(Get-Date)"
```

## Service Configuration Details

### Service Properties
- **Service Name:** CimianWatcher
- **Display Name:** Cimian Bootstrap File Watcher  
- **Description:** Monitors for Cimian bootstrap flag file and triggers software updates
- **Start Type:** Automatic
- **Log On As:** Local System Account
- **Dependencies:** None

### Monitored Components
- **Flag File:** `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`
- **Target Executable:** `C:\Program Files\Cimian\managedsoftwareupdate.exe`
- **Polling Interval:** 10 seconds
- **Trigger Command:** `managedsoftwareupdate.exe --auto --show-status`

## Troubleshooting Guide

### Issue: Service Not Installing
**Symptoms:** MSI completes but service not registered
**Common Causes:**
1. Insufficient installation privileges
2. Antivirus blocking service registration
3. Service executable not accessible

**Solutions:**
1. Ensure MSI runs with administrative privileges
2. Add exclusions for `cimiwatcher.exe`
3. Verify file permissions on executable

### Issue: Service Installed but Not Starting
**Symptoms:** Service registered but Status = Stopped
**Common Causes:**
1. Service account permissions
2. File access permissions
3. Missing dependencies

**Solutions:**
1. Verify Local System account access
2. Check ProgramData folder permissions
3. Run dependency walker on executable

### Issue: Bootstrap Not Triggering
**Symptoms:** Service running but no response to flag file
**Common Causes:**
1. Incorrect flag file location
2. File permission issues
3. Service not monitoring correctly

**Solutions:**
1. Verify flag file path: `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`
2. Check service has read access to ProgramData
3. Review service logs in Event Viewer

## Deployment Best Practices

### 1. Pre-Deployment Testing
- Test MSI installation in isolated environment
- Validate service functionality with test bootstrap files
- Verify uninstallation removes service cleanly

### 2. Staged Rollout
- Deploy to pilot group first
- Monitor service health across deployment
- Validate bootstrap triggering in production

### 3. Monitoring and Maintenance
- Set up service monitoring alerts
- Regular health checks using test scripts
- Monitor Event Log for service activities

## Conclusion

The CimianWatcher service installation is **fully automated** through MSI custom actions. No manual service registration steps are required when the MSI is installed with appropriate privileges, which is standard for all enterprise deployment scenarios.

The system is designed for enterprise deployment where software installation occurs in system context with administrative privileges, ensuring reliable and consistent service registration across all target machines.

---
*Enterprise Deployment Guide*  
*Version: 2025.07.12*  
*Author: GitHub Copilot Assistant*
