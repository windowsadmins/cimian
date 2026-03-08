# Cimian Conditional Items & Custom Facts System

This document describes Cimian's conditional items feature, which provides NSPredicate-style conditional evaluation for dynamic software deployment based on system facts. The enhanced system now supports complex expressions with OR/AND operators and nested conditional items for hierarchical logic.

## Overview

Conditional items allow you to define software packages that are only installed, updated, or removed when specific system conditions are met. This enables sophisticated deployment scenarios such as:

- Installing different software based on hostname patterns
- Architecture-specific deployments (x64, arm64, x86)
- Role-based software installation (lab machines, workstations, servers)
- Environment-specific tools (development, testing, production)
- Time-based deployments
- Hardware-specific software
- Complex multi-condition deployments with nested logic

## System Facts

Cimian automatically gathers the following system facts for conditional evaluation:

### Core Facts
- **hostname**: System hostname
- **arch**: System architecture (x64, arm64, x86) - *Primary architecture fact*
- **architecture**: System architecture (x64, arm64, x86) - *Kept for backward compatibility*
- **os_version**: Windows OS version string
- **os_vers_major**: Windows OS major version (e.g., 10, 11)
- **os_vers_minor**: Windows OS minor version
- **domain**: Windows domain name
- **username**: Current username
- **machine_type**: Type of machine ("laptop" or "desktop")
- **machine_model**: Computer model (e.g., "Dell OptiPlex 7070")
- **serial_number**: System serial number
- **joined_type**: Domain join status ("domain", "hybrid", "entra", or "workgroup")
- **catalogs**: Available catalogs from configuration (array of strings)
- **battery_state**: Battery state information
- **date**: Current date and time

### Enhanced Facts (New)
- **enrolled_usage**: Device enrollment usage type (from registration)
- **enrolled_area**: Device enrollment area (from registration) 
- **device_id**: Unique device identifier
- **build_number**: Windows build number

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

### Special Operators (Enhanced)
- **ANY**: Checks if any element in an array matches (e.g., `ANY catalogs != "Testing"`)
- **NOT**: Negates the following condition (e.g., `NOT hostname CONTAINS "Kiosk"`)
- **OR**: Logical OR operator for complex expressions
- **AND**: Logical AND operator for complex expressions

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
  - condition: "hostname DOES_NOT_CONTAIN Camera"
    managed_installs:
      - LabSoftware
```

## Condition Formats

Cimian supports multiple formats for conditions to provide maximum flexibility:

### Enhanced Simple String Format (Recommended)

The enhanced system now supports complex expressions with OR/AND operators within a single condition string:

```yaml
conditional_items:
  # Simple condition
  - condition: "hostname DOES_NOT_CONTAIN Camera"
    managed_installs:
      - StandardApps
      
  # Complex OR expression in single condition
  - condition: hostname CONTAINS "Design-" OR hostname CONTAINS "Studio-" OR hostname CONTAINS "Edit-"
    managed_installs:
      - CreativeApplications
      
  # Complex AND expression
  - condition: os_vers_major >= 11 AND arch == "x64"
    managed_installs:
      - ModernApplications
      
  # Mixed AND/OR with special operators and parentheses
  - condition: NOT hostname CONTAINS "Kiosk" AND (domain == "CORP" OR domain == "EDU")
    managed_installs:
      - EnterpriseApplications
      
  # ANY operator for array checking
  - condition: ANY catalogs != "Testing"
    managed_installs:
      - ProductionSoftware
      
  # Architecture-specific with quoted strings
  - condition: "arch == x64"
    managed_installs:
      - x64OnlyApp

  # Complex enrollment-based condition
  - condition: enrolled_area != "Classroom" OR enrolled_area != "Podium"
    managed_installs:
      - SharedAreaTools
```

### Nested Conditional Items (New Feature)

Create hierarchical conditional logic with nested conditional items for complex deployment scenarios:

```yaml
conditional_items:
  # Main condition with nested subconditions
  - condition: enrolled_usage == "Shared"
    conditional_items:
      # Nested conditions within the main condition
      - condition: enrolled_area != "Classroom" OR enrolled_area != "Podium"
        managed_installs:
          - CollaborativeTools
          - GroupSoftware
      
      # Architecture-specific nested deployment
      - condition: machine_type == "desktop"
        conditional_items:
          # Further nesting for granular control
          - condition: os_vers_major >= 11 AND arch == "x64"
            managed_installs:
              - HighEndApplications
              - PerformanceTools
        
        managed_installs:
          - BasicDesktopTools
    
    # Items for all machines matching the main condition
    managed_installs:
      - SharedMachineConfiguration
      - SecurityHardening

  # Another top-level conditional item
  - condition: domain == "CORPORATE"
    conditional_items:
      # Nested conditions for corporate environment
      - condition: hostname CONTAINS "DEV-" OR hostname CONTAINS "TEST-"
        managed_installs:
          - DevelopmentTools
          - TestingFrameworks
      
      - condition: hostname CONTAINS "PROD-"
        managed_installs:
          - ProductionMonitoring
          - ComplianceTools
    
    # Base corporate tools for all corporate machines
    managed_installs:
      - CorporateVPN
      - EnterpriseAntivirus
