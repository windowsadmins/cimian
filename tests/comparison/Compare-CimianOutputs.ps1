#Requires -Version 7.0
<#
.SYNOPSIS
    Compares outputs between Go and C# Cimian binaries to validate migration parity.

.DESCRIPTION
    This script runs both Go and C# implementations with identical inputs and compares
    their outputs. It's the core validation tool for the Go-to-C# migration.

.PARAMETER TestSuite
    The test suite to run: 'all', 'conditional', 'version', 'catalog', 'manifest', 'status'

.PARAMETER GoBinaryPath
    Path to the Go binary directory (default: .\release\<arch>\)

.PARAMETER CSharpBinaryPath
    Path to the C# binary directory (default: .\release\<arch>_csharp\)

.PARAMETER FixturesPath
    Path to the test fixtures directory (default: .\tests\fixtures\)

.PARAMETER OutputPath
    Path to save comparison results (default: .\tests\comparison\results\)

.PARAMETER UpdateGolden
    If specified, updates golden files with Go output instead of comparing

.PARAMETER Verbose
    Enable verbose output for debugging

.EXAMPLE
    .\Compare-CimianOutputs.ps1 -TestSuite all
    
.EXAMPLE
    .\Compare-CimianOutputs.ps1 -TestSuite conditional -Verbose
    
.EXAMPLE
    .\Compare-CimianOutputs.ps1 -UpdateGolden
#>

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet('all', 'conditional', 'version', 'catalog', 'manifest', 'status')]
    [string]$TestSuite = 'all',
    
    [Parameter()]
    [string]$GoBinaryPath,
    
    [Parameter()]
    [string]$CSharpBinaryPath,
    
    [Parameter()]
    [string]$FixturesPath,
    
    [Parameter()]
    [string]$OutputPath,
    
    [Parameter()]
    [switch]$UpdateGolden,
    
    [Parameter()]
    [switch]$StopOnFirstFailure
)

# Determine architecture
$arch = if ([Environment]::Is64BitOperatingSystem) {
    if ([Environment]::GetEnvironmentVariable("PROCESSOR_IDENTIFIER") -match "ARM") { "arm64" } else { "x64" }
} else { "x86" }

# Set defaults based on script location
$ScriptRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent (Split-Path -Parent $ScriptRoot)

if (-not $GoBinaryPath) { $GoBinaryPath = Join-Path $RepoRoot "release\$arch" }
if (-not $CSharpBinaryPath) { $CSharpBinaryPath = Join-Path $RepoRoot "release\${arch}_csharp" }
if (-not $FixturesPath) { $FixturesPath = Join-Path $RepoRoot "tests\fixtures" }
if (-not $OutputPath) { $OutputPath = Join-Path $ScriptRoot "results" }

# Create output directory
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

# Test result tracking
$script:TestResults = @{
    Passed = 0
    Failed = 0
    Skipped = 0
    Errors = @()
}

function Write-TestHeader {
    param([string]$TestName)
    Write-Host "`n$('=' * 60)" -ForegroundColor Cyan
    Write-Host "TEST: $TestName" -ForegroundColor Cyan
    Write-Host "$('=' * 60)" -ForegroundColor Cyan
}

function Write-TestResult {
    param(
        [string]$TestName,
        [bool]$Passed,
        [string]$Message = ""
    )
    
    if ($Passed) {
        Write-Host "  [PASS] $TestName" -ForegroundColor Green
        $script:TestResults.Passed++
    } else {
        Write-Host "  [FAIL] $TestName" -ForegroundColor Red
        if ($Message) {
            Write-Host "         $Message" -ForegroundColor Yellow
        }
        $script:TestResults.Failed++
        $script:TestResults.Errors += @{
            TestName = $TestName
            Message = $Message
        }
        
        if ($StopOnFirstFailure) {
            throw "Test failed: $TestName"
        }
    }
}

