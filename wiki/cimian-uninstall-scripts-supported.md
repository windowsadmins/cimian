# Cimian Uninstall Scripts - Supported Types and Methods

This document provides a comprehensive overview of the uninstall script types and methods supported by the Cimian package management system.

## Overview

Cimian supports multiple uninstall methods and script types to accommodate different software packaging formats and uninstallation requirements. The system follows Munki's proven approach to package uninstallability with both explicit control and automatic determination.

The uninstall execution path is `InstallerService.UninstallAsync` in `cli/managedsoftwareupdate/Services/InstallerService.cs`. The auto-uninstallability rules are defined by `CatalogItem.IsUninstallable()` in `cli/managedsoftwareupdate/Models/UpdateModels.cs`.

## Flags vs Switches Support

Cimian provides distinct support for `flags` and `switches` to accommodate different argument conventions used across Windows applications:

### Switches (Windows-style)
- Use forward slash prefix (`/`)
- Common in Windows installers and native applications
- Examples: `/silent`, `/norestart`, `/log=install.log`

### Flags (Unix-style)  
- Use dash prefix (`--` or `-`)
- Auto-default to `--` prefix when not explicitly specified
- Common in cross-platform and modern applications
- Examples: `--quiet`, `--force`, `--config=value`

### Usage Examples

`uninstaller` is a list in the pkginfo model, but only the first entry (`item.Uninstaller[0]`) is executed by `UninstallAsync`. Supported `type` values are `msi`, `exe`, `powershell`/`ps1`, and `msix`/`appx`.

```yaml
uninstaller:
  - type: msi
    product_code: "{12345678-1234-1234-1234-123456789012}"
    switches:
      - q                    # Becomes /q
      - norestart            # Becomes /norestart
      - log=c:\temp\uninstall.log  # Becomes /log=c:\temp\uninstall.log
    flags:
      - passive              # Becomes --passive
      - force                # Becomes --force
      - "-v"                 # Preserved as -v (explicit single dash)
      - "--debug"            # Preserved as --debug (explicit double dash)

# For type: exe / powershell, the executable or script is given in `command`:
uninstaller:
  - type: exe
    command: C:\Program Files\MyApp\uninstall.exe
    switches:
      - S                    # Becomes /S (silent mode)
      - REMOVEDATA           # Becomes /REMOVEDATA
    flags:
      - quiet                # Becomes --quiet
      - force                # Becomes --force
      - config=production    # Becomes --config=production
```

**Smart prefix detection**: When switches/flags are given without an explicit prefix, `UninstallerInfo.GetAllArgs()` normalizes switches to `/` and flags to `--` (single-character flags are normalized to `-`). Explicit prefixes (`-v`, `--debug`, `/SILENT`) are preserved as-is.

## Supported Uninstall Script Types

### 1. MSI Packages (Built-in Support)
- **Type**: `msi`
- **Method**: Uses Windows Installer's built-in uninstall functionality
- **Command**: `msiexec /x <ProductCode> /qn /norestart` (the `ProductCode` is read from the `uninstaller` block, from `installs[]` of `type: msi`, or from a legacy `installer.product_code` field)
- **Auto-uninstallable**: Yes — when a `ProductCode` is available
- **Example**:
```yaml
installer:
  type: msi
  location: /packages/app-1.0.0.msi
# Automatically uninstallable via msiexec /x — ProductCode is captured by
# cimiimport/makepkginfo into installs[] (type: msi) and used at uninstall time.
```

### 2. PowerShell Scripts (.ps1)
- **Type**: `powershell` or `ps1`
- **Method**: Executes the script defined by `command:` via the script service
- **Auto-uninstallable**: Only when an explicit `uninstaller` block is defined; an installer of `type: ps1` alone does NOT make the package self-uninstallable
- **Example**:
```yaml
installer:
  type: ps1
  location: /scripts/install-app.ps1
uninstaller:
  - type: powershell
    command: /scripts/uninstall-app.ps1
    flags: [Force, Quiet]
```

### 3. EXE Uninstallers
- **Type**: `exe`
- **Method**: Executes the uninstaller given in `command:` with switches/flags/args
- **Auto-uninstallable**: Only when an explicit `uninstaller` block is defined
- **Example**:
```yaml
installer:
  type: exe
  location: /packages/app-installer.exe
uninstaller:
  - type: exe
    command: C:\Program Files\MyApp\uninstall.exe
    switches: [S, NORESTART]
```