```

### 5. Legacy Verbose Format (Deprecated)

The verbose format is maintained for backward compatibility but is deprecated in favor of the enhanced simple string format:

```yaml
conditional_items:
  - condition:
      key: "hostname"
      operator: "DOES_NOT_CONTAIN"
      value: "Camera"
    managed_installs:
      - StandardApps
```

**Note**: The enhanced simple string format is preferred for new manifests as it supports complex expressions and is more readable.

### 6. Multiple Conditions (Legacy - Use Complex Expressions Instead)

```yaml
conditional_items:
  # Legacy AND logic (prefer complex expressions)
  - conditions:
      - "domain == CORPORATE"
      - "arch == x64"
    condition_type: "AND"  # Default is AND
    managed_installs:
      - CorporateX64App
      
  # Legacy OR logic (prefer complex expressions)
  - conditions:
      - "hostname CONTAINS DEV"
      - "hostname CONTAINS TEST"
    condition_type: "OR"
    managed_installs:
      - DeveloperTools
```

**Recommendation**: Use complex expressions instead:
```yaml
conditional_items:
  # Preferred: Complex expression format
  - condition: domain == "CORPORATE" AND arch == "x64"
    managed_installs:
      - CorporateX64App
      
  - condition: hostname CONTAINS "DEV" OR hostname CONTAINS "TEST"
    managed_installs:
      - DeveloperTools
```

## Common Use Cases

### 1. Complex Expression-Based Deployment (Enhanced)

```yaml
# Install creative software on design machines using complex OR expression
conditional_items:
  - condition: hostname CONTAINS "Ind-Design-" OR hostname CONTAINS "C3234-" OR hostname CONTAINS "IndLab-"
    managed_installs:
      - AdobeCreativeSuite
      - SketchSoftware
      - DesignTools

# Mixed AND/OR conditions with enrollment data
  - condition: enrolled_usage == "Shared" AND (enrolled_area != "Classroom" OR enrolled_area != "Podium")
    managed_installs:
      - CollaborativeTools
      - SharedWorkspaceApps

# Complex system requirements
  - condition: os_vers_major >= 11 AND arch == "x64" AND NOT hostname CONTAINS "Legacy"
    managed_installs:
      - ModernApplications
      - NextGenTools
```

### 2. Nested Conditional Hierarchies (New)

```yaml
# Hierarchical deployment based on organization structure
conditional_items:
  # Top-level: Corporate environment
  - condition: domain == "CORPORATE"
    conditional_items:
      # Department-specific software
      - condition: hostname BEGINSWITH "HR-"
        managed_installs:
          - HRManagementSuite
          - PayrollSoftware
      
      - condition: hostname BEGINSWITH "IT-"
        conditional_items:
          # Further subdivision by role
          - condition: hostname CONTAINS "ADMIN"
            managed_installs:
              - NetworkManagementTools
              - ServerAdminSuite
          
          - condition: hostname CONTAINS "DEV"
            managed_installs:
              - DevelopmentEnvironment
              - CodeRepositoryTools
        
        # Base IT tools for all IT machines
        managed_installs:
          - RemoteAccessTools
          - TechnicalUtilities
    
    # Base corporate software for all corporate machines
    managed_installs:
      - CorporateVPN
      - ComplianceAgent
      - SecuritySuite
```

### 3. Architecture-Specific Software (Enhanced)

```yaml
# Enhanced architecture-specific deployment with complex conditions
conditional_items:
  # Modern x64 systems get latest applications
  - condition: arch == "x64" AND os_vers_major >= 11
    managed_installs:
      - ModernApplication64
      - AdvancedGraphicsTools
    managed_uninstalls:
      - LegacyApplication32
      
  # ARM64 support with fallback logic
  - condition: arch == "arm64"
    conditional_items:
      - condition: ANY catalogs CONTAINS "ARM64-Compatible"
        managed_installs:
          - NativeARMApplications
      
      # Fallback for ARM64 without native apps
      - condition: NOT ANY catalogs CONTAINS "ARM64-Compatible"
        managed_installs:
          - EmulatedApplications
          - CompatibilityLayer
