<#
.SYNOPSIS
  One-command real-client checkpoint: builds the mock, starts it as a single process,
  runs the FreeRDP checkpoint against it, then tears the server down.

.EXAMPLE
  pwsh scripts/run-checkpoint.ps1 -ExpectStage tls
#>
param(
    [int]$Port = 33389,
    [ValidateSet("tcp", "x224", "tls", "mcs", "capabilities", "active")]
    [string]$ExpectStage = "tls",
    [int]$TimeoutSec = 12
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "src\MockRdp\bin\Debug\net10.0\MockRdp.exe"

Write-Host "Building MockRdp..."
& dotnet build (Join-Path $repo "src\MockRdp\MockRdp.csproj") -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "Starting server on port $Port (single process)..."
$server = Start-Process -FilePath $exe -ArgumentList "--port", $Port, "--log-level", "debug" -PassThru -WindowStyle Minimized
try {
    # Wait for the listener to accept connections.
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { $c = [Net.Sockets.TcpClient]::new(); $c.Connect("127.0.0.1", $Port); $c.Close(); $ready = $true; break }
        catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $ready) { throw "server did not open port $Port" }

    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "freerdp-checkpoint.ps1") -Port $Port -ExpectStage $ExpectStage -TimeoutSec $TimeoutSec
    $checkpointExit = $LASTEXITCODE
}
finally {
    if ($server -and -not $server.HasExited) { $server.Kill(); $server.WaitForExit(3000) }
}

exit $checkpointExit