### 4. Chocolatey Packages (.nupkg)
- **Type**: `nupkg`
- **Install**: Routed through sbin-installer with a Chocolatey fallback
- **Uninstall**: Not currently handled as a first-class case — `UninstallAsync` has no `nupkg` branch and falls back to the MSI handler, which requires a `ProductCode`. To remove a nupkg-installed package, supply an explicit `uninstaller` block (e.g., `type: powershell` invoking `choco uninstall`) or rely on `installs[]`-based tracking with a separate uninstall mechanism.
- **Status**: Planned — first-class `choco uninstall` support is not yet implemented.

### 5. MSIX / APPX Packages
- **Type**: `msix` (also covers `appx`, `msixbundle`, `appxbundle`)
- **Install command**: `Add-AppxProvisionedPackage -Online -PackagePath <file> -SkipLicense`,
  preceded by a preflight check that removes older per-user installs for the same
  identity if necessary (see "Install behavior" below).
- **Uninstall command**: Two steps in sequence, both required to fully remove the app:
  1. `Remove-AppxProvisionedPackage -Online -PackageName <PackageFullName>` — removes
     the system-wide provisioning entry so new user profiles don't re-provision it.
  2. `Get-AppxPackage -AllUsers -Name <IdentityName> | Remove-AppxPackage -AllUsers` —
     removes any per-user registrations left behind (from the provisioned package
     itself, from vendor auto-updates, or from previous Store installs).
  Removing only the provisioned entry leaves the app fully functional for currently-
  registered users, which surprised us during testing and is inconsistent with what
  an admin expects from "uninstall".
- **Auto-uninstallable**: Yes — cimiimport emits an `installs` entry with `type: msix`
  and `identity_name`, and a matching `uninstaller` block. When the pkginfo has no
  explicit uninstaller, Cimian synthesizes one from the installs-array entry.
- **How PackageFullName is resolved**: at install time, the `PackageName` returned by
  `Add-AppxProvisionedPackage` is persisted to `HKLM\SOFTWARE\ManagedInstalls\<Name>`
  alongside `IdentityName` and `InstallerType`. Uninstall reads that value. If the
  registry entry is missing, uninstall falls back to a runtime
  `Get-AppxProvisionedPackage -Online` query filtered by `identity_name`.

#### Install behavior

Cimian's MSIX install path runs a preflight against both Appx stores before calling
`Add-AppxProvisionedPackage`:

1. **Higher version already installed → silent skip, no action.** This is the
   never-downgrade policy. If `Get-AppxPackage -AllUsers` or `Get-AppxProvisionedPackage`
   reports the package at a version >= the catalog version, Cimian logs
   `"Newer version installed"` and returns success without calling the install cmdlet.
   Detection normally catches this case first, but the preflight is a safety net for
   races where the auto-updater runs between detection and install.
2. **Older per-user install blocks provisioning → auto-remediation.** When a vendor
   like Slack auto-updates itself, the app is registered per-user rather than
   provisioned. An older per-user registration conflicts with
   `Add-AppxProvisionedPackage` and surfaces as `0x80070490 Element not found`.
   Cimian auto-remediates by running `Remove-AppxPackage -AllUsers` for the identity
   first, then provisioning the catalog version. **User data is preserved** by
   Windows' Appx subsystem — Slack chat history, settings, and anything else under
   `%LOCALAPPDATA%\Packages\<PackageFamilyName>\` survives the remediation, so the
   user experience is "Slack restarted" not "Slack reset". This is worth
   communicating to end users in deployment announcements so nobody panics the
   first time they see a reinstall in their session.
3. **Nothing installed → direct provisioning.** The common path: just
   `Add-AppxProvisionedPackage`, capture the returned `PackageFullName`, persist
   to the ManagedInstalls registry, done.

#### Requirements

- **Signing certificate must be trusted on the client before install.** MSIX
  packages from vendors whose signing certs aren't in the Windows-managed trusted
  roots (many legitimate open-source and third-party vendors) will fail install
  with `0x800b0109 CERT_E_UNTRUSTEDROOT`. Cimian does **not** auto-install
  publisher certs into `LocalMachine\TrustedPublisher` — that's an explicit
  policy decision, because once a cert is in `TrustedPublisher`, any future
  package signed by that publisher can sideload without further review. Use one
  of these pre-deployment channels instead:
  - **Group Policy**: `Computer Configuration → Windows Settings → Security
    Settings → Public Key Policies → Trusted Publishers`, importing the vendor's
    code-signing cert.
  - **Intune / MDM cert profile**: deploy a trusted-publisher certificate profile
    from your MDM tenant before the MSIX package is assigned.
  - **One-off manual import** (dev machines / pilot testing):
    `Import-Certificate -FilePath vendor.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher`
  Vendor certs from Microsoft, Slack, Adobe, etc. are typically already trusted
  via Windows' cross-signing roots and don't need this step. The failure mode is
  obvious in the Cimian install log, so you'll know immediately which packages
  need this treatment.

#### Out of scope

- License files (`.xml`) required by Store-signed apps — use `preinstall_script`
  to `Add-AppxProvisionedPackage -LicensePath ...`.
- Per-architecture nested manifest extraction from `.msixbundle` payloads. Bundles
  install transparently through `Add-AppxProvisionedPackage`, but cimiimport only
  parses the top-level bundle manifest into pkginfo metadata.
- Auto-extraction of the MSIX signing cert during import (considered and rejected
  — see "Requirements" above for why).

#### Example (auto-generated by cimiimport)

```yaml
installer:
  type: msix
  location: /apps/comms/Slack-x64-4.45.69.0.msix