```

### 4. Enrollment-Based Deployment (New)

```yaml
# Deployment based on device enrollment information
conditional_items:
  # Shared usage devices
  - condition: enrolled_usage == "Shared"
    conditional_items:
      # Exclude certain areas from getting full shared software suite
      - condition: enrolled_area != "Classroom" AND enrolled_area != "Podium"
        managed_installs:
          - AdvancedCollaborationTools
          - ContentCreationSuite
      
      # Classroom-specific software
      - condition: enrolled_area == "Classroom"
        managed_installs:
          - EducationalSoftware
          - StudentManagementTools
    
    # Base shared machine configuration
    managed_installs:
      - KioskMode
      - SessionCleanup
      - SharedMachinePolicy

  # Personal devices get different software
  - condition: enrolled_usage == "Personal"
    managed_installs:
      - PersonalProductivitySuite
      - UserCustomizationTools
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
      - key: "arch"
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
- Use complex expressions to reduce the number of conditional items

### 2. Leverage Nested Conditionals
- Use nested conditional items for hierarchical organizational structures
- Combine top-level organizational conditions with specific deployment logic
- Avoid deeply nested structures (limit to 3-4 levels for readability)

### 3. Expression Complexity Management
- Break very complex expressions into multiple simpler conditional items
- Use parentheses to clearly group logical operations
- Comment complex conditions for future maintenance

### 4. Naming Conventions
- Use consistent hostname patterns to enable effective conditional deployment
- Consider standardized domain and organizational unit structures
- Document naming conventions for enrollment data fields

### 5. Performance Considerations
- Place most restrictive conditions first to reduce evaluation time
- Use nested conditionals to avoid redundant fact gathering
- Consider the order of OR operations (most likely matches first)

### 6. Fallback Strategy
- Always include unconditional items for critical software
- Use optional_installs for software that might not apply to all systems
- Provide fallback conditions for systems that don't match primary criteria

### 7. Testing
- Test conditional manifests on representative systems
- Use checkonly mode to verify which items would be deployed:
  ```powershell
  managedsoftwareupdate.exe --checkonly --verbose
  ```
- Test complex expressions in isolation before combining them

### 8. Logging and Monitoring
- Monitor logs to ensure conditional items are evaluating correctly
- Use debug logging to troubleshoot condition evaluation
- Track deployment success rates by conditional criteria

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
Test complex expressions by using simple managed_installs items to verify parsing:

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

### 4. Nested Conditional Debugging
Debug nested conditionals by temporarily flattening them:

```yaml
# Original nested structure
conditional_items:
  - condition: domain == "CORPORATE"
    conditional_items:
      - condition: hostname CONTAINS "DEV"
        managed_installs:
          - DevTools

# Flattened for debugging
conditional_items:
  - condition: domain == "CORPORATE" AND hostname CONTAINS "DEV"
    managed_installs:
      - DevTools
```

### 5. Log Analysis
Look for these log messages:
- "Processing conditional items"
- "Conditional item matched, including items"
- "Conditional item did not match, skipping"
- "Error evaluating conditional items"
- "Complex condition parsed successfully"
- "Nested conditional evaluation started"

### 6. Common Issues and Solutions

**Issue**: Complex expressions not evaluating correctly
**Solution**: Check operator precedence and use parentheses to group operations

**Issue**: Nested conditionals not working as expected
**Solution**: Verify that parent conditions are met before expecting child conditions to evaluate

**Issue**: Special operators (ANY, NOT) not working
**Solution**: Ensure proper syntax: `ANY catalogs != "value"` not `catalogs ANY != "value"`

**Issue**: Quoted strings causing problems
**Solution**: Use consistent quoting: either `"value"` or unquoted `value`, but be consistent

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

## Example: Complete Organizational Deployment (Enhanced)

