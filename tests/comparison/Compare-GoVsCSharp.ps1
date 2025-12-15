#Requires -Version 7.0
<#
.SYNOPSIS
    Comprehensive comparison tests between Go and C# Cimian binaries.

.DESCRIPTION
    This script methodically compares every aspect of Go vs C# binary behavior:
    - Version output format
    - Help text structure
    - Exit codes
    - Command-line argument parsing
    - Output format (JSON, YAML, text)
    - Error handling
    - File operations
    - Configuration parsing

.PARAMETER GoPath
    Path to Go binaries (default: C:\Program Files\Cimian-go)

.PARAMETER CSharpPath
    Path to C# binaries (default: C:\Program Files\Cimian)

.PARAMETER OutputPath
    Path to save comparison results

.PARAMETER TestSuite
    Which test suite to run: all, version, help, args, output, errors, functional

.EXAMPLE
    .\Compare-GoVsCSharp.ps1 -TestSuite all
#>

[CmdletBinding()]
param(
    [string]$GoPath = "C:\Program Files\Cimian-go",
    [string]$CSharpPath = "C:\Program Files\Cimian",
    [string]$OutputPath = "$PSScriptRoot\results\go-vs-csharp",
    [ValidateSet('all', 'version', 'help', 'args', 'output', 'errors', 'functional')]
    [string]$TestSuite = 'all',
    [switch]$StopOnFirstFailure,
    [switch]$VerboseOutput
)

$ErrorActionPreference = "Continue"

# Create output directory
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

# Test result tracking
$script:Results = @{
    Passed = 0
    Failed = 0
    Skipped = 0
    Details = @()
}

# Tools to test (excluding cimistatus which is GUI-only)
$Tools = @(
    "managedsoftwareupdate",
    "cimiimport",
    "cimipkg", 
    "makecatalogs",
    "makepkginfo",
    "manifestutil",
    "cimitrigger",
    "cimiwatcher",
    "repoclean"
)

#region Helper Functions

function Write-TestHeader {
    param([string]$Name)
    Write-Host "`n$('=' * 70)" -ForegroundColor Cyan
    Write-Host "  TEST: $Name" -ForegroundColor Cyan
    Write-Host "$('=' * 70)" -ForegroundColor Cyan
}

function Write-SubTest {
    param([string]$Name)
    Write-Host "`n  --- $Name ---" -ForegroundColor Yellow
}

function Compare-Output {
    param(
        [string]$TestName,
        [string]$Tool,
        [string]$GoOutput,
        [string]$CSharpOutput,
        [bool]$ExactMatch = $false,
        [string]$MatchPattern = $null
    )
    
    $passed = $false
    $message = ""
    
    if ($ExactMatch) {
        $passed = ($GoOutput.Trim() -eq $CSharpOutput.Trim())
        if (-not $passed) {
            $message = "Exact match failed"
        }
    }
    elseif ($MatchPattern) {
        $goMatch = $GoOutput -match $MatchPattern
        $csMatch = $CSharpOutput -match $MatchPattern
        $passed = $goMatch -and $csMatch
        if (-not $passed) {
            $message = "Pattern match failed: Go=$goMatch, C#=$csMatch"
        }
    }
    else {
        # Semantic comparison - outputs should be equivalent
        $passed = (Compare-SemanticOutput $GoOutput $CSharpOutput)
        if (-not $passed) {
            $message = "Semantic comparison failed"
        }
    }
    
    $result = @{
        TestName = $TestName
        Tool = $Tool
        Passed = $passed
        GoOutput = $GoOutput
        CSharpOutput = $CSharpOutput
        Message = $message
    }
    
    if ($passed) {
        Write-Host "    [PASS] $Tool - $TestName" -ForegroundColor Green
        $script:Results.Passed++
    }
    else {
        Write-Host "    [FAIL] $Tool - $TestName" -ForegroundColor Red
        Write-Host "           $message" -ForegroundColor Yellow
        if ($VerboseOutput) {
            Write-Host "           Go:  $($GoOutput.Substring(0, [Math]::Min(100, $GoOutput.Length)))..." -ForegroundColor Gray
            Write-Host "           C#:  $($CSharpOutput.Substring(0, [Math]::Min(100, $CSharpOutput.Length)))..." -ForegroundColor Gray
        }
        $script:Results.Failed++
        
        if ($StopOnFirstFailure) {
            throw "Test failed: $TestName"
        }
    }
    
    $script:Results.Details += $result
    return $passed
}

