param(
    [string]$EnvironmentFile = ".env.production"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $EnvironmentFile)) {
    throw "Missing production environment file. Copy .env.production.example and populate it from approved secret sources."
}

$values = @{}

foreach ($line in Get-Content -LiteralPath $EnvironmentFile) {
    $trimmed = $line.Trim()

    if (-not $trimmed -or $trimmed.StartsWith("#")) {
        continue
    }

    $parts = $trimmed -split "=", 2
    if ($parts.Count -ne 2 -or -not $parts[0].Trim()) {
        throw "Invalid production environment entry. Expected NAME=VALUE syntax."
    }

    $values[$parts[0].Trim()] = $parts[1].Trim()
}

$requiredValues = @(
    "RABBITMQ_DEFAULT_USER",
    "RABBITMQ_DEFAULT_PASS",
    "AIOFFICE_DB_CONNECTION"
)

foreach ($name in $requiredValues) {
    if (-not $values.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($values[$name])) {
        throw "$name must be populated before production deployment."
    }

    $value = $values[$name]

    if ($value -match "(?i)replace-with-") {
        throw "$name still contains a tracked placeholder value."
    }
}

if ($values["RABBITMQ_DEFAULT_USER"] -eq "ai-office-dev") {
    throw "RABBITMQ_DEFAULT_USER must not use the local-development identity in production."
}

Write-Host "Production environment preflight passed for required runtime values."
