param(
    [string]$EnvironmentFile = ".env.production"
)

$ErrorActionPreference = "Stop"

function Normalize-EnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowEmptyString()]
        [string]$Value
    )

    $normalized = $Value.Trim()
    if ($normalized.Length -eq 0) {
        return $normalized
    }

    $quoteCharacters = @([char]34, [char]39)
    $startsWithQuote = $quoteCharacters -contains $normalized[0]
    $endsWithQuote = $quoteCharacters -contains $normalized[$normalized.Length - 1]

    if ($startsWithQuote -or $endsWithQuote) {
        if (-not $startsWithQuote -or -not $endsWithQuote -or $normalized[0] -ne $normalized[$normalized.Length - 1]) {
            throw "$Name has unmatched or mixed quotes."
        }

        $normalized = $normalized.Substring(1, $normalized.Length - 2).Trim()
    }

    return $normalized
}

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

    $name = $parts[0].Trim()
    $values[$name] = Normalize-EnvironmentValue -Name $name -Value $parts[1]
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
