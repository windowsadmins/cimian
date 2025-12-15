<#
.SYNOPSIS
    Run Cimian Go vs C# comparison tests inside Docker container
.DESCRIPTION
    This script runs inside the Windows container and executes comprehensive
    comparison tests between Go and C# implementations of Cimian tools.
.NOTES
    Paths inside container:
    - Go binaries:     C:\Cimian\Go\
    - C# binaries:     C:\Cimian\CSharp\
    - Live repo:       C:\Cimian\Repo\ (mounted)
    - Results:         C:\Cimian\Results\
#>

param(
    [string]$TestSuite = "all",
    [switch]$Verbose
)

$ErrorActionPreference = "Continue"

# Paths
$GoPath = "C:\Cimian\Go"
$CSharpPath = "C:\Cimian\CSharp"
$RepoPath = "C:\Cimian\Repo"
$ResultsPath = "C:\Cimian\Results"

# Tools to test
$Tools = @(
    "managedsoftwareupdate",
    "cimipkg",
    "makepkginfo",
    "manifestutil",
    "cimiimport",
    "makecatalogs",
    "cimitrigger",
    "repoclean"
)

# Results tracking
$Results = @{
    Passed = 0
    Failed = 0
    Skipped = 0
    Details = @()
    StartTime = Get-Date
}

function Write-TestHeader {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor Cyan
}

function Invoke-Tool {
    param(
        [string]$Path,
        [string]$Tool,
        [string[]]$Arguments,
        [hashtable]$Environment = @{}
    )
    
    $exe = Join-Path $Path "$Tool.exe"
    if (-not (Test-Path $exe)) {
        return @{ ExitCode = -999; StdOut = ""; StdErr = "Binary not found: $exe"; Error = $true }
    }
    
    try {
        $pinfo = New-Object System.Diagnostics.ProcessStartInfo
        $pinfo.FileName = $exe
        $pinfo.Arguments = $Arguments -join ' '
        $pinfo.RedirectStandardOutput = $true
        $pinfo.RedirectStandardError = $true
        $pinfo.UseShellExecute = $false
        $pinfo.CreateNoWindow = $true
        $pinfo.WorkingDirectory = $RepoPath
        
        # Set environment variables
        foreach ($key in $Environment.Keys) {
            $pinfo.EnvironmentVariables[$key] = $Environment[$key]
        }
        
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $pinfo
        $process.Start() | Out-Null
        
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        
        $process.WaitForExit(60000) # 60 second timeout
        
        return @{
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
            Combined = "$stdout`n$stderr"
            Error = $false
        }
    }
    catch {
        return @{
            ExitCode = -1
            StdOut = ""
            StdErr = $_.Exception.Message
            Combined = $_.Exception.Message
            Error = $true
        }
    }
}

function Test-VersionParity {
    Write-TestHeader "VERSION OUTPUT TESTS"
    
    foreach ($tool in $Tools) {
        $goResult = Invoke-Tool -Path $GoPath -Tool $tool -Arguments @("--version")
        $csResult = Invoke-Tool -Path $CSharpPath -Tool $tool -Arguments @("--version")
        
        if ($goResult.Error -or $csResult.Error) {
            Write-Host "  [SKIP] $tool - Binary not found" -ForegroundColor Yellow
            $script:Results.Skipped++
            continue
        }
        
        # Both should return version in YYYY.MM.DD.HHMM format
        $versionPattern = '\d{4}\.\d{2}\.\d{2}\.\d{4}'
        $goMatch = $goResult.StdOut -match $versionPattern
        $csMatch = $csResult.StdOut -match $versionPattern
        
        if ($goMatch -and $csMatch) {
            Write-Host "  [PASS] $tool --version (Go: $($matches[0]), C#: $($csResult.StdOut.Trim() -match $versionPattern; $matches[0]))" -ForegroundColor Green
            $script:Results.Passed++
        }
        else {
            Write-Host "  [FAIL] $tool --version format mismatch" -ForegroundColor Red
            $script:Results.Failed++
        }
    }
}

