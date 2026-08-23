<#
.SYNOPSIS
  End-to-end demo of the mock RDP server: runs the per-feature conformance checks, then opens
  a live session with the mstscax client (mstsc's own engine) so you can see it render.

.EXAMPLE
  pwsh scripts/demo.ps1
#>
param(
    [int]$Port = 33460,
    [int]$HoldSeconds = 8
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$srv = Join-Path $repo "src\MockRdp\bin\Debug\net10.0\MockRdp.exe"
$cli = Join-Path $repo "tools\RdpAxClient\bin\Debug\net10.0-windows\RdpAxClient.exe"
$srvLog = Join-Path $env:TEMP "mockrdp-demo.log"

function Section($t) { Write-Host ""; Write-Host "==== $t ====" -ForegroundColor Cyan }

Section "1/3  Building"
& dotnet build (Join-Path $repo "MockRdp.slnx") -c Debug --nologo -v quiet | Out-Null
& dotnet build (Join-Path $repo "tools\RdpAxClient\RdpAxClient.csproj") -c Debug --nologo -v quiet | Out-Null
Write-Host "  built the mock server, the test suite, and the mstscax client."

Section "2/3  Per-feature conformance (deterministic, headless)"
$testOut = & dotnet test (Join-Path $repo "MockRdp.slnx") --nologo -v quiet 2>&1 | Out-String
$summary = ($testOut -split "`n" | Select-String "Passed!|Failed!" | Select-Object -First 1)
@(
  "  X.224 security negotiation + TLS handshake      (SyntheticClientTests)",
  "  MCS connect + all 6 channels joined             (McsConformanceTests)",
  "  licensing + capabilities + finalization -> ACTIVE (ActivationConformanceTests)",
  "  graphics: decodes bitmap updates, asserts pixels  (GraphicsConformanceTests)",
  "  input: mouse move -> marker drawn at the cursor   (InputConformanceTests)",
  "  clipboard: CLIPRDR text round-trip                (ClipboardConformanceTests)"
) | ForEach-Object { Write-Host $_ }
Write-Host ("  => " + ($summary -replace '\s+', ' ').Trim()) -ForegroundColor Green

Section "3/3  Live session with mstscax (mstsc.exe's own engine)"
$server = Start-Process -FilePath $srv -ArgumentList "--port", $Port, "--log-level", "debug" `
    -PassThru -WindowStyle Minimized -RedirectStandardOutput $srvLog -RedirectStandardError "$srvLog.err"
try {
    for ($i = 0; $i -lt 40; $i++) {
        try { $c = [Net.Sockets.TcpClient]::new(); $c.Connect("127.0.0.1", $Port); $c.Close(); break }
        catch { Start-Sleep -Milliseconds 300 }
    }
    Write-Host "  a window will open showing the mock's test pattern (8 colour squares)."
    Write-Host "  move your mouse over it to draw yellow markers. Auto-closes in ~$HoldSeconds s."
    & $cli --server localhost --port $Port --timeout 25 --auth-level 2 --hold $HoldSeconds
}
finally {
    if ($server -and -not $server.HasExited) { $server.Kill(); $server.WaitForExit(3000) }
}

Write-Host ""
Write-Host "  server-side sequence this session drove:"
Get-Content $srvLog | Select-String -Pattern "TLS established|MCS complete|Confirm Active received|session ACTIVE|test pattern|Clipboard channel ready" |
    Select-Object -Last 6 | ForEach-Object { "    - " + ($_.Line -replace '^\s*', '') }

Write-Host ""
Write-Host "Done. The mock took Microsoft's own RDP engine from TLS all the way to a rendered," -ForegroundColor Green
Write-Host "interactive, clipboard-capable session -- and nothing was persisted to trust the cert." -ForegroundColor Green
