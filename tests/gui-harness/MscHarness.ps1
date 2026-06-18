#requires -Version 7
<#
.SYNOPSIS
  Headless driver for Managed Software Center (MSC) self-service flows.

.DESCRIPTION
  The MSC window is a thin shell. Every button does exactly two things:
    1. mutate  C:\ProgramData\ManagedInstalls\SelfServeManifest.yaml
    2. run the engine (managedsoftwareupdate) with a --item filter
  ...and then reads the result back from InstallInfo.yaml.

  This module reproduces that contract so any GUI interaction can be exercised
  and asserted from the terminal -- no WinUI window, no clicking, no waiting on
  the 10s CimianWatcher poll. Plan mode (--checkonly) verifies the *decision*
  (will-be-installed / will-be-removed) in seconds without touching the system;
  -Apply performs the real install/removal only when you ask for it.

  Canonical contract (do not duplicate logic -- mirror it):
    GUI mutation:  shared/core/Services/SelfServiceManifestService.cs
    GUI trigger:   gui/ManagedSoftwareCenter/Services/TriggerService.cs
    Flag args:     shared/core/Services/BootstrapArgsBuilder.cs
    Engine write:  cli/managedsoftwareupdate/Services/UpdateEngine.cs (WriteInstallInfo)
    Paths:         shared/core/Services/CimianPaths.cs

.EXAMPLE
  . .\MscHarness.ps1            # dot-source to load the functions
  Show-MscState                # what the Updates tab would show right now
  Invoke-MscInstall Blender    # plan: mimic clicking Install on Blender (safe, no install)
  Invoke-MscInstall Blender -Apply   # actually install it
  Invoke-MscRemove VLC         # plan: mimic clicking Remove on VLC
  Clear-MscStuck Gimp          # unstick a wedged pending item
  Invoke-MscScenario Blender   # full install->assert->remove->assert round trip (plan mode)

.EXAMPLE
  # One-shot CLI form (no dot-sourcing):
  pwsh .\MscHarness.ps1 state
  pwsh .\MscHarness.ps1 install Blender
  pwsh .\MscHarness.ps1 remove VLC -Apply
  pwsh .\MscHarness.ps1 clear Gimp
#>

# ----------------------------------------------------------------------------
# Paths -- keep in sync with shared/core/Services/CimianPaths.cs
# ----------------------------------------------------------------------------
$script:DataDir     = 'C:\ProgramData\ManagedInstalls'
$script:SelfServe   = Join-Path $script:DataDir 'SelfServeManifest.yaml'
$script:InstallInfo = Join-Path $script:DataDir 'InstallInfo.yaml'
$script:FlagFile    = Join-Path $script:DataDir '.cimian.bootstrap'
$script:StateJson   = Join-Path $script:DataDir 'reports\state.json'
$script:Msu         = 'C:\Program Files\Cimian\managedsoftwareupdate.exe'

Import-Module powershell-yaml -ErrorAction Stop

function Test-Elevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

# ----------------------------------------------------------------------------
# Engine runner -- the only thing that needs elevation
# ----------------------------------------------------------------------------
function Invoke-Msu {
    <#  Runs managedsoftwareupdate with the given args, captures output + exit code.
        Mirrors how CimianWatcher launches the engine, minus the service hop. #>
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments)] [string[]] $MsuArgs)

    if (-not (Test-Path $script:Msu)) { throw "managedsoftwareupdate not found at $script:Msu" }

    Write-Host "  > managedsoftwareupdate $($MsuArgs -join ' ')" -ForegroundColor DarkGray
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if (Test-Elevated) {
        $out = & $script:Msu @MsuArgs 2>&1 | Out-String
    } else {
        $out = & sudo $script:Msu @MsuArgs 2>&1 | Out-String
    }
    $sw.Stop()
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "  engine exit=$code -- last 20 lines:" -ForegroundColor Red
        ($out -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 20) |
            ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
    }
    [pscustomobject]@{
        ExitCode = $code
        Seconds  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Output   = $out
    }
}