function Compare-BinaryOutput {
    <#
    .SYNOPSIS
        Runs both Go and C# binaries and compares their outputs.
    #>
    param(
        [string]$TestName,
        [string]$GoBinary,
        [string]$CSharpBinary,
        [string[]]$Arguments,
        [hashtable]$Environment = @{}
    )
    
    # Set up environment
    $originalEnv = @{}
    foreach ($key in $Environment.Keys) {
        $originalEnv[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $Environment[$key])
    }
    
    try {
        # Run Go binary
        Write-Verbose "Running Go: $GoBinary $($Arguments -join ' ')"
        $goProcess = Start-Process -FilePath $GoBinary -ArgumentList $Arguments `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput "$OutputPath\go_stdout.txt" `
            -RedirectStandardError "$OutputPath\go_stderr.txt"
        $goExitCode = $goProcess.ExitCode
        $goStdout = Get-Content "$OutputPath\go_stdout.txt" -Raw -ErrorAction SilentlyContinue
        $goStderr = Get-Content "$OutputPath\go_stderr.txt" -Raw -ErrorAction SilentlyContinue
        
        # Run C# binary
        Write-Verbose "Running C#: $CSharpBinary $($Arguments -join ' ')"
        $csharpProcess = Start-Process -FilePath $CSharpBinary -ArgumentList $Arguments `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput "$OutputPath\csharp_stdout.txt" `
            -RedirectStandardError "$OutputPath\csharp_stderr.txt"
        $csharpExitCode = $csharpProcess.ExitCode
        $csharpStdout = Get-Content "$OutputPath\csharp_stdout.txt" -Raw -ErrorAction SilentlyContinue
        $csharpStderr = Get-Content "$OutputPath\csharp_stderr.txt" -Raw -ErrorAction SilentlyContinue
        
        # Compare results
        $result = [PSCustomObject]@{
            TestName = $TestName
            ExitCodeMatch = $goExitCode -eq $csharpExitCode
            GoExitCode = $goExitCode
            CSharpExitCode = $csharpExitCode
            StdoutMatch = $goStdout -eq $csharpStdout
            StderrMatch = $goStderr -eq $csharpStderr
            GoStdout = $goStdout
            CSharpStdout = $csharpStdout
            GoStderr = $goStderr
            CSharpStderr = $csharpStderr
        }
        
        $allMatch = $result.ExitCodeMatch -and $result.StdoutMatch
        
        if (-not $allMatch) {
            $message = ""
            if (-not $result.ExitCodeMatch) {
                $message += "Exit codes differ: Go=$goExitCode, C#=$csharpExitCode. "
            }
            if (-not $result.StdoutMatch) {
                $message += "Stdout differs. "
            }
            Write-TestResult -TestName $TestName -Passed $false -Message $message
            
            # Save diff for analysis
            $diffPath = Join-Path $OutputPath "$TestName.diff.txt"
            @"
=== TEST: $TestName ===
Exit Codes: Go=$goExitCode, C#=$csharpExitCode

=== GO STDOUT ===
$goStdout

=== C# STDOUT ===
$csharpStdout

=== GO STDERR ===
$goStderr

=== C# STDERR ===
$csharpStderr
"@ | Set-Content -Path $diffPath
        } else {
            Write-TestResult -TestName $TestName -Passed $true
        }
        
        return $result
    } finally {
        # Restore original environment
        foreach ($key in $originalEnv.Keys) {
            [Environment]::SetEnvironmentVariable($key, $originalEnv[$key])
        }
    }
}

