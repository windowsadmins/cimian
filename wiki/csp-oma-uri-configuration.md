# Cimian CSP OMA-URI Configuration Guide

> **Status note (verified 2026-05-29):** The current `ConfigurationService` in
> `cli/managedsoftwareupdate/Services/ConfigurationService.cs` loads
> configuration exclusively from the YAML file at
> `C:\ProgramData\ManagedInstalls\Config.yaml`. When that file is missing it
> falls back to a hard-coded default object, **not** to registry/CSP values.
> The CSP/OMA-URI registry fallback described in this document is a
> deployment pattern and design target rather than a feature implemented in
> source today. The registry path and OMA-URI examples below are guidance
> for how policy could be pre-staged via Intune/GPO so that an external
> tool (or a future Cimian release) can materialize Config.yaml from it.

## Overview

Cimian's primary configuration source is `Config.yaml`. This guide describes
how to express the same settings via CSP OMA-URI policies so that Intune,
Group Policy, or another management tool can deliver them to a device. A
common pattern is to apply registry values via CSP, then have a provisioning
script write `Config.yaml` from those values before `managedsoftwareupdate`
runs for the first time.

## Configuration Priority (as implemented today)

1. **Primary (and only) source read by managedsoftwareupdate**:
   `C:\ProgramData\ManagedInstalls\Config.yaml` (YAML file)
2. **If the file is missing**: built-in defaults are used (placeholder
   `SoftwareRepoURL`, machine name as `ClientIdentifier`, `Production`
   catalog). The process does **not** read the registry today.

## Intended CSP-staging Flow

When a CSP-driven deployment is used:

1. CSP OMA-URI policy writes values under `HKLM\SOFTWARE\Cimian\Config` (or a
   Policies hive) at device enrollment time.
2. A provisioning step (Intune Win32 app, custom script, or bootstrap task)
   reads those registry values and writes `Config.yaml`.
3. `managedsoftwareupdate` then loads `Config.yaml` normally.

## CSP Registry Path

Cimian reads CSP configuration from this registry location:

```
HKEY_LOCAL_MACHINE\SOFTWARE\Cimian\Config
```

## Supported Configuration Values

The names below mirror the fields on the `CimianConfig` model
(`cli/managedsoftwareupdate/Models/UpdateModels.cs`) — i.e. the keys that
appear in `Config.yaml`. A provisioning script that materializes
`Config.yaml` from CSP-applied registry values should use these names.

### String Values
| Name | Reg type | Description | Example |
|---|---|---|---|
| `SoftwareRepoURL` | REG_SZ | Primary software repository URL | `https://cimian.company.com` |
| `ClientIdentifier` | REG_SZ | Unique client identifier | `DESKTOP-ABC123` |
| `LogLevel` | REG_SZ | Logging verbosity | `ERROR`, `WARN`, `INFO`, `DEBUG` |
| `CachePath` | REG_SZ | Cache directory path | `C:\ProgramData\ManagedInstalls\Cache` |
| `CatalogsPath` | REG_SZ | Catalogs directory path | `C:\ProgramData\ManagedInstalls\Catalogs` |
| `ManifestsPath` | REG_SZ | Manifests directory path | `C:\ProgramData\ManagedInstalls\Manifests` |
| `LocalOnlyManifest` | REG_SZ | Path to local-only manifest | `C:\Local\manifest.yaml` |
| `PreflightFailureAction` | REG_SZ | `continue` or `abort` | `continue` |
| `PostflightFailureAction` | REG_SZ | `continue` or `abort` | `continue` |
| `AuthUser` / `AuthPassword` / `AuthToken` | REG_SZ | Repo credentials (store via secure means) | — |
| `SbinInstallerPath` | REG_SZ | Path to `sbin\installer.exe` | `C:\Program Files\sbin\installer.exe` |
| `SbinInstallerTargetRoot` | REG_SZ | sbin-installer target root | `/` |
| `ClientCertificatePath` / `ClientCertificateThumbprint` / `ClientCertificatePassword` / `ClientKeyPath` | REG_SZ | SSL client cert auth | — |
| `SoftwareRepoCACertificate` | REG_SZ | CA certificate for repo TLS | — |

### Boolean Values
| Name | Reg type | Description |
|---|---|---|
| `Verbose` | REG_DWORD or REG_SZ | Enable verbose output |
| `Debug` | REG_DWORD or REG_SZ | Enable debug logging |
| `CheckOnly` | REG_DWORD or REG_SZ | Check-only mode |
| `NoPreflight` | REG_DWORD or REG_SZ | Skip preflight scripts |
| `NoPostflight` | REG_DWORD or REG_SZ | Skip postflight scripts |
| `SkipSelfService` | REG_DWORD or REG_SZ | Skip self-service manifest processing |
| `UseCache` | REG_DWORD or REG_SZ | Use the local download cache (default `true`) |
| `ForceChocolatey` | REG_DWORD or REG_SZ | Force Chocolatey provider |
| `PreferSbinInstaller` | REG_DWORD or REG_SZ | Prefer sbin-installer (default `true`) |
| `PkgRequireSignature` | REG_DWORD or REG_SZ | Require signature on .pkg packages |
| `AutoRemove` | REG_DWORD or REG_SZ | Auto-remove orphaned packages |
| `UseClientCertificate` | REG_DWORD or REG_SZ | Use SSL client certificate auth |
| `UseClientCertificateCNAsClientIdentifier` | REG_DWORD or REG_SZ | Use cert CN as `ClientIdentifier` |

