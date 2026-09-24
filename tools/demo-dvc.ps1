<#
.SYNOPSIS
  One-command DVC round-trip demo: loads a SERVER-side DVC plugin into the mock and a CLIENT-side DVC
  plugin into mstsc, connects, and shows them talk on one channel — proof that testing both ends of a
  dynamic virtual channel is easy and needs no RDP infrastructure.

.DESCRIPTION
  Builds the mock + both sample plugins (samples/UppercaseDvcPlugin server, samples/EchoDvcClient
  client), registers the client per-user (HKCU, no admin), starts the mock with the server plugin,
  pins its cert, and launches mstsc with a credential-less .rdp. The client opens SAMPLE::upper, sends
  a line, and the server echoes it upper-cased. Tails the client's log so you watch it live. Ctrl+C
  tears everything down (stops the mock, unregisters the client).

.EXAMPLE
  tools\demo-dvc.ps1
  tools\demo-dvc.ps1 -Port 33389 -Configuration Release
#>
[CmdletBinding()]
param(
  [int]    $Port = 33389,
  [string] $Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$tfm = 'net10.0-windows'
$registered = $false
$mockProc = $null

function Build-One([string] $proj) {
  dotnet build (Join-Path $repo $proj) -c $Configuration --nologo | Out-Null
}
function Find-Exe([string] $proj, [string] $name) {
  (Get-ChildItem (Join-Path $repo "$proj\bin") -Recurse -Filter $name |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

try {
  Write-Host "Building the mock + both sample plugins ($Configuration)..." -ForegroundColor Cyan
  Build-One 'src\MockRdp'
  Build-One 'samples\UppercaseDvcPlugin'
  Build-One 'samples\EchoDvcClient'

  $mock       = Find-Exe 'src\MockRdp'                'MockRdpCli.exe'
  $serverDll  = Find-Exe 'samples\UppercaseDvcPlugin' 'UppercaseDvcPlugin.dll'
  $clientExe  = Find-Exe 'samples\EchoDvcClient'      'EchoDvcClient.exe'
  if (-not ($mock -and $serverDll -and $clientExe)) { throw "Build outputs not found." }

  $clientLog = Join-Path $env:TEMP 'echo-dvc-client.log'
  $mockLog   = Join-Path $env:TEMP "mockrdp-dvc-$Port.log"
  $cer       = Join-Path $env:TEMP "mockrdp-dvc-$Port.cer"
  Remove-Item $clientLog, $mockLog, $cer -ErrorAction SilentlyContinue

  Write-Host "Registering the CLIENT plugin (per-user, no admin)..." -ForegroundColor Cyan
  & (Join-Path $repo 'samples\EchoDvcClient\register.ps1') -ExePath $clientExe | Out-Null
  $registered = $true

  Write-Host "Starting the mock with the SERVER plugin ($([IO.Path]::GetFileName($serverDll)))..." -ForegroundColor Cyan
  $mockProc = Start-Process $mock -PassThru -WindowStyle Minimized -ArgumentList @(
    '--port', $Port, '--desktop', '--plugin', $serverDll, '--cert-out', $cer, '--log-file', $mockLog)

  for ($i = 0; $i -lt 100 -and -not (Test-Path $cer); $i++) { Start-Sleep -Milliseconds 100 }
  if (-not (Test-Path $cer)) { throw "Mock didn't export its cert (see $mockLog)." }

  # Pin the cert so mstsc connects without a trust prompt.
  $hash = ([Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)).GetCertHash()
  foreach ($srv in @('127.0.0.1', "127.0.0.1:$Port")) {
    $k = New-Item -Path "HKCU:\Software\Microsoft\Terminal Server Client\Servers\$srv" -Force
    New-ItemProperty -Path $k.PSPath -Name CertHash -Value $hash -PropertyType Binary -Force | Out-Null
  }

  $rdp = Join-Path $env:TEMP "mockrdp-dvc-$Port.rdp"
  @"
full address:s:127.0.0.1:$Port
authentication level:i:2
enablecredsspsupport:i:0
prompt for credentials:i:0
screen mode id:i:1
desktopwidth:i:1280
desktopheight:i:800
"@ | Set-Content $rdp -Encoding ASCII

  Write-Host "Launching mstsc — it loads the CLIENT plugin and connects..." -ForegroundColor Cyan
  Start-Process mstsc.exe -ArgumentList "`"$rdp`""

  Write-Host ""
  Write-Host "Watch the round-trip below (client log). The server side is in the mock's desktop" -ForegroundColor Green
  Write-Host "'DVC Console'/'Channel Monitor', and in $mockLog." -ForegroundColor Green
  Write-Host "Expect: sent 'hello from the client sample'  ->  received 'HELLO FROM THE CLIENT SAMPLE'." -ForegroundColor Green
  Write-Host "Ctrl+C to stop everything.`n" -ForegroundColor DarkGray

  for ($i = 0; $i -lt 60 -and -not (Test-Path $clientLog); $i++) { Start-Sleep -Milliseconds 250 }
  Get-Content $clientLog -Wait
}
finally {
  Write-Host "`nStopping..." -ForegroundColor Cyan
  if ($mockProc -and -not $mockProc.HasExited) { $mockProc | Stop-Process -Force -ErrorAction SilentlyContinue }
  if ($registered) { & (Join-Path $repo 'samples\EchoDvcClient\unregister.ps1') | Out-Null }
}