# ----------------------------------------------------------------------------
# SelfServeManifest.yaml -- read / mutate / write
# Mirrors SelfServiceManifestService.{AddInstallRequest,AddRemovalRequest,RemoveRequest}Async
# ----------------------------------------------------------------------------
function Get-MscSelfServe {
    if (-not (Test-Path $script:SelfServe)) {
        return [pscustomobject]@{ ManagedInstalls=@(); ManagedUninstalls=@(); OptionalInstalls=@() }
    }
    $y = Get-Content $script:SelfServe -Raw | ConvertFrom-Yaml
    [pscustomobject]@{
        ManagedInstalls   = @($y.managed_installs)   | Where-Object { $_ }
        ManagedUninstalls = @($y.managed_uninstalls) | Where-Object { $_ }
        OptionalInstalls  = @($y.optional_installs)  | Where-Object { $_ }
    }
}

function Set-MscSelfServe {
    param([Parameter(Mandatory)] $Manifest)
    # Format manually to match the on-disk style the C# service writes
    # (list items flush-left, empty lists as []).
    $sb = [Text.StringBuilder]::new()
    [void]$sb.AppendLine('name: SelfServeManifest')
    foreach ($pair in @(
        @('managed_installs',   $Manifest.ManagedInstalls),
        @('managed_uninstalls', $Manifest.ManagedUninstalls),
        @('optional_installs',  $Manifest.OptionalInstalls))) {
        $key = $pair[0]; $list = @($pair[1]) | Where-Object { $_ }
        if ($list.Count -eq 0) { [void]$sb.AppendLine("${key}: []"); continue }
        [void]$sb.AppendLine("${key}:")
        foreach ($n in $list) { [void]$sb.AppendLine("- $n") }
    }
    Set-Content -Path $script:SelfServe -Value $sb.ToString().TrimEnd() -Encoding utf8
}

function Add-MscInstallRequest {
    param([Parameter(Mandatory)] [string] $Name)
    $m = Get-MscSelfServe
    $m.ManagedUninstalls = @($m.ManagedUninstalls | Where-Object { $_ -ne $Name })          # cancel any pending removal
    if ($m.ManagedInstalls -notcontains $Name) { $m.ManagedInstalls = @($m.ManagedInstalls) + $Name }
    Set-MscSelfServe $m
}

function Add-MscRemovalRequest {
    param([Parameter(Mandatory)] [string] $Name)
    $m = Get-MscSelfServe
    $m.ManagedInstalls  = @($m.ManagedInstalls  | Where-Object { $_ -ne $Name })            # cancel any pending install
    $m.OptionalInstalls = @($m.OptionalInstalls | Where-Object { $_ -ne $Name })
    if ($m.ManagedUninstalls -notcontains $Name) { $m.ManagedUninstalls = @($m.ManagedUninstalls) + $Name }
    Set-MscSelfServe $m
}

function Remove-MscRequest {
    param([Parameter(Mandatory)] [string] $Name)
    $m = Get-MscSelfServe
    $m.ManagedInstalls   = @($m.ManagedInstalls   | Where-Object { $_ -ne $Name })
    $m.ManagedUninstalls = @($m.ManagedUninstalls | Where-Object { $_ -ne $Name })
    $m.OptionalInstalls  = @($m.OptionalInstalls  | Where-Object { $_ -ne $Name })
    Set-MscSelfServe $m
}

