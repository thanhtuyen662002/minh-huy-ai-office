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

function Require-ProductionValue {
    param([string]$Name)
    if (-not $values.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace($values[$Name])) {
        throw "$Name must be populated before production deployment."
    }
    if ($values[$Name] -match "(?i)replace-with-|\.example\.invalid") {
        throw "$Name still contains a tracked placeholder value."
    }
}

$requiredValues = @(
    "RABBITMQ_DEFAULT_USER",
    "RABBITMQ_DEFAULT_PASS",
    "AIOFFICE_DB_CONNECTION",
    "AIOFFICE_AUTHORITY",
    "AIOFFICE_AUDIENCE",
    "PILOT_ERP_CONNECTION"
)

foreach ($name in $requiredValues) {
    Require-ProductionValue $name
}

if ($values["RABBITMQ_DEFAULT_USER"] -eq "ai-office-dev") {
    throw "RABBITMQ_DEFAULT_USER must not use the local-development identity in production."
}

function Require-HttpsOrigin {
    param([string]$Name)
    Require-ProductionValue $Name
    $uri = $null
    $value = $values[$Name]
    if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne "https" -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.IsLoopback -or $value -cne $uri.GetLeftPart([UriPartial]::Authority)) {
        throw "$Name must be a canonical HTTPS origin without a trailing slash, credentials, path, query or fragment."
    }
}

if ($values.ContainsKey("AIOFFICE_KEYCLOAK_ENABLED") -and
    $values["AIOFFICE_KEYCLOAK_ENABLED"] -cnotin @("true", "false")) {
    throw "AIOFFICE_KEYCLOAK_ENABLED must be true or false."
}

if ($values["AIOFFICE_KEYCLOAK_ENABLED"] -ceq "true") {
    Require-HttpsOrigin "AIOFFICE_KEYCLOAK_HOSTNAME"
    Require-HttpsOrigin "AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN"
    $issuer = $values["AIOFFICE_KEYCLOAK_HOSTNAME"] + "/realms/aioffice"
    $expectedUrls = @{
        AIOFFICE_AUTHORITY = $issuer
        AIOFFICE_BROWSER_OIDC_ISSUER = $issuer
        AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT = "$issuer/protocol/openid-connect/auth"
        AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT = "$issuer/protocol/openid-connect/token"
        AIOFFICE_BROWSER_OIDC_JWKS_URI = "$issuer/protocol/openid-connect/certs"
    }
    foreach ($name in $expectedUrls.Keys) {
        Require-ProductionValue $name
        if ($values[$name] -cne $expectedUrls[$name]) {
            throw "$name must match the configured Keycloak aioffice realm endpoint exactly."
        }
    }
    Require-ProductionValue "AIOFFICE_BROWSER_OIDC_CLIENT_ID"
    if ($values["AIOFFICE_BROWSER_OIDC_CLIENT_ID"] -cne "aioffice-browser") {
        throw "AIOFFICE_BROWSER_OIDC_CLIENT_ID must be aioffice-browser for the production realm."
    }
    if ($values["AIOFFICE_BROWSER_OIDC_CLIENT_SECRET"]) {
        throw "AIOFFICE_BROWSER_OIDC_CLIENT_SECRET must be empty for the public Code/S256 client."
    }
    if ($values["AIOFFICE_BROWSER_OIDC_LOCAL_HTTP"] -and $values["AIOFFICE_BROWSER_OIDC_LOCAL_HTTP"] -cne "false") {
        throw "AIOFFICE_BROWSER_OIDC_LOCAL_HTTP must be false in production."
    }
    # Strict host grammar prevents JDBC property injection; databaseName and TLS are fixed in Compose.
    Require-ProductionValue "AIOFFICE_KEYCLOAK_DB_HOST"
    if ($values["AIOFFICE_KEYCLOAK_DB_HOST"] -cnotmatch '\A[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?\z') {
        throw "AIOFFICE_KEYCLOAK_DB_HOST must be a private SQL DNS name or IPv4 address, without JDBC properties."
    }
    foreach ($name in @("AIOFFICE_KEYCLOAK_PORT", "AIOFFICE_KEYCLOAK_DB_PORT")) {
        Require-ProductionValue $name
        $port = 0
        if ($values[$name] -cnotmatch '\A[1-9][0-9]{0,4}\z' -or
            -not [int]::TryParse($values[$name], [ref]$port) -or $port -gt 65535) {
            throw "$name must be a port between 1 and 65535."
        }
    }
    foreach ($name in @("AIOFFICE_KEYCLOAK_DB_USERNAME_FILE", "AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE",
            "AIOFFICE_KEYCLOAK_ADMIN_USERNAME_FILE", "AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE")) {
        Require-ProductionValue $name
        if (-not [IO.Path]::IsPathFullyQualified($values[$name]) -or
            -not (Test-Path -LiteralPath $values[$name] -PathType Leaf) -or
            (Get-Item -LiteralPath $values[$name]).Length -eq 0) {
            throw "$name must reference an existing nonempty protected secret file using an absolute path."
        }
    }
}

if ($values.ContainsKey("AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED") -and
    $values["AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED"] -cnotin @("true", "false")) {
    throw "AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED must be true or false."
}
if ($values["AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED"] -ceq "true") {
    if ($values["AIOFFICE_KEYCLOAK_ENABLED"] -cne "true") {
        throw "AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED requires AIOFFICE_KEYCLOAK_ENABLED=true."
    }
    $name = "AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE"
    Require-ProductionValue $name
    if (-not [IO.Path]::IsPathFullyQualified($values[$name]) -or
        -not (Test-Path -LiteralPath $values[$name] -PathType Leaf)) {
        throw "$name must reference an existing protected secret file using an absolute path."
    }
    $secret = [IO.File]::ReadAllText($values[$name]).TrimEnd([char]13, [char]10)
    if ([string]::IsNullOrWhiteSpace($secret) -or $secret -match '(?i)replace-with-' -or
        $secret.Length -lt 32 -or $secret -match '[\r\n]') {
        throw "$name must contain a single non-placeholder secret of at least 32 characters."
    }
    $secret = $null
}

Write-Host "Production environment preflight passed for required runtime values."
