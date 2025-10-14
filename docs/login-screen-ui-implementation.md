# Login Screen UI Support - Implementation Summary

## Overview
Implemented MunkiStatus-style login screen UI capability for CimianStatus, enabling visual progress feedback during bootstrap provisioning when no user is logged in.

## Problem Statement
Previously, CimianStatus could only display UI when a user was logged in. During zero-touch bootstrap provisioning at the Windows login screen (like macOS's MunkiStatus), there was no visual feedback for administrators or end users.

## Solution

### Architecture
Similar to how MunkiStatus runs on macOS login windows, CimianStatus now detects when running as SYSTEM without active user sessions and launches a simplified login screen UI.

### Key Components

#### 1. CimianStatus (cmd/cimistatus/)

**Program.cs Enhancements:**
- Added `--login-screen` command line argument support
- Auto-detection of SYSTEM context + bootstrap file presence
- Platform-specific APIs: `SetProcessDPIAware()`, `GetSystemMetrics()` for remote session detection
- New `RunAtLoginScreen()` method for simplified UI mode

**LoginScreenWindow (Views/):**
- Minimalist, borderless window design optimized for login screen
- Dark theme (matching Windows login aesthetic)
- Real-time progress monitoring via events.jsonl parsing
- Auto-close when bootstrap file is removed
- Key Features:
  - Title: "Cimian System Configuration"
  - Status text with current operation
  - Progress bar (0-100%)
  - Detail text for package names
  - No window chrome, always on top, centered

**Progress Monitoring:**
- Monitors `C:\ProgramData\ManagedInstalls\logs\` for latest session
- Parses events.jsonl for progress/status updates
- 1-second polling interval
- Graceful handling of missing/incomplete data

#### 2. CimianWatcher Service (cmd/cimiwatcher/)

**Enhanced Session Detection:**
- `hasActiveUserSession()` - Detects logged-in users via WTSGetActiveConsoleSessionId
- Validates user token availability to confirm active sessions
- Distinguishes between Session 0 (services) and user sessions (1+)

**Dual-Mode GUI Launching:**
- **Active User Session**: Uses existing `launchGUIInUserSession()` with Win32 APIs
  - WTSQueryUserToken
  - DuplicateTokenEx  
  - CreateProcessAsUser
  - Launches in user's interactive desktop
  
- **Login Screen (No User)**: New `launchAtLoginScreen()` function
  - Launches with `--login-screen` flag
  - Runs as SYSTEM with CREATE_NEW_CONSOLE flag
  - No token duplication needed
  - Direct CreateProcess call

**Bootstrap Trigger Logic:**
- Checks `hasActiveUserSession()` before GUI launch
- Routes to appropriate launch method based on session state
- Comprehensive event logging for both modes

### File Structure

```
cmd/cimistatus/
├── Program.cs (enhanced with login screen support)
├── Views/
│   ├── LoginScreenWindow.xaml (new)
│   ├── LoginScreenWindow.xaml.cs (new)
│   └── MainWindow.xaml (existing, unmodified)
└── Models/
    └── InstallEvents.cs (existing, used for progress data)

cmd/cimiwatcher/
└── main.go (enhanced with session detection and dual-mode launching)
```

## Usage Scenarios

### Scenario 1: Zero-Touch Provisioning (No User Logged In)
1. Computer boots, no user logs in
2. MDM/GPO creates `.cimian.bootstrap` file
3. CimianWatcher detects file
4. No active user session detected
5. Launches `cimistatus.exe --login-screen`
6. Simplified UI appears at login screen
7. Shows real-time bootstrap progress
8. Auto-closes when complete

### Scenario 2: Standard Bootstrap (User Logged In)
1. User logged in and active
2. Bootstrap file created
3. CimianWatcher detects file
4. Active user session detected
5. Launches full CimianStatus UI in user session
6. Standard rich UI with all features

### Scenario 3: Remote Desktop Session
1. User connected via RDP
2. Bootstrap triggered
3. Remote session detection (SM_REMOTESESSION)
4. Login screen UI suppressed (cannot display in RDP)
5. Bootstrap continues headless

## Technical Details

### Windows APIs Used

**Session Management:**
- `WTSGetActiveConsoleSessionId` - Get physical console session
- `WTSQueryUserToken` - Validate user session has token
- `GetSystemMetrics(SM_REMOTESESSION)` - Detect remote sessions

**Display Management:**
- `SetProcessDPIAware` - Ensure proper scaling at login screen
- `CREATE_NEW_CONSOLE` - Create visible console for SYSTEM process

### Security Context

**Login Screen Mode:**
- Runs as SYSTEM (NT AUTHORITY\SYSTEM)
- No user impersonation
- Full access to ProgramData paths
- Can read events.jsonl directly

**User Session Mode:**
- Impersonates logged-in user
- Runs in user's security context
- Uses user's desktop and profile

## Benefits

1. **Visual Feedback**: Administrators can monitor bootstrap progress even when no user is logged in
2. **End-User Confidence**: New computer deployments show clear progress indication
3. **Munki Parity**: Matches macOS MunkiStatus behavior on Windows
4. **Enterprise Deployment**: Ideal for autopilot/zero-touch provisioning scenarios
5. **Debugging**: Easier to diagnose bootstrap issues with visible UI

## Limitations

1. **Remote Desktop**: Cannot display UI in RDP sessions at login screen
2. **Session 0 Isolation**: Modern Windows limits Session 0 UI interaction
3. **Simplified UI**: Login screen mode uses basic WPF window (no resource-intensive features)
4. **Single Instance**: Only one login screen instance can run at a time

## Testing Checklist

- [ ] Bootstrap at login screen (no user logged in)
- [ ] Bootstrap with active user session
- [ ] Bootstrap via RDP (should suppress UI)
- [ ] Progress bar updates correctly
- [ ] Auto-close when bootstrap completes
- [ ] Event log entries for both modes
- [ ] Graceful handling of missing events.jsonl
- [ ] DPI scaling on high-resolution displays

## Future Enhancements

Potential improvements for consideration:

1. **Credential Provider Integration**: Deeper Windows login integration
2. **Branding Support**: Custom logos/colors via configuration
3. **Multi-Language**: Localization for status messages
4. **Network Status Indicator**: Show network wait status visually
5. **Estimated Time Remaining**: Calculate ETA based on progress
6. **Screenshot Capture**: For remote diagnostics
7. **Sound Notifications**: Audio feedback for completion

## Comparison to Munki

| Feature | Munki (macOS) | Cimian (Windows) | Status |
|---------|---------------|------------------|--------|
| Login Window UI | MunkiStatus.app | CimianStatus.exe --login-screen | ✅ Implemented |
| SYSTEM Context | launchd as root | Windows Service as SYSTEM | ✅ Implemented |
| Progress Display | Native Cocoa UI | WPF Window | ✅ Implemented |
| Auto-close | Yes | Yes | ✅ Implemented |
| Session Detection | IOKit APIs | WTS* APIs | ✅ Implemented |
| Bootstrap Looping | checkandinstallatstartup | .cimian.bootstrap | ✅ Implemented |

## Conclusion

This implementation brings Windows deployment automation to feature parity with macOS Munki, enabling true zero-touch provisioning with visual feedback at the login screen. The dual-mode architecture elegantly handles both attended and unattended deployment scenarios while maintaining security best practices and Windows platform conventions.
