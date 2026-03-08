# Cimian Go-to-C# Migration Testing Strategy

## Executive Summary

This document outlines the comprehensive testing strategy for validating the C# migration against the Go implementation. The goal is to ensure **100% behavioral parity** between both codebases before retirement of the Go implementation.

> **Core Principle**: No Go code is retired until the C# equivalent produces identical outputs for all test scenarios.

---

## Testing Philosophy

### Why This Matters

Cimian manages software deployment for **10,000+ computers**. The Go codebase contains years of battle-tested logic for:

- Complex conditional item evaluation with nested OR/AND expressions
- Multi-source package version detection (Registry, file system, scripts)
- Hierarchical manifest processing with inheritance
- Intelligent version comparison across multiple formats
- Architecture-aware package selection
- Dependency resolution and installation ordering
- Rollback and recovery mechanisms

**A single logic error in migration could cause:**
- Mass failed installations across thousands of devices
- Silent version mismatches leading to security gaps
- Broken conditional deployments leaving devices misconfigured
- Self-update failures requiring manual intervention at scale

### Testing Principles

1. **Black-Box Comparison**: Treat both Go and C# as black boxes; compare outputs, not implementation details
2. **Real-World Data**: Use production-like manifests, catalogs, and packages for testing
3. **Comprehensive Coverage**: Test all documented features and edge cases
4. **Regression Prevention**: Every bug found becomes a permanent test case
5. **Continuous Validation**: Run comparison tests throughout migration, not just at the end

---

## Test Categories

### 1. Unit Parity Tests

Individual function/method output comparison between Go and C#.

| Go Package | C# Namespace | Priority | Test Focus |
|------------|--------------|----------|------------|
| `pkg/predicates` | `Cimian.Core.Predicates` | **CRITICAL** | Expression parsing, condition evaluation |
| `pkg/status` | `Cimian.Core.Status` | **CRITICAL** | Version comparison, installation detection |
| `pkg/catalog` | `Cimian.Core.Catalog` | HIGH | YAML parsing, item deduplication |
| `pkg/manifest` | `Cimian.Core.Manifest` | HIGH | Hierarchy processing, item collection |
| `pkg/installer` | `Cimian.Engine.Installer` | **CRITICAL** | Command generation, timeout calculation |
| `pkg/version` | `Cimian.Core.Version` | HIGH | Version normalization, comparison |
| `pkg/config` | `Cimian.Core.Configuration` | MEDIUM | YAML parsing, default values |
| `pkg/download` | `Cimian.Infrastructure.Http` | MEDIUM | Retry logic, hash validation |

### 2. Integration Parity Tests

End-to-end workflow comparison between Go and C# binaries.

| Test Scenario | Description | Validation Method |
|---------------|-------------|-------------------|
| Catalog Processing | Parse catalogs, select versions | Compare selected items list |
| Manifest Hierarchy | Process manifest chain | Compare final item lists |
| Conditional Evaluation | Evaluate all condition types | Compare matched items |
| Installation Planning | Determine what to install/update | Compare action plans |
| Status Detection | Detect installed versions | Compare detection results |

### 3. Decision Tree Validation

The most critical tests focus on Cimian's complex decision trees.

```
Decision Tree Categories:
├── What needs to be installed?
│   ├── Manifest item resolution
│   ├── Conditional item evaluation
│   ├── Dependency resolution
│   └── Version selection
├── What is already installed?
│   ├── Registry detection
│   ├── File system verification
│   ├── Install check scripts
│   └── Version comparison
├── What action is needed?
│   ├── Install (not present)
│   ├── Update (older version)
│   ├── Skip (current version)
│   └── Uninstall (managed_uninstalls)
└── How to perform the action?
    ├── Installer type selection
    ├── Argument construction
    ├── Timeout calculation
    └── Script execution order
```

---

## Test Infrastructure

### Test Fixtures Repository

Create a dedicated test fixtures directory with real-world-like test data:

