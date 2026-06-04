# Cimian Conditional Items & Custom Facts System

This document describes Cimian's conditional items feature, which provides NSPredicate-style conditional evaluation for dynamic software deployment based on system facts. The condition expression syntax supports complex boolean expressions using `AND`, `OR`, `NOT`, parentheses, and the `ANY` collection operator.

## Overview

Conditional items allow you to define software packages that are only installed, updated, or removed when specific system conditions are met. This enables deployment scenarios such as:

- Installing different software based on hostname patterns
- Architecture-specific deployments (x64, arm64, x86)
- Role-based software installation (lab machines, workstations, servers)
- Environment-specific tools (development, testing, production)
- Time-based deployments
- Hardware-specific software

## System Facts

Cimian automatically gathers the following system facts for conditional evaluation. The authoritative list and the fact-key mappings are in `shared/core/Models/SystemFacts.cs` (`GetFactValue`).

### Core Facts
- **hostname**: System hostname
- **arch**: System architecture (x64, arm64, x86) - *Primary architecture fact*
- **architecture**: System architecture (x64, arm64, x86) - *Alias of `arch` for backward compatibility*
- **os_version**: Windows OS version string (e.g., "10.0.22621")
- **os_vers_major**: Windows OS major version (e.g., 10, 11)
- **os_vers_minor**: Windows OS minor version
- **os_build_number**: Windows OS build number (integer)
- **domain**: Active Directory domain name (if domain-joined)
- **username**: Current logged-in username
- **machine_type**: Type of machine ("laptop", "desktop", "virtual", or "server")
- **machine_model**: Computer model (e.g., "Dell OptiPlex 7070")
- **model_version**: Friendly model name from `Win32_ComputerSystemProduct.Version` (e.g., "ThinkCentre M75q Gen 2"). May be empty on vendors that don't populate this field.
- **joined_type**: Domain join status ("domain", "hybrid", "entra", or "workgroup")
- **catalogs**: Catalog names this machine is assigned to (array of strings — use with the `ANY` operator)
- **battery_state**: Battery state ("connected", "disconnected", or "unknown")
- **date**: Current date in `YYYY-MM-DD` format

### Hardware Facts
- **gpu_names** / **gpu_name**: GPU names from `Win32_VideoController` (array — use with `ANY` / `CONTAINS`)
- **gpu_driver_version**: Driver version of the primary GPU
- **gpu_vram_gb**: VRAM of primary GPU in GB
- **cpu_name**: Cleaned processor name (e.g., "Core i9-13900K")
- **cpu_manufacturer**: CPU manufacturer (Intel, AMD, Qualcomm, ARM)
- **cpu_cores**: Physical core count
- **cpu_logical_cores**: Logical processor count (including hyperthreading)
- **npu_name**: NPU name if present (e.g., "Qualcomm Hexagon NPU")
- **npu_available**: Boolean — whether an NPU is detected
- **ram_total_gb**: Total RAM in GB rounded to common sizes (8, 16, 32, 64, 128)
- **ram_type**: RAM type (DDR3, DDR4, DDR5, LPDDR4, LPDDR5)
- **storage_type**: Primary drive type (NVMe, SSD, HDD)
- **storage_capacity_gb**: Primary drive capacity in GB

### MDM / Enrollment Facts
- **isenrolled**: Boolean — whether the system is enrolled in MDM (Intune)
- **isdomainjoined**: Boolean — whether the system is domain-joined

> **Note**: There is no `enrolled_usage`, `enrolled_area`, `device_id`, `build_number`, or `serial_number` fact in the current fact map. Use `os_build_number` for the build number. If you need a per-device custom fact, populate `SystemFacts.CustomFacts`, `EnvironmentVariables`, or `RegistryValues` — `GetFactValue` will look these up by name as a fallback.

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
- **DOES_NOT_CONTAIN**: String does not contain substring
- **BEGINSWITH**: String starts with value
- **ENDSWITH**: String ends with value

### Special Operators
- **ANY**: Checks if any element in an array fact matches (e.g., `ANY catalogs != "Testing"`). Supported sub-operators for `ANY` are `==`/`EQUALS`, `!=`/`NOT_EQUALS`, and `CONTAINS`.
- **NOT**: Negates the following condition (e.g., `NOT hostname CONTAINS "Kiosk"`)
- **OR**: Logical OR for combining sub-expressions
- **AND**: Logical AND for combining sub-expressions
- **Parentheses**: Use `(...)` to group sub-expressions explicitly

