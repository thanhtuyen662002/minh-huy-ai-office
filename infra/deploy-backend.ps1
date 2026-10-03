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
        "rabbitmq", "redis", "otel-collector", "jaeger", "core-api", "agent-worker", "web"
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

$workerReady = $false
for ($attempt = 1; $attempt -le 30; $attempt++) {
    $workerId = (& docker @composeBase ps -q agent-worker).Trim()
    if ($LASTEXITCODE -eq 0 -and $workerId) {
        $workerRunning = (& docker inspect -f "{{.State.Running}}" $workerId 2>$null).Trim()
        if ($LASTEXITCODE -eq 0 -and $workerRunning -eq "true") {
            $queueRows = @(& docker @composeBase exec -T rabbitmq rabbitmqctl list_queues name consumers -q 2>$null)
            if ($LASTEXITCODE -eq 0) {
                foreach ($row in $queueRows) {
                    if ($row -match "^\s*minhhuy\.work\.v1\s+([1-9][0-9]*)\s*$") {
                        $workerReady = $true
                        break
                    }
                }
            }
        }
    }

    if ($workerReady) {
        break
    }

    Start-Sleep -Seconds 2
}

if (-not $workerReady) {
    throw "Agent.Worker did not establish a RabbitMQ consumer on minhhuy.work.v1. Keep traffic on the previous known-good release and inspect worker/RabbitMQ logs."
}

$webPortResult = (& docker @composeBase port web 3000).Trim()
if ($LASTEXITCODE -ne 0 -or -not $webPortResult) {
    throw "Unable to resolve the FE loopback port after deployment."
}

$webUrl = "http://$webPortResult/"
$webReady = $false
for ($attempt = 1; $attempt -le 30; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri $webUrl -UseBasicParsing -TimeoutSec 3
        if ($response.StatusCode -eq 200) {
            $webReady = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 2
    }
}

if (-not $webReady) {
    throw "FE did not serve the application at $webUrl. Keep traffic on the previous known-good release and inspect web logs."
}

Write-Host "Application release $releaseSha has a healthy Core.Api at $healthUrl"
Write-Host "FE is serving at $webUrl"
Write-Host "Agent.Worker is consuming minhhuy.work.v1"
Write-Host "Do not expose this loopback endpoint directly. Route only through the approved secure tunnel/private network."