function Test-VersionComparison {
    <#
    .SYNOPSIS
        Tests version comparison logic between Go and C# implementations.
    #>
    Write-TestHeader "Version Comparison Tests"
    
    $versionTests = @(
        # Semantic versioning
        @{ Local = "1.0.0"; Remote = "1.0.1"; Description = "Semantic: patch upgrade" }
        @{ Local = "1.0.1"; Remote = "1.0.0"; Description = "Semantic: patch downgrade" }
        @{ Local = "1.0.0"; Remote = "1.0.0"; Description = "Semantic: same version" }
        @{ Local = "1.1.0"; Remote = "1.0.9"; Description = "Semantic: minor upgrade" }
        @{ Local = "2.0.0"; Remote = "1.9.9"; Description = "Semantic: major upgrade" }
        
        # Windows build numbers
        @{ Local = "10.0.19045"; Remote = "10.0.22621"; Description = "Windows: 19045 vs 22621" }
        @{ Local = "10.0.22621"; Remote = "10.0.22631"; Description = "Windows: 22621 vs 22631" }
        
        # Chrome-style versions
        @{ Local = "139.0.7258.139"; Remote = "140.0.0.1"; Description = "Chrome: major upgrade" }
        @{ Local = "139.0.7258.100"; Remote = "139.0.7258.139"; Description = "Chrome: patch upgrade" }
        
        # Date-based versions
        @{ Local = "2024.1.2.3"; Remote = "2024.1.2.4"; Description = "Date-based: same month" }
        @{ Local = "2024.1.2.3"; Remote = "2024.2.1.1"; Description = "Date-based: different month" }
        
        # 4-part versions
        @{ Local = "1.2.3.4"; Remote = "1.2.3.5"; Description = "4-part: last digit" }
        @{ Local = "1.2.3.4"; Remote = "1.2.4.0"; Description = "4-part: third digit" }
        
        # v-prefix handling
        @{ Local = "v1.0.0"; Remote = "1.0.1"; Description = "v-prefix: local has v" }
        @{ Local = "1.0.0"; Remote = "v1.0.1"; Description = "v-prefix: remote has v" }
        @{ Local = "v1.0.0"; Remote = "v1.0.1"; Description = "v-prefix: both have v" }
        
        # Pre-release versions
        @{ Local = "1.0.0-alpha"; Remote = "1.0.0"; Description = "Pre-release: alpha vs release" }
        @{ Local = "1.0.0-beta"; Remote = "1.0.0"; Description = "Pre-release: beta vs release" }
        @{ Local = "1.0.0-alpha"; Remote = "1.0.0-beta"; Description = "Pre-release: alpha vs beta" }
        
        # Edge cases
        @{ Local = "0.0.1"; Remote = "0.0.2"; Description = "Edge: zero versions" }
        @{ Local = ""; Remote = "1.0.0"; Description = "Edge: empty local" }
        @{ Local = "1.0.0"; Remote = ""; Description = "Edge: empty remote" }
    )
    
    foreach ($test in $versionTests) {
        # This would call a version comparison utility
        # For now, we'll document what needs to be tested
        Write-Verbose "Test: $($test.Description) - $($test.Local) vs $($test.Remote)"
        # TODO: Implement actual version comparison test once C# utilities exist
    }
    
    Write-Host "`n  Version comparison tests defined: $($versionTests.Count)" -ForegroundColor Yellow
    Write-Host "  (Requires C# version utility to be implemented)" -ForegroundColor Yellow
}

function Test-ConditionalItems {
    <#
    .SYNOPSIS
        Tests conditional item evaluation between Go and C# implementations.
    #>
    Write-TestHeader "Conditional Items Evaluation Tests"
    
    $manifestFiles = Get-ChildItem -Path "$FixturesPath\manifests" -Filter "conditional_*.yaml" -Recurse
    $systemFactFiles = Get-ChildItem -Path "$FixturesPath\system_facts" -Filter "*.json"
    
    foreach ($manifest in $manifestFiles) {
        foreach ($facts in $systemFactFiles) {
            $testName = "$($manifest.BaseName)_$($facts.BaseName)"
            Write-Verbose "Testing: $testName"
            
            # This would run the conditional evaluation test
            # TODO: Implement when C# conditional evaluator exists
        }
    }
    
    Write-Host "`n  Conditional test combinations: $($manifestFiles.Count * $systemFactFiles.Count)" -ForegroundColor Yellow
    Write-Host "  (Requires C# conditional evaluator to be implemented)" -ForegroundColor Yellow
}

function Test-CatalogProcessing {
    <#
    .SYNOPSIS
        Tests catalog processing between Go and C# implementations.
    #>
    Write-TestHeader "Catalog Processing Tests"
    
    $catalogFiles = Get-ChildItem -Path "$FixturesPath\catalogs" -Filter "*.yaml"
    
    foreach ($catalog in $catalogFiles) {
        Write-Verbose "Testing catalog: $($catalog.Name)"
        
        # Tests to run:
        # 1. YAML parsing produces identical structures
        # 2. Version deduplication selects same item
        # 3. Architecture filtering produces same results
    }
    
    Write-Host "`n  Catalog files to test: $($catalogFiles.Count)" -ForegroundColor Yellow
    Write-Host "  (Requires C# catalog processor to be implemented)" -ForegroundColor Yellow
}

