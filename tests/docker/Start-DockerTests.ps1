<#
.SYNOPSIS
    Build and run Cimian Docker test container
.DESCRIPTION
    This script builds a Windows container with both Go and C# Cimian binaries,
    mounts the live deployment repo, and runs comparison tests.
.EXAMPLE
    .\Start-DockerTests.ps1
    # Build container and run all tests
.EXAMPLE
    .\Start-DockerTests.ps1 -TestSuite checkonly -Interactive
    # Run specific test suite interactively
.EXAMPLE
    .\Start-DockerTests.ps1 -Shell
    # Start container with interactive shell for manual testing
#>

param(
    [string]$TestSuite = "all",
    [switch]$Interactive,
    [switch]$Shell,
    [switch]$NoBuild,
    [switch]$CleanResults,
    [string]$GoPath = "C:\Program Files\Cimian",
    [string]$CSharpPath = "",
    [string]$RepoPath = "$env:USERPROFILE\DevOps\Cimian\deployment"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DockerDir = $ScriptDir
$ProjectRoot = (Resolve-Path "$ScriptDir\..\..").Path

# Auto-detect C# binary path
if (-not $CSharpPath) {
    $buildPath = Join-Path $ProjectRoot "build\nupkg\x64"
    if (Test-Path $buildPath) {
        $CSharpPath = $buildPath
    }
    else {
        Write-Host "[ERROR] C# binaries not found. Run build.ps1 first." -ForegroundColor Red
        exit 1
    }
}

$ImageName = "cimian-test"
$ContainerName = "cimian-test-container"
$ResultsDir = Join-Path $DockerDir "results"

Write-Host ""
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host "  CIMIAN DOCKER TEST ORCHESTRATOR" -ForegroundColor Cyan
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host ""
Write-Host "  Go Binaries:     $GoPath" -ForegroundColor Yellow
Write-Host "  C# Binaries:     $CSharpPath" -ForegroundColor Green
Write-Host "  Live Repo:       $RepoPath" -ForegroundColor Cyan
Write-Host "  Results Dir:     $ResultsDir" -ForegroundColor Gray
Write-Host ""

# Verify paths
if (-not (Test-Path $GoPath)) {
    Write-Host "[ERROR] Go binary path not found: $GoPath" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $CSharpPath)) {
    Write-Host "[ERROR] C# binary path not found: $CSharpPath" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $RepoPath)) {
    Write-Host "[ERROR] Live repo path not found: $RepoPath" -ForegroundColor Red
    exit 1
}

# Clean results if requested
if ($CleanResults -and (Test-Path $ResultsDir)) {
    Remove-Item $ResultsDir -Recurse -Force
}
New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null

# Prepare staging directory for Docker build
$StagingDir = Join-Path $env:TEMP "cimian-docker-staging-$(Get-Random)"
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

Write-Host "[1/4] Staging files for Docker build..." -ForegroundColor Yellow

# Copy Dockerfile
Copy-Item "$DockerDir\Dockerfile" $StagingDir

# Stage Go binaries
$GoStaging = Join-Path $StagingDir "go-binaries"
New-Item -ItemType Directory -Path $GoStaging -Force | Out-Null
Copy-Item "$GoPath\*.exe" $GoStaging -ErrorAction SilentlyContinue

# Stage C# binaries
$CSharpStaging = Join-Path $StagingDir "csharp-binaries"
New-Item -ItemType Directory -Path $CSharpStaging -Force | Out-Null
Copy-Item "$CSharpPath\*.exe" $CSharpStaging -ErrorAction SilentlyContinue

# Stage test scripts
$TestScriptsStaging = Join-Path $StagingDir "test-scripts"
New-Item -ItemType Directory -Path $TestScriptsStaging -Force | Out-Null
Copy-Item "$DockerDir\test-scripts\*.ps1" $TestScriptsStaging

Write-Host "  Go binaries: $((Get-ChildItem $GoStaging -Filter *.exe).Count) files" -ForegroundColor Gray
Write-Host "  C# binaries: $((Get-ChildItem $CSharpStaging -Filter *.exe).Count) files" -ForegroundColor Gray

if (-not $NoBuild) {
    Write-Host ""
    Write-Host "[2/4] Building Docker image..." -ForegroundColor Yellow
    
    Push-Location $StagingDir
    try {
        docker build -t $ImageName .
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[ERROR] Docker build failed" -ForegroundColor Red
            exit 1
        }
    }
    finally {
        Pop-Location
    }
}

# Remove existing container if any
docker rm -f $ContainerName 2>$null | Out-Null

Write-Host ""
Write-Host "[3/4] Starting container..." -ForegroundColor Yellow

# Build docker run command
$dockerArgs = @(
    "run"
    "--name", $ContainerName
)

# Mount live repo (read-only for safety)
$dockerArgs += @("-v", "${RepoPath}:C:\Cimian\Repo:ro")

# Mount results directory
$dockerArgs += @("-v", "${ResultsDir}:C:\Cimian\Results")

if ($Shell) {
    Write-Host ""
    Write-Host "Starting interactive shell in container..." -ForegroundColor Cyan
    Write-Host "  - Go binaries at:     C:\Cimian\Go\" -ForegroundColor Gray
    Write-Host "  - C# binaries at:     C:\Cimian\CSharp\" -ForegroundColor Gray
    Write-Host "  - Live repo at:       C:\Cimian\Repo\" -ForegroundColor Gray
    Write-Host "  - Results at:         C:\Cimian\Results\" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Type 'exit' to leave the container" -ForegroundColor Yellow
    Write-Host ""
    
    $dockerArgs += @("-it", $ImageName, "powershell")
    docker @dockerArgs
}
elseif ($Interactive) {
    Write-Host ""
    Write-Host "Running tests interactively..." -ForegroundColor Cyan
    
    $dockerArgs += @("-it", $ImageName, "powershell", "-File", "C:\Cimian\Tests\Run-DockerTests.ps1", "-TestSuite", $TestSuite)
    docker @dockerArgs
}
else {
    Write-Host ""
    Write-Host "[4/4] Running tests..." -ForegroundColor Yellow
    
    $dockerArgs += @($ImageName, "powershell", "-File", "C:\Cimian\Tests\Run-DockerTests.ps1", "-TestSuite", $TestSuite)
    docker @dockerArgs
    $exitCode = $LASTEXITCODE
    
    Write-Host ""
    Write-Host "=" * 70 -ForegroundColor Cyan
    Write-Host "  RESULTS" -ForegroundColor Cyan
    Write-Host "=" * 70 -ForegroundColor Cyan
    Write-Host ""
    
    # Show results
    $resultsFile = Join-Path $ResultsDir "test-results.json"
    if (Test-Path $resultsFile) {
        $results = Get-Content $resultsFile | ConvertFrom-Json
        Write-Host "  Passed:   $($results.Passed)" -ForegroundColor Green
        Write-Host "  Failed:   $($results.Failed)" -ForegroundColor $(if ($results.Failed -gt 0) { 'Red' } else { 'Green' })
        Write-Host "  Skipped:  $($results.Skipped)" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  Full results: $ResultsDir" -ForegroundColor Gray
    }
    
    # List output files
    Write-Host ""
    Write-Host "  Output files:" -ForegroundColor Gray
    Get-ChildItem $ResultsDir | ForEach-Object {
        Write-Host "    - $($_.Name)" -ForegroundColor Gray
    }
}

# Cleanup staging
Remove-Item $StagingDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
