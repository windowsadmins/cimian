param(
    [string]$InputDirectory = 'C:\ValidationInput',
    [string]$OutputDirectory = 'C:\ValidationOutput',
    [switch]$ShutdownWhenDone,
    [switch]$ProvisionLegacyVBScript,
    [switch]$RequireVBScriptFree
)

$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    throw 'This validation changes local users and installs software. Run it only inside Windows Sandbox.'
}

New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
Start-Transcript (Join-Path $OutputDirectory 'validation.log') -Force
$results = [System.Collections.Generic.List[string]]::new()
function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Failed: $Name" }
    $results.Add($Name)
    Write-Host "PASS: $Name"
}
function Install-Msi([string]$Path, [string]$Name, [switch]$Remove) {
    $verb = if ($Remove) { '/x' } else { '/i' }
    $log = Join-Path $OutputDirectory "$Name-msi.log"
    $process = Start-Process msiexec.exe -ArgumentList "$verb `"$Path`" /qn /norestart /L*v! `"$log`"" -PassThru
    if (-not $process.WaitForExit(600000)) {
        Stop-Process -Id $process.Id -Force
        throw "MSI operation timed out: $Name"
    }
    Assert-Check ($process.ExitCode -in 0,3010) "$Name MSI exit code"
    Stop-Service CimianWatcher -ErrorAction SilentlyContinue
}
function Enable-LegacyVBScriptFixture {
    foreach ($arch in @('System32','SysWOW64')) {
        Copy-Item (Join-Path $InputDirectory "vbscript-$arch.dll") (Join-Path $env:SystemRoot "$arch\vbscript.dll") -Force
    }
    foreach ($registration in Get-ChildItem $InputDirectory -Filter 'vbscript-*.reg') {
        & reg.exe import $registration.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not register the guest VBScript fixture.' }
    }
    Write-Host 'Legacy VBScript fixture enabled only in this disposable guest.'
}
function Disable-LegacyVBScriptFixture {
    Stop-Service msiserver -Force -ErrorAction SilentlyContinue
    Get-Process msiexec -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($view in @('CLSID','WOW6432Node\CLSID')) {
        foreach ($id in @('{B54F3741-5B07-11cf-A4B0-00AA004A55E8}','{B54F3742-5B07-11cf-A4B0-00AA004A55E8}','{B54F3743-5B07-11cf-A4B0-00AA004A55E8}')) {
            & reg.exe delete "HKLM\SOFTWARE\Classes\$view\$id" /f | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not remove the guest VBScript registration.' }
        }
    }
    foreach ($name in @('VBScript','VBScript.Encode')) {
        & reg.exe delete "HKLM\SOFTWARE\Classes\$name" /f | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not remove the guest VBScript ProgID.' }
    }
    foreach ($arch in @('System32','SysWOW64')) { Remove-Item "$env:SystemRoot\$arch\vbscript.dll" -Force }
}
function Assert-VBScriptAbsent([string]$Name) {
    $probe = 'C:\CimianValidationPackages\engine-probe.vbs'
    Set-Content $probe 'WScript.Echo "VBScript available"'
    $process = Start-Process cscript.exe -ArgumentList "//nologo //E:VBScript $probe" -RedirectStandardOutput (Join-Path $OutputDirectory "$Name-vbs.txt") -RedirectStandardError (Join-Path $OutputDirectory "$Name-vbs-error.txt") -Wait -PassThru
    Assert-Check ((-not (Test-Path "$env:SystemRoot\System32\vbscript.dll")) -and (-not (Test-Path "$env:SystemRoot\SysWOW64\vbscript.dll")) -and $process.ExitCode -ne 0) $Name
}

