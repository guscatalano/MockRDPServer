<#
.SYNOPSIS
  Bootstrap a fresh paired DVC sample — a server-side plugin and a client-side plugin on your own
  channel — by stamping out copies of samples/UppercaseDvcPlugin + samples/EchoDvcClient with your
  name, channel, and a brand-new client CLSID substituted in.

.DESCRIPTION
  Creates samples/<Name>Server (IServerDvcPlugin, loaded by the mock) and samples/<Name>Client
  (IWTSPlugin COM plugin, loaded by mstsc). Both talk on <Channel>. Edit the two RunAsync/callback
  bodies and you have your own both-ends DVC. Then run the printed commands to see it round-trip.

.EXAMPLE
  tools\new-dvc-sample.ps1 -Name Chatty -Channel "MYAPP::chat"
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z][A-Za-z0-9]*$')] [string] $Name,
  [Parameter(Mandatory)] [string] $Channel,
  [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$samples = Join-Path $repo 'samples'

$serverSrc = Join-Path $samples 'UppercaseDvcPlugin'
$clientSrc = Join-Path $samples 'EchoDvcClient'
$serverDst = Join-Path $samples "${Name}Server"
$clientDst = Join-Path $samples "${Name}Client"

foreach ($d in @($serverDst, $clientDst)) {
  if (Test-Path $d) {
    if ($Force) { Remove-Item $d -Recurse -Force } else { throw "$d already exists (use -Force to overwrite)." }
  }
}

# Copy a sample dir minus build output, then rewrite tokens inside every text file and rename files.
function Copy-Sample([string] $src, [string] $dst, [hashtable] $tokens) {
  Copy-Item $src $dst -Recurse
  Get-ChildItem (Join-Path $dst 'bin'), (Join-Path $dst 'obj') -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

  Get-ChildItem $dst -Recurse -File -Include *.cs, *.csproj, *.ps1, *.md | ForEach-Object {
    $text = Get-Content $_.FullName -Raw
    foreach ($k in $tokens.Keys) { $text = $text.Replace($k, $tokens[$k]) }
    Set-Content $_.FullName $text -Encoding UTF8 -NoNewline
  }
  # Rename any file whose name carries an old token (e.g. the .csproj).
  Get-ChildItem $dst -Recurse -File | ForEach-Object {
    $new = $_.Name
    foreach ($k in $tokens.Keys) { if ($_.Name -like "*$k*") { $new = $new.Replace($k, $tokens[$k]) } }
    if ($new -ne $_.Name) { Rename-Item $_.FullName (Join-Path $_.DirectoryName $new) }
  }
}

$newClsid = ([guid]::NewGuid().ToString('D')).ToUpperInvariant()   # fresh CLSID for the client plugin

Write-Host "Stamping samples/${Name}Server + samples/${Name}Client on channel '$Channel'..." -ForegroundColor Cyan

Copy-Sample $serverSrc $serverDst @{
  'UppercaseDvcPlugin' = "${Name}Server"
  'UppercasePlugin'    = "${Name}ServerPlugin"
  'Uppercase echo (sample)' = "$Name (sample)"
  'SAMPLE::upper'      = $Channel
}

Copy-Sample $clientSrc $clientDst @{
  'EchoDvcClient'      = "${Name}Client"
  'EchoClientPlugin'   = "${Name}ClientPlugin"
  'UppercaseDvcPlugin' = "${Name}Server"        # keep the cross-reference comment accurate
  'echo-dvc-client'    = "$($Name.ToLowerInvariant())-client"
  'SAMPLE::upper'      = $Channel
  '7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE02' = $newClsid
}

Write-Host "`nCreated:" -ForegroundColor Green
Write-Host "  samples/${Name}Server   (server-side IServerDvcPlugin)"
Write-Host "  samples/${Name}Client   (client-side IWTSPlugin, CLSID {$newClsid})"
Write-Host "`nBuild + demo it:" -ForegroundColor Green
Write-Host "  dotnet build samples/${Name}Server  -c Debug"
Write-Host "  dotnet build samples/${Name}Client  -c Debug"
Write-Host "  # load samples/${Name}Server's DLL into the mock (tray or --plugin), register the client, connect mstsc:"
Write-Host "  .\samples\${Name}Client\register.ps1 -ExePath .\samples\${Name}Client\bin\Debug\net10.0-windows\${Name}Client.exe"
Write-Host "`nThen edit the RunAsync (server) and ChannelCallback (client) bodies to your protocol."