# ----------------------------------------------------------------------------
# InstallInfo.yaml -- the observable result the GUI renders
# ----------------------------------------------------------------------------
function Get-MscState {
    <#  Flattens InstallInfo.yaml into one row per item, the same data the
        Software/Updates tabs bind to. #>
    if (-not (Test-Path $script:InstallInfo)) { return @() }
    $y = Get-Content $script:InstallInfo -Raw | ConvertFrom-Yaml
    $rows = foreach ($section in 'managed_installs','removals','optional_installs','problem_items') {
        foreach ($it in @($y.$section)) {
            if (-not $it.name) { continue }
            [pscustomobject]@{
                Section      = $section
                Name         = $it.name
                Version      = $it.version_to_install
                Installed    = [bool]$it.installed
                Status       = $it.status
                WillInstall  = [bool]$it.will_be_installed
                WillRemove   = [bool]$it.will_be_removed
                NeedsUpdate  = [bool]$it.needs_update
                Uninstallable= [bool]$it.uninstallable
                Error        = $it.error_message
            }
        }
    }
    @($rows)
}

function Show-MscState {
    <#  Renders what the Updates tab would show: pending installs + removals. #>
    $all = Get-MscState
    # Dedupe by Name -- an item can appear in both its primary section and
    # optional_installs; the GUI shows it once.
    $pendIn  = @($all | Where-Object { $_.WillInstall } | Sort-Object Name -Unique)
    $pendOut = @($all | Where-Object { $_.WillRemove } | Sort-Object Name -Unique)
    $probs   = @($all | Where-Object { $_.Section -eq 'problem_items' } | Sort-Object Name -Unique)

    Write-Host "`nUpdates tab  --  $($pendIn.Count + $pendOut.Count) item(s) pending" -ForegroundColor Cyan
    if ($pendIn.Count)  { Write-Host "`n  Pending Installs:" -ForegroundColor Green
        $pendIn  | Format-Table Name, Version, Status -AutoSize | Out-Host }
    if ($pendOut.Count) { Write-Host "  Pending Removals:" -ForegroundColor Yellow
        $pendOut | Format-Table Name, Version, Status -AutoSize | Out-Host }
    if ($probs.Count)   { Write-Host "  Problem Items:" -ForegroundColor Red
        $probs   | Format-Table Name, Error -AutoSize | Out-Host }
    if (-not ($pendIn.Count -or $pendOut.Count)) { Write-Host "  (nothing pending)`n" -ForegroundColor DarkGray }

    Write-Host "SelfServeManifest:" -ForegroundColor Cyan
    $ss = Get-MscSelfServe
    Write-Host "  managed_installs  : $($ss.ManagedInstalls   -join ', ')"
    Write-Host "  managed_uninstalls: $($ss.ManagedUninstalls -join ', ')`n"
}

# ----------------------------------------------------------------------------
# GUI button equivalents
# ----------------------------------------------------------------------------
function Write-MscFlag {
    <#  Mirrors TriggerService.WriteFlagFileAsync -- drops the bootstrap flag for
        CimianWatcher to consume. Used by -ViaWatcher to exercise the real IPC. #>
    param([Parameter(Mandatory)] [string] $ArgsLine)
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    "Bootstrap triggered at: $ts`nSource: MscHarness`nArgs: $ArgsLine`n" |
        Set-Content -Path $script:FlagFile -Encoding utf8
    Write-Host "  flag written: Args: $ArgsLine" -ForegroundColor DarkGray
}

function Wait-MscFlagConsumed {
    param([int] $TimeoutSec = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path $script:FlagFile)) { return $true }   # watcher deleted it = consumed
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Invoke-MscInstall {
    <#  Mimics clicking "Install" on an optional item.
        Default = plan mode (--checkonly): proves the engine WILL install it, no install.
        -Apply  = actually install.  -ViaWatcher = go through the flag-file + service. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Name, [switch] $Apply, [switch] $ViaWatcher)

    Write-Host "`n[Install click] $Name" -ForegroundColor Cyan
    Add-MscInstallRequest $Name                                   # step 1: mutate SelfServe
    Write-Host "  SelfServe.managed_installs += $Name" -ForegroundColor DarkGray

    $a = @('--item', $Name, '--no-preflight', '-vv')             # step 2: run engine targeted
    if (-not $Apply) { $a += '--checkonly' }

    if ($ViaWatcher) {
        Write-MscFlag (($a | Where-Object { $_ -ne '--checkonly' }) -join ' ')
        if (Wait-MscFlagConsumed) { Write-Host "  watcher consumed the flag" -ForegroundColor DarkGray }
        else { Write-Host "  TIMEOUT waiting for CimianWatcher" -ForegroundColor Red }
    } else {
        $r = Invoke-Msu @a
        Write-Host "  engine exit=$($r.ExitCode) in $($r.Seconds)s" -ForegroundColor DarkGray
    }
    Show-MscItem $Name
}