function Test-ManifestHierarchy {
    <#
    .SYNOPSIS
        Tests manifest hierarchy processing between Go and C# implementations.
    #>
    Write-TestHeader "Manifest Hierarchy Tests"
    
    $hierarchyPath = Join-Path $FixturesPath "manifests\hierarchy"
    
    if (Test-Path $hierarchyPath) {
        $manifests = Get-ChildItem -Path $hierarchyPath -Filter "*.yaml"
        Write-Host "`n  Hierarchy manifests to test: $($manifests.Count)" -ForegroundColor Yellow
    }
    
    # Test cases:
    # 1. IT.yaml should include all items from Assigned.yaml and CoreManifest.yaml
    # 2. managed_installs should be correctly merged
    # 3. Conditional items should be evaluated at each level
    
    Write-Host "  (Requires C# manifest processor to be implemented)" -ForegroundColor Yellow
}

function Show-Summary {
    Write-Host "`n$('=' * 60)" -ForegroundColor Cyan
    Write-Host "TEST SUMMARY" -ForegroundColor Cyan
    Write-Host "$('=' * 60)" -ForegroundColor Cyan
    
    $total = $script:TestResults.Passed + $script:TestResults.Failed + $script:TestResults.Skipped
    
    Write-Host "Total Tests: $total" -ForegroundColor White
    Write-Host "  Passed:  $($script:TestResults.Passed)" -ForegroundColor Green
    Write-Host "  Failed:  $($script:TestResults.Failed)" -ForegroundColor $(if ($script:TestResults.Failed -gt 0) { "Red" } else { "White" })
    Write-Host "  Skipped: $($script:TestResults.Skipped)" -ForegroundColor Yellow
    
    if ($script:TestResults.Failed -gt 0) {
        Write-Host "`nFailed Tests:" -ForegroundColor Red
        foreach ($error in $script:TestResults.Errors) {
            Write-Host "  - $($error.TestName): $($error.Message)" -ForegroundColor Red
        }
    }
    
    # Save results to JSON
    $resultsFile = Join-Path $OutputPath "results_$(Get-Date -Format 'yyyyMMdd_HHmmss').json"
    $script:TestResults | ConvertTo-Json -Depth 10 | Set-Content -Path $resultsFile
    Write-Host "`nResults saved to: $resultsFile" -ForegroundColor Cyan
}

# Main execution
Write-Host @"

 ██████╗██╗███╗   ███╗██╗ █████╗ ███╗   ██╗
██╔════╝██║████╗ ████║██║██╔══██╗████╗  ██║
██║     ██║██╔████╔██║██║███████║██╔██╗ ██║
██║     ██║██║╚██╔╝██║██║██╔══██║██║╚██╗██║
╚██████╗██║██║ ╚═╝ ██║██║██║  ██║██║ ╚████║
 ╚═════╝╚═╝╚═╝     ╚═╝╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝
                                            
      Migration Parity Test Suite
"@ -ForegroundColor Cyan

Write-Host "Architecture: $arch" -ForegroundColor Yellow
Write-Host "Go Binary Path: $GoBinaryPath" -ForegroundColor Yellow
Write-Host "C# Binary Path: $CSharpBinaryPath" -ForegroundColor Yellow
Write-Host "Fixtures Path: $FixturesPath" -ForegroundColor Yellow
Write-Host "Output Path: $OutputPath" -ForegroundColor Yellow

try {
    switch ($TestSuite) {
        'all' {
            Test-VersionComparison
            Test-ConditionalItems
            Test-CatalogProcessing
            Test-ManifestHierarchy
        }
        'version' { Test-VersionComparison }
        'conditional' { Test-ConditionalItems }
        'catalog' { Test-CatalogProcessing }
        'manifest' { Test-ManifestHierarchy }
    }
} catch {
    Write-Host "Error during test execution: $_" -ForegroundColor Red
}

Show-Summary
