<#
.SYNOPSIS
  Builds the mock RDP server and the mstscax-based client, then verifies the client (the same
  engine mstsc.exe uses) completes a full session against the mock.

.DESCRIPTION
  This is the "mstsc-grade" acceptance test. The client hosts the mstscax ActiveX control and
  connects over TLS; it auto-accepts the self-signed cert warning transiently, so NOTHING is
  persisted — no certificate-store changes, no registry changes. Exits 0 if the session reaches
  the active state, else 1.

.EXAMPLE
  pwsh tools/RdpAxClient/test-against-mock.ps1
#>
param(
    [int]$Port = 33450,
    [int]$TimeoutSec = 25
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$srv = Join-Path $repo "src\MockRdp\bin\Debug\net10.0\MockRdp.exe"
$cli = Join-Path $repo "tools\RdpAxClient\bin\Debug\net10.0-windows\RdpAxClient.exe"
$srvLog = Join-Path $env:TEMP "mockrdp-mstscax.log"

Write-Host "Building..."
& dotnet build (Join-Path $repo "src\MockRdp\MockRdp.csproj") -c Debug --nologo -v quiet
& dotnet build (Join-Path $repo "tools\RdpAxClient\RdpAxClient.csproj") -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$server = Start-Process -FilePath $srv -ArgumentList "--port", $Port, "--log-level", "debug" `
    -PassThru -WindowStyle Minimized -RedirectStandardOutput $srvLog -RedirectStandardError "$srvLog.err"
try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { $c = [Net.Sockets.TcpClient]::new(); $c.Connect("127.0.0.1", $Port); $c.Close(); $ready = $true; break }
        catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $ready) { throw "server did not open port $Port" }

    Write-Host "Connecting the mstscax client to localhost:$Port ..."
    & $cli --server localhost --port $Port --timeout $TimeoutSec --auth-level 2
    $exit = $LASTEXITCODE
}
finally {
    if ($server -and -not $server.HasExited) { $server.Kill(); $server.WaitForExit(3000) }
}

Write-Host "--- server reached ---"
Get-Content $srvLog | Select-String -Pattern "MCS complete|Confirm Active|ACTIVE|Clipboard channel ready" |
    Select-Object -Last 4 | ForEach-Object { "  " + $_.Line }

if ($exit -eq 0) { Write-Host "PASS — mstscax completed a full session against the mock." }
else { Write-Host "FAIL — client did not reach the active state." }
exit $exit