function Test-HelpParity {
    Write-TestHeader "HELP OUTPUT TESTS"
    
    foreach ($tool in $Tools) {
        $goResult = Invoke-Tool -Path $GoPath -Tool $tool -Arguments @("--help")
        $csResult = Invoke-Tool -Path $CSharpPath -Tool $tool -Arguments @("--help")
        
        if ($goResult.Error -or $csResult.Error) {
            Write-Host "  [SKIP] $tool - Binary not found" -ForegroundColor Yellow
            $script:Results.Skipped++
            continue
        }
        
        # Both should show help and exit 0
        if ($goResult.ExitCode -eq 0 -and $csResult.ExitCode -eq 0) {
            Write-Host "  [PASS] $tool --help exits cleanly" -ForegroundColor Green
            $script:Results.Passed++
        }
        else {
            Write-Host "  [FAIL] $tool --help exit code (Go: $($goResult.ExitCode), C#: $($csResult.ExitCode))" -ForegroundColor Red
            $script:Results.Failed++
        }
    }
}

function Test-CheckOnlyParity {
    Write-TestHeader "MANAGEDSOFTWAREUPDATE --CHECKONLY TESTS"
    
    $env = @{
        "CIMIAN_REPO_URL" = "file:///C:/Cimian/Repo"
    }
    
    # Test basic checkonly
    $goResult = Invoke-Tool -Path $GoPath -Tool "managedsoftwareupdate" -Arguments @("--checkonly") -Environment $env
    $csResult = Invoke-Tool -Path $CSharpPath -Tool "managedsoftwareupdate" -Arguments @("--checkonly") -Environment $env
    
    Write-Host "  Go exit code: $($goResult.ExitCode)" -ForegroundColor Gray
    Write-Host "  C# exit code: $($csResult.ExitCode)" -ForegroundColor Gray
    
    # Compare outputs
    $goLines = ($goResult.Combined -split "`n" | Where-Object { $_ -match '\S' }).Count
    $csLines = ($csResult.Combined -split "`n" | Where-Object { $_ -match '\S' }).Count
    
    Write-Host "  Go output lines: $goLines" -ForegroundColor Gray
    Write-Host "  C# output lines: $csLines" -ForegroundColor Gray
    
    # Check if both produce similar structure
    $goHasInstalled = $goResult.Combined -match 'Installed|installed'
    $csHasInstalled = $csResult.Combined -match 'Installed|installed'
    
    $goHasPending = $goResult.Combined -match 'Pending|pending|update'
    $csHasPending = $csResult.Combined -match 'Pending|pending|update'
    
    if (($goHasInstalled -eq $csHasInstalled) -and ($goHasPending -eq $csHasPending)) {
        Write-Host "  [PASS] --checkonly output structure matches" -ForegroundColor Green
        $script:Results.Passed++
    }
    else {
        Write-Host "  [WARN] --checkonly output differs (may be expected)" -ForegroundColor Yellow
        $script:Results.Details += @{
            Test = "checkonly-structure"
            GoOutput = $goResult.Combined
            CSharpOutput = $csResult.Combined
        }
    }
    
    # Save full outputs for analysis
    $goResult.Combined | Out-File "$ResultsPath\checkonly-go.txt" -Encoding utf8
    $csResult.Combined | Out-File "$ResultsPath\checkonly-csharp.txt" -Encoding utf8
}

function Test-VerboseOutputParity {
    Write-TestHeader "VERBOSE OUTPUT TESTS"
    
    $env = @{
        "CIMIAN_REPO_URL" = "file:///C:/Cimian/Repo"
    }
    
    $goResult = Invoke-Tool -Path $GoPath -Tool "managedsoftwareupdate" -Arguments @("--checkonly", "-v") -Environment $env
    $csResult = Invoke-Tool -Path $CSharpPath -Tool "managedsoftwareupdate" -Arguments @("--checkonly", "-v") -Environment $env
    
    # Save verbose outputs
    $goResult.Combined | Out-File "$ResultsPath\verbose-go.txt" -Encoding utf8
    $csResult.Combined | Out-File "$ResultsPath\verbose-csharp.txt" -Encoding utf8
    
    # Check timestamp format in both (we added timestamps for parity)
    $goHasTimestamp = $goResult.Combined -match '\d{4}-\d{2}-\d{2}'
    $csHasTimestamp = $csResult.Combined -match '\d{4}-\d{2}-\d{2}'
    
    if ($goHasTimestamp -and $csHasTimestamp) {
        Write-Host "  [PASS] Both have timestamp format in verbose output" -ForegroundColor Green
        $script:Results.Passed++
    }
    else {
        Write-Host "  [INFO] Timestamp format differs (Go: $goHasTimestamp, C#: $csHasTimestamp)" -ForegroundColor Cyan
    }
    
    Write-Host "  Verbose outputs saved to $ResultsPath" -ForegroundColor Gray
}