```yaml
name: "Organization-Wide Deployment with Enhanced Conditionals"
catalogs:
  - Production

# Base software for all systems
managed_installs:
  - BaseSecurityAgent
  - WindowsUpdates
  - ComplianceTools

conditional_items:
  # Corporate environment with nested departmental logic
  - condition: domain == "CORPORATE"
    conditional_items:
      # Executive workstations with complex hostname patterns
      - condition: hostname CONTAINS "EXEC-" OR hostname CONTAINS "C-SUITE-" OR hostname CONTAINS "BOARD-"
        managed_installs:
          - ExecutiveSuite
          - PremiumOffice
          - VIPSupport
          - ExecutiveReporting
          
      # Development environment with nested role-based deployment
      - condition: hostname CONTAINS "DEV-" OR hostname CONTAINS "TEST-"
        conditional_items:
          # Senior developers get additional tools
          - condition: hostname CONTAINS "SENIOR" OR hostname CONTAINS "LEAD"
            managed_installs:
              - AdvancedDevelopmentSuite
              - ArchitectureTools
              - LeadershipDashboard
          
          # All developers get base tools
          - condition: arch == "x64" AND os_vers_major >= 11
            managed_installs:
              - ModernDevelopmentEnvironment
              - ContainerTools
              - CloudSDKs
        
        # Base development tools for all dev machines
        managed_installs:
          - VisualStudio
          - GitTools
          - DeveloperUtilities
      
      # Creative department with enrollment-based deployment
      - condition: enrolled_area == "Creative" OR hostname CONTAINS "DESIGN-"
        conditional_items:
          # High-end workstations for 3D work
          - condition: machine_type == "desktop" AND NOT hostname CONTAINS "LAPTOP"
            managed_installs:
              - 3DModelingSuite
              - VideoEditingPro
              - RenderFarm
          
          # All creative machines get Adobe suite
          - condition: ANY catalogs CONTAINS "Creative"
            managed_installs:
              - AdobeCreativeSuite
              - SketchSoftware
              - ColorCalibration
    
    # Base corporate software for all corporate machines
    managed_installs:
      - CorporateVPN
      - EnterpriseAntivirus
      - ComplianceAgent
      - CorporatePortal

  # Educational environment with shared device logic
  - condition: domain == "EDUCATION" OR enrolled_usage == "Shared"
    conditional_items:
      # Classroom exclusions with complex OR expression
      - condition: enrolled_area != "Classroom" AND enrolled_area != "Podium" AND enrolled_area != "Testing"
        managed_installs:
          - AdvancedEducationalSuite
          - ResearchTools
          - CollaborativeWorkspace
      
      # Lab computers with architecture-specific deployment
      - condition: hostname CONTAINS "LAB-" OR hostname CONTAINS "COMPUTER-LAB"
        conditional_items:
          # Modern x64 labs get full software suite
          - condition: arch == "x64" AND os_vers_major >= 11
            managed_installs:
              - ModernLabSuite
              - VirtualizationTools
              - AdvancedSimulation
          
          # Legacy systems get basic lab tools
          - condition: arch != "x64" OR os_vers_major < 11
            managed_installs:
              - BasicLabSoftware
              - LegacyCompatibilityTools
        
        # Base lab management for all lab machines
        managed_installs:
          - LabManagementAgent
          - SessionCleanup
          - UsageTracking
    
    # Base educational software
    managed_installs:
      - EducationalPortal
      - StudentManagementTools
      - SafeBrowsing

  # Special deployment for high-performance systems
  - condition: (machine_type == "desktop" AND arch == "x64") OR hostname CONTAINS "WORKSTATION"
    conditional_items:
      # Engineering workstations
      - condition: hostname CONTAINS "ENG-" OR enrolled_area == "Engineering"
        managed_installs:
          - CADSoftware
          - SimulationTools
          - EngineeringAnalysis
      
      # Media production workstations
      - condition: hostname CONTAINS "MEDIA-" OR enrolled_area == "MediaProduction"
        managed_installs:
          - VideoEditingSuite
          - AudioProduction
          - MediaAssetManagement
    
    # High-performance base tools
    managed_installs:
      - PerformanceMonitoring
      - AdvancedDrivers
      - WorkstationOptimization

  # Fallback for unmatched systems
  - condition: NOT (domain == "CORPORATE" OR domain == "EDUCATION" OR enrolled_usage == "Shared")
    managed_installs:
      - BasicProductivitySuite
      - StandardSecurity
      - RemoteSupport
      
optional_installs:
  # Make certain applications available for self-service
  - OptionalCreativeTools
  - DeveloperExtensions
  - SpecializedUtilities
```

This enhanced example demonstrates:
- **Complex OR expressions** in single condition strings
- **Nested conditional hierarchies** for organizational structure
- **Mixed deployment strategies** using enrollment data and hostname patterns
- **Architecture and OS version awareness** for modern vs. legacy systems
- **Fallback conditions** for unmatched systems
- **Integration with enrollment data** for role-based deployment

This system provides the flexibility and power of Munki's conditional items while maintaining Cimian's Windows-focused approach and integration with existing deployment tools.
