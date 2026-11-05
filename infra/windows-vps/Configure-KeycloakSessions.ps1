#Requires -Version 7.0
<#
Keeps a working day signed in. AI Office is only used inside the company, so the Keycloak
sign-in of the aioffice realm lasts 12 hours, idle or not, instead of Keycloak's 30 minute
idle default; the page renews its short-lived access silently within that sign-in.
Safe to run again; it only changes ssoSessionIdleTimeout and ssoSessionMaxLifespan on the
live realm (the realm file is imported once, so later edits to it do not reach an existing realm).
#>
param(
    [string]$EnvironmentFile = "C:\AIOffice\secrets\.env.production",
    [string]$KeycloakUrl,
    [int]$SessionSeconds = 43200
)

$ErrorActionPreference = "Stop"

function Read-KeyValueFile([string]$Path) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding utf8) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        $index = $trimmed.IndexOf('=')
        if ($index -lt 1) { continue }
        $values[$trimmed.Substring(0, $index).Trim()] = $trimmed.Substring($index + 1).Trim().Trim('"', "'")
    }
    return $values
}

function Read-SecretFile([string]$Path, [string]$What) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing $What file." }
    $value = (Get-Content -LiteralPath $Path -Raw -Encoding utf8).Trim()
    if (-not $value -or $value -like '*replace-with-*') { throw "$What file is empty or a placeholder." }
    return $value
}

if ($SessionSeconds -lt 3600 -or $SessionSeconds -gt 86400) { throw 'SessionSeconds must be between 1 and 24 hours.' }
$environment = Read-KeyValueFile $EnvironmentFile
if (-not $KeycloakUrl) {
    $kcPort = if ($environment['AIOFFICE_KEYCLOAK_PORT']) { $environment['AIOFFICE_KEYCLOAK_PORT'] } else { '8081' }
    $KeycloakUrl = "http://127.0.0.1:$kcPort"
}
$KeycloakUrl = $KeycloakUrl.TrimEnd('/')
$adminUser = Read-SecretFile $environment['AIOFFICE_KEYCLOAK_ADMIN_USERNAME_FILE'] 'Keycloak admin username'
$adminPassword = Read-SecretFile $environment['AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE'] 'Keycloak admin password'

try {
    $token = Invoke-RestMethod -Method Post -Uri "$KeycloakUrl/realms/master/protocol/openid-connect/token" -Body @{
        client_id = 'admin-cli'; grant_type = 'password'; username = $adminUser; password = $adminPassword
    }
}
catch {
    throw "Could not sign in to Keycloak admin at $KeycloakUrl (is Keycloak running, is the bootstrap admin still there?)."
}
$headers = @{ Authorization = "Bearer $($token.access_token)" }
$realmUrl = "$KeycloakUrl/admin/realms/aioffice"

$update = @{ ssoSessionIdleTimeout = $SessionSeconds; ssoSessionMaxLifespan = $SessionSeconds } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri $realmUrl -Headers $headers -ContentType 'application/json' -Body $update | Out-Null

$realm = Invoke-RestMethod -Method Get -Uri $realmUrl -Headers $headers
if ($realm.ssoSessionIdleTimeout -ne $SessionSeconds -or $realm.ssoSessionMaxLifespan -ne $SessionSeconds) {
    throw 'Keycloak did not keep the sign-in lifetime.'
}
Write-Host "Sign-ins last $([math]::Round($SessionSeconds / 3600, 1)) hours."