installs:
  - type: msix
    identity_name: com.tinyspeck.slackdesktop
    version: 4.45.69.0
uninstaller:
  - type: msix
    identity_name: com.tinyspeck.slackdesktop
```

### 7. Uninstalls Array
- **Status**: **Planned — not yet implemented.** The current `CatalogItem` model does NOT define an `uninstalls:` property, and `InstallerService.UninstallAsync` does not consume one. The dispatch path only branches on `item.Uninstaller[0]` (the first entry of the `uninstaller:` list) or `item.UninstallScript`, with self-uninstallable fallbacks for MSI and MSIX. A typed multi-operation `uninstalls:` array (file/directory/registry/application removal) is a Munki-inspired feature that has not been wired into the C# code yet.
- **Workaround today**: For multi-step cleanup, use a `postuninstall_script` (PowerShell) that runs after `UninstallAsync` succeeds.

### 6. Batch Scripts (.bat)
- **Type**: Pre/post install scripts support batch format
- **Method**: Detects batch scripts by content (`@echo off`, `rem`, `::`)
- **Support**: Limited to pre/post install scripts
- **Detection**: Automatic based on script content

## Uninstall Method Determination

### Automatic Uninstallability

`CatalogItem.IsUninstallable()` returns true when `uninstallable` is not explicitly `false` AND any of the following applies:

1. **Explicit uninstaller defined** — `uninstaller:` has at least one entry
   ```yaml
   uninstaller:
     - type: exe
       command: C:\Program Files\MyApp\uninstall.exe
       switches: [S]
   ```

2. **Registry tracking defined** — `check.registry.name` is set
   ```yaml
   check:
     registry:
       name: MyApplication
       version: 1.0.0
   ```
   > Note: `IsUninstallable()` returns true in this case, but `UninstallAsync` will only succeed if there is also a usable uninstaller (explicit `uninstaller:` block, an `installs[]` MSI/MSIX entry as below, or an `uninstall_script`). Otherwise it returns "No uninstaller defined".

3. **Self-uninstallable MSI via `installs[]`** — an `installs` entry of `type: msi` with a `product_code`. `UninstallAsync` synthesizes an MSI uninstaller from this entry.
   ```yaml
   installs:
     - type: msi
       product_code: "{12345678-1234-1234-1234-123456789012}"
       version: 1.0.0
   ```

4. **Self-uninstallable MSI via legacy `installer.product_code`** — for pkginfos written before `product_code` moved to `installs[]`.

5. **Self-uninstallable MSIX/APPX via `installs[]`** — an `installs` entry of `type: msix` or `type: appx` with `identity_name`. `UninstallAsync` synthesizes an MSIX uninstaller using the two-step `Remove-AppxProvisionedPackage` + `Remove-AppxPackage` flow.
   ```yaml
   installs:
     - type: msix
       identity_name: com.tinyspeck.slackdesktop
       version: 4.45.69.0
   ```

### Explicit Control

You can explicitly control uninstallability:

```yaml
# Prevent uninstalling
uninstallable: false

# Allow uninstalling
uninstallable: true

