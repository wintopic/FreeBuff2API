[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$pidFile = Join-Path $repoRoot 'logs\freebuff2api.pid'
$envFile = Join-Path $repoRoot '.env'

if (-not (Test-Path -LiteralPath $pidFile)) {
    Write-Host 'freebuff2api is not running (PID file not found).'
    exit 0
}

$servicePid = [int](Get-Content -LiteralPath $pidFile -Raw).Trim()
$processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $servicePid" -ErrorAction SilentlyContinue
if (-not $processInfo) {
    Remove-Item -LiteralPath $pidFile -Force
    Write-Host 'freebuff2api is already stopped.'
    exit 0
}

$listenPort = 8877
if (Test-Path -LiteralPath $envFile) {
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line -match '^PORT=(\d+)$') { $listenPort = [int]$Matches[1] }
    }
}
$listener = Get-NetTCPConnection -State Listen -LocalPort $listenPort -ErrorAction SilentlyContinue |
    Where-Object { $_.OwningProcess -eq $servicePid } |
    Select-Object -First 1
if ($processInfo.Name -notmatch '^node(\.exe)?$' -or
    $processInfo.CommandLine -notmatch 'server\.js' -or
    -not $listener) {
    throw "PID $servicePid does not match this freebuff2api deployment; refusing to stop it."
}

Stop-Process -Id $servicePid
Wait-Process -Id $servicePid -Timeout 10 -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
Write-Host "freebuff2api stopped (PID $servicePid)."
