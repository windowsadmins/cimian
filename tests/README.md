# Cimian Migration Testing

This directory contains the test infrastructure for validating the Go-to-C# migration.

## Directory Structure

```
tests/
├── fixtures/                    # Test data files
│   ├── catalogs/               # Sample catalog YAML files
│   │   ├── simple_catalog.yaml
│   │   ├── version_variety.yaml
│   │   └── architecture_mixed.yaml
│   ├── manifests/              # Sample manifest files
│   │   ├── conditional_simple.yaml
│   │   ├── conditional_complex.yaml
│   │   ├── conditional_nested.yaml
│   │   ├── hierarchy/          # Manifest inheritance chain
│   │   │   ├── CoreManifest.yaml
│   │   │   ├── Assigned.yaml
│   │   │   └── IT.yaml
│   │   └── edge_cases/         # Edge case test files
│   ├── system_facts/           # Simulated system facts
│   │   ├── desktop_x64.json
│   │   ├── laptop_arm64.json
│   │   ├── shared_lab.json
│   │   ├── kiosk.json
│   │   └── workgroup.json
│   └── expected_outputs/       # Expected outputs for validation
├── comparison/                  # Comparison test framework
│   ├── Compare-CimianOutputs.ps1
│   └── results/                # Test run results
└── golden/                      # Golden file testing
    ├── golden_test_runner.ps1
    └── outputs/                # Captured golden outputs from Go
```

## Quick Start

### 1. Generate Golden Files (from Go)

First, capture the expected outputs from the Go implementation:

```powershell
# Build Go binaries
.\build.ps1 -Sign -Binaries

# Generate golden files
.\tests\golden\golden_test_runner.ps1 -Mode Generate -GoBinary .\release\arm64\managedsoftwareupdate.exe
```

### 2. Validate C# Implementation

After implementing C# components, validate against golden files:

```powershell
# Build C# binaries
dotnet build src\Cimian.sln -c Release

# Validate against golden files
.\tests\golden\golden_test_runner.ps1 -Mode Validate -CSharpBinary .\release\arm64\managedsoftwareupdate_csharp.exe
```

### 3. Run Full Comparison Suite

Run comprehensive comparison between Go and C#:

```powershell
.\tests\comparison\Compare-CimianOutputs.ps1 -TestSuite all -Verbose
```

## Test Categories

### Version Comparison Tests
Validates that version comparison logic produces identical results.

Test cases include:
- Semantic versioning (1.0.0, 1.0.1)
- Windows build numbers (10.0.19045, 10.0.22621)
- Chrome-style versions (139.0.7258.139)
- Date-based versions (2024.1.2.3)
- Pre-release versions (1.0.0-alpha, 1.0.0-beta)

### Conditional Items Tests
Validates complex conditional evaluation logic.

Test cases include:
- Simple operators (==, !=, CONTAINS)
- OR expressions (hostname CONTAINS "A" OR hostname CONTAINS "B")
- AND expressions (arch == "x64" AND os_vers_major >= 11)
- NOT operator (NOT hostname CONTAINS "Kiosk")
- ANY operator (ANY catalogs != "Testing")
- Nested conditional_items (3 levels deep)

### Catalog Processing Tests
Validates catalog parsing and item selection.

Test cases include:
- YAML parsing fidelity
- Version deduplication (select highest version)
- Architecture filtering
- Multiple versions of same item

### Manifest Hierarchy Tests
Validates manifest inheritance processing.

Test cases include:
- Multi-level inheritance (CoreManifest → Assigned → IT)
- Item merging from parent manifests
- Conditional items at each level

## Adding New Test Cases

### 1. Add a Fixture File

Create a new YAML/JSON file in the appropriate fixtures subdirectory:

```yaml
# tests/fixtures/manifests/my_test_case.yaml
name: "My Test Case"
catalogs:
  - Testing
managed_installs:
  - SomePackage
```

### 2. Add System Facts (if needed)

Create a JSON file representing system state:

```json
{
  "hostname": "TEST-MACHINE",
  "arch": "x64",
  "os_vers_major": 11,
  "domain": "TESTDOMAIN"
}
```

### 3. Generate Golden Output

Run the Go implementation and capture output:

```powershell
.\tests\golden\golden_test_runner.ps1 -Mode Generate
```

### 4. Commit Golden Files

Golden files should be committed to version control so CI can validate C#.

## Continuous Integration

The parity tests are designed to run in CI pipelines:

```yaml
# Example GitHub Actions workflow
- name: Run Parity Tests
  run: |
    .\tests\golden\golden_test_runner.ps1 -Mode Validate -CSharpBinary $env:CSHARP_BINARY
    if ($LASTEXITCODE -ne 0) { exit 1 }
```

## Troubleshooting

### Golden File Mismatch

If C# output differs from golden files:

1. Check the diff file: `tests/golden/outputs/<scenario>.diff.txt`
2. Analyze the difference
3. Fix the C# implementation to match Go behavior
4. Re-run validation

### Missing Golden Files

If golden files don't exist:

1. Ensure Go binaries are built: `.\build.ps1 -Sign -Binaries`
2. Generate golden files: `.\tests\golden\golden_test_runner.ps1 -Mode Generate`
3. Commit the generated files

## Success Criteria

Before retiring any Go component, the following must pass:

- [ ] 100% of golden file tests pass
- [ ] All version comparison edge cases pass
- [ ] All conditional item expressions evaluate identically
- [ ] Catalog deduplication selects same versions
- [ ] Manifest hierarchy produces identical item lists
