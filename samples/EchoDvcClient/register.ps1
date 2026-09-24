<#
.SYNOPSIS
    Register the EchoDvcClient sample plugin (LocalServer32 COM activation) so mstsc loads it.

.DESCRIPTION
    Writes two things, per-user (HKCU, no admin) by default or machine-wide with -Machine:
      1. ...\Software\Classes\CLSID\{Clsid}\LocalServer32  (default) = path to the plugin exe
      2. ...\Terminal Server Client\Default\AddIns\{PluginName}  Name = {Clsid}

    Per-user (HKCU) is what mstsc reads for the interactive user. Run unregister.ps1 to remove it.

.EXAMPLE
    .\register.ps1 -ExePath .\bin\Debug\net10.0-windows\EchoDvcClient.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [string] $PluginName = 'EchoDvcClient',
    [string] $Clsid      = '{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE02}',
    [switch] $Machine
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ExePath)) { throw "Plugin exe not found: $ExePath" }
$ExePath = (Resolve-Path $ExePath).Path

$root = if ($Machine) { 'HKLM:' } else { 'HKCU:' }
$clsidKey = "$root\Software\Classes\CLSID\$Clsid\LocalServer32"
$addinKey = "$root\Software\Microsoft\Terminal Server Client\Default\AddIns\$PluginName"

New-Item -Path $clsidKey -Force | Out-Null
Set-ItemProperty -Path $clsidKey -Name '(default)' -Value $ExePath
New-Item -Path $addinKey -Force | Out-Null
Set-ItemProperty -Path $addinKey -Name 'Name' -Value $Clsid

Write-Host "Registered '$PluginName' ($(if ($Machine) {'HKLM'} else {'HKCU'}))" -ForegroundColor Green
Write-Host "  CLSID  : $Clsid"
Write-Host "  Server : $ExePath"
Write-Host "Now connect mstsc to the mock (Uppercase server plugin loaded); mstsc loads this plugin."
Write-Host "Watch: $env:TEMP\echo-dvc-client.log"