## Manifest Structure

Add conditional items to your manifest using the `conditional_items` array. Each conditional item has a single `condition` expression (a string) plus any combination of `managed_installs`, `managed_uninstalls`, `managed_updates`, and `optional_installs` arrays:

```yaml
name: "My Conditional Manifest"
catalogs:
  - Testing

# Standard items (always processed)
managed_installs:
  - BasePackage

# Conditional items
conditional_items:
  - condition: "hostname DOES_NOT_CONTAIN Camera"
    managed_installs:
      - LabSoftware
```

## Condition Format

The `condition` field is always a single string expression evaluated by the predicate engine (`shared/engine/Predicates/PredicateEngine.cs`). Complex boolean logic is expressed by combining sub-expressions with `AND`, `OR`, `NOT`, and parentheses inside that one string.

### Examples

```yaml
conditional_items:
  # Simple condition
  - condition: "hostname DOES_NOT_CONTAIN Camera"
    managed_installs:
      - StandardApps

  # OR expression in a single condition string
  - condition: hostname CONTAINS "Design-" OR hostname CONTAINS "Studio-" OR hostname CONTAINS "Edit-"
    managed_installs:
      - CreativeApplications

  # AND expression
  - condition: os_vers_major >= 11 AND arch == "x64"
    managed_installs:
      - ModernApplications

  # Mixed AND/OR with NOT and parentheses
  - condition: NOT hostname CONTAINS "Kiosk" AND (domain == "CORP" OR domain == "EDU")
    managed_installs:
      - EnterpriseApplications

  # ANY operator for an array fact (catalogs)
  - condition: ANY catalogs != "Testing"
    managed_installs:
      - ProductionSoftware

  # Architecture-specific
  - condition: "arch == x64"
    managed_installs:
      - x64OnlyApp

  # MDM enrollment check (boolean fact)
  - condition: isenrolled == true
    managed_installs:
      - MDMOnlyTools

  # Multiple operations under one condition
  - condition: domain == "CORPORATE" AND arch == "x64"
    managed_installs:
      - CorporateX64App
    managed_uninstalls:
      - LegacyCorporateApp
    optional_installs:
      - OptionalCorporateTools
```

### Not supported

The following forms appear in some older documentation and Munki examples but are **not** parsed by the current Cimian models (`ManifestFile.ConditionalItem` in `cli/managedsoftwareupdate/Models/UpdateModels.cs`):

- **Nested `conditional_items:` inside a conditional item** — the deployed manifest model has no nested property. Hierarchical conditions must be flattened into combined `AND` expressions on each item. (A separate `Manifest`/`ConditionalItem` model in `shared/core/Models/Manifest.cs` does have a nested field, but it is not the model used to parse deployed manifests.)
- **Dictionary-form `condition: {key:, operator:, value:}`** — `condition` is a string only.
- **Plural `conditions:` array with `condition_type: AND|OR`** — use a single expression with `AND` / `OR` operators instead.

If you previously had a nested structure, flatten it:

```yaml
# Was (NOT supported):
#   - condition: domain == "CORPORATE"
#     conditional_items:
#       - condition: hostname CONTAINS "DEV"
#         managed_installs: [DevTools]

# Use instead:
conditional_items:
  - condition: domain == "CORPORATE" AND hostname CONTAINS "DEV"
    managed_installs:
      - DevTools
```

## Common Use Cases

### 1. Hostname-Pattern Deployment

```yaml
# Install creative software on design machines using an OR expression
conditional_items:
  - condition: hostname CONTAINS "Ind-Design-" OR hostname CONTAINS "C3234-" OR hostname CONTAINS "IndLab-"
    managed_installs:
      - AdobeCreativeSuite
      - SketchSoftware
      - DesignTools

  # System-requirements expression
  - condition: os_vers_major >= 11 AND arch == "x64" AND NOT hostname CONTAINS "Legacy"
    managed_installs:
      - ModernApplications
      - NextGenTools
```

### 2. Architecture-Specific Software

