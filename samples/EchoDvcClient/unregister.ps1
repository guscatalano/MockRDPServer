<#
.SYNOPSIS  Remove the EchoDvcClient sample plugin registration written by register.ps1.
.EXAMPLE   .\unregister.ps1
#>
[CmdletBinding()]
param(
    [string] $PluginName = 'EchoDvcClient',
    [string] $Clsid      = '{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE02}',
    [switch] $Machine
)

$root = if ($Machine) { 'HKLM:' } else { 'HKCU:' }
Remove-Item -Path "$root\Software\Classes\CLSID\$Clsid" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$root\Software\Microsoft\Terminal Server Client\Default\AddIns\$PluginName" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Unregistered '$PluginName' ($(if ($Machine) {'HKLM'} else {'HKCU'}))." -ForegroundColor Green