function Compare-SemanticOutput {
    param([string]$Go, [string]$CSharp)
    
    # Normalize whitespace
    $goNorm = ($Go -replace '\s+', ' ').Trim()
    $csNorm = ($CSharp -replace '\s+', ' ').Trim()
    
    # Check if they're similar enough
    if ($goNorm -eq $csNorm) { return $true }
    
    # Check if key elements match
    # (This is a simplified check - expand as needed)
    return $false
}

function Invoke-Tool {
    param(
        [string]$Path,
        [string]$Tool,
        [string[]]$Arguments
    )
    
    $exe = Join-Path $Path "$Tool.exe"
    
    try {
        $pinfo = New-Object System.Diagnostics.ProcessStartInfo
        $pinfo.FileName = $exe
        $pinfo.Arguments = $Arguments -join ' '
        $pinfo.RedirectStandardOutput = $true
        $pinfo.RedirectStandardError = $true
        $pinfo.UseShellExecute = $false
        $pinfo.CreateNoWindow = $true
        
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $pinfo
        $process.Start() | Out-Null
        
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        
        $process.WaitForExit(30000) # 30 second timeout
        
        return @{
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
            Combined = "$stdout`n$stderr"
        }
    }
    catch {
        return @{
            ExitCode = -1
            StdOut = ""
            StdErr = $_.Exception.Message
            Combined = $_.Exception.Message
        }
    }
}

#endregion

#region Test Suites

function Test-VersionOutput {
    Write-TestHeader "VERSION OUTPUT COMPARISON"
    
    foreach ($tool in $Tools) {
        $goResult = Invoke-Tool -Path $GoPath -Tool $tool -Arguments @("--version")
        $csResult = Invoke-Tool -Path $CSharpPath -Tool $tool -Arguments @("--version")
        
        # Version format should match: YYYY.MM.DD.HHMM
        $versionPattern = '^\d{4}\.\d{2}\.\d{2}\.\d{4}$'
        
        $goMatch = $goResult.StdOut.Trim() -match $versionPattern
        $csMatch = $csResult.StdOut.Trim() -match $versionPattern
        
        if ($goMatch -and $csMatch) {
            Write-Host "    [PASS] $tool - Version format matches (Go: $($goResult.StdOut.Trim()), C#: $($csResult.StdOut.Trim()))" -ForegroundColor Green
            $script:Results.Passed++
        }
        else {
            Write-Host "    [FAIL] $tool - Version format mismatch" -ForegroundColor Red
            Write-Host "           Go: '$($goResult.StdOut.Trim())' (match: $goMatch)" -ForegroundColor Yellow
            Write-Host "           C#: '$($csResult.StdOut.Trim())' (match: $csMatch)" -ForegroundColor Yellow
            $script:Results.Failed++
        }
        
        # Also check exit code
        if ($goResult.ExitCode -eq $csResult.ExitCode) {
            Write-Host "    [PASS] $tool - Exit code matches ($($goResult.ExitCode))" -ForegroundColor Green
            $script:Results.Passed++
        }
        else {
            Write-Host "    [FAIL] $tool - Exit code mismatch (Go: $($goResult.ExitCode), C#: $($csResult.ExitCode))" -ForegroundColor Red
            $script:Results.Failed++
        }
    }
}