### Integer Values
| Name | Reg type | Description | Default |
|---|---|---|---|
| `InstallerTimeout` | REG_DWORD or REG_SZ | Installer timeout in **seconds** | `900` |
| `CacheRetentionDays` | REG_DWORD or REG_SZ | Days to retain cached downloads | `30` |

### Array Values
| Name | Reg type | Description | Example |
|---|---|---|---|
| `Catalogs` | REG_MULTI_SZ | Available catalogs | `Production` |

> Fields that do not exist on `CimianConfig` (such as `CloudBucket`,
> `CloudProvider`, `DefaultArch`, `InstallPath`, `RepoPath`,
> `ForceBasicAuth`, `OpenImportedYaml`, `ForceExecutionPolicyBypass`,
> `LocalManifests`) have been removed from this list. PowerShell
> execution-policy bypass is applied unconditionally — see the link in the
> section below.

## Microsoft Intune Deployment

### Creating CSP OMA-URI Policies

1. **Sign in** to Microsoft Intune admin center
2. **Navigate** to Devices > Configuration profiles
3. **Create** new profile:
   - **Platform**: Windows 10 and later
   - **Profile type**: Templates > Custom
4. **Add OMA-URI Settings** for each configuration value

### Example OMA-URI Settings

#### Required: Software Repository URL
```
Name: Cimian Software Repository URL
Description: Primary software repository for Cimian
OMA-URI: ./Device/Vendor/MSFT/Policy/Config/Software/Cimian/Config/SoftwareRepoURL
Data type: String
Value: https://cimian.yourcompany.com
```

#### Optional: Default Catalog
```
Name: Cimian Default Catalog
Description: Default software catalog
OMA-URI: ./Device/Vendor/MSFT/Policy/Config/Software/Cimian/Config/DefaultCatalog
Data type: String
Value: production
```

#### Optional: Debug Mode
```
Name: Cimian Debug Mode
Description: Enable debug logging
OMA-URI: ./Device/Vendor/MSFT/Policy/Config/Software/Cimian/Config/Debug
Data type: Integer
Value: 1
```

#### Optional: Catalogs Array
```
Name: Cimian Available Catalogs
Description: List of available catalogs
OMA-URI: ./Device/Vendor/MSFT/Policy/Config/Software/Cimian/Config/Catalogs
Data type: String
Value: production,testing,development
```

## Group Policy Deployment

### Using Administrative Templates

1. **Create** or edit Group Policy Object (GPO)
2. **Navigate** to Computer Configuration > Policies > Administrative Templates
3. **Create custom ADMX** template or use registry preferences

### Registry Preferences Method

1. **Navigate** to Computer Configuration > Preferences > Windows Settings > Registry
2. **Create** new registry items for each configuration value
3. **Target** to `HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Cimian`

#### Example Registry Preferences

```
Key: HKEY_LOCAL_MACHINE\SOFTWARE\Cimian\Config
Value: SoftwareRepoURL
Type: REG_SZ
Data: https://cimian.yourcompany.com

Key: HKEY_LOCAL_MACHINE\SOFTWARE\Cimian\Config
Value: DefaultCatalog
Type: REG_SZ
Data: production

Key: HKEY_LOCAL_MACHINE\SOFTWARE\Cimian\Config
Value: Debug
Type: REG_DWORD
Data: 1
```

## PowerShell DSC Configuration

```powershell
Configuration CimianCSPConfiguration {
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    
    Registry CimianSoftwareRepoURL {
        Key       = "HKLM:\SOFTWARE\Cimian\Config"
        ValueName = "SoftwareRepoURL"
        ValueData = "https://cimian.yourcompany.com"
        ValueType = "String"
        Ensure    = "Present"
    }
    
    Registry CimianDefaultCatalog {
        Key       = "HKLM:\SOFTWARE\Cimian\Config"
        ValueName = "DefaultCatalog"
        ValueData = "production"
        ValueType = "String"
        Ensure    = "Present"
    }
    
    Registry CimianDebugMode {
        Key       = "HKLM:\SOFTWARE\Cimian\Config"
        ValueName = "Debug"
        ValueData = 1
        ValueType = "Dword"
        Ensure    = "Present"
    }
}
```

## Testing CSP Configuration

