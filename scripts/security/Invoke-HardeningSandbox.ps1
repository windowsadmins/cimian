param(
    [Parameter(Mandatory)][string]$CandidateMsi,
    [Parameter(Mandatory)][string]$BaselineMsi,
    [switch]$ProvisionLegacyVBScript,
    [switch]$RequireVBScriptFree,
    [string]$OutputDirectory = (Join-Path $env:TEMP ('CimianHardening-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$null = Get-Command WindowsSandbox.exe -ErrorAction Stop
$candidate = (Resolve-Path $CandidateMsi).Path
$baseline = (Resolve-Path $BaselineMsi).Path
$signature = Get-AuthenticodeSignature $candidate
if ($signature.Status -ne 'Valid') { throw 'The candidate MSI must have a valid signature.' }

$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $root) { throw 'Choose a new output directory to avoid mixing validation results.' }
$inputDir = Join-Path $root 'input'
$outputDir = Join-Path $root 'output'
New-Item $inputDir,$outputDir -ItemType Directory -Force | Out-Null
Copy-Item $candidate (Join-Path $inputDir 'candidate.msi')
Copy-Item $baseline (Join-Path $inputDir 'baseline.msi')
Copy-Item (Join-Path $PSScriptRoot 'Test-HardeningSandbox.ps1') $inputDir

$chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
$chain.ChainPolicy.RevocationMode = 'NoCheck'
if (-not $chain.Build($signature.SignerCertificate)) { throw 'The candidate signing chain could not be built.' }
$i = 0
foreach ($element in $chain.ChainElements) {
    if ($element.Certificate.Thumbprint -ne $signature.SignerCertificate.Thumbprint -or $chain.ChainElements.Count -eq 1) {
        Export-Certificate -Cert $element.Certificate -FilePath (Join-Path $inputDir "trust-$i.cer") | Out-Null
        $i++
    }
}
$guestArguments = ''
if ($RequireVBScriptFree) { $guestArguments += ' -RequireVBScriptFree' }
if ($ProvisionLegacyVBScript) {
    $guestArguments += ' -ProvisionLegacyVBScript'
    foreach ($arch in @('System32','SysWOW64')) {
        $dll = Join-Path $env:SystemRoot "$arch\vbscript.dll"
        if ((Get-AuthenticodeSignature $dll).Status -ne 'Valid') { throw "The $arch VBScript fixture must be signed." }
        Copy-Item $dll (Join-Path $inputDir "vbscript-$arch.dll") -Force
    }
    $index = 0
    foreach ($view in @('CLSID','WOW6432Node\CLSID')) {
        foreach ($id in @('{B54F3741-5B07-11cf-A4B0-00AA004A55E8}','{B54F3742-5B07-11cf-A4B0-00AA004A55E8}','{B54F3743-5B07-11cf-A4B0-00AA004A55E8}')) {
            $key = "HKLM\SOFTWARE\Classes\$view\$id"
            if (Test-Path ("Registry::HKEY_LOCAL_MACHINE" + $key.Substring(4))) {
                & reg.exe export $key (Join-Path $inputDir "vbscript-$index.reg") /y | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not export VBScript fixture registration.' }
                $index++
            }
        }
    }
    foreach ($name in @('VBScript','VBScript.Encode')) {
        & reg.exe export "HKLM\SOFTWARE\Classes\$name" (Join-Path $inputDir "vbscript-progid-$name.reg") /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not export VBScript fixture ProgID.' }
    }
}
$inputXml = [Security.SecurityElement]::Escape($inputDir)
$outputXml = [Security.SecurityElement]::Escape($outputDir)
$config = @"
<Configuration>
  <Networking>Disable</Networking>
  <VGpu>Disable</VGpu>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\ValidationInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$outputXml</HostFolder><SandboxFolder>C:\ValidationOutput</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ValidationInput\Test-HardeningSandbox.ps1 -ShutdownWhenDone$guestArguments</Command></LogonCommand>
</Configuration>
"@
$path = Join-Path $root 'validation.wsb'
Set-Content $path $config
@{
    CandidateSha256 = (Get-FileHash $candidate).Hash
    BaselineSha256 = (Get-FileHash $baseline).Hash
} | ConvertTo-Json | Set-Content (Join-Path $root 'inputs.json')
Start-Process WindowsSandbox.exe -ArgumentList "`"$path`""
Write-Host "Sandbox started. Results will be written to $outputDir"