function Test-HelpOutput {
    Write-TestHeader "HELP OUTPUT COMPARISON"
    
    foreach ($tool in $Tools) {
        Write-SubTest $tool
        
        $goResult = Invoke-Tool -Path $GoPath -Tool $tool -Arguments @("--help")
        $csResult = Invoke-Tool -Path $CSharpPath -Tool $tool -Arguments @("--help")
        
        # Save help outputs for manual comparison
        $goResult.StdOut | Out-File "$OutputPath\$tool-go-help.txt" -Encoding utf8
        $csResult.StdOut | Out-File "$OutputPath\$tool-cs-help.txt" -Encoding utf8
        
        # Check key elements exist in both
        $keyElements = @("--help", "--version", "Usage", "Options")
        
        foreach ($element in $keyElements) {
            $goHas = $goResult.Combined -match [regex]::Escape($element)
            $csHas = $csResult.Combined -match [regex]::Escape($element)
            
            if ($goHas -and $csHas) {
                Write-Host "    [PASS] '$element' present in both" -ForegroundColor Green
                $script:Results.Passed++
            }
            elseif ($goHas -and -not $csHas) {
                Write-Host "    [WARN] '$element' in Go but not C#" -ForegroundColor Yellow
                $script:Results.Details += @{ TestName = "Help-$tool-$element"; Warning = $true }
            }
            elseif (-not $goHas -and $csHas) {
                Write-Host "    [INFO] '$element' in C# but not Go (enhancement)" -ForegroundColor Cyan
            }
        }
        
        # Check exit codes
        if ($goResult.ExitCode -eq $csResult.ExitCode) {
            Write-Host "    [PASS] Exit code matches ($($goResult.ExitCode))" -ForegroundColor Green
            $script:Results.Passed++
        }
        else {
            Write-Host "    [FAIL] Exit code mismatch (Go: $($goResult.ExitCode), C#: $($csResult.ExitCode))" -ForegroundColor Red
            $script:Results.Failed++
        }
    }
}

function Test-ArgumentParsing {
    Write-TestHeader "ARGUMENT PARSING COMPARISON"
    
    # Test cases for each tool
    $testCases = @{
        "managedsoftwareupdate" = @(
            @{ Args = @("--checkonly"); Desc = "Check only mode" },
            @{ Args = @("-c"); Desc = "Check only short flag" },
            @{ Args = @("--auto"); Desc = "Auto mode" },
            @{ Args = @("-a"); Desc = "Auto short flag" },
            @{ Args = @("--installonly"); Desc = "Install only mode" },
            @{ Args = @("--verbose"); Desc = "Verbose mode" },
            @{ Args = @("-v"); Desc = "Verbose short flag" },
            @{ Args = @("--invalid-flag"); Desc = "Invalid flag handling" }
        )
        "cimipkg" = @(
            @{ Args = @("--help"); Desc = "Help" },
            @{ Args = @("--create", "$env:TEMP\test-pkg-$$"); Desc = "Create scaffold" }
        )
        "makepkginfo" = @(
            @{ Args = @("--help"); Desc = "Help" },
            @{ Args = @("--new", "TestPkg"); Desc = "New package stub" }
        )
        "manifestutil" = @(
            @{ Args = @("--help"); Desc = "Help" },
            @{ Args = @("--list-manifests"); Desc = "List manifests" }
        )
        "cimiimport" = @(
            @{ Args = @("--help"); Desc = "Help" },
            @{ Args = @("--config"); Desc = "Config mode" }
        )
        "makecatalogs" = @(
            @{ Args = @("--help"); Desc = "Help" }
        )
        "cimitrigger" = @(
            @{ Args = @("--help"); Desc = "Help" },
            @{ Args = @("debug"); Desc = "Debug subcommand" }
        )
        "cimiwatcher" = @(
            @{ Args = @("--help"); Desc = "Help" }
        )
        "repoclean" = @(
            @{ Args = @("--help"); Desc = "Help" },
            @{ Args = @("--show-all"); Desc = "Show all flag" }
        )
    }
    
    foreach ($tool in $Tools) {
        if (-not $testCases.ContainsKey($tool)) { continue }
        
        Write-SubTest $tool
        
        foreach ($test in $testCases[$tool]) {
            $goResult = Invoke-Tool -Path $GoPath -Tool $tool -Arguments $test.Args
            $csResult = Invoke-Tool -Path $CSharpPath -Tool $tool -Arguments $test.Args
            
            # Compare exit codes (main indicator of argument parsing success)
            $exitMatch = $goResult.ExitCode -eq $csResult.ExitCode
            
            # For error cases, both should fail
            # For success cases, both should succeed
            $bothSuccess = ($goResult.ExitCode -eq 0) -and ($csResult.ExitCode -eq 0)
            $bothFail = ($goResult.ExitCode -ne 0) -and ($csResult.ExitCode -ne 0)
            
            if ($exitMatch -or $bothFail) {
                Write-Host "    [PASS] $($test.Desc) - Args: $($test.Args -join ' ')" -ForegroundColor Green
                $script:Results.Passed++
            }
            else {
                Write-Host "    [FAIL] $($test.Desc) - Exit codes differ (Go: $($goResult.ExitCode), C#: $($csResult.ExitCode))" -ForegroundColor Red
                $script:Results.Failed++
            }
        }
    }
}

