# Uninstallable Key Usage

The `uninstallable` key in Cimian catalogs allows you to control whether a package can be uninstalled. This feature is based on Munki's implementation and provides both explicit control and automatic determination of uninstallability.

## Basic Usage

### Explicit Control

You can explicitly set whether an item can be uninstalled:

```yaml
# Example: Package that should never be uninstalled
name: "critical-system-component"
version: "1.0.0"
installer:
  type: "msi"
  location: "/packages/critical-component-1.0.0.msi"
  hash: "abc123..."
uninstallable: false  # Explicitly prevent uninstalling
```

```yaml
# Example: Package that can always be uninstalled
name: "optional-tool"
version: "2.1.0"
installer:
  type: "exe"
  location: "/packages/optional-tool-2.1.0.exe"
  hash: "def456..."
uninstaller:
  location: "/packages/optional-tool-uninstaller.exe"
  arguments: ["/S"]
uninstallable: true   # Explicitly allow uninstalling
```

### Automatic Determination

When the `uninstallable` key is omitted (or set to `null`), Cimian automatically determines if the item can be uninstalled based on these criteria:

#### Automatically Uninstallable Items

1. **Items with explicit uninstallers:**
```yaml
name: "app-with-uninstaller"
version: "1.0.0"
installer:
  type: "exe"
  location: "/packages/app-1.0.0.exe"
uninstaller:
  location: "/packages/app-uninstaller.exe"
  arguments: ["/quiet"]
# uninstallable: auto-determined as true
```

2. **MSI packages (built-in uninstall support):**
```yaml
name: "msi-application"
version: "3.2.1"
installer:
  type: "msi"
  location: "/packages/app-3.2.1.msi"
# uninstallable: auto-determined as true
```

3. **Chocolatey packages:**
```yaml
name: "choco-package"
version: "1.5.0"
installer:
  type: "nupkg"
  location: "/packages/app.1.5.0.nupkg"
# uninstallable: auto-determined as true
```

4. **MSIX packages:**
```yaml
name: "store-app"
version: "2.0.0"
installer:
  type: "msix"
  location: "/packages/store-app-2.0.0.msix"
# uninstallable: auto-determined as true
```

5. **Items with registry checks:**
```yaml
name: "registry-tracked-app"
version: "1.1.0"
installer:
  type: "exe"
  location: "/packages/app-1.1.0.exe"
check:
  registry:
    name: "MyApplication"
    version: "1.1.0"
# uninstallable: auto-determined as true
```

6. **Items with installs array (trackable files/directories):**
```yaml
name: "file-tracked-app"
version: "1.0.0"
installer:
  type: "exe"
  location: "/packages/app-1.0.0.exe"
installs:
  - type: "file"
    path: "C:\Program Files\MyApp\app.exe"
    version: "1.0.0"
# uninstallable: auto-determined as true
```

#### Automatically Non-Uninstallable Items

1. **OnDemand items (never considered "installed"):**
```yaml
name: "on-demand-script"
version: "1.0.0"
installer:
  type: "powershell"
  location: "/scripts/maintenance-script.ps1"
OnDemand: true
# uninstallable: auto-determined as false
```

2. **EXE installers without uninstaller or tracking:**
```yaml
name: "simple-exe"
version: "1.0.0"
installer:
  type: "exe"
  location: "/packages/simple-app-1.0.0.exe"
# No uninstaller, registry check, or installs array
# uninstallable: auto-determined as false
```

3. **PowerShell scripts without tracking:**
```yaml
name: "configuration-script"
version: "1.0.0"
installer:
  type: "powershell"
  location: "/scripts/configure-system.ps1"
# No tracking mechanism
# uninstallable: auto-determined as false
```

## Implementation Details

The uninstallable check is enforced at multiple levels:

1. **Status Check Level**: `CheckStatus()` in the status package will return `false` for uninstall operations on non-uninstallable items
2. **Installer Level**: `uninstallItem()` will fail early if the item is marked as non-uninstallable
3. **Removal Identification**: `identifyRemovals()` will skip items that are not uninstallable

## Use Cases

### System Components
```yaml
name: "windows-updates"
version: "2024.01"
installer:
  type: "msi"
  location: "/packages/critical-updates.msi"
uninstallable: false  # System critical, should never be removed
```

### Development Tools
```yaml
name: "visual-studio-code"
version: "1.85.0"
installer:
  type: "exe"
  location: "/packages/VSCode-1.85.0.exe"
uninstaller:
  location: "/packages/VSCode-uninstaller.exe"
  arguments: ["/SILENT"]
# uninstallable: auto-determined as true (has uninstaller)
```

### Security Software
```yaml
name: "antivirus-engine"
version: "2024.1.0"
installer:
  type: "msi"
  location: "/packages/antivirus-2024.1.0.msi"
uninstallable: false  # Prevent accidental removal of security software
```

This implementation provides both the flexibility of explicit control and the convenience of automatic determination, following Munki's proven approach to package uninstallability.