```
tests/
├── fixtures/
│   ├── catalogs/
│   │   ├── simple_catalog.yaml          # Basic catalog with few items
│   │   ├── version_variety.yaml         # Multiple versions of same items
│   │   ├── architecture_mixed.yaml      # Mixed x64/arm64/x86 items
│   │   ├── complex_items.yaml           # Items with all optional fields
│   │   └── production_sample.yaml       # Sanitized production catalog
│   ├── manifests/
│   │   ├── base_manifest.yaml           # Simple base manifest
│   │   ├── conditional_simple.yaml      # Basic conditional items
│   │   ├── conditional_complex.yaml     # Nested OR/AND conditions
│   │   ├── conditional_nested.yaml      # Nested conditional_items
│   │   ├── hierarchy/                   # Manifest hierarchy chain
│   │   │   ├── CoreManifest.yaml
│   │   │   ├── Assigned.yaml
│   │   │   └── IT.yaml
│   │   └── edge_cases/
│   │       ├── empty_lists.yaml
│   │       ├── duplicate_items.yaml
│   │       └── circular_deps.yaml
│   ├── system_facts/
│   │   ├── desktop_x64.json             # Simulated system facts
│   │   ├── laptop_arm64.json
│   │   ├── domain_joined.json
│   │   ├── workgroup.json
│   │   └── entra_joined.json
│   ├── packages/
│   │   ├── mock_msi/                    # Mock MSI installers
│   │   ├── mock_exe/                    # Mock EXE installers
│   │   └── mock_nupkg/                  # Mock NuGet packages
│   └── expected_outputs/
│       ├── catalog_processing/          # Expected outputs per scenario
│       ├── manifest_processing/
│       └── status_detection/
├── comparison/
│   ├── Compare-CimianOutputs.ps1        # Main comparison script
│   ├── Compare-ConditionalItems.ps1     # Conditional evaluation tests
│   ├── Compare-VersionLogic.ps1         # Version comparison tests
│   └── Generate-ComparisonReport.ps1    # HTML report generation
└── golden/
    ├── golden_test_runner.ps1           # Golden file test runner
    └── outputs/                         # Committed golden outputs from Go
```

### Comparison Test Framework

#### PowerShell Comparison Script

```powershell
# Compare-CimianOutputs.ps1
# Runs both Go and C# binaries with identical inputs and compares outputs

param(
    [Parameter(Mandatory)]
    [string]$TestName,
    
    [Parameter(Mandatory)]
    [string]$GoBinaryPath,
    
    [Parameter(Mandatory)]
    [string]$CSharpBinaryPath,
    
    [Parameter(Mandatory)]
    [string]$FixturesPath,
    
    [switch]$UpdateGolden
)

function Invoke-ComparisonTest {
    param(
        [string]$TestCase,
        [string]$Arguments
    )
    
    # Run Go binary
    $goOutput = & $GoBinaryPath $Arguments 2>&1
    $goExitCode = $LASTEXITCODE
    
    # Run C# binary
    $csharpOutput = & $CSharpBinaryPath $Arguments 2>&1
    $csharpExitCode = $LASTEXITCODE
    
    # Compare results
    $result = [PSCustomObject]@{
        TestCase = $TestCase
        ExitCodeMatch = $goExitCode -eq $csharpExitCode
        GoExitCode = $goExitCode
        CSharpExitCode = $csharpExitCode
        OutputMatch = (Compare-Object $goOutput $csharpOutput -SyncWindow 0).Count -eq 0
        Differences = @()
    }
    
    if (-not $result.OutputMatch) {
        $result.Differences = Compare-Object $goOutput $csharpOutput -PassThru
    }
    
    return $result
}
```

### Golden File Testing

"Golden files" capture known-good outputs from the Go implementation that C# must match:

1. **Generate Golden Files**: Run Go binaries against all test fixtures
2. **Commit Golden Files**: Version control the expected outputs
3. **Validate C# Against Golden**: C# outputs must match golden files exactly
4. **Update Process**: When intentional changes occur, regenerate golden files

```powershell
# Generate golden files from Go implementation
.\golden_test_runner.ps1 -Mode Generate -GoBinary .\release\arm64\managedsoftwareupdate.exe

# Validate C# against golden files
.\golden_test_runner.ps1 -Mode Validate -CSharpBinary .\release\arm64\managedsoftwareupdate_csharp.exe
```

---

## Critical Test Scenarios

### 1. Conditional Items Evaluation

**CRITICAL**: This is the most complex logic in Cimian.

```yaml
# Test: conditional_complex.yaml
conditional_items:
  # Test OR evaluation
  - condition: hostname CONTAINS "Design-" OR hostname CONTAINS "Studio-"
    managed_installs:
      - CreativeSuite
      
  # Test AND evaluation
  - condition: os_vers_major >= 11 AND arch == "x64"
    managed_installs:
      - ModernApp
      
  # Test nested conditions
  - condition: enrolled_usage == "Shared"
    conditional_items:
      - condition: machine_type == "desktop"
        managed_installs:
          - KioskMode
    managed_installs:
      - SharedConfig
      
  # Test NOT operator
  - condition: NOT hostname CONTAINS "Kiosk"
    managed_installs:
      - StandardApps
      
  # Test ANY operator with arrays
  - condition: ANY catalogs != "Testing"
    managed_installs:
      - ProductionOnly
```

