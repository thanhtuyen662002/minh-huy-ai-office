#Requires -Version 7.0
<#
Creates the protected secrets folder and an initial env file for a single
Windows host. Generated values (RabbitMQ password, encryption keys, session
Redis password + TLS certificates, Keycloak bootstrap admin) never leave this
machine and are never printed.

Values that come from other systems are left as placeholders and listed at the
end: open the env file in Notepad on the host and paste them there. Never send
them through chat, email or Git.

Refuses to overwrite an existing env file.
#>
param(
    [Parameter(Mandatory = $true)][string]$WebHost,
    [Parameter(Mandatory = $true)][string]$ApiHost,
    [Parameter(Mandatory = $true)][string]$IdentityHost,
    [string]$SecretsDirectory = "C:\AIOffice\secrets",
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"

function New-RandomBytes([int]$Count) {
    return [System.Security.Cryptography.RandomNumberGenerator]::GetBytes($Count)
}

function New-Password {
    # URL-safe so it can be embedded in rediss:// URLs without escaping.
    return [Convert]::ToBase64String((New-RandomBytes 24)).Replace("+", "-").Replace("/", "_").TrimEnd("=")
}

function Set-EnvValue {
    param([System.Collections.Generic.List[string]]$Lines, [string]$Name, [string]$Value)
    $prefix = "$Name="
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].StartsWith($prefix, [StringComparison]::Ordinal)) {
            $Lines[$i] = $prefix + $Value
            return
        }
    }
    $Lines.Add($prefix + $Value)
}

function Write-SecretFile([string]$Path, [string]$Value) {
    Set-Content -LiteralPath $Path -Value $Value -NoNewline -Encoding ascii
}

$envFile = Join-Path $SecretsDirectory ".env.production"
if (Test-Path -LiteralPath $envFile) {
    throw "$envFile already exists. Edit it directly instead of regenerating secrets."
}

# Administrators, SYSTEM and the signed-in operator only. The operator is named
# explicitly because Docker Desktop runs unelevated and must read the mounts.
New-Item -ItemType Directory -Force -Path $SecretsDirectory | Out-Null
$operator = "$env:USERDOMAIN\$env:USERNAME"
& icacls $SecretsDirectory /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "${operator}:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Unable to restrict permissions on $SecretsDirectory."
}

$webOrigin = "https://$WebHost"
$identityOrigin = "https://$IdentityHost"
$issuer = "$identityOrigin/realms/aioffice"
$sessionRedisPassword = New-Password

$keycloakAdminUser = Join-Path $SecretsDirectory "keycloak-bootstrap-admin-username"
$keycloakAdminPassword = Join-Path $SecretsDirectory "keycloak-bootstrap-admin-password"
Write-SecretFile $keycloakAdminUser "bootstrap-admin"
Write-SecretFile $keycloakAdminPassword (New-Password)

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($line in Get-Content -LiteralPath (Join-Path $RepositoryRoot ".env.production.example")) {
    $lines.Add($line)
}

$generated = [ordered]@{
    RABBITMQ_DEFAULT_USER = "aioffice"
    RABBITMQ_DEFAULT_PASS = New-Password
    AIOFFICE_AUTHORITY = $issuer
    AIOFFICE_KEYCLOAK_ENABLED = "true"
    AIOFFICE_KEYCLOAK_HOSTNAME = $identityOrigin
    AIOFFICE_KEYCLOAK_DB_USERNAME_FILE = (Join-Path $SecretsDirectory "keycloak-db-username")
    AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE = (Join-Path $SecretsDirectory "keycloak-db-password")
    AIOFFICE_KEYCLOAK_ADMIN_USERNAME_FILE = $keycloakAdminUser
    AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE = $keycloakAdminPassword
    AIOFFICE_BROWSER_OIDC_ENABLED = "false"
    AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN = $webOrigin
    AIOFFICE_BROWSER_OIDC_ISSUER = $issuer
    AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT = "$issuer/protocol/openid-connect/auth"
    AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT = "$issuer/protocol/openid-connect/token"
    AIOFFICE_BROWSER_OIDC_JWKS_URI = "$issuer/protocol/openid-connect/certs"
    AIOFFICE_BROWSER_OIDC_CLIENT_ID = "aioffice-browser"
    AIOFFICE_BROWSER_OIDC_CLIENT_SECRET = ""
    AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY = [Convert]::ToBase64String((New-RandomBytes 32)).Replace("+", "-").Replace("/", "_").TrimEnd("=")
    AIOFFICE_BROWSER_CORE_API_ORIGIN = "https://$ApiHost"
    AIOFFICE_BROWSER_SESSION_REDIS_URL = "rediss://:$sessionRedisPassword@session-redis:6380/0"
    AIOFFICE_SECRET_ENCRYPTION_KEY = [Convert]::ToBase64String((New-RandomBytes 32))
    AIOFFICE_SESSION_REDIS_PASSWORD = $sessionRedisPassword
    AIOFFICE_VPS_TLS_DIR = (Join-Path $SecretsDirectory "session-redis-tls")
    AIOFFICE_VPS_WEB_HOST = $WebHost
    AIOFFICE_VPS_API_HOST = $ApiHost
    AIOFFICE_VPS_IDENTITY_HOST = $IdentityHost
}
foreach ($entry in $generated.GetEnumerator()) {
    Set-EnvValue -Lines $lines -Name $entry.Key -Value $entry.Value
}
Set-Content -LiteralPath $envFile -Value $lines -Encoding utf8NoBOM

# Private CA + server certificate for the session Redis TLS listener.
$tlsDirectory = $generated["AIOFFICE_VPS_TLS_DIR"]
New-Item -ItemType Directory -Force -Path $tlsDirectory | Out-Null
$openssl = @'
set -e
apk add --no-cache openssl >/dev/null
cd /out
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=AI Office session Redis CA" -keyout ca.key -out ca.crt 2>/dev/null
openssl req -newkey rsa:2048 -nodes -subj "/CN=session-redis" -keyout session-redis.key -out session-redis.csr 2>/dev/null
printf "subjectAltName=DNS:session-redis\nextendedKeyUsage=serverAuth\n" > ext.cnf
openssl x509 -req -in session-redis.csr -CA ca.crt -CAkey ca.key -CAcreateserial -days 3650 -extfile ext.cnf -out session-redis.crt 2>/dev/null
rm -f ca.key ca.srl session-redis.csr ext.cnf
chmod 644 session-redis.key
'@
& docker run --rm -v "${tlsDirectory}:/out" alpine:3.22 sh -c $openssl.Replace("`r", "")
if ($LASTEXITCODE -ne 0) {
    throw "Unable to create session Redis TLS certificates. Is Docker Desktop running?"
}

Write-Host "Created $envFile and generated host secrets."
Write-Host ""
Write-Host "Still to fill in on this machine (Notepad), from their owners:"
Write-Host "  AIOFFICE_DB_CONNECTION         - AI_Office connection string"
Write-Host "  AIOFFICE_KEYCLOAK_DB_HOST      - SQL Server host for AIOffice_Identity"
Write-Host "  PILOT_ERP_CONNECTION           - optional pilot ERP read-only connection"
Write-Host "  AIOFFICE_AI_PRIMARY_*          - AI provider base URL, model, Authorization header"
Write-Host "  $($generated['AIOFFICE_KEYCLOAK_DB_USERNAME_FILE'])  - Keycloak SQL login name (single value)"
Write-Host "  $($generated['AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE'])  - Keycloak SQL login password (single value)"