```yaml
conditional_items:
  # Modern x64 systems get latest applications
  - condition: arch == "x64" AND os_vers_major >= 11
    managed_installs:
      - ModernApplication64
      - AdvancedGraphicsTools
    managed_uninstalls:
      - LegacyApplication32

  # Native ARM64 builds when the ARM64-Compatible catalog is assigned
  - condition: arch == "arm64" AND ANY catalogs == "ARM64-Compatible"
    managed_installs:
      - NativeARMApplications

  # Fallback for ARM64 machines without the ARM64-Compatible catalog
  - condition: arch == "arm64" AND NOT (ANY catalogs == "ARM64-Compatible")
    managed_installs:
      - EmulatedApplications
      - CompatibilityLayer
```

### 3. Domain-Specific Deployment

```yaml
# Corporate domain gets enterprise software
conditional_items:
  - condition: domain == "CORPORATE"
    managed_installs:
      - EnterpriseAntivirus
      - CorporateVPN
```

### 4. Combined Multi-Condition Logic

Express AND/OR within a single condition string — there is no plural `conditions:` field.

```yaml
conditional_items:
  - condition: domain == "ENTERPRISE" AND hostname BEGINSWITH "WS" AND arch == "x64"
    managed_installs:
      - EnterpriseWorkstationSuite
      - SecurityAgent
```

### 5. Hardware-Aware Deployment

```yaml
conditional_items:
  # Workstations with a discrete GPU and 32 GB+ RAM get heavyweight tools
  - condition: ANY gpu_names CONTAINS "NVIDIA" AND ram_total_gb >= 32
    managed_installs:
      - 3DModelingSuite
      - VideoEditingPro

  # NPU-enabled devices get on-device ML tools
  - condition: npu_available == true
    managed_installs:
      - LocalAIInference
```

## Best Practices

### 1. Use Specific Conditions
- Make conditions as specific as possible to avoid unintended deployments
- Test conditions thoroughly before deploying to production
- Use combined AND/OR expressions to reduce the number of conditional items

### 2. Expression Complexity Management
- Break very complex expressions into multiple simpler conditional items
- Use parentheses to group logical operations explicitly
- Comment complex conditions for future maintenance

### 3. Naming Conventions
- Use consistent hostname patterns to enable effective conditional deployment
- Consider standardized domain and organizational unit structures

### 4. Fallback Strategy
- Always include unconditional items for critical software
- Use `optional_installs` for software that might not apply to all systems
- Provide fallback conditions for systems that don't match primary criteria

### 5. Testing
- Test conditional manifests on representative systems
- Use checkonly mode to verify which items would be deployed:
  ```powershell
  sudo .\release\arm64\managedsoftwareupdate.exe -v --checkonly
  ```
- Test expressions in isolation before combining them

### 6. Logging and Monitoring
- Monitor logs to ensure conditional items are evaluating correctly
- Use verbose logging to troubleshoot condition evaluation

## Debugging Conditional Items

### 1. Check System Facts
You can verify what facts are available by checking the logs when Cimian processes conditional items. Look for "System facts gathered" entries.

### 2. Test Conditions
Use simple conditions first and build up complexity:

```yaml
# Start simple
conditional_items:
  - condition: "hostname != ''"
    managed_installs:
      - TestPackage  # Should install on any system with a hostname
      
# Test complex expressions incrementally
  - condition: hostname CONTAINS "LAB"
    managed_installs:
      - TestPackage1
      
  - condition: hostname CONTAINS "LAB" OR hostname CONTAINS "DEV"
    managed_installs:
      - TestPackage2
      
  - condition: (hostname CONTAINS "LAB" OR hostname CONTAINS "DEV") AND arch == "x64"
    managed_installs:
      - TestPackage3
```

### 3. Validate Expression Parsing
Test expressions by using simple managed_installs items to verify parsing:

```yaml
conditional_items:
  # Test parentheses parsing
  - condition: (domain == "CORP" OR domain == "EDU") AND NOT hostname CONTAINS "Kiosk"
    managed_installs:
      - ParenthesesTest

  # Test OR/AND precedence
  - condition: hostname CONTAINS "A" OR hostname CONTAINS "B" AND arch == "x64"
    managed_installs:
      - PrecedenceTest

  # Test special operators
  - condition: ANY catalogs != "Testing"
    managed_installs:
      - AnyOperatorTest
```

