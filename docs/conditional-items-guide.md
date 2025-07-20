# Cimian Conditional Items & Custom Facts System

This document describes Cimian's conditional items feature, which provides NSPredicate-style conditional evaluation for dynamic software deployment based on system facts.

## Overview

Conditional items allow you to define software packages that are only installed, updated, or removed when specific system conditions are met. This enables sophisticated deployment scenarios such as:

- Installing different software based on hostname patterns
- Architecture-specific deployments (x64, arm64, x86)
- Role-based software installation (lab machines, workstations, servers)
- Environment-specific tools (development, testing, production)
- Time-based deployments
- Hardware-specific software

## System Facts

Cimian automatically gathers the following system facts for conditional evaluation:

### Core Facts
- **hostname**: System hostname
- **architecture**: System architecture (x64, arm64, x86)
- **os_version**: Windows OS version
- **domain**: Windows domain name
- **username**: Current username

### Available Operators

- **==** or **EQUALS**: Exact equality
- **!=** or **NOT_EQUALS**: Not equal
- **>** or **GREATER_THAN**: Greater than (string comparison)
- **<** or **LESS_THAN**: Less than (string comparison)
- **>=** or **GREATER_THAN_OR_EQUAL**: Greater than or equal
- **<=** or **LESS_THAN_OR_EQUAL**: Less than or equal
- **LIKE**: Wildcard pattern matching (simplified)
- **IN**: Value is in a list of options
- **CONTAINS**: String contains substring
- **BEGINSWITH**: String starts with value
- **ENDSWITH**: String ends with value

## Manifest Structure

Add conditional items to your manifest using the `conditional_items` array:

```yaml
name: "My Conditional Manifest"
catalogs:
  - Testing

# Standard items (always processed)
managed_installs:
  - BasePackage

# Conditional items
conditional_items:
  - condition:
      key: "hostname"
      operator: "CONTAINS"
      value: "LAB"
    managed_installs:
      - LabSoftware
```

## Condition Formats

### Single Condition

```yaml
conditional_items:
  - condition:
      key: "architecture"
      operator: "=="
      value: "x64"
    managed_installs:
      - x64OnlyApp
```

### Multiple Conditions (AND Logic)

```yaml
conditional_items:
  - conditions:
      - key: "domain"
        operator: "=="
        value: "CORPORATE"
      - key: "architecture"
        operator: "=="
        value: "x64"
    condition_type: "AND"  # Default is AND
    managed_installs:
      - CorporateX64App
```

### Multiple Conditions (OR Logic)

```yaml
conditional_items:
  - conditions:
      - key: "hostname"
        operator: "CONTAINS"
        value: "DEV"
      - key: "hostname"
        operator: "CONTAINS"
        value: "TEST"
    condition_type: "OR"
    managed_installs:
      - DeveloperTools
```

## Common Use Cases

### 1. Hostname-Based Deployment

```yaml
# Install lab software on machines with "LAB" in hostname
conditional_items:
  - condition:
      key: "hostname"
      operator: "CONTAINS"
      value: "LAB"
    managed_installs:
      - LabManagementSoftware
      - StudentTools
```

### 2. Architecture-Specific Software

```yaml
# Install x64-specific software
conditional_items:
  - condition:
      key: "architecture"
      operator: "=="
      value: "x64"
    managed_installs:
      - x64Application
    managed_uninstalls:
      - LegacyX86App
```

### 3. Role-Based Installation

```yaml
# Different software for different machine roles
conditional_items:
  # Workstations (hostname starts with WS)
  - condition:
      key: "hostname"
      operator: "BEGINSWITH"
      value: "WS"
    managed_installs:
      - OfficeTools
      - BusinessApps
      
  # Servers (hostname starts with SRV)
  - condition:
      key: "hostname"
      operator: "BEGINSWITH"
      value: "SRV"
    managed_installs:
      - ServerManagementTools
      - MonitoringAgent
```

### 4. Domain-Specific Deployment

```yaml
# Corporate domain gets enterprise software
conditional_items:
  - condition:
      key: "domain"
      operator: "=="
      value: "CORPORATE"
    managed_installs:
      - EnterpriseAntivirus
      - CorporateVPN
```

