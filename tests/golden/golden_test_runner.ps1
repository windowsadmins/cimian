#Requires -Version 7.0
<#
.SYNOPSIS
    Golden file test runner for Cimian Go-to-C# migration validation.

.DESCRIPTION
    Golden files capture known-good outputs from the Go implementation.
    This script either generates golden files from Go, or validates C# against them.

.PARAMETER Mode
    'Generate' - Run Go binaries and save outputs as golden files
    'Validate' - Run C# binaries and compare against golden files

.PARAMETER GoBinary
    Path to Go binary (for Generate mode)

.PARAMETER CSharpBinary
    Path to C# binary (for Validate mode)

.PARAMETER GoldenPath
    Path to golden files directory

.PARAMETER FixturesPath
    Path to test fixtures directory

.EXAMPLE
    .\golden_test_runner.ps1 -Mode Generate -GoBinary .\release\arm64\managedsoftwareupdate.exe
    
.EXAMPLE
    .\golden_test_runner.ps1 -Mode Validate -CSharpBinary .\release\arm64\managedsoftwareupdate_csharp.exe
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Generate', 'Validate')]
    [string]$Mode,
    
    [Parameter()]
    [string]$GoBinary,
    
    [Parameter()]
    [string]$CSharpBinary,
    
    [Parameter()]
    [string]$GoldenPath,
    
    [Parameter()]
    [string]$FixturesPath
)

$ScriptRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent (Split-Path -Parent $ScriptRoot)

if (-not $GoldenPath) { $GoldenPath = Join-Path $ScriptRoot "outputs" }
if (-not $FixturesPath) { $FixturesPath = Join-Path $RepoRoot "tests\fixtures" }

# Ensure golden path exists
if (-not (Test-Path $GoldenPath)) {
    New-Item -ItemType Directory -Path $GoldenPath -Force | Out-Null
}

# Test scenarios to capture
$TestScenarios = @(
    @{
        Name = "checkonly_simple_manifest"
        Description = "Basic --checkonly with simple manifest"
        Arguments = @("--checkonly", "--verbose")
        Environment = @{
            CIMIAN_MANIFEST = "simple_manifest.yaml"
            CIMIAN_CATALOGS = "simple_catalog.yaml"
        }
    }
    @{
        Name = "checkonly_conditional_complex"
        Description = "--checkonly with complex conditionals"
        Arguments = @("--checkonly", "--verbose")
        Environment = @{
            CIMIAN_MANIFEST = "conditional_complex.yaml"
            CIMIAN_CATALOGS = "version_variety.yaml"
        }
    }
    @{
        Name = "checkonly_hierarchy"
        Description = "--checkonly with manifest hierarchy"
        Arguments = @("--checkonly", "--verbose")
        Environment = @{
            CIMIAN_MANIFEST = "hierarchy/IT.yaml"
            CIMIAN_CATALOGS = "simple_catalog.yaml"
        }
    }
)

function Save-GoldenFile {
    param(
        [string]$ScenarioName,
        [string]$Output,
        [int]$ExitCode
    )
    
    $goldenFile = Join-Path $GoldenPath "$ScenarioName.golden.txt"
    $metaFile = Join-Path $GoldenPath "$ScenarioName.meta.json"
    
    # Save output
    $Output | Set-Content -Path $goldenFile -NoNewline
    
    # Save metadata
    @{
        GeneratedAt = (Get-Date).ToString("o")
        ExitCode = $ExitCode
        Generator = "Go"
        ScenarioName = $ScenarioName
    } | ConvertTo-Json | Set-Content -Path $metaFile
    
    Write-Host "  Saved: $goldenFile" -ForegroundColor Green
}