### Verify Registry Settings
```powershell
# Check if CSP settings exist
Get-ItemProperty -Path "HKLM:\SOFTWARE\Cimian\Config" -ErrorAction SilentlyContinue

# List all CSP values
Get-Item -Path "HKLM:\SOFTWARE\Cimian\Config" | Select-Object -ExpandProperty Property
```

### Inspect Effective Configuration
```cmd
# Print the effective CimianConfig fields managedsoftwareupdate is using
managedsoftwareupdate.exe --show-config -v
```

### Simulate Missing Config.yaml

Without `Config.yaml`, `ConfigurationService.LoadConfig` returns hard-coded
defaults (placeholder `SoftwareRepoURL`, machine name as
`ClientIdentifier`, `Production` catalog). Policy-applied registry values
are **not** read in the current implementation, so this is only a useful
test if you have a provisioning step that materializes `Config.yaml` from
the registry beforehand.

## Deployment Scenarios

### Scenario 1: New Device Provisioning
1. **Intune** applies CSP OMA-URI policies during device enrollment, writing
   the desired settings into `HKLM\SOFTWARE\Cimian\Config`.
2. **A provisioning script** (run before the Cimian MSI or as part of it)
   reads those registry values and writes `Config.yaml`.
3. **Cimian MSI** is deployed via Intune Win32 app.
4. **First `managedsoftwareupdate` run** loads `Config.yaml` normally.

### Scenario 2: Existing Device Migration
1. `Config.yaml` remains the configuration source read by Cimian.
2. CSP policies can be applied as a compliance/auditing signal — but on
   their own they do not change Cimian's runtime behavior today.
3. If `Config.yaml` is deleted, defaults take over until a provisioning
   step regenerates the file.

### Scenario 3: Zero-Touch Deployment
1. **Autopilot** enrolls the device and applies CSP policies.
2. A bootstrap step (CimianWatcher, Win32 app, or custom script)
   materializes `Config.yaml` from the policy-applied registry values.
3. Cimian is deployed during ESP and immediately uses the generated
   `Config.yaml`.

## Troubleshooting

### Common Issues

#### CSP Settings Not Applied
- **Check Intune policy assignment** and targeting
- **Verify device compliance** and enrollment status
- **Review device configuration** in Intune console
- **Use `rsop.msc`** to verify Group Policy application

#### Invalid Configuration Values
- **Check registry value types** (REG_SZ vs REG_DWORD)
- **Validate boolean values** (`1`/`0` or `"true"`/`"false"`)
- **Verify array formatting** (comma-separated or REG_MULTI_SZ)

#### Registry Permission Issues
- **Ensure SYSTEM account** can read HKLM\SOFTWARE\Policies
- **Check inheritance** on Policies registry key
- **Verify no explicit deny** permissions

### Debug Logging

Run with verbose output to inspect the loaded configuration:

```cmd
managedsoftwareupdate.exe --show-config -v
```

`--show-config` prints the effective `CimianConfig` field values. Cimian
itself does **not** currently log "CSP fallback" messages — if `Config.yaml`
is missing, defaults are used silently. To confirm policy-applied registry
values, inspect `HKLM\SOFTWARE\Cimian\Config` with `reg query` or
`Get-ItemProperty`.

## PowerShell Execution Policy Bypass

Cimian always invokes PowerShell with `-NoProfile -ExecutionPolicy Bypass` for
every script it runs (preinstall, postinstall, install-check, uninstall,
nopkg, etc.). The bypass is built into `ScriptService` and is **not**
controlled by a config or CSP setting today.

See [PowerShell Execution Policy Bypass](powershell-execution-policy-bypass.md) for details.

## Best Practices

### Security
- **Use least privilege** for CSP policy application
- **Protect sensitive values** (credentials should use secure methods)
- **Regular auditing** of applied policies
- **Test in isolated environment** before production deployment

### Reliability
- **Set essential values only** via CSP (SoftwareRepoURL minimum)
- **Allow Config.yaml override** for flexibility
- **Monitor policy application** success rates
- **Have rollback procedures** for policy changes

### Performance
- **Minimize registry reads** by using Config.yaml when possible
- **Cache configuration** appropriately
- **Avoid frequent policy changes** that require registry updates

## Migration Guide

### From Manual Config.yaml to CSP

1. **Document current** Config.yaml settings
2. **Create equivalent** CSP OMA-URI policies
3. **Test CSP settings** in pilot group
4. **Deploy CSP policies** organization-wide
5. **Optionally remove** Config.yaml files to test fallback
6. **Monitor logs** for successful CSP loading

### Hybrid Approach (Recommended)

1. **Keep Config.yaml** as primary configuration
2. **Set CSP policies** for essential settings only
3. **Use CSP as safety net** for missing/corrupted files
4. **Leverage both** for maximum reliability

This hybrid approach provides the best of both worlds: flexibility of YAML configuration with the reliability of enterprise policy management.