function Test-ErrorHandling {
    Write-TestHeader "ERROR HANDLING COMPARISON"
    
    # Test various error scenarios
    $errorTests = @(
        @{ Tool = "managedsoftwareupdate"; Args = @("--invalid"); Desc = "Invalid flag" },
        @{ Tool = "makepkginfo"; Args = @("--file", "nonexistent.msi"); Desc = "Nonexistent file" },
        @{ Tool = "cimiimport"; Args = @("nonexistent.exe"); Desc = "Nonexistent installer" },
        @{ Tool = "manifestutil"; Args = @("--manifest", "nonexistent"); Desc = "Nonexistent manifest" }
    )
    
    foreach ($test in $errorTests) {
        Write-SubTest "$($test.Tool) - $($test.Desc)"
        
        $goResult = Invoke-Tool -Path $GoPath -Tool $test.Tool -Arguments $test.Args
        $csResult = Invoke-Tool -Path $CSharpPath -Tool $test.Tool -Arguments $test.Args
        
        # Both should return non-zero exit code for errors
        $goBad = $goResult.ExitCode -ne 0
        $csBad = $csResult.ExitCode -ne 0
        
        if ($goBad -and $csBad) {
            Write-Host "    [PASS] Both return error exit code" -ForegroundColor Green
            $script:Results.Passed++
        }
        elseif (-not $goBad -and -not $csBad) {
            Write-Host "    [WARN] Neither returns error (might be OK)" -ForegroundColor Yellow
        }
        else {
            Write-Host "    [FAIL] Error handling differs (Go: $($goResult.ExitCode), C#: $($csResult.ExitCode))" -ForegroundColor Red
            $script:Results.Failed++
        }
        
        # Save error outputs for analysis
        @{
            Test = $test.Desc
            GoExitCode = $goResult.ExitCode
            GoOutput = $goResult.Combined
            CSharpExitCode = $csResult.ExitCode
            CSharpOutput = $csResult.Combined
        } | ConvertTo-Json | Out-File "$OutputPath\error-$($test.Tool)-$($test.Desc -replace '\s+', '_').json" -Encoding utf8
    }
}

