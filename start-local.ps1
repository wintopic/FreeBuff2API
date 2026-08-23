[CmdletBinding()]
param(
    [switch]$Foreground
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$envFile = Join-Path $repoRoot '.env'
$sourceCredential = Join-Path $repoRoot 'freebuff_tools\freebuff_credentials.json'
$credentialDir = Join-Path $repoRoot 'credentials'
$runtimeDir = Join-Path $repoRoot 'logs'
$pidFile = Join-Path $runtimeDir 'freebuff2api.pid'
$stdoutLog = Join-Path $runtimeDir 'freebuff2api.out.log'
$stderrLog = Join-Path $runtimeDir 'freebuff2api.err.log'

if (-not (Test-Path -LiteralPath $envFile)) {
    throw "Missing local configuration: $envFile"
}

$config = @{}
foreach ($line in Get-Content -LiteralPath $envFile) {
    if (-not $line -or $line.TrimStart().StartsWith('#')) { continue }
    $pair = $line.Split('=', 2)
    if ($pair.Count -eq 2) { $config[$pair[0].Trim()] = $pair[1].Trim() }
}

$listenPort = if ($config.ContainsKey('PORT') -and $config['PORT']) { [int]$config['PORT'] } else { 8877 }
$listenHost = if ($config.ContainsKey('HOST') -and $config['HOST']) { $config['HOST'] } else { '127.0.0.1' }
$proxyAddress = if ($config.ContainsKey('HTTPS_PROXY') -and $config['HTTPS_PROXY']) {
    $config['HTTPS_PROXY']
} elseif ($config.ContainsKey('HTTP_PROXY') -and $config['HTTP_PROXY']) {
    $config['HTTP_PROXY']
} else {
    $null
}
if (-not $proxyAddress) {
    throw 'HTTP_PROXY/HTTPS_PROXY is not configured in .env'
}

$proxyUri = [Uri]$proxyAddress
$proxyListener = Get-NetTCPConnection -State Listen -LocalPort $proxyUri.Port -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -in @($proxyUri.Host, '0.0.0.0', '::', '::1') } |
    Select-Object -First 1
if (-not $proxyListener) {
    throw "Local proxy is not listening at $proxyAddress"
}

$nodeExe = (Get-Command node -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

if (Test-Path -LiteralPath $sourceCredential) {
    New-Item -ItemType Directory -Path $credentialDir -Force | Out-Null
    Copy-Item -LiteralPath $sourceCredential -Destination (Join-Path $credentialDir 'freebuff_credentials.json') -Force
} else {
    Write-Warning 'No Freebuff credential found yet. Health checks will work, but chat requests will return 503 until login-freebuff.ps1 is completed.'
}

$existingListener = Get-NetTCPConnection -State Listen -LocalPort $listenPort -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($existingListener) {
    try {
        $health = Invoke-RestMethod -Uri "http://${listenHost}:$listenPort/healthz" -TimeoutSec 3
        if ($health.status) {
            Set-Content -LiteralPath $pidFile -Value $existingListener.OwningProcess -Encoding ascii
            Write-Host "freebuff2api is already running at http://${listenHost}:$listenPort (PID $($existingListener.OwningProcess))."
            exit 0
        }
    } catch {
    }
    throw "Port $listenPort is already occupied by PID $($existingListener.OwningProcess)."
}

$nodeArgs = @('--use-env-proxy', "--env-file=$envFile", 'server.js')
if ($Foreground) {
    Set-Location -LiteralPath $repoRoot
    & $nodeExe @nodeArgs
    exit $LASTEXITCODE
}

$process = Start-Process -FilePath $nodeExe `
    -ArgumentList $nodeArgs `
    -WorkingDirectory $repoRoot `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -WindowStyle Hidden `
    -PassThru

Set-Content -LiteralPath $pidFile -Value $process.Id -Encoding ascii
$deadline = (Get-Date).AddSeconds(15)
do {
    Start-Sleep -Milliseconds 300
    $process.Refresh()
    if ($process.HasExited) { break }
    try {
        $health = Invoke-RestMethod -Uri "http://${listenHost}:$listenPort/healthz" -TimeoutSec 2
        if ($health.status) {
            Write-Host "freebuff2api started at http://${listenHost}:$listenPort (PID $($process.Id))."
            Write-Host "Proxy: configured"
            Write-Host "Logs: $stdoutLog and $stderrLog"
            exit 0
        }
    } catch {
    }
} while ((Get-Date) -lt $deadline)

if (-not $process.HasExited) {
    Stop-Process -Id $process.Id -Force
}
Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
$stderrTail = if (Test-Path -LiteralPath $stderrLog) { Get-Content -LiteralPath $stderrLog -Tail 30 } else { @() }
throw "freebuff2api failed to start. $($stderrTail -join [Environment]::NewLine)"