**Test Matrix**:
| System Facts | Expected matched_installs | Test ID |
|--------------|---------------------------|---------|
| hostname="Design-Workstation" | CreativeSuite | COND-001 |
| hostname="Studio-1", os_vers_major=11, arch=x64 | CreativeSuite, ModernApp | COND-002 |
| enrolled_usage="Shared", machine_type="desktop" | SharedConfig, KioskMode | COND-003 |
| hostname="Kiosk-Lobby" | (none from StandardApps) | COND-004 |
| catalogs=["Production", "Approved"] | ProductionOnly | COND-005 |

### 2. Version Comparison Logic

```powershell
# Version pairs to test (local vs remote -> expected result)
$versionTests = @(
    @{ Local = "1.0.0"; Remote = "1.0.1"; Expected = "older" }    # Semantic
    @{ Local = "1.0.1"; Remote = "1.0.0"; Expected = "newer" }
    @{ Local = "1.0.0"; Remote = "1.0.0"; Expected = "same" }
    @{ Local = "10.0.19045"; Remote = "10.0.22621"; Expected = "older" }  # Windows build
    @{ Local = "139.0.7258.139"; Remote = "140.0.0.1"; Expected = "older" }  # Chrome-style
    @{ Local = "2024.1.2.3"; Remote = "2024.1.2.4"; Expected = "older" }  # Date-based
    @{ Local = "1.2.3.4"; Remote = "1.2.3.5"; Expected = "older" }  # 4-part
    @{ Local = "v1.0.0"; Remote = "1.0.1"; Expected = "older" }    # v-prefix
    @{ Local = "1.0.0-beta"; Remote = "1.0.0"; Expected = "older" }  # Pre-release
)
```

### 3. Installation Detection

Test that both Go and C# detect installations identically:

```powershell
# Detection scenarios
$detectionTests = @(
    @{
        Name = "MSI-Registry"
        Item = @{ name = "7-Zip"; installs = @(@{ path = "C:\Program Files\7-Zip\7z.exe" }) }
        ExpectedVersion = "24.09"
    }
    @{
        Name = "Uninstall-Registry"
        Item = @{ name = "VSCode"; uninstall_name = "Microsoft Visual Studio Code" }
        ExpectedVersion = "1.85.0"
    }
    @{
        Name = "InstallCheck-Script"
        Item = @{ name = "CustomApp"; installcheck_script = "Get-ItemPropertyValue..." }
        ExpectedVersion = "from-script"
    }
)
```

### 4. Manifest Hierarchy Processing

```yaml
# Test manifest hierarchy resolution
# CoreManifest.yaml -> Assigned.yaml -> IT.yaml

# Expected final state after processing IT.yaml:
# - All managed_installs from all levels
# - Proper deduplication
# - Correct source tracking
```

---

## Automated Testing Pipeline

### CI/CD Integration

```yaml
# .github/workflows/migration-parity-tests.yml
name: Migration Parity Tests

on:
  push:
    paths:
      - 'src/**'
      - 'pkg/**'
  pull_request:
    branches: [dev, main]

jobs:
  parity-tests:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Build Go Binaries
        run: .\build.ps1 -Sign -Binaries
        
      - name: Build C# Binaries
        run: dotnet build src/Cimian.sln -c Release
        
      - name: Run Unit Parity Tests
        run: dotnet test tests/Cimian.ParityTests -c Release
        
      - name: Run Integration Parity Tests
        run: .\tests\comparison\Compare-CimianOutputs.ps1 -All
        
      - name: Validate Golden Files
        run: .\tests\golden\golden_test_runner.ps1 -Mode Validate
        
      - name: Generate Comparison Report
        run: .\tests\comparison\Generate-ComparisonReport.ps1 -OutputPath artifacts/
        
      - name: Upload Report
        uses: actions/upload-artifact@v4
        with:
          name: parity-report
          path: artifacts/
```

### Test Reporting

Generate comprehensive HTML reports showing:
- Pass/fail status for each test scenario
- Side-by-side diff for failed comparisons
- Performance comparison (execution time)
- Coverage metrics per Go package vs C# namespace

---

## Migration Validation Gates

### Gate 1: Core Library Parity (Required before Phase 2)

| Package | Test Count | Pass Rate Required |
|---------|------------|-------------------|
| `pkg/predicates` → `Cimian.Core.Predicates` | 50+ | 100% |
| `pkg/version` → `Cimian.Core.Version` | 30+ | 100% |
| `pkg/status` → `Cimian.Core.Status` | 40+ | 100% |