function Invoke-MscRemove {
    <#  Mimics clicking "Remove" on an installed item. Plan mode by default. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Name, [switch] $Apply, [switch] $ViaWatcher)

    Write-Host "`n[Remove click] $Name" -ForegroundColor Cyan
    Add-MscRemovalRequest $Name
    Write-Host "  SelfServe.managed_uninstalls += $Name" -ForegroundColor DarkGray

    $a = @('--item', $Name, '--no-preflight', '-vv')
    if (-not $Apply) { $a += '--checkonly' }

    if ($ViaWatcher) {
        Write-MscFlag (($a | Where-Object { $_ -ne '--checkonly' }) -join ' ')
        if (Wait-MscFlagConsumed) { Write-Host "  watcher consumed the flag" -ForegroundColor DarkGray }
        else { Write-Host "  TIMEOUT waiting for CimianWatcher" -ForegroundColor Red }
    } else {
        $r = Invoke-Msu @a
        Write-Host "  engine exit=$($r.ExitCode) in $($r.Seconds)s" -ForegroundColor DarkGray
    }
    Show-MscItem $Name
}

function Invoke-MscCancel {
    <#  Mimics "Cancel" on a pending request -- drops it from SelfServe, refreshes state. #>
    param([Parameter(Mandatory)] [string] $Name)
    Write-Host "`n[Cancel click] $Name" -ForegroundColor Cyan
    Remove-MscRequest $Name
    [void](Invoke-Msu '--checkonly' '--no-preflight')
    Show-MscItem $Name
}

function Invoke-MscInstallNow {
    <#  Mimics the Updates tab "Install Now" -- one targeted batch over everything pending. #>
    [CmdletBinding()] param([switch] $Apply, [switch] $ViaWatcher)
    $targets = @(Get-MscState | Where-Object { $_.WillInstall -or $_.WillRemove } |
                 Select-Object -Expand Name -Unique)
    if (-not $targets) { Write-Host "Nothing pending." -ForegroundColor DarkGray; return }
    Write-Host "`n[Install Now] $($targets -join ', ')" -ForegroundColor Cyan

    # Single --item flag followed by all values. The engine's --item is a sequence
    # option; repeating the flag ('--item A --item B') exits 1 ("defined multiple times").
    $a = @('--item') + $targets
    $a += '--no-preflight','-vv'
    if (-not $Apply) { $a += '--checkonly' }

    if ($ViaWatcher) {
        Write-MscFlag (($a | Where-Object { $_ -ne '--checkonly' }) -join ' ')
        if (-not (Wait-MscFlagConsumed)) { Write-Host "  TIMEOUT" -ForegroundColor Red }
    } else {
        $r = Invoke-Msu @a
        Write-Host "  engine exit=$($r.ExitCode) in $($r.Seconds)s" -ForegroundColor DarkGray
    }
    Show-MscState
}

function Show-MscItem {
    param([Parameter(Mandatory)] [string] $Name)
    $row = Get-MscState | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($row) { $row | Format-List Name, Section, Installed, Status, WillInstall, WillRemove, Uninstallable | Out-Host }
    else { Write-Host "  ($Name not present in InstallInfo)" -ForegroundColor DarkGray }
}

