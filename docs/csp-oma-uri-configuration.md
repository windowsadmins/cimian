# Cimian CSP OMA-URI Configuration Guide

## Overview

Cimian now supports **CSP OMA-URI registry settings as a fallback configuration mechanism**. This enhancement enables enterprise deployment scenarios where configuration files might be missing during initial deployment, but CSP policies have been applied via Intune, Group Policy, or other management tools.

## Configuration Priority

1. **Primary**: `C:\ProgramData\ManagedInstalls\Config.yaml` (YAML file)
2. **Fallback**: CSP OMA-URI registry settings

## How It Works

When `managedsoftwareupdate` starts:

1. **Attempts to load Config.yaml** from `C:\ProgramData\ManagedInstalls\Config.yaml`
2. **If Config.yaml doesn't exist**, automatically attempts to load configuration from CSP registry settings
3. **If CSP settings are found**, uses them to configure Cimian
4. **If neither exists**, reports configuration error and exits

## CSP Registry Path

Cimian reads CSP configuration from this registry location:

```
HKEY_LOCAL_MACHINE\SOFTWARE\Cimian\Config
```

## Supported Configuration Values

All major Cimian configuration options are supported via CSP registry settings:

### String Values
| Registry Value | Type | Description | Example |
|---|---|---|---|
| `SoftwareRepoURL` | REG_SZ | Primary software repository URL | `https://cimian.company.com` |
| `ClientIdentifier` | REG_SZ | Unique client identifier | `DESKTOP-ABC123` |
| `CloudBucket` | REG_SZ | Cloud storage bucket name | `cimian-packages` |
| `CloudProvider` | REG_SZ | Cloud provider type | `azure`, `aws`, `none` |
| `DefaultArch` | REG_SZ | Default architecture(s) | `x64,arm64` |
| `DefaultCatalog` | REG_SZ | Default catalog name | `production` |
| `InstallPath` | REG_SZ | Installation directory | `C:\Program Files\Cimian` |
| `LocalOnlyManifest` | REG_SZ | Path to local-only manifest | `C:\Local\manifest.yaml` |
| `LogLevel` | REG_SZ | Logging verbosity | `ERROR`, `WARN`, `INFO`, `DEBUG` |
| `RepoPath` | REG_SZ | Local repository path | `C:\ProgramData\ManagedInstalls\repo` |
| `CachePath` | REG_SZ | Cache directory path | `C:\ProgramData\ManagedInstalls\cache` |
| `CatalogsPath` | REG_SZ | Catalogs directory path | `C:\ProgramData\ManagedInstalls\catalogs` |

### Boolean Values  
| Registry Value | Type | Description | Example |
|---|---|---|---|
| `Debug` | REG_DWORD or REG_SZ | Enable debug logging | `1` or `"true"` |
| `Verbose` | REG_DWORD or REG_SZ | Enable verbose output | `1` or `"true"` |
| `CheckOnly` | REG_DWORD or REG_SZ | Check-only mode | `0` or `"false"` |
| `ForceBasicAuth` | REG_DWORD or REG_SZ | Force basic authentication | `1` or `"true"` |
| `NoPreflight` | REG_DWORD or REG_SZ | Skip preflight scripts | `0` or `"false"` |
| `OpenImportedYaml` | REG_DWORD or REG_SZ | Auto-open imported YAML | `1` or `"true"` |

### Array Values
| Registry Value | Type | Description | Example |
|---|---|---|---|
| `Catalogs` | REG_MULTI_SZ or REG_SZ | Available catalogs | Multi-string or `"production,testing"` |
| `LocalManifests` | REG_MULTI_SZ or REG_SZ | Local manifest paths | Multi-string or `"C:\Local\app1.yaml,C:\Local\app2.yaml"` |

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

### Test Configuration Loading
```cmd
# Run with verbose output to see CSP loading
managedsoftwareupdate.exe --show-config -v
```

### Simulate Missing Config.yaml
```powershell
# Temporarily rename config file to test CSP fallback
Rename-Item "C:\ProgramData\ManagedInstalls\Config.yaml" "C:\ProgramData\ManagedInstalls\Config.yaml.backup"

# Run Cimian - should fall back to CSP
managedsoftwareupdate.exe --show-config -v

# Restore config file
Rename-Item "C:\ProgramData\ManagedInstalls\Config.yaml.backup" "C:\ProgramData\ManagedInstalls\Config.yaml"
```

## Deployment Scenarios

### Scenario 1: New Device Provisioning
1. **Intune** applies CSP OMA-URI policies during device enrollment
2. **Cimian MSI** is deployed via Intune Win32 app
3. **First run** uses CSP settings since Config.yaml doesn't exist yet
4. **cimiimport** can optionally create Config.yaml from CSP settings

### Scenario 2: Existing Device Migration
1. **Existing Config.yaml** remains primary configuration source
2. **CSP policies** applied as backup/compliance measure
3. **If Config.yaml** is deleted or corrupted, CSP provides fallback
4. **Consistent behavior** across all managed devices

### Scenario 3: Zero-Touch Deployment
1. **Autopilot** enrolls device and applies CSP policies
2. **Cimian** deployed during ESP (Enrollment Status Page)
3. **Immediate functionality** without manual configuration
4. **Self-service portal** available immediately

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

Enable verbose logging to see CSP fallback in action:

```cmd
managedsoftwareupdate.exe -vv --show-config
```

Expected output when using CSP fallback:
```
2025/08/15 11:13:54 Configuration file does not exist: C:\ProgramData\ManagedInstalls\Config.yaml
2025/08/15 11:13:54 Attempting to load configuration from CSP OMA-URI registry settings...
2025/08/15 11:13:54 Loaded CSP configuration from primary registry path: SOFTWARE\Policies\Cimian
2025/08/15 11:13:54 CSP: Loaded SoftwareRepoURL = https://cimian.yourcompany.com
2025/08/15 11:13:54 CSP: Loaded DefaultCatalog = production
2025/08/15 11:13:54 CSP: Loaded Debug = true
2025/08/15 11:13:54 Successfully loaded configuration from CSP OMA-URI registry settings
```

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
