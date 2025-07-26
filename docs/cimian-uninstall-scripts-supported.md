# Cimian Uninstall Scripts - Supported Types and Methods

This document provides a comprehensive overview of the uninstall script types and methods supported by the Cimian package management system.

## Overview

Cimian supports multiple uninstall methods and script types to accommodate different software packaging formats and uninstallation requirements. The system follows Munki's proven approach to package uninstallability with both explicit control and automatic determination.

## Complex Application with Uninstalls Array
```yaml
name: complex-application
version: 2.1.0
installer:
  type: exe
  location: /packages/complex-app-2.1.0.exe 
  arguments: [/S, /INSTALLDIR=C:\Program Files\ComplexApp]
uninstaller:
  location: /packages/complex-app-uninstaller.exe
  arguments: [/S]
uninstalls:
  # Remove specific files
  - type: file
    path: C:\Program Files\ComplexApp\app.exe
    force: true
  - type: file 
    path: C:\Program Files\ComplexApp\legacy.dll
    force: true
  # Remove directories
  - type: directory
    path: C:\ProgramData\ComplexApp
    recursive: true
    force: true
  - type: directory
    path: C:\Users\Public\ComplexApp
    recursive: true
    force: true
  # Remove registry entries
  - type: registry
    path: HKLM\SOFTWARE\ComplexApp
  - type: registry
    path: HKCU\SOFTWARE\ComplexApp
  # Terminate and uninstall services/applications
  - type: application
    name: ComplexAppService.exe
    switches: [stop, remove]          # Windows-style switches (/stop, /remove)
    flags: [force, no-prompt]         # Unix-style flags (--force, --no-prompt)
  - type: application  
    name: ComplexAppAgent.exe
    switches: [terminate]             # Windows-style switch (/terminate)
    flags: [immediate]                # Unix-style flag (--immediate)
# Auto-determined as uninstallable (has both uninstaller and uninstalls array)
```

### Development Tools (Auto-uninstallable)
```yaml
name: visual-studio-code
version: 1.85.0
installer:
  type: exe
  location: /packages/VSCode-1.85.0.exe
uninstaller:
  location: /packages/VSCode-uninstaller.exe
  arguments: [/SILENT]
# Auto-determined as uninstallable (has uninstaller)
```

## Overview

Cimian supports multiple uninstall methods and script types to accommodate different software packaging formats and uninstallation requirements. The system follows Munki's proven approach to package uninstallability with both explicit control and automatic determination.

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

```yaml
uninstaller:
  - type: msi
    product_code: "{12345678-1234-1234-1234-123456789012}"
    switches:
      - q                    # Becomes /q
      - norestart           # Becomes /norestart  
      - log=c:\temp\uninstall.log  # Becomes /log=c:\temp\uninstall.log
    flags:
      - passive             # Becomes --passive
      - force               # Becomes --force
      - "-v"                # Preserved as -v (explicit single dash)
      - "--debug"           # Preserved as --debug (explicit double dash)

  - type: exe
    path: C:\Program Files\MyApp\uninstall.exe
    switches:
      - S                   # Becomes /S (silent mode)
      - REMOVEDATA         # Becomes /REMOVEDATA
    flags:
      - quiet              # Becomes --quiet
      - force              # Becomes --force
      - config=production  # Becomes --config=production
```

**Smart Flag Prefix Detection**: When flags don't have explicit prefixes, Cimian intelligently defaults to `--` for compatibility with modern applications while respecting explicitly provided prefixes.

## Supported Uninstall Script Types

### 1. MSI Packages (Built-in Support)
- **Type**: `msi`
- **Method**: Uses Windows Installer's built-in uninstall functionality
- **Command**: `msiexec /x <package.msi> /qn /norestart`
- **Auto-uninstallable**: Yes
- **Example**:
```yaml
installer:
  type: msi
  location: /packages/app-1.0.0.msi
# Automatically uninstallable via MSI uninstall
```

### 2. PowerShell Scripts (.ps1)
- **Type**: `ps1`
- **Method**: Executes PowerShell uninstall scripts
- **Command**: `powershell -NoProfile -ExecutionPolicy Bypass -File <script.ps1>`
- **Auto-uninstallable**: Only if explicit uninstaller defined or has tracking
- **Example**:
```yaml
installer:
  type: ps1
  location: /scripts/install-app.ps1
uninstaller:
  location: /scripts/uninstall-app.ps1
  arguments: [-Force, -Quiet]
```

### 3. EXE Uninstallers
- **Type**: `exe`
- **Method**: Executes dedicated uninstaller executables
- **Auto-uninstallable**: Only if explicit uninstaller defined or has tracking
- **Example**:
```yaml
installer:
  type: exe
  location: /packages/app-installer.exe
uninstaller:
  location: /packages/app-uninstaller.exe
  arguments: [/S, /NORESTART]
```

### 4. Chocolatey Packages (.nupkg)
- **Type**: `nupkg`
- **Method**: Uses Chocolatey's built-in uninstall functionality
- **Command**: `choco uninstall <packageId> --version <version> --source <cache> -y --force`
- **Auto-uninstallable**: Yes
- **Example**:
```yaml
installer:
  type: nupkg
  location: /packages/app.1.5.0.nupkg
# Automatically uninstallable via Chocolatey
```

### 5. MSIX Packages
- **Type**: `msix`
- **Method**: Uses PowerShell's `Remove-AppxPackage` cmdlet
- **Command**: `Remove-AppxPackage`
- **Auto-uninstallable**: Yes
- **Example**:
```yaml
installer:
  type: msix
  location: /packages/store-app.msix
# Automatically uninstallable via MSIX removal
```