try {
    if ($ProvisionLegacyVBScript -and -not $RequireVBScriptFree) { Enable-LegacyVBScriptFixture }
    foreach ($cert in Get-ChildItem $InputDirectory -Filter 'trust-*.cer') {
        Import-Certificate -FilePath $cert.FullName -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    }
    $packages = 'C:\CimianValidationPackages'
    New-Item $packages -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $InputDirectory 'candidate.msi') $packages
    Copy-Item (Join-Path $InputDirectory 'baseline.msi') $packages
    $candidate = Join-Path $packages 'candidate.msi'
    Assert-Check ((Get-AuthenticodeSignature $candidate).Status -eq 'Valid') 'Candidate MSI signature'
    $install = 'C:\Program Files\Cimian'
    $root = 'C:\ProgramData\ManagedInstalls'
    if ($RequireVBScriptFree) { Assert-VBScriptAbsent 'VBScript absent before fresh install' }
    Install-Msi $candidate 'fresh'
    Assert-Check (Test-Path "$install\manifestutil.exe") 'Fresh install payload'
    Assert-Check ((Get-AuthenticodeSignature "$install\managedsoftwareupdate.exe").Status -eq 'Valid') 'Installed executable signature'
    & "$install\managedsoftwareupdate.exe" --repair-permissions
    Assert-Check ($LASTEXITCODE -eq 0) 'Fresh install permissions repair'

    Install-Msi $candidate 'remove-fresh' -Remove
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
    if ($ProvisionLegacyVBScript -and $RequireVBScriptFree) { Enable-LegacyVBScriptFixture }
    Install-Msi (Join-Path $packages 'baseline.msi') 'baseline'
    if ($ProvisionLegacyVBScript -and $RequireVBScriptFree) { Disable-LegacyVBScriptFixture }
    if ($RequireVBScriptFree) { Assert-VBScriptAbsent 'VBScript absent before upgrade' }
    New-Item $root -ItemType Directory -Force | Out-Null
    Set-Content "$root\Config.yaml" "SoftwareRepoURL: http://127.0.0.1:9`nClientIdentifier: sandbox-validation"

    $password = [guid]::NewGuid().ToString('N') + '!aA1'
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    New-LocalUser CimianValidationUser -Password $secure -PasswordNeverExpires | Out-Null
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member CimianValidationUser
    $credential = [pscredential]::new("$env:COMPUTERNAME\CimianValidationUser", $secure)
    $work = 'C:\CimianValidation'
    New-Item $work -ItemType Directory -Force | Out-Null
    & icacls.exe $work /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
    $seed = @'
$ErrorActionPreference='Stop'
$root='C:\ProgramData\ManagedInstalls'
New-Item "$root\sbin","$root\conditions","$root\Cache" -ItemType Directory -Force | Out-Null
Set-Content "$root\sbin\preflight.ps1" "Set-Content 'C:\CimianValidation\payload-ran.txt' 'unexpected'"
Set-Content "$root\conditions\planted.ps1" "Set-Content 'C:\CimianValidation\payload-ran.txt' 'unexpected'"
Set-Content "$root\Cache\planted.exe" 'not an installer'
Set-Content "$root\Cache\planted.exe.verified" 'not verified'
Set-Content 'C:\CimianValidation\seeded.txt' 'seeded'
'@
    Set-Content "$work\seed.ps1" $seed
    $process = Start-Process powershell.exe -Credential $credential -WorkingDirectory $work -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File $work\seed.ps1" -RedirectStandardOutput "$work\seed-output.txt" -RedirectStandardError "$work\seed-error.txt" -Wait -PassThru
    Copy-Item "$work\seed-*.txt" $OutputDirectory
    Assert-Check ($process.ExitCode -eq 0 -and (Test-Path "$work\seeded.txt")) 'Standard user planted content before upgrade'

    Install-Msi $candidate 'upgrade'
    & "$install\managedsoftwareupdate.exe" --repair-permissions
    Assert-Check ($LASTEXITCODE -eq 0) 'Upgrade permissions repair'
    Assert-Check (-not (Test-Path "$work\payload-ran.txt")) 'Planted scripts never executed'
    Assert-Check (-not (Test-Path "$root\sbin\preflight.ps1")) 'Planted preflight quarantined'
    Assert-Check (-not (Test-Path "$root\conditions\planted.ps1")) 'Planted condition quarantined'
    Assert-Check (-not (Test-Path "$root\Cache\planted.exe")) 'Planted cache payload removed'
    Assert-Check ((Get-Content "$root\Config.yaml" -Raw) -match 'sandbox-validation') 'Administrator configuration preserved'

    Stop-Service CimianWatcher -ErrorAction Stop
    Get-ScheduledTask | Where-Object TaskName -Like 'Cimian*' | Disable-ScheduledTask | Out-Null
    Get-Process managedsoftwareupdate,cimiwatcher -ErrorAction SilentlyContinue | Stop-Process -Force
    New-Item "$root\manifests" -ItemType Directory -Force | Out-Null
    $probe = @'
