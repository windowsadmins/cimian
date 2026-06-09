# ReportMateCache.psm1
#
# Shared PowerShell helpers for Cimian pkginfos (installcheck_script, postinstall_script)
# that need to consume ReportMate's local module cache on disk.
#
# ReportMate writes each collection run to a timestamped folder:
#   C:\ProgramData\ManagedReports\cache\<yyyy-MM-dd-HHmmss>\<module>.json
#
# Pkginfos should NEVER read a hardcoded filename — always use Get-LatestReportMateCacheDir
# to resolve the most recent run, since older runs are pruned by ReportMate's cleanup
# (keeps last 24h + min 5 dirs).
#
# Usage in a pkginfo installcheck_script:
#
#   Import-Module "$env:ProgramFiles\Cimian\scripts\ReportMateCache.psm1" -Force
#   $sec = Get-ReportMateModule -Module security -MaxAgeHours 6
#   if (-not $sec) { Write-Host "ReportMate cache missing or stale"; exit 1 }
#   if ($sec.secureBoot.isEnabled) { exit 1 }   # already done
#   exit 0                                       # remediation needed

$script:CacheBase = 'C:\ProgramData\ManagedReports\cache'

<#
.SYNOPSIS
    Returns the most recent timestamped cache directory, or $null if none exists.

.DESCRIPTION
    ReportMate names cache directories yyyy-MM-dd-HHmmss (lexicographically sortable).
    This function returns a System.IO.DirectoryInfo for the latest one, or $null if:
      - The base C:\ProgramData\ManagedReports\cache directory doesn't exist
      - No directory inside it matches the timestamp pattern
#>
function Get-LatestReportMateCacheDir {
    [CmdletBinding()]
    param()

    if (-not (Test-Path -LiteralPath $script:CacheBase -PathType Container)) {
        return $null
    }

    $latest = Get-ChildItem -LiteralPath $script:CacheBase -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}-\d{6}$' } |
        Sort-Object Name -Descending |
        Select-Object -First 1

    return $latest
}

<#
.SYNOPSIS
    Reads and parses a ReportMate module's most recent cache file, optionally enforcing freshness.

.PARAMETER Module
    Module name without extension (security, hardware, inventory, installs, network, etc.)

.PARAMETER MaxAgeHours
    If specified, returns $null when the cache's collectedAt timestamp is older than this.
    Pass 0 or negative to skip the freshness check entirely.

.OUTPUTS
    Parsed PSCustomObject from the JSON, or $null when:
      - No cache directory exists yet (new device)
      - The module file isn't present in the latest directory
      - JSON parse fails
      - Freshness check fails

.NOTES
    Pkginfos should treat $null as "skip, retry next cycle" — not as a hard failure,
    since brand-new devices may not have run ReportMate yet.
#>
function Get-ReportMateModule {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Module,

        [int]$MaxAgeHours = 6
    )

    $dir = Get-LatestReportMateCacheDir
    if (-not $dir) { return $null }

    $path = Join-Path $dir.FullName "$Module.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }

    try {
        $raw = Get-Content -LiteralPath $path -Raw -ErrorAction Stop
        $data = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-Verbose "ReportMateCache: failed to parse $path - $_"
        return $null
    }

    if ($MaxAgeHours -gt 0 -and $data.collectedAt) {
        try {
            $collected = [DateTime]::Parse($data.collectedAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
            $ageHours = ([DateTime]::UtcNow - $collected).TotalHours
            if ($ageHours -gt $MaxAgeHours) {
                Write-Verbose "ReportMateCache: $Module is $([Math]::Round($ageHours,1))h old (limit $MaxAgeHours h)"
                return $null
            }
        } catch {
            Write-Verbose "ReportMateCache: could not parse collectedAt '$($data.collectedAt)' - returning cache anyway"
        }
    }

    return $data
}

<#
.SYNOPSIS
    Returns $true when the latest cache for $Module exists and is no older than $MaxAgeHours.

.DESCRIPTION
    Convenience wrapper for installcheck scripts that just want to gate on freshness
    without needing the parsed data themselves.
#>
function Test-ReportMateCacheFresh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Module,

        [int]$MaxAgeHours = 6
    )

    return $null -ne (Get-ReportMateModule -Module $Module -MaxAgeHours $MaxAgeHours)
}

Export-ModuleMember -Function Get-LatestReportMateCacheDir, Get-ReportMateModule, Test-ReportMateCacheFresh
