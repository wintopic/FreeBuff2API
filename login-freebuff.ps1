[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$credentialSource = Join-Path $repoRoot 'freebuff_tools\freebuff_credentials.json'
$credentialDir = Join-Path $repoRoot 'credentials'
$envFile = Join-Path $repoRoot '.env'

if (Test-Path -LiteralPath $envFile) {
    $config = @{}
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if (-not $line -or $line.TrimStart().StartsWith('#')) { continue }
        $pair = $line.Split('=', 2)
        if ($pair.Count -eq 2) { $config[$pair[0].Trim()] = $pair[1].Trim() }
    }
    foreach ($name in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')) {
        if ($config.ContainsKey($name) -and $config[$name]) {
            [Environment]::SetEnvironmentVariable($name, $config[$name], 'Process')
        }
    }
}

Set-Location -LiteralPath $repoRoot
python .\freebuff_tools\extract_freebuff.py login
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $credentialSource)) {
    throw "Login completed but credential file was not created: $credentialSource"
}

New-Item -ItemType Directory -Path $credentialDir -Force | Out-Null
Copy-Item -LiteralPath $credentialSource -Destination (Join-Path $credentialDir 'freebuff_credentials.json') -Force
Write-Host 'Credential installed. Restarting the local API so it can load the token.'
& (Join-Path $repoRoot 'stop-local.ps1')
& (Join-Path $repoRoot 'start-local.ps1')