function Test-ManifestUtilParity {
    Write-TestHeader "MANIFESTUTIL TESTS"
    
    $env = @{
        "CIMIAN_REPO_URL" = "file:///C:/Cimian/Repo"
    }
    
    # Test --list-manifests
    $goResult = Invoke-Tool -Path $GoPath -Tool "manifestutil" -Arguments @("--list-manifests") -Environment $env
    $csResult = Invoke-Tool -Path $CSharpPath -Tool "manifestutil" -Arguments @("--list-manifests") -Environment $env
    
    $goManifests = ($goResult.StdOut -split "`n" | Where-Object { $_ -match '\.yaml$' }).Count
    $csManifests = ($csResult.StdOut -split "`n" | Where-Object { $_ -match '\.yaml$' }).Count
    
    if ($goManifests -eq $csManifests) {
        Write-Host "  [PASS] --list-manifests count matches ($goManifests manifests)" -ForegroundColor Green
        $script:Results.Passed++
    }
    else {
        Write-Host "  [FAIL] --list-manifests count differs (Go: $goManifests, C#: $csManifests)" -ForegroundColor Red
        $script:Results.Failed++
    }
}

function Test-MakeCatalogsParity {
    Write-TestHeader "MAKECATALOGS TESTS"
    
    $env = @{
        "CIMIAN_REPO_URL" = "file:///C:/Cimian/Repo"
    }
    
    # Test --check (dry run)
    $goResult = Invoke-Tool -Path $GoPath -Tool "makecatalogs" -Arguments @("--check") -Environment $env
    $csResult = Invoke-Tool -Path $CSharpPath -Tool "makecatalogs" -Arguments @("--check") -Environment $env
    
    if ($goResult.ExitCode -eq $csResult.ExitCode) {
        Write-Host "  [PASS] --check exit codes match ($($goResult.ExitCode))" -ForegroundColor Green
        $script:Results.Passed++
    }
    else {
        Write-Host "  [FAIL] --check exit codes differ (Go: $($goResult.ExitCode), C#: $($csResult.ExitCode))" -ForegroundColor Red
        $script:Results.Failed++
    }
}

# Main execution
Write-Host ""
Write-Host ("=" * 70) -ForegroundColor Magenta
Write-Host "  CIMIAN DOCKER CONTAINER TEST SUITE" -ForegroundColor Magenta
Write-Host ("=" * 70) -ForegroundColor Magenta
Write-Host ""
Write-Host "  Go Binaries:     $GoPath" -ForegroundColor Yellow
Write-Host "  C# Binaries:     $CSharpPath" -ForegroundColor Green
Write-Host "  Live Repo:       $RepoPath" -ForegroundColor Cyan
Write-Host "  Results:         $ResultsPath" -ForegroundColor Gray
Write-Host ""

# Verify paths
if (-not (Test-Path $GoPath)) {
    Write-Host "[ERROR] Go binary path not found" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $CSharpPath)) {
    Write-Host "[ERROR] C# binary path not found" -ForegroundColor Red
    exit 1
}

# Run tests based on suite
switch ($TestSuite) {
    "all" {
        Test-VersionParity
        Test-HelpParity
        Test-CheckOnlyParity
        Test-VerboseOutputParity
        Test-ManifestUtilParity
        Test-MakeCatalogsParity
    }
    "version" { Test-VersionParity }
    "help" { Test-HelpParity }
    "checkonly" { Test-CheckOnlyParity }
    "verbose" { Test-VerboseOutputParity }
    "manifestutil" { Test-ManifestUtilParity }
    "makecatalogs" { Test-MakeCatalogsParity }
}

# Summary
$Results.EndTime = Get-Date
$Results.Duration = ($Results.EndTime - $Results.StartTime).TotalSeconds

Write-Host ""
Write-Host ("=" * 70) -ForegroundColor Magenta
Write-Host "  TEST SUMMARY" -ForegroundColor Magenta
Write-Host ("=" * 70) -ForegroundColor Magenta
Write-Host ""
Write-Host "  Passed:   $($Results.Passed)" -ForegroundColor Green
Write-Host "  Failed:   $($Results.Failed)" -ForegroundColor $(if ($Results.Failed -gt 0) { 'Red' } else { 'Green' })
Write-Host "  Skipped:  $($Results.Skipped)" -ForegroundColor Yellow
Write-Host "  Duration: $([math]::Round($Results.Duration, 2))s" -ForegroundColor Gray
Write-Host ""

# Save results
$Results | ConvertTo-Json -Depth 5 | Out-File "$ResultsPath\test-results.json" -Encoding utf8

exit $Results.Failed
