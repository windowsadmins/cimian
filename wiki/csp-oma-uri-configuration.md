# Cimian CSP OMA-URI Configuration Guide

> **Status note (verified 2026-09-17):** `ConfigurationService` loads
> `C:\ProgramData\ManagedInstalls\Config.yaml`, then applies overrides from
> `HKLM\SOFTWARE\Policies\Cimian` (ADMX / Policy CSP). Policy wins over YAML
> for the keys listed below. Values not present in the Policies hive leave
> the YAML (or default) value unchanged. Missing `Config.yaml` still falls
> back to built-in defaults, then the same policy overlay.

## Overview

Cimian's primary configuration source is `Config.yaml`. Fleet-wide settings
can also ship via Intune/GPO Policy CSP writing to
`HKLM\SOFTWARE\Policies\Cimian`. For SSL client certificates, the usual
split is:

1. An MDM **certificate-install** CSP places the leaf in `LocalMachine\My`
2. Cimian policy sets `UseClientCertificate` + `ClientCertificateThumbprint`

File-based paths (`ClientCertificatePath` / `ClientKeyPath`) remain
supported when you prefer a PFX/PEM on disk.

## Configuration Priority (as implemented today)

1. **Base config**: `C:\ProgramData\ManagedInstalls\Config.yaml` (or built-in
   defaults when the file is missing)
2. **Policy overlay** (wins when the value is present):
   `HKLM\SOFTWARE\Policies\Cimian`

## Policy keys read by managedsoftwareupdate

| Name | Notes |
|---|---|
| `SoftwareRepoURL` | REG_SZ |
| `ClientIdentifier` | REG_SZ |
| `InstallerTimeout` | REG_DWORD or numeric string; applied when ≥ 60 |
| `CacheRetentionDays` | REG_DWORD or numeric string; applied when ≥ 0 |
| `UseClientCertificate` | REG_DWORD 0/1 or string `true`/`false`/`0`/`1` |
| `UseClientCertificateCNAsClientIdentifier` | same boolean encodings |
| `ClientCertificateThumbprint` | REG_SZ; trimmed |
| `ClientCertificatePath` | REG_SZ |
| `ClientKeyPath` | REG_SZ |
| `ClientCertificatePassword` | REG_SZ (empty string clears) |
| `SoftwareRepoCACertificate` | REG_SZ |

## CSP Registry Path

Cimian reads policy overrides from:

```
HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Cimian
```

> Older drafts mentioned `HKLM\SOFTWARE\Cimian\Config`. That path is **not**
> read for config overrides. Use the Policies hive above.

## Supported Configuration Values

The names below mirror the fields on the `CimianConfig` model
(`cli/managedsoftwareupdate/Models/UpdateModels.cs`) — i.e. the keys that
appear in `Config.yaml`. Prefer writing them under
`HKLM\SOFTWARE\Policies\Cimian` so `managedsoftwareupdate` applies them
directly. A provisioning script that materializes `Config.yaml` from
registry values can use the same names.

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

#### Optional: Client certificate thumbprint (mTLS)
```
Name: Cimian Client Certificate Thumbprint
Description: SHA-1 thumbprint of the leaf in LocalMachine\My used for repo mTLS
OMA-URI: ./Device/Vendor/MSFT/Policy/Config/Software/Cimian/Config/ClientCertificateThumbprint
Data type: String
Value: A9D2AC8463746C2EC10CF2318B351ACBE9913E85
```

#### Optional: Enable client certificate auth
```
Name: Cimian Use Client Certificate
Description: Enable SSL client certificate authentication for SoftwareRepoURL
OMA-URI: ./Device/Vendor/MSFT/Policy/Config/Software/Cimian/Config/UseClientCertificate
Data type: Integer
Value: 1
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
Key: HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Cimian
Value: SoftwareRepoURL
Type: REG_SZ
Data: https://cimian.yourcompany.com

Key: HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Cimian
Value: UseClientCertificate
Type: REG_DWORD
Data: 1

Key: HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Cimian
Value: ClientCertificateThumbprint
Type: REG_SZ
Data: A9D2AC8463746C2EC10CF2318B351ACBE9913E85

Key: HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Cimian
Value: InstallerTimeout
Type: REG_DWORD
Data: 900
```

## PowerShell DSC Configuration

```powershell
Configuration CimianCSPConfiguration {
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    
    Registry CimianSoftwareRepoURL {
        Key       = "HKLM:\SOFTWARE\Policies\Cimian"
        ValueName = "SoftwareRepoURL"
        ValueData = "https://cimian.yourcompany.com"
        ValueType = "String"
        Ensure    = "Present"
    }
    
    Registry CimianUseClientCertificate {
        Key       = "HKLM:\SOFTWARE\Policies\Cimian"
        ValueName = "UseClientCertificate"
        ValueData = 1
        ValueType = "Dword"
        Ensure    = "Present"
    }
    
    Registry CimianClientCertificateThumbprint {
        Key       = "HKLM:\SOFTWARE\Policies\Cimian"
        ValueName = "ClientCertificateThumbprint"
        ValueData = "A9D2AC8463746C2EC10CF2318B351ACBE9913E85"
        ValueType = "String"
        Ensure    = "Present"
    }
}
```

## Testing CSP Configuration

### Verify Registry Settings
```powershell
# Check if policy settings exist
Get-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Cimian" -ErrorAction SilentlyContinue

# List all policy values
Get-Item -Path "HKLM:\SOFTWARE\Policies\Cimian" | Select-Object -ExpandProperty Property
```

### Inspect Effective Configuration
```cmd
# Print the effective CimianConfig fields managedsoftwareupdate is using
managedsoftwareupdate.exe --show-config -v
```

### Simulate Missing Config.yaml

Without `Config.yaml`, `ConfigurationService.LoadConfig` returns hard-coded
defaults (placeholder `SoftwareRepoURL`, machine name as
`ClientIdentifier`, `Production` catalog), then applies any values present
under `HKLM\SOFTWARE\Policies\Cimian`. Policy-only mTLS setup therefore
works even when the YAML file is absent, as long as the Policies hive is
populated.

## Deployment Scenarios

### Scenario 1: New Device Provisioning
1. **Intune** applies Policy CSP values under `HKLM\SOFTWARE\Policies\Cimian`
   (repo URL, mTLS thumbprint, etc.) and a separate certificate-install
   profile that places the client leaf in `LocalMachine\My`.
2. **Cimian MSI** is deployed via Intune Win32 app (or Fleet).
3. **First `managedsoftwareupdate` run** loads `Config.yaml` if present,
   then applies policy overrides — including client-certificate settings.

### Scenario 2: Existing Device Migration
1. `Config.yaml` remains a valid configuration source.
2. CSP/policy values for the keys listed above override YAML when present.
3. If `Config.yaml` is deleted, defaults take over, then policy overlay.

### Scenario 3: Zero-Touch Deployment
1. **Autopilot** enrolls the device and applies Policy CSP + cert install.
2. Cimian is deployed during ESP and uses policy overrides immediately
   (YAML optional for host-local settings).

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

`--show-config` prints the effective `CimianConfig` field values after
YAML load and policy overlay. To confirm policy-applied registry values,
inspect `HKLM\SOFTWARE\Policies\Cimian` with `reg query` or
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