### 5. Multi-Value Conditions

```yaml
# Install on specific machines
conditional_items:
  - condition:
      key: "hostname"
      operator: "IN"
      value: ["PC-LAB-01", "PC-LAB-02", "PC-LAB-03"]
    managed_installs:
      - SpecializedSoftware
```

### 6. Complex Multi-Condition Logic

```yaml
# Enterprise workstations only
conditional_items:
  - conditions:
      - key: "domain"
        operator: "=="
        value: "ENTERPRISE"
      - key: "hostname"
        operator: "BEGINSWITH"
        value: "WS"
      - key: "architecture"
        operator: "=="
        value: "x64"
    condition_type: "AND"
    managed_installs:
      - EnterpriseWorkstationSuite
      - SecurityAgent
```

## Best Practices

### 1. Use Specific Conditions
- Make conditions as specific as possible to avoid unintended deployments
- Test conditions thoroughly before deploying to production

### 2. Naming Conventions
- Use consistent hostname patterns to enable effective conditional deployment
- Consider standardized domain and organizational unit structures

### 3. Fallback Strategy
- Always include unconditional items for critical software
- Use optional_installs for software that might not apply to all systems

### 4. Testing
- Test conditional manifests on representative systems
- Use checkonly mode to verify which items would be deployed:
  ```powershell
  managedsoftwareupdate.exe --checkonly --verbose
  ```

### 5. Logging
- Monitor logs to ensure conditional items are evaluating correctly
- Use debug logging to troubleshoot condition evaluation

## Debugging Conditional Items

### 1. Check System Facts
You can verify what facts are available by checking the logs when Cimian processes conditional items.

### 2. Test Conditions
Use simple conditions first and build up complexity:

```yaml
# Start simple
conditional_items:
  - condition:
      key: "hostname"
      operator: "!="
      value: ""
    managed_installs:
      - TestPackage  # Should install on any system with a hostname
```

### 3. Log Analysis
Look for these log messages:
- "Processing conditional items"
- "Conditional item matched, including items"
- "Conditional item did not match, skipping"
- "Error evaluating conditional items"

## Advanced Features

### Custom Facts (Future Enhancement)
The system is designed to support custom facts from:
- Registry values
- WMI queries
- PowerShell scripts
- External data sources

### Integration with Existing Workflow
Conditional items work seamlessly with:
- Standard manifest items
- Catalog entries
- Dependencies
- OnDemand items
- Self-service manifests

## Migration from Static Manifests

To migrate existing static manifests to use conditional items:

1. **Identify patterns** in your current hostname/role structure
2. **Group similar systems** by common characteristics
3. **Create conditions** that match these groups
4. **Test incrementally** with small groups of systems
5. **Expand gradually** to cover all deployment scenarios

## Example: Complete Organizational Deployment

```yaml
name: "Organization-Wide Deployment"
catalogs:
  - Production

# Base software for all systems
managed_installs:
  - BaseSecurityAgent
  - WindowsUpdates
  - ComplianceTools

conditional_items:
  # Executive workstations
  - conditions:
      - key: "hostname"
        operator: "BEGINSWITH"
        value: "EXEC"
      - key: "domain"
        operator: "=="
        value: "CORPORATE"
    condition_type: "AND"
    managed_installs:
      - ExecutiveSuite
      - PremiumOffice
      - VIPSupport
      
  # Developer machines
  - condition:
      key: "hostname"
      operator: "CONTAINS"
      value: "DEV"
    managed_installs:
      - VisualStudio
      - GitTools
      - DeveloperUtilities
    optional_installs:
      - AdvancedDebugger
      
  # Lab computers
  - condition:
      key: "hostname"
      operator: "CONTAINS"
      value: "LAB"
    managed_installs:
      - LabSoftware
      - StudentTools
      - EducationalApps
      
  # x64 systems get modern applications
  - condition:
      key: "architecture"
      operator: "=="
      value: "x64"
    managed_installs:
      - ModernApp64
    managed_uninstalls:
      - LegacyApp32
```

This system provides the flexibility and power of Munki's conditional items while maintaining Cimian's Windows-focused approach and integration with existing deployment tools.