### Gate 2: Tool Parity (Required before each tool switch)

Before retiring any Go tool:
1. Run 100% of golden file tests
2. Run against production manifest samples (sanitized)
3. Run on multiple Windows versions (10, 11)
4. Run on multiple architectures (x64, arm64)

### Gate 3: Full System Parity (Required for v1.0)

1. Deploy C# binaries to 100 test machines
2. Run in shadow mode (C# runs, logs, but Go executes)
3. Compare all outputs for 2 weeks
4. Zero unexplained differences
5. Performance within 10% of Go

---

## Implementation Roadmap

### Phase 1: Test Infrastructure (Week 1-2)
- [ ] Create test fixtures directory structure
- [ ] Generate comprehensive test manifests and catalogs
- [ ] Implement `Compare-CimianOutputs.ps1`
- [ ] Set up golden file workflow

### Phase 2: Unit Parity Tests (Concurrent with C# development)
- [ ] `Cimian.Core.Predicates` - Expression parsing tests
- [ ] `Cimian.Core.Version` - Version comparison tests
- [ ] `Cimian.Core.Status` - Detection logic tests
- [ ] `Cimian.Core.Catalog` - YAML parsing tests

### Phase 3: Integration Parity Tests (After core libraries complete)
- [ ] `managedsoftwareupdate --checkonly` comparison
- [ ] `cimiimport` output comparison
- [ ] `makecatalogs` output comparison
- [ ] Full workflow comparison

### Phase 4: Production Validation (Before Go retirement)
- [ ] Shadow mode deployment
- [ ] 2-week parallel operation
- [ ] Sign-off from stakeholders

---

## Success Criteria

### Technical Criteria
- [ ] 100% of unit parity tests pass
- [ ] 100% of golden file tests pass
- [ ] Zero unexplained behavioral differences
- [ ] Performance within 10% of Go implementation
- [ ] All edge cases documented and tested

### Business Criteria
- [ ] Zero production incidents during shadow period
- [ ] Stakeholder sign-off on test results
- [ ] Rollback plan documented and tested
- [ ] Training completed for support team

---

## Appendix: Test Case Templates

### Unit Parity Test Template (xUnit)

```csharp
[Theory]
[MemberData(nameof(VersionComparisonTestData))]
public void VersionComparison_ShouldMatchGoImplementation(
    string localVersion, 
    string remoteVersion, 
    bool expectedIsOlder)
{
    // Arrange - Get expected result from Go (via golden file or subprocess)
    var goResult = GetGoVersionComparisonResult(localVersion, remoteVersion);
    
    // Act
    var csharpResult = VersionService.IsOlderVersion(localVersion, remoteVersion);
    
    // Assert
    csharpResult.Should().Be(goResult, 
        $"C# result {csharpResult} should match Go result {goResult} " +
        $"for versions {localVersion} vs {remoteVersion}");
}

public static IEnumerable<object[]> VersionComparisonTestData =>
    new List<object[]>
    {
        new object[] { "1.0.0", "1.0.1", true },
        new object[] { "1.0.1", "1.0.0", false },
        new object[] { "10.0.19045", "10.0.22621", true },
        // ... extensive test data from production analysis
    };
```

### Integration Test Template

```powershell
Describe "ManagedsoftwareUpdate --checkonly Parity" {
    BeforeAll {
        $goBinary = ".\release\arm64\managedsoftwareupdate.exe"
        $csharpBinary = ".\release\arm64\managedsoftwareupdate_csharp.exe"
        $testConfig = ".\tests\fixtures\config\test_config.yaml"
    }
    
    It "Should produce identical output for <TestName>" -ForEach @(
        @{ TestName = "EmptyManifest"; ManifestPath = ".\tests\fixtures\manifests\empty.yaml" }
        @{ TestName = "SimpleManifest"; ManifestPath = ".\tests\fixtures\manifests\simple.yaml" }
        @{ TestName = "ComplexConditional"; ManifestPath = ".\tests\fixtures\manifests\conditional_complex.yaml" }
    ) {
        # Run Go
        $goOutput = & $goBinary --checkonly --config $testConfig --manifest $ManifestPath 2>&1
        $goExit = $LASTEXITCODE
        
        # Run C#
        $csharpOutput = & $csharpBinary --checkonly --config $testConfig --manifest $ManifestPath 2>&1
        $csharpExit = $LASTEXITCODE
        
        # Compare
        $csharpExit | Should -Be $goExit
        $csharpOutput | Should -BeExactly $goOutput
    }
}
```

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-11-30 | Migration Team | Initial testing strategy |