### 7. Uninstalls Array (Advanced Feature)
- **Type**: Array of uninstall operations
- **Method**: Executes multiple uninstall operations with specific parameters
- **Auto-uninstallable**: Yes (when present)
- **Supported Operations**:
  - `file`: Remove specific files with force option
  - `directory`: Remove directories with recursive option
  - `registry`: Remove registry keys and values
  - `application`: Terminate/uninstall applications with custom arguments
- **Example**:
```yaml
uninstalls:
  - type: file
    path: C:\Program Files\MyApp\app.exe
    force: true
  - type: directory
    path: C:\ProgramData\MyApp
    recursive: true
    force: true
  - type: registry
    path: HKLM\SOFTWARE\MyApp
  - type: application
    name: MyAppService.exe
    arguments: [/stop, /uninstall]
    switches: [--force, --no-restart]
```

### 6. Batch Scripts (.bat)
- **Type**: Pre/post install scripts support batch format
- **Method**: Detects batch scripts by content (`@echo off`, `rem`, `::`)
- **Support**: Limited to pre/post install scripts
- **Detection**: Automatic based on script content

## Uninstall Method Determination

### Automatic Uninstallability

Cimian automatically determines if a package can be uninstalled based on:

1. **Explicit uninstaller defined**
   ```yaml
   uninstaller:
     location: /packages/uninstaller.exe
     arguments: [/quiet]
   ```

2. **Package type with built-in uninstall support**
   - MSI packages
   - Chocolatey packages (nupkg)
   - MSIX packages

3. **Registry tracking defined**
   ```yaml
   check:
     registry:
       name: MyApplication
       version: 1.0.0
   ```

4. **File/directory tracking defined**
   ```yaml
   installs:
     - type: file
       path: C:\Program Files\MyApp\app.exe
       version: 1.0.0
   ```

5. **Uninstalls array defined**
   ```yaml
   uninstalls:
     - type: file
       path: C:\Program Files\MyApp\app.exe
       force: true
     - type: directory
       path: C:\ProgramData\MyApp
       recursive: true
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

1. **Check uninstallability**: Verify the item can be uninstalled
2. **Dependency checking**: Remove dependent items first
3. **Update handling**: Remove any update items
4. **Execute uninstall**: Run the appropriate uninstall method
   - If `uninstalls` array is present: Process each uninstall item with specific arguments/switches
   - Fallback to traditional uninstaller (exe, msi, ps1, etc.)
5. **Registry cleanup**: Remove installation tracking from registry
6. **Cache cleanup**: Clean up cached installer files

## Advanced Features

### Uninstalls Array (New Feature)
- **Enhanced uninstall control**: Define specific files, directories, registry keys, and applications to remove
- **Flexible arguments**: Support for custom flags, switches, and arguments per uninstall item
- **Multiple uninstall methods**: Mix different uninstall approaches in a single package
- **Granular control**: Specify exact uninstall behavior for each component

Example `uninstalls` array:
```yaml
uninstalls:
  - type: file
    path: C:\Program Files\MyApp\uninstall.exe
    switches:
      - silent
      - force
    flags:
      - quiet
      - no-restart
  - type: msi
    product_code: "{12345678-1234-1234-1234-123456789012}"
    switches:
      - "REBOOT=Suppress"
    flags:
      - "ALLUSERS=1"
  - type: directory 
    path: C:\ProgramData\MyApp
  - type: registry
    path: HKLM\SOFTWARE\MyApp
  - type: ps1
    path: C:\Scripts\cleanup.ps1
    flags:
      - "Force"
      - "RemoveData=true"
```

#### Uninstalls Array Item Types

- **file**: Remove specific files from the system
- **directory**: Remove directories and their contents  
- **msi**: Uninstall MSI packages using ProductCode
- **exe**: Execute uninstaller executables with arguments
- **ps1/powershell**: Run PowerShell scripts for custom uninstall logic
- **registry**: Remove registry keys/values (placeholder for future enhancement)

#### Switches vs Flags

The uninstalls array supports both `switches` and `flags` for different argument styles:

- **switches**: Traditional Windows-style arguments with `/` prefix (e.g., `/silent`, `/force`)
- **flags**: Unix-style arguments with `-` or `--` prefix (e.g., `--quiet`, `-no-restart`)

This distinction mirrors the `installs` array pattern and provides maximum flexibility for different installer types.

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
  location: /packages/VSCode-uninstaller.exe
  arguments: [/SILENT]
# Auto-determined as uninstallable
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

- Uninstall scripts are executed with appropriate privileges
- PowerShell scripts run with `-NoProfile` and `-ExecutionPolicy Bypass`
- MSI uninstalls use quiet mode (`/qn`) by default
- Chocolatey uninstalls include proper version and source specification
- MSIX packages use Windows PowerShell cmdlets for removal

## Best Practices

1. **Always provide explicit uninstallers** for EXE-based installations
2. **Use tracking mechanisms** (registry checks or installs array) for custom installations
3. **Test uninstall processes** thoroughly before deployment
4. **Consider dependencies** when designing package relationships
5. **Use appropriate uninstallable settings** for system-critical components
6. **Provide detailed logging** in custom uninstall scripts

## Limitations

- Batch scripts (`.bat`) are supported only for pre/post install operations, not as primary uninstallers
- OnDemand items cannot be uninstalled as they're not considered "installed"
- EXE installers without explicit uninstallers or tracking are not automatically uninstallable
- PowerShell scripts without tracking mechanisms are not automatically uninstallable

---

*This document reflects the current implementation of Cimian's uninstall system as of the latest version. For the most up-to-date information, refer to the source code and official documentation.*