# ----------------------------------------------------------------------------
# Stuck-state tooling
# ----------------------------------------------------------------------------
function Get-MscLoopState {
    <#  Shows LoopGuard suppression -- why an item silently refuses to (re)install. #>
    if (-not (Test-Path $script:StateJson)) { Write-Host "(no loop state)"; return }
    $j = Get-Content $script:StateJson -Raw | ConvertFrom-Json
    $j.loop_guard.packages.PSObject.Properties | ForEach-Object {
        $p = $_.Value
        [pscustomobject]@{
            Package        = $p.package_name
            Attempts       = $p.attempt_count
            Sessions       = $p.session_count
            SuppressedUntil= $p.suppressed_until
            Reason         = $p.suppression_reason
        }
    } | Format-Table -AutoSize | Out-Host
}

function Clear-MscNsisOrphans {
    <#  Kills orphaned NSIS installer/uninstaller dialogs left behind when an
        installer is launched without a silent flag (e.g. the pre-fix VLC bug:
        an uninstaller handed Inno /VERYSILENT, which NSIS ignores, pops a
        "Installer Language" dialog and sits forever). NSIS relaunches its
        (un)installer from %TEMP% as Au_.exe / Un_*.exe, and the engine runs as
        SYSTEM, so these survive a user-context Stop-Process — elevate to clear.
        Returns the number of processes killed. #>
    [CmdletBinding()]
    param()

    $names = @('Un','Un_A','Au_','uninstall','setup')
    $procs = Get-Process -Name $names -ErrorAction SilentlyContinue
    if (-not $procs) { Write-Host "[NSIS sweep] no orphaned installer dialogs" -ForegroundColor DarkGray; return 0 }

    $list = ($procs | ForEach-Object { "$($_.Name)#$($_.Id)" }) -join ', '
    Write-Host "[NSIS sweep] killing $($procs.Count) orphan(s): $list" -ForegroundColor Yellow
    # User-context first, then sudo for the elevated survivors.
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    $survivors = Get-Process -Name $names -ErrorAction SilentlyContinue
    if ($survivors) {
        $nameArg = ($names | ForEach-Object { "'$_'" }) -join ','
        & sudo pwsh -NoProfile -Command "Get-Process -Name $nameArg -ErrorAction SilentlyContinue | Stop-Process -Force" 2>$null
    }
    # Leftover NSIS temp dirs (~nsu*.tmp) — harmless but tidy up.
    Get-ChildItem $env:TEMP -Directory -Filter '~nsu*' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    $procs.Count
}

function Clear-MscStuck {
    <#  Unsticks a wedged pending item:
          1. drop it from SelfServeManifest (stop re-requesting it),
          2. clear its LoopGuard suppression,
          3. sweep orphaned NSIS installer dialogs,
          4. refresh InstallInfo so the GUI stops showing it pending.
        Use -All to clear every LoopGuard suppression. #>
    [CmdletBinding()]
    param([string] $Name, [switch] $All)

    [void](Clear-MscNsisOrphans)

    if ($All) {
        Write-Host "`n[Clear stuck] ALL loop suppression" -ForegroundColor Cyan
        [void](Invoke-Msu '--clear-loop' 'all')
    } elseif ($Name) {
        Write-Host "`n[Clear stuck] $Name" -ForegroundColor Cyan
        Remove-MscRequest $Name
        Write-Host "  removed $Name from SelfServeManifest" -ForegroundColor DarkGray
        [void](Invoke-Msu '--clear-loop' $Name)
    } else {
        throw "Specify -Name <item> or -All"
    }
    [void](Invoke-Msu '--checkonly' '--no-preflight')
    Show-MscState
}

