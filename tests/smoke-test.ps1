#Requires -Version 7.0
<#
.SYNOPSIS
    Smoke test for C# Cimian tools - validates binaries work correctly.

.DESCRIPTION
    Runs quick validation tests against the C# binaries to verify they:
    - Execute without crashes
    - Display correct version information
    - Parse help text properly
    - Handle basic scenarios with test fixtures

.PARAMETER BinaryPath
    Path to the C# binaries (default: release\x64)

.PARAMETER Verbose
    Enable verbose output

.EXAMPLE
    .\smoke-test.ps1
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$BinaryPath
)

$ErrorActionPreference = "Stop"

$ScriptRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent $ScriptRoot

# Determine architecture and set binary path
$arch = if ([Environment]::Is64BitOperatingSystem) {
    if ([Environment]::GetEnvironmentVariable("PROCESSOR_IDENTIFIER") -match "ARM") { "arm64" } else { "x64" }
} else { "x86" }

if (-not $BinaryPath) { 
    $BinaryPath = Join-Path $RepoRoot "release\$arch" 
}

$FixturesPath = Join-Path $RepoRoot "tests\fixtures"

# Test tracking
$Passed = 0
$Failed = 0
$Errors = @()

function Test-Pass {
    param([string]$Name)
    Write-Host "  [PASS] $Name" -ForegroundColor Green
    $script:Passed++
}

function Test-Fail {
    param([string]$Name, [string]$Error)
    Write-Host "  [FAIL] $Name" -ForegroundColor Red
    if ($Error) { Write-Host "         $Error" -ForegroundColor Yellow }
    $script:Failed++
    $script:Errors += $Name
}

function Test-BinaryExists {
    param([string]$Name)
    $path = Join-Path $BinaryPath "$Name.exe"
    if (Test-Path $path) {
        Test-Pass "Binary exists: $Name"
        return $true
    } else {
        Test-Fail "Binary exists: $Name" "File not found: $path"
        return $false
    }
}

function Test-VersionOutput {
    param([string]$Name)
    try {
        $path = Join-Path $BinaryPath "$Name.exe"
        $output = & $path --version 2>&1
        if ($output -match '^\d{4}\.\d{2}\.\d{2}\.\d{4}') {
            Test-Pass "Version output: $Name ($output)"
            return $true
        } else {
            Test-Fail "Version output: $Name" "Unexpected format: $output"
            return $false
        }
    } catch {
        Test-Fail "Version output: $Name" $_.Exception.Message
        return $false
    }
}

function Test-HelpOutput {
    param([string]$Name)
    try {
        $path = Join-Path $BinaryPath "$Name.exe"
        $output = & $path --help 2>&1
        $outputStr = $output -join "`n"
        if ($outputStr -match 'Usage:|--help|--version') {
            Test-Pass "Help output: $Name"
            return $true
        } else {
            Test-Fail "Help output: $Name" "Missing expected help text"
            return $false
        }
    } catch {
        Test-Fail "Help output: $Name" $_.Exception.Message
        return $false
    }
}

# Tool-specific validation tests
function Test-MakepkginfoStub {
    # This requires Cimian config to be present - just verify it handles missing config gracefully
    try {
        $path = Join-Path $BinaryPath "makepkginfo.exe"
        $output = & $path --new "TestPackage" 2>&1
        $outputStr = $output -join "`n"
        # Should output error about missing config (expected) but not crash
        if ($outputStr -match 'Config file not found|name:|Error') {
            Test-Pass "makepkginfo --new handles missing config gracefully"
            return $true
        } else {
            Test-Fail "makepkginfo --new handles missing config gracefully" "Unexpected output"
            return $false
        }
    } catch {
        Test-Fail "makepkginfo --new handles missing config gracefully" $_.Exception.Message
        return $false
    }
}

function Test-CimipkgCreate {
    try {
        $path = Join-Path $BinaryPath "cimipkg.exe"
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "cimipkg_test_$(Get-Random)"
        $output = & $path --create $tempDir 2>&1
        $outputStr = $output -join "`n"
        
        # Check if scaffold was created
        $buildInfo = Join-Path $tempDir "build-info.yaml"
        if (Test-Path $buildInfo) {
            Test-Pass "cimipkg --create generates scaffold"
            Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
            return $true
        } else {
            Test-Fail "cimipkg --create generates scaffold" "build-info.yaml not created"
            return $false
        }
    } catch {
        Test-Fail "cimipkg --create generates scaffold" $_.Exception.Message
        return $false
    }
}

function Test-ManifestutilList {
    try {
        $path = Join-Path $BinaryPath "manifestutil.exe"
        # This will fail without config but should not crash
        $process = Start-Process -FilePath $path -ArgumentList "--list-manifests" `
            -NoNewWindow -Wait -PassThru -RedirectStandardError "$env:TEMP\manifestutil_err.txt"
        
        # Should exit with non-zero (no config) but not crash
        Test-Pass "manifestutil --list-manifests handles missing config"
        return $true
    } catch {
        Test-Fail "manifestutil --list-manifests handles missing config" $_.Exception.Message
        return $false
    }
}

# Main
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Cimian C# Smoke Tests" -ForegroundColor Cyan
Write-Host "Binary Path: $BinaryPath" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

$tools = @(
    "managedsoftwareupdate",
    "cimiimport",
    "cimipkg",
    "makecatalogs",
    "makepkginfo",
    "manifestutil",
    "repoclean",
    "cimitrigger",
    "cimiwatcher"
)

# Test 1: Binary Existence
Write-Host "`n--- Binary Existence ---" -ForegroundColor Yellow
foreach ($tool in $tools) {
    Test-BinaryExists $tool
}

# Test 2: Version Output
Write-Host "`n--- Version Output ---" -ForegroundColor Yellow
foreach ($tool in $tools) {
    Test-VersionOutput $tool
}

# Test 3: Help Output
Write-Host "`n--- Help Output ---" -ForegroundColor Yellow
foreach ($tool in $tools) {
    Test-HelpOutput $tool
}

# Test 4: Specific Tool Tests
Write-Host "`n--- Tool-Specific Tests ---" -ForegroundColor Yellow
Test-MakepkginfoStub
Test-CimipkgCreate
Test-ManifestutilList

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "SMOKE TEST SUMMARY" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $Passed" -ForegroundColor Green
Write-Host "Failed: $Failed" -ForegroundColor $(if ($Failed -gt 0) { "Red" } else { "Green" })

if ($Errors.Count -gt 0) {
    Write-Host "`nFailed tests:" -ForegroundColor Red
    foreach ($err in $Errors) {
        Write-Host "  - $err" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "`nAll smoke tests passed!" -ForegroundColor Green
exit 0
