# Managed Profiles and Apps - Microsoft Graph API Integration

This document describes the new `managed_profiles` and `managed_apps` array keys in Cimian pkgsinfo and manifest files for Microsoft Store app and configuration profile deployment via Graph API.

## Overview

Cimian now supports two new array keys that work alongside the existing `managed_installs`, `managed_uninstalls`, `managed_updates`, and `optional_installs` arrays:

- **`managed_profiles`**: Configuration profiles deployed via Microsoft Graph API
- **`managed_apps`**: Microsoft Store applications deployed via Microsoft Graph API

These arrays follow the same patterns as existing managed arrays and support:
- ✅ Manifest-level configuration
- ✅ Conditional item evaluation (NSPredicate-style)
- ✅ Deduplication logic to prevent conflicts
- ✅ Integration with Cimian's logging and reporting system

## Usage Examples

### Basic Manifest Configuration

```yaml
name: "corporate-baseline"
catalogs:
  - "Production"

managed_installs:
  - "Firefox"
  - "Chrome"

managed_profiles:
  - "CompanySecurityProfile"
  - "WiFiConfigProfile"  
  - "EmailSettingsProfile"

managed_apps:
  - "Microsoft Teams"
  - "Microsoft Office"
  - "Microsoft To Do"
  - "Microsoft Whiteboard"
```

### Conditional Deployment

```yaml
name: "department-specific"
catalogs:
  - "Production"

conditional_items:
  - condition:
      key: "hostname"
      operator: "LIKE"
      value: "DEV-*"
    managed_profiles:
      - "DeveloperProfile"
      - "VPNProfile"
    managed_apps:
      - "Microsoft Visual Studio"
      - "GitHub Desktop"

  - condition:
      key: "domain"
      operator: "=="
      value: "ACCOUNTING"
    managed_profiles:
      - "AccountingProfile"
    managed_apps:
      - "Microsoft Excel"
      - "Microsoft Power BI"
```

### Multiple Conditions

```yaml
conditional_items:
  - conditions:
      - key: "arch"
        operator: "=="
        value: "x64"
      - key: "os_version"
        operator: ">="
        value: "10.0.19041"
    condition_type: "AND"
    managed_apps:
      - "Microsoft Visual Studio Code"
      - "Microsoft PowerToys"
```

## Integration Points

### 1. Manifest Processing
The new arrays are processed during manifest loading and conditional evaluation, following the same deduplication logic as existing arrays.

### 2. Installation Pipeline
When items with `action: "profile"` or `action: "app"` are encountered, Cimian logs them for Graph API pipeline processing but does not attempt traditional installation.

### 3. Logging and Reporting
- Profile and app deployment actions are logged with `event_type: "profile"` and `event_type: "app"`
- Items appear in standard Cimian reports with `item_type: "managed_profiles"` and `item_type: "managed_apps"`
- Session tracking includes counts of profiles and apps scheduled for deployment

### 4. Status Reporting
```json
{
  "event_id": "evt_001_profile_deployment",
  "session_id": "cimian-1736689852-20250804-143052",
  "timestamp": "2025-08-04T14:30:53Z",
  "level": "INFO",
  "event_type": "profile",
  "package": "CompanySecurityProfile",
  "action": "deploy_profile",
  "status": "scheduled",
  "message": "Configuration profile scheduled for deployment via Graph API"
}
```

## Pipeline Integration

The Graph API pipeline will:

1. **Monitor Cimian Reports**: Read from `C:\ProgramData\ManagedInstalls\reports\events.json` for profile/app deployment events
2. **Process Deployment Requests**: Use Microsoft Graph API to deploy configuration profiles and install Store apps
3. **Update Status**: Report back deployment status to Cimian logging system
4. **Handle Conflicts**: Use Cimian's deduplication logic to prevent conflicting deployments

## Benefits

### Unified Management
- Single source of truth for all managed items (traditional apps, Store apps, and profiles)
- Consistent conditional evaluation across all deployment types
- Unified logging and reporting

### Operational Efficiency  
- Leverage existing Cimian infrastructure for new deployment types
- Maintain familiar manifest syntax and conditional logic
- Integrated monitoring and troubleshooting

### Flexibility
- Mix traditional and modern deployment methods in same manifest
- Apply consistent conditional logic across all item types
- Gradual migration path from traditional to cloud-native management

## Migration Notes

### From Existing Systems
If migrating from other profile/app management solutions:

1. **Profile Names**: Use descriptive, unique names for profiles (e.g., "WiFiConfigProfile" not just "WiFi")
2. **App Identification**: Use official Microsoft Store app names or identifiers
3. **Testing**: Start with Development catalogs and conditional deployment for testing

### Backwards Compatibility
- Existing manifests continue to work unchanged
- New arrays are optional and ignored by older Cimian versions
- No breaking changes to existing functionality

## Troubleshooting

### Common Issues

1. **Duplicate Items**: Use Cimian's built-in deduplication - same item in multiple arrays will only be processed once
2. **Conditional Logic**: Test conditional expressions in Development catalogs first
3. **Graph API Connectivity**: Ensure pipeline has proper Graph API permissions and connectivity

### Debugging

Check Cimian logs for profile/app scheduling:
```bash
# Check recent events
cat "C:\ProgramData\ManagedInstalls\reports\events.json" | findstr "profile\|app"

# Check session summary
cat "C:\ProgramData\ManagedInstalls\reports\sessions.json" | findstr "profiles\|apps"
```

## Example Workflows

### 1. New Employee Onboarding
```yaml
name: "new-employee-baseline"
managed_installs:
  - "Firefox"
  - "7-Zip"
managed_profiles:
  - "CompanySecurityProfile"
  - "EmailProfile"
  - "WiFiProfile"
managed_apps:
  - "Microsoft Teams"
  - "Microsoft Outlook"
  - "Microsoft OneDrive"
```

### 2. Department-Specific Configuration
```yaml
name: "department-configs"
conditional_items:
  - condition:
      key: "hostname"
      operator: "LIKE"
      value: "HR-*"
    managed_profiles:
      - "HRComplianceProfile"
    managed_apps:
      - "Microsoft Viva Insights"
  
  - condition:
      key: "hostname"
      operator: "LIKE"  
      value: "IT-*"
    managed_profiles:
      - "ITAdminProfile"
    managed_apps:
      - "Microsoft System Center"
      - "Microsoft Remote Desktop"
```

This integration provides a seamless path to modern endpoint management while maintaining the familiar Cimian workflow and infrastructure.