# ----------------------------------------------------------------------------
# Assertions + scenario runner
# ----------------------------------------------------------------------------
$script:Assertions = [Collections.Generic.List[object]]::new()
function Assert-Msc {
    param([Parameter(Mandatory)] [string] $Desc, [Parameter(Mandatory)] [bool] $Condition)
    $ok = [bool]$Condition
    $script:Assertions.Add([pscustomobject]@{ Pass = $ok; Desc = $Desc })
    $glyph = if ($ok) { 'PASS' } else { 'FAIL' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}" -f $glyph, $Desc) -ForegroundColor $color
    $ok
}

function Get-MscItemRow { param([string] $Name) Get-MscState | Where-Object { $_.Name -eq $Name } | Select-Object -First 1 }

function Invoke-MscScenario {
    <#  Full round trip for one item, the headless equivalent of a manual click-test:
          install (plan) -> assert will-be-installed
          [-Apply] install -> assert installed
          remove (plan)  -> assert will-be-removed
          [-Apply] remove -> assert not installed
        Default is plan mode (fast, no system change). -Apply does the real installs. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Name, [switch] $Apply)

    $script:Assertions.Clear()
    Write-Host "`n=== Scenario: $Name ($(if ($Apply) {'APPLY'} else {'PLAN'})) ===" -ForegroundColor Magenta

    Invoke-MscInstall $Name -Apply:$Apply | Out-Null
    $row = Get-MscItemRow $Name
    if ($Apply) { Assert-Msc "$Name installed after Install"      ([bool]$row.Installed) | Out-Null }
    else        { Assert-Msc "$Name marked will-be-installed"     ($row.Status -eq 'will-be-installed') | Out-Null }

    Invoke-MscRemove $Name -Apply:$Apply | Out-Null
    $row = Get-MscItemRow $Name
    if ($Apply) { Assert-Msc "$Name not installed after Remove"   (-not [bool]$row.Installed) | Out-Null }
    else        { Assert-Msc "$Name marked will-be-removed"       ($row.Status -eq 'will-be-removed') | Out-Null }

    # Leave SelfServe clean so the scenario is repeatable.
    Remove-MscRequest $Name

    $pass = @($script:Assertions | Where-Object Pass).Count
    $tot  = $script:Assertions.Count
    $color = if ($pass -eq $tot) { 'Green' } else { 'Red' }
    Write-Host "`n=== $pass/$tot passed ===`n" -ForegroundColor $color
    return ($pass -eq $tot)
}

# ----------------------------------------------------------------------------
# One-shot CLI dispatch (only when run as a script, not dot-sourced)
# ----------------------------------------------------------------------------
if ($MyInvocation.InvocationName -ne '.' -and $args.Count -gt 0) {
    $cmd = $args[0]; $rest = @($args[1..($args.Count-1)])
    $apply  = $rest -contains '-Apply';     $rest = @($rest | Where-Object { $_ -ne '-Apply' })
    $viaW   = $rest -contains '-ViaWatcher'; $rest = @($rest | Where-Object { $_ -ne '-ViaWatcher' })
    $item   = $rest | Select-Object -First 1
    switch ($cmd.ToLower()) {
        'state'      { Show-MscState }
        'install'    { Invoke-MscInstall   $item -Apply:$apply -ViaWatcher:$viaW }
        'remove'     { Invoke-MscRemove    $item -Apply:$apply -ViaWatcher:$viaW }
        'cancel'     { Invoke-MscCancel    $item }
        'installnow' { Invoke-MscInstallNow      -Apply:$apply -ViaWatcher:$viaW }
        'clear'      { if ($item -eq 'all') { Clear-MscStuck -All } else { Clear-MscStuck -Name $item } }
        'nsisweep'   { [void](Clear-MscNsisOrphans) }
        'loops'      { Get-MscLoopState }
        'scenario'   { Invoke-MscScenario  $item -Apply:$apply }
        default      { Write-Host "Commands: state | install <n> | remove <n> | cancel <n> | installnow | clear <n|all> | nsisweep | loops | scenario <n>   [-Apply] [-ViaWatcher]" }
    }
}
