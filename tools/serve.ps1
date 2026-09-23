<#
.SYNOPSIS
  One-command launcher for the mock RDP server: builds it, boots it, trusts its
  self-signed cert for mstsc, writes a .rdp, and connects — no prompts.

.EXAMPLE
  tools\serve.ps1                      # test-pattern server + mstsc on 127.0.0.1:3389
  tools\serve.ps1 -Desktop             # fake desktop instead of the test pattern
  tools\serve.ps1 -Port 33389 -Desktop
  tools\serve.ps1 -ServerOnly          # just the server (connect your own client)
  tools\serve.ps1 -Dvc dvc::diag::inspector -DvcBridge dvc::diag::inspector=127.0.0.1:9999

.NOTES
  The cert-pin + credential-less .rdp is why this "just connects" where a raw
  MockRdpCli.exe launch makes mstsc prompt for trust and credentials.
#>
[CmdletBinding()]
param(
  [int]    $Port       = 3389,
  [string] $Bind       = "0.0.0.0",
  [switch] $Desktop,
  [switch] $ServerOnly,
  [string] $Dvc        = "",
  [string] $DvcBridge  = "",
  [string] $Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

Write-Host "Building MockRdpCli ($Configuration)..." -ForegroundColor Cyan
dotnet build (Join-Path $repo 'src\MockRdp') -c $Configuration --nologo | Out-Null
$exe = (Get-ChildItem (Join-Path $repo 'src\MockRdp\bin') -Recurse -Filter 'MockRdpCli.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
if (-not $exe) { throw "MockRdpCli.exe not found after build." }

$cer  = Join-Path $env:TEMP "mockrdp-$Port.cer"
$log  = Join-Path $env:TEMP "mockrdp-$Port.log"
Remove-Item $cer -ErrorAction SilentlyContinue

$srvArgs = @('--port', $Port, '--bind', $Bind, '--cert-out', $cer, '--log-file', $log)
if ($Desktop)   { $srvArgs += '--desktop' }
if ($Dvc)       { $srvArgs += @('--dvc', $Dvc) }
if ($DvcBridge) { $srvArgs += @('--dvc-bridge', $DvcBridge) }

Write-Host "Starting server: $exe $($srvArgs -join ' ')" -ForegroundColor Cyan
Start-Process $exe -ArgumentList $srvArgs -WindowStyle Minimized | Out-Null

# Wait for the server to export its cert (means it's listening).
for ($i = 0; $i -lt 100 -and -not (Test-Path $cer); $i++) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path $cer)) { throw "Server didn't export its cert (see $log)." }

if ($ServerOnly) {
  Write-Host "Server up on ${Bind}:$Port. Cert: $cer  ·  Log: $log" -ForegroundColor Green
  return
}

# Pin the cert so mstsc connects without the "do you trust this?" prompt. mstsc reads
# CertHash (SHA-1 thumbprint, REG_BINARY) under the per-server key.
$hash = ([Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)).GetCertHash()
foreach ($srv in @('127.0.0.1', "127.0.0.1:$Port")) {
  $k = New-Item -Path "HKCU:\Software\Microsoft\Terminal Server Client\Servers\$srv" -Force
  New-ItemProperty -Path $k.PSPath -Name CertHash -Value $hash -PropertyType Binary -Force | Out-Null
}

# A credential-less, no-NLA .rdp so mstsc doesn't prompt for a username/password.
$rdp = Join-Path $env:TEMP "mockrdp-$Port.rdp"
@"
full address:s:127.0.0.1:$Port
authentication level:i:2
enablecredsspsupport:i:0
prompt for credentials:i:0
screen mode id:i:1
desktopwidth:i:1280
desktopheight:i:800
"@ | Set-Content $rdp -Encoding ASCII

# Auto-dismiss any residual mstsc prompt (Connect / Yes / OK on a #32770 dialog).
Start-Job {
  Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;
public static class K{const uint C=0x00F5;delegate bool E(IntPtr h,IntPtr l);
[DllImport("user32.dll")]static extern bool EnumWindows(E c,IntPtr l);
[DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr p,E c,IntPtr l);
[DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder s,int n);
[DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
[DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
static string Cl(IntPtr h){var s=new StringBuilder(64);GetClassName(h,s,64);return s.ToString();}
static string Tx(IntPtr h){var s=new StringBuilder(256);GetWindowText(h,s,256);return s.ToString();}
public static void A(){EnumWindows((h,_)=>{if(IsWindowVisible(h)&&Cl(h)=="#32770"){EnumChildWindows(h,(c,__)=>{if(Cl(c)=="Button"){var t=Tx(c).Replace("&","").Trim();if(t=="Connect"||t=="Yes"||t=="OK")SendMessage(c,C,IntPtr.Zero,IntPtr.Zero);}return true;},IntPtr.Zero);}return true;},IntPtr.Zero);}}
"@
  1..30 | ForEach-Object { [K]::A(); Start-Sleep -Milliseconds 500 }
} | Out-Null

Start-Process mstsc.exe -ArgumentList $rdp | Out-Null
Write-Host "Connected mstsc to 127.0.0.1:$Port  ·  Log: $log" -ForegroundColor Green
Write-Host "Stop with:  Get-Process MockRdpCli,mstsc | Stop-Process -Force"