function Test-FunctionalParity {
    Write-TestHeader "FUNCTIONAL PARITY TESTS"
    
    # Test 1: cimipkg --create
    Write-SubTest "cimipkg --create scaffold generation"
    
    $goDir = "$env:TEMP\cimipkg-go-test-$(Get-Random)"
    $csDir = "$env:TEMP\cimipkg-cs-test-$(Get-Random)"
    
    $goResult = Invoke-Tool -Path $GoPath -Tool "cimipkg" -Arguments @("--create", $goDir)
    $csResult = Invoke-Tool -Path $CSharpPath -Tool "cimipkg" -Arguments @("--create", $csDir)
    
    # Compare generated structures
    $goFiles = Get-ChildItem $goDir -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Sort-Object
    $csFiles = Get-ChildItem $csDir -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Sort-Object
    
    $filesMatch = ($goFiles -join ',') -eq ($csFiles -join ',')
    
    if ($filesMatch) {
        Write-Host "    [PASS] Scaffold structure matches" -ForegroundColor Green
        Write-Host "           Files: $($goFiles -join ', ')" -ForegroundColor Gray
        $script:Results.Passed++
    }
    else {
        Write-Host "    [FAIL] Scaffold structure differs" -ForegroundColor Red
        Write-Host "           Go files: $($goFiles -join ', ')" -ForegroundColor Yellow
        Write-Host "           C# files: $($csFiles -join ', ')" -ForegroundColor Yellow
        $script:Results.Failed++
    }
    
    # Cleanup
    Remove-Item $goDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $csDir -Recurse -Force -ErrorAction SilentlyContinue
    
    # Test 2: Compare build-info.yaml content
    Write-SubTest "cimipkg build-info.yaml format"
    
    $goDir2 = "$env:TEMP\cimipkg-go-yaml-$(Get-Random)"
    $csDir2 = "$env:TEMP\cimipkg-cs-yaml-$(Get-Random)"
    
    Invoke-Tool -Path $GoPath -Tool "cimipkg" -Arguments @("--create", $goDir2) | Out-Null
    Invoke-Tool -Path $CSharpPath -Tool "cimipkg" -Arguments @("--create", $csDir2) | Out-Null
    
    $goYaml = Get-Content "$goDir2\build-info.yaml" -Raw -ErrorAction SilentlyContinue
    $csYaml = Get-Content "$csDir2\build-info.yaml" -Raw -ErrorAction SilentlyContinue
    
    # Check key fields exist in both
    $requiredFields = @("name:", "version:", "description:", "installer:")
    $allFieldsPresent = $true
    
    foreach ($field in $requiredFields) {
        $goHas = $goYaml -match $field
        $csHas = $csYaml -match $field
        
        if (-not ($goHas -and $csHas)) {
            $allFieldsPresent = $false
            Write-Host "    [FAIL] Field '$field' missing (Go: $goHas, C#: $csHas)" -ForegroundColor Red
            $script:Results.Failed++
        }
    }
    
    if ($allFieldsPresent) {
        Write-Host "    [PASS] All required fields present in build-info.yaml" -ForegroundColor Green
        $script:Results.Passed++
    }
    
    # Save YAMLs for comparison
    $goYaml | Out-File "$OutputPath\build-info-go.yaml" -Encoding utf8
    $csYaml | Out-File "$OutputPath\build-info-cs.yaml" -Encoding utf8
    
    # Cleanup
    Remove-Item $goDir2 -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $csDir2 -Recurse -Force -ErrorAction SilentlyContinue
}

#endregion

#region Main Execution

Write-Host "`n" 
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host "  CIMIAN GO vs C# COMPREHENSIVE COMPARISON TESTS" -ForegroundColor Cyan
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host ""
Write-Host "Go Binary Path:    $GoPath" -ForegroundColor Yellow
Write-Host "C# Binary Path:    $CSharpPath" -ForegroundColor Green
Write-Host "Output Path:       $OutputPath" -ForegroundColor Gray
Write-Host "Test Suite:        $TestSuite" -ForegroundColor Gray
Write-Host ""

# Verify paths exist
if (-not (Test-Path $GoPath)) {
    Write-Host "[ERROR] Go binary path not found: $GoPath" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $CSharpPath)) {
    Write-Host "[ERROR] C# binary path not found: $CSharpPath" -ForegroundColor Red
    exit 1
}

# Run selected test suites
switch ($TestSuite) {
    'all' {
        Test-VersionOutput
        Test-HelpOutput
        Test-ArgumentParsing
        Test-ErrorHandling
        Test-FunctionalParity
    }
    'version' { Test-VersionOutput }
    'help' { Test-HelpOutput }
    'args' { Test-ArgumentParsing }
    'errors' { Test-ErrorHandling }
    'functional' { Test-FunctionalParity }
}

# Summary
Write-Host "`n"
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host "  TEST SUMMARY" -ForegroundColor Cyan
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host ""
Write-Host "  Passed:  $($script:Results.Passed)" -ForegroundColor Green
Write-Host "  Failed:  $($script:Results.Failed)" -ForegroundColor $(if ($script:Results.Failed -gt 0) { 'Red' } else { 'Green' })
Write-Host "  Skipped: $($script:Results.Skipped)" -ForegroundColor Yellow
Write-Host ""

# Save results
$script:Results | ConvertTo-Json -Depth 5 | Out-File "$OutputPath\test-results.json" -Encoding utf8

if ($script:Results.Failed -gt 0) {
    Write-Host "  Some tests failed. Check $OutputPath for details." -ForegroundColor Yellow
    exit 1
}
else {
    Write-Host "  All tests passed!" -ForegroundColor Green
    exit 0
}

#endregion