$ErrorActionPreference='Stop'
$root='C:\ProgramData\ManagedInstalls'
$blocked=@()
foreach($path in @('Config.yaml','sbin\blocked.ps1','conditions\blocked.ps1','Cache\blocked.exe','catalogs\blocked.yaml','manifests\blocked.yaml','SelfUpdateBackup\blocked.dll')) {
    try { [IO.File]::WriteAllText((Join-Path $root $path),'blocked'); throw "Unexpected write access: $path" }
    catch [UnauthorizedAccessException] { $blocked += $path }
}
try { [IO.File]::ReadAllText("$root\Config.yaml") | Out-Null; throw 'Configuration was readable' }
catch [UnauthorizedAccessException] { $blocked += 'Config read' }
[IO.Directory]::GetFiles($root) | Out-Null
Set-Content "$root\triggers\validation.yaml" 'Args: --checkonly'
[IO.Directory]::GetFiles("$root\triggers") | Out-Null
if([IO.File]::ReadAllText("$root\triggers\validation.yaml").Trim() -ne 'Args: --checkonly') { throw 'Own trigger was not readable' }
$tool='C:\Program Files\Cimian\manifestutil.exe'
& $tool --selfservice-request Firefox
if($LASTEXITCODE -ne 0) { throw 'Standard-user install request failed' }
& $tool --selfservice-remove Firefox
if($LASTEXITCODE -ne 0) { throw 'Standard-user cancellation failed' }
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$own=@([IO.Directory]::GetFiles("$root\selfservice", "*.yaml") | Where-Object { (Get-Acl $_).GetOwner([Security.Principal.SecurityIdentifier]).Value -eq $sid })
if($own.Count -eq 0) {
    Get-ChildItem "$root\selfservice\*.yaml" | ForEach-Object {
        "$($_.Name): $((Get-Acl $_.FullName).GetOwner([Security.Principal.SecurityIdentifier]).Value)"
    } | Set-Content 'C:\CimianValidation\request-owners.txt'
    throw "No request owned by the individual user $sid"
}
@{Blocked=$blocked;Sid=$sid;RequestFiles=$own.Count} | ConvertTo-Json | Set-Content 'C:\CimianValidation\standard-result.json'
'@
    Set-Content "$work\probe.ps1" $probe
    $process = Start-Process powershell.exe -Credential $credential -WorkingDirectory $work -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File $work\probe.ps1" -RedirectStandardOutput "$work\probe-output.txt" -RedirectStandardError "$work\probe-error.txt" -Wait -PassThru
    Copy-Item "$work\probe-*.txt" $OutputDirectory
    Copy-Item "$work\request-owners.txt" $OutputDirectory -ErrorAction SilentlyContinue
    Assert-Check ($process.ExitCode -eq 0) 'Standard user blocked from trusted content and can submit requests'
    $standard = Get-Content "$work\standard-result.json" -Raw | ConvertFrom-Json
    Assert-Check ($standard.Blocked.Count -eq 8) 'All protected standard-user access probes denied'

    & "$install\manifestutil.exe" --selfservice-request Firefox
    Assert-Check ($LASTEXITCODE -eq 0) 'Elevated administrator install request'
    & "$install\manifestutil.exe" --selfservice-remove Firefox
    Assert-Check ($LASTEXITCODE -eq 0) 'Elevated administrator cancellation'
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $owned = @(Get-ChildItem "$root\selfservice\*.yaml" | Where-Object {
        (Get-Acl $_.FullName).GetOwner([Security.Principal.SecurityIdentifier]) -eq $sid
    })
    Assert-Check ($owned.Count -eq 2) 'Elevated requests owned by the individual administrator'
    Assert-Check (-not (Test-Path "$work\payload-ran.txt")) 'No planted payload executed during validation'
    @{Passed=$true;RequireVBScriptFree=[bool]$RequireVBScriptFree;LegacyVBScriptProvisioned=[bool]$ProvisionLegacyVBScript;Checks=$results;StandardUser=$standard} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'result.json')
} catch {
    @{Passed=$false;LegacyVBScriptProvisioned=[bool]$ProvisionLegacyVBScript;Checks=$results;Error=$_.Exception.Message} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'result.json')
    $_ | Out-String | Set-Content (Join-Path $OutputDirectory 'error.txt')
} finally {
    Stop-Transcript
    if ($ShutdownWhenDone) { shutdown.exe /s /t 5 }
}
