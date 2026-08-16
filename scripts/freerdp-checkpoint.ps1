<#
.SYNOPSIS
  Real-client checkpoint: drives FreeRDP (wfreerdp) against the mock RDP server, captures a
  TRACE log, and reports the furthest stage of the connection sequence the client reached.

.DESCRIPTION
  Launches wfreerdp with TLS security forced (the mock is TLS-only for now), waits for it to
  connect and then fail at the first unimplemented layer, kills it, and scans the log for
  ordered stage markers. Exits 0 if the client reached at least -ExpectStage, else 1.

.EXAMPLE
  pwsh scripts/freerdp-checkpoint.ps1 -Port 33890 -ExpectStage tls
#>
param(
    [string]$RdpHost = "127.0.0.1",
    [Parameter(Mandatory)][int]$Port,
    [ValidateSet("tcp", "x224", "tls", "mcs", "capabilities", "active")]
    [string]$ExpectStage = "tls",
    [int]$TimeoutSec = 12,
    [string]$LogDir = "$env:TEMP\mockrdp-freerdp"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$logName = "freerdp-{0}.log" -f $Port
$logFile = Join-Path $LogDir $logName
if (Test-Path $logFile) { Remove-Item $logFile -Force }

# FreeRDP logs via WLog; route it to a file appender the child process inherits.
$env:WLOG_APPENDER = "FILE"
$env:WLOG_LEVEL = "TRACE"
$env:WLOG_FILEAPPENDER_OUTPUT_FILE_PATH = $LogDir
$env:WLOG_FILEAPPENDER_OUTPUT_FILE_NAME = $logName

# Ordered stages and the FreeRDP log markers that indicate each was reached.
$stageOrder = @("tcp", "x224", "tls", "mcs", "capabilities", "active")
# Markers chosen to fire only when a stage was genuinely reached (mostly FreeRDP's own
# connection-state transitions and successful-parse debug lines).
$stageMarkers = [ordered]@{
    tcp          = @("com.freerdp.core.transport")
    x224         = @("Negotiated \[SSL\]", "selected_protocol", "CONNECTION_STATE_NEGO")
    tls          = @("tls_verify_certificate", "TLS connection", "CONNECTION_STATE_MCS_CREATE")
    mcs          = @("gcc_read_server_security_data", "CONNECTION_STATE_MCS_ERECT_DOMAIN",
                     "CONNECTION_STATE_MCS_ATTACH_USER", "CONNECTION_STATE_MCS_CHANNEL")
    capabilities = @("CONNECTION_STATE_LICENSING", "CONNECTION_STATE_CAPABILITIES",
                     "Demand Active", "demand_active")
    active       = @("CONNECTION_STATE_ACTIVE", "CONNECTION_STATE_FINALIZATION", "connected to")
}

$rdpArgs = @(
    "/v:$($RdpHost):$Port", "/cert:ignore", "/sec:tls",
    "/u:test", "/p:test", "/log-level:TRACE",
    "/timeout:$([int]($TimeoutSec * 1000))"
)

Write-Host "Launching: wfreerdp $($rdpArgs -join ' ')"
$proc = Start-Process -FilePath "wfreerdp" -ArgumentList $rdpArgs -PassThru -WindowStyle Minimized

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while (-not $proc.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(2000) }

if (-not (Test-Path $logFile)) {
    Write-Host "FAIL: no log produced (is wfreerdp installed and did it launch?)"
    exit 1
}
$log = Get-Content -Raw $logFile

# Determine the furthest stage whose markers appear in the log.
$reachedIndex = -1
foreach ($stage in $stageOrder) {
    $hit = $false
    foreach ($m in $stageMarkers[$stage]) { if ($log -match [regex]::Escape($m) -or $log -match $m) { $hit = $true; break } }
    if ($hit) { $reachedIndex = [Math]::Max($reachedIndex, $stageOrder.IndexOf($stage)) }
}
$reached = if ($reachedIndex -ge 0) { $stageOrder[$reachedIndex] } else { "(none)" }
$expectIndex = $stageOrder.IndexOf($ExpectStage)

Write-Host "Furthest stage reached: $reached (expected >= $ExpectStage)"
Write-Host "Log: $logFile"
if ($reachedIndex -ge $expectIndex) { Write-Host "PASS"; exit 0 }
else {
    Write-Host "FAIL"
    Write-Host "--- last 25 log lines ---"
    Get-Content $logFile -Tail 25 | ForEach-Object { Write-Host $_ }
    exit 1
}