# Auto-determine (default)
uninstallable: null  # or omit the key
```

## Uninstall Process Flow

`InstallerService.UninstallAsync` executes the following steps for an item flagged for uninstall:

1. **Run `preuninstall_script`** if defined; abort on failure.
2. **Dispatch by uninstaller type** (first entry of `uninstaller:`):
   - `msi` → `msiexec /x <ProductCode> /qn /norestart` plus any switches/flags
   - `exe` → execute `command:` with combined switches/flags/args
   - `powershell` / `ps1` → run the script in `command:` via the script service
   - `msix` / `appx` → two-step `Remove-AppxProvisionedPackage` + `Remove-AppxPackage -AllUsers`
   - Anything else falls through to the MSI handler (which will fail without a `ProductCode`)
3. **If no `uninstaller:` block**, fall back to:
   - `uninstall_script` (raw script body) if present, or
   - A synthesized MSI uninstall if `installs[]` carries a `product_code`, or
   - A synthesized MSIX uninstall if `installs[]` carries `type: msix`/`appx` with `identity_name`.
4. **Run `postuninstall_script`** if the uninstall succeeded (warns but does not fail on script error).
5. **Unregister from `HKLM\SOFTWARE\ManagedInstalls\<Name>`** to clear installation tracking.

## Advanced Features

### Dependency-Aware Uninstallation
- Automatically removes dependent packages first
- Handles update relationships
- Prevents orphaned dependencies

### Blocking Applications
- Checks for running applications that would block uninstall
- Provides warnings about blocking processes
- Allows for graceful handling of locked files

### Error Handling
- Continues with other packages if some fail
- Provides detailed logging of failures
- Returns success if any packages were successfully removed

## Use Cases and Examples

### System Components (Non-uninstallable)
```yaml
name: critical-system-component
version: 1.0.0
installer:
  type: msi
  location: /packages/critical-1.0.0.msi
uninstallable: false  # Explicitly prevent removal
```

### Development Tools (Auto-uninstallable)
```yaml
name: visual-studio-code
version: 1.85.0
installer:
  type: exe
  location: /packages/VSCode-1.85.0.exe
uninstaller:
  - type: exe
    command: C:\Program Files\Microsoft VS Code\unins000.exe
    switches: [SILENT, NORESTART]
# Auto-determined as uninstallable because uninstaller is defined
```

### Script-only Packages
```yaml
name: configuration-script
version: 1.0.0
installer:
  type: ps1
  location: /scripts/configure.ps1
# Auto-determined as non-uninstallable (no tracking)
```

### OnDemand Items (Never Uninstallable)
```yaml
name: maintenance-task
version: 1.0.0
installer:
  type: ps1
  location: /scripts/maintenance.ps1
OnDemand: true
# Auto-determined as non-uninstallable
```

## Implementation Notes

- Uninstall scripts are executed with the privileges of the `managedsoftwareupdate` process (typically SYSTEM when run via the scheduled task / service)
- PowerShell scripts run via the script service with `-NoProfile` and `-ExecutionPolicy Bypass`
- MSI uninstalls use quiet mode (`/qn /norestart`) by default
- MSIX/APPX uninstall is a two-step PowerShell flow (`Remove-AppxProvisionedPackage` then `Remove-AppxPackage -AllUsers`) — see the MSIX section above

## Best Practices

1. **Provide an explicit `uninstaller:` block** for EXE-based installations
2. **Use `installs[]` with `product_code` or `identity_name`** to enable self-uninstall for MSI/MSIX without writing a separate uninstaller
3. **Test uninstall paths** thoroughly before deployment (use `--checkonly` to dry-run detection)
4. **Mark system-critical packages** as `uninstallable: false` to block accidental removal

## Limitations

- Batch scripts (`.bat`) are supported only for pre/post install operations, not as primary uninstallers
- OnDemand items cannot be uninstalled as they're not considered "installed"
- EXE installers without an explicit `uninstaller:` block are not automatically uninstallable
- PowerShell-only installers without an explicit `uninstaller:` block are not automatically uninstallable
- The `uninstaller:` field is a list but only the first entry is dispatched — multi-step uninstall must currently be handled by a single PowerShell script (or `postuninstall_script`)
- A typed `uninstalls:` array (Munki-style file/directory/registry/application cleanup) is **not implemented** — see the "Uninstalls Array" section above

---

*This document reflects the current implementation of Cimian's uninstall system as of the latest version. For the most up-to-date information, refer to the source code and official documentation.*