function Compare-WithGolden {
    param(
        [string]$ScenarioName,
        [string]$Output,
        [int]$ExitCode
    )
    
    $goldenFile = Join-Path $GoldenPath "$ScenarioName.golden.txt"
    $metaFile = Join-Path $GoldenPath "$ScenarioName.meta.json"
    
    if (-not (Test-Path $goldenFile)) {
        Write-Host "  [SKIP] Golden file not found: $goldenFile" -ForegroundColor Yellow
        return $null
    }
    
    $goldenOutput = Get-Content -Path $goldenFile -Raw
    $meta = Get-Content -Path $metaFile -Raw | ConvertFrom-Json
    
    $outputMatch = $Output -eq $goldenOutput
    $exitCodeMatch = $ExitCode -eq $meta.ExitCode
    
    if ($outputMatch -and $exitCodeMatch) {
        Write-Host "  [PASS] $ScenarioName" -ForegroundColor Green
        return $true
    } else {
        Write-Host "  [FAIL] $ScenarioName" -ForegroundColor Red
        
        if (-not $exitCodeMatch) {
            Write-Host "         Exit code: expected $($meta.ExitCode), got $ExitCode" -ForegroundColor Yellow
        }
        
        if (-not $outputMatch) {
            Write-Host "         Output differs from golden file" -ForegroundColor Yellow
            
            # Save diff for analysis
            $diffFile = Join-Path $GoldenPath "$ScenarioName.diff.txt"
            @"
=== EXPECTED (Golden) ===
$goldenOutput

=== ACTUAL (C#) ===
$Output
"@ | Set-Content -Path $diffFile
            Write-Host "         Diff saved: $diffFile" -ForegroundColor Yellow
        }
        
        return $false
    }
}

function Run-Scenario {
    param(
        [hashtable]$Scenario,
        [string]$Binary
    )
    
    Write-Host "`nScenario: $($Scenario.Description)" -ForegroundColor Cyan
    
    # Set environment variables
    $originalEnv = @{}
    foreach ($key in $Scenario.Environment.Keys) {
        $originalEnv[$key] = [Environment]::GetEnvironmentVariable($key)
        $value = Join-Path $FixturesPath $Scenario.Environment[$key]
        [Environment]::SetEnvironmentVariable($key, $value)
    }
    
    try {
        $tempOutput = [System.IO.Path]::GetTempFileName()
        $tempError = [System.IO.Path]::GetTempFileName()
        
        $process = Start-Process -FilePath $Binary -ArgumentList $Scenario.Arguments `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $tempOutput `
            -RedirectStandardError $tempError
        
        $output = Get-Content $tempOutput -Raw -ErrorAction SilentlyContinue
        $exitCode = $process.ExitCode
        
        # Clean up temp files
        Remove-Item $tempOutput, $tempError -ErrorAction SilentlyContinue
        
        return @{
            Output = $output
            ExitCode = $exitCode
        }
    } finally {
        # Restore environment
        foreach ($key in $originalEnv.Keys) {
            [Environment]::SetEnvironmentVariable($key, $originalEnv[$key])
        }
    }
}

# Main execution
Write-Host @"

╔═══════════════════════════════════════════════════════════╗
║         CIMIAN GOLDEN FILE TEST RUNNER                    ║
║                                                           ║
║  Mode: $Mode                                              
╚═══════════════════════════════════════════════════════════╝
"@ -ForegroundColor Cyan

$passed = 0
$failed = 0
$skipped = 0

foreach ($scenario in $TestScenarios) {
    $binary = if ($Mode -eq 'Generate') { $GoBinary } else { $CSharpBinary }
    
    if (-not $binary -or -not (Test-Path $binary)) {
        Write-Host "Binary not found: $binary" -ForegroundColor Red
        Write-Host "Please specify the binary path." -ForegroundColor Yellow
        $skipped++
        continue
    }
    
    $result = Run-Scenario -Scenario $scenario -Binary $binary
    
    if ($Mode -eq 'Generate') {
        Save-GoldenFile -ScenarioName $scenario.Name -Output $result.Output -ExitCode $result.ExitCode
        $passed++
    } else {
        $compareResult = Compare-WithGolden -ScenarioName $scenario.Name -Output $result.Output -ExitCode $result.ExitCode
        if ($null -eq $compareResult) {
            $skipped++
        } elseif ($compareResult) {
            $passed++
        } else {
            $failed++
        }
    }
}

Write-Host "`n$('=' * 60)" -ForegroundColor Cyan
Write-Host "SUMMARY" -ForegroundColor Cyan
Write-Host "$('=' * 60)" -ForegroundColor Cyan
Write-Host "  Passed:  $passed" -ForegroundColor Green
Write-Host "  Failed:  $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "White" })
Write-Host "  Skipped: $skipped" -ForegroundColor Yellow

if ($failed -gt 0) {
    exit 1
}
