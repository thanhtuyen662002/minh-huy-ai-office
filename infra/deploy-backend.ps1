param(
    [string]$EnvironmentFile = ".env.production"
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $EnvironmentFile)) {
    throw "Missing $EnvironmentFile. Copy .env.production.example and populate it from approved secret sources."
}

& (Join-Path $PSScriptRoot "validate-production-env.ps1") -EnvironmentFile $EnvironmentFile

$dirty = (& git status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect Git working tree."
}
if ($dirty) {
    throw "Deployment requires a clean Git working tree so the release is attributable to one commit."
}

$releaseSha = (& git rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $releaseSha) {
    throw "Unable to resolve the current Git commit."
}

$env:AIOFFICE_RELEASE_TAG = $releaseSha.Substring(0, 12)

$composeBase = @(
    "compose",
    "--env-file", $EnvironmentFile,
    "-f", "compose.yaml",
    "-f", "compose.production.yaml"
)

Invoke-Checked -Command "docker" -Arguments ($composeBase + @("config", "--quiet"))
Invoke-Checked -Command "docker" -Arguments (
    $composeBase + @(
        "up", "-d", "--build", "--remove-orphans",
        "rabbitmq", "redis", "otel-collector", "jaeger", "core-api", "agent-worker"
    )
)

$portResult = (& docker @composeBase port core-api 8080).Trim()
if ($LASTEXITCODE -ne 0 -or -not $portResult) {
    throw "Unable to resolve Core.Api loopback port after deployment."
}

$healthUrl = "http://$portResult/health"
$healthy = $false

for ($attempt = 1; $attempt -le 30; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3
        if ($response.StatusCode -eq 200) {
            $healthy = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 2
    }
}

if (-not $healthy) {
    throw "Core.Api did not become healthy at $healthUrl. Keep traffic on the previous known-good release and inspect container logs."
}

Write-Host "Backend release $releaseSha is healthy at $healthUrl"
Write-Host "Do not expose this loopback endpoint directly. Route only through the approved secure tunnel/private network."
