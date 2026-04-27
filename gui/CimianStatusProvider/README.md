# CimianStatusProvider

Native C++/COM Pre-Logon-Access Provider (PLAP) credential provider that
displays Cimian bootstrap progress on the Windows logon screen — the closest
supported analogue to Munki's `MunkiStatus` at macOS `loginwindow`.

## What it does

When LogonUI runs (Winlogon secure desktop, before any user has logged in),
this DLL is loaded as a PLAP and shows a tile containing:

- A status string (`statusMessage`)
- A detail string (`detailMessage`)
- A progress bar bitmap re-rendered on each `percentProgress`
- A percent label
- A "View log" command link toggling the tail of
  `C:\ProgramData\ManagedInstalls\Logs\managedsoftwareupdate.log`

The DLL listens on `127.0.0.1:19847` — the same TCP/JSON channel the existing
WPF `cimistatus.exe` consumes — so `managedsoftwareupdate.exe`'s
`StatusReporter` works unchanged.

## Why a native DLL

Credential providers are loaded in-process by `LogonUI.exe`. They are COM DLLs
and must be self-contained — no .NET runtime, no third-party dependencies that
LogonUI does not already have. This DLL links only `Ws2_32`, `Shlwapi`, `Gdi32`,
`User32`, `Ole32`, `Advapi32`, and `credui`.

## Registration

`DllRegisterServer` writes:

- `HKLM\SOFTWARE\Classes\CLSID\{C1819A88-...}\InprocServer32` → DLL path
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\PLAP Providers\{C1819A88-...}`

Run `regsvr32 /s CimianStatusProvider.dll` after install (handled by
`build/pkg/postinstall.ps1`).

## Build

This project is built by `build.ps1` alongside the .NET binaries — see the
`cimianstatusprovider` entry in the build map.