### 4. Log Analysis
Look for log messages such as:
- "Conditional item matched: <condition>"
- "Conditional item did not match: <condition>"
- "Error evaluating condition: <condition>"

### 5. Common Issues and Solutions

**Issue**: Expressions not evaluating correctly
**Solution**: Check operator precedence and use parentheses to group operations

**Issue**: Special operators (ANY, NOT) not working
**Solution**: Ensure proper syntax: `ANY catalogs != "value"` (the keyword comes before the fact name), and `NOT <expression>` (the keyword comes before the sub-expression)

**Issue**: Quoted strings causing problems
**Solution**: Use consistent quoting: either `"value"` or unquoted `value`, but be consistent

**Issue**: Fact appears empty or returns false unexpectedly
**Solution**: Confirm the fact key exists in `SystemFacts.GetFactValue`. Unknown keys fall through to `CustomFacts`, `EnvironmentVariables`, and `RegistryValues`; if not present in any of those, the fact is `null`.

## Advanced Features

### Custom Facts
`SystemFacts.GetFactValue` falls back to three dictionaries when a name isn't a known fact:

1. `CustomFacts` — populated programmatically by extensions/plugins
2. `EnvironmentVariables` — process environment
3. `RegistryValues` — registry values gathered during fact collection

You can therefore reference an environment variable or a gathered registry value by name in a `condition` string. Hooks for WMI/PowerShell-driven custom facts are not currently wired into the fact collector.

### Integration with Existing Workflow
Conditional items work alongside:
- Standard manifest items (`managed_installs`, `managed_uninstalls`, `managed_updates`, `optional_installs`, `included_manifests`)
- Catalog entries
- Dependencies and updates declared in catalog items
- OnDemand items
- Self-service manifests

## Migration from Static Manifests

To migrate existing static manifests to use conditional items:

1. **Identify patterns** in your current hostname/role structure
2. **Group similar systems** by common characteristics
3. **Create conditions** that match these groups
4. **Test incrementally** with small groups of systems
5. **Expand gradually** to cover all deployment scenarios

## Example: Organizational Deployment

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
  # Corporate base software
  - condition: domain == "CORPORATE"
    managed_installs:
      - CorporateVPN
      - EnterpriseAntivirus
      - ComplianceAgent
      - CorporatePortal

  # Executive workstations
  - condition: domain == "CORPORATE" AND (hostname CONTAINS "EXEC-" OR hostname CONTAINS "C-SUITE-" OR hostname CONTAINS "BOARD-")
    managed_installs:
      - ExecutiveSuite
      - PremiumOffice

  # Development machines on modern x64 systems
  - condition: domain == "CORPORATE" AND (hostname CONTAINS "DEV-" OR hostname CONTAINS "TEST-") AND arch == "x64" AND os_vers_major >= 11
    managed_installs:
      - ModernDevelopmentEnvironment
      - ContainerTools
      - CloudSDKs

  # Educational base software
  - condition: domain == "EDUCATION"
    managed_installs:
      - EducationalPortal
      - StudentManagementTools
      - SafeBrowsing

  # Modern x64 lab machines
  - condition: domain == "EDUCATION" AND (hostname CONTAINS "LAB-" OR hostname CONTAINS "COMPUTER-LAB") AND arch == "x64" AND os_vers_major >= 11
    managed_installs:
      - ModernLabSuite
      - VirtualizationTools

  # Engineering workstations with discrete GPUs
  - condition: hostname CONTAINS "ENG-" AND machine_type == "desktop" AND ANY gpu_names CONTAINS "NVIDIA"
    managed_installs:
      - CADSoftware
      - SimulationTools

  # Fallback for systems outside corporate / education domains
  - condition: NOT (domain == "CORPORATE" OR domain == "EDUCATION")
    managed_installs:
      - BasicProductivitySuite
      - StandardSecurity
      - RemoteSupport

optional_installs:
  - OptionalCreativeTools
  - DeveloperExtensions
  - SpecializedUtilities
```

This example demonstrates:
- Combined AND/OR expressions in a single `condition` string
- Architecture and OS-version awareness for modern vs. legacy systems
- Hardware-aware deployment via `gpu_names` with `ANY`
- A fallback `NOT (...)` condition for unmatched systems

This system provides the flexibility of Munki-style conditional items while staying within the Windows-focused fact model and expression syntax supported by Cimian.
