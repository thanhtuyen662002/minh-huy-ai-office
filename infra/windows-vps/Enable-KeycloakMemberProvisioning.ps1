#Requires -Version 7.0
<#
Turns on "Thêm thành viên" account creation for an aioffice realm that is already imported:
  - enables the aioffice-member-provisioner client with a secret kept in a protected file
    (reused when the file already exists, otherwise generated here; never printed),
  - makes sure its service account can manage users,
  - gives the browser client a base URL so Keycloak's "back to application" link returns to AI Office,
  - sets AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED=true and the secret file path in the env file.
Safe to run again. Afterwards redeploy the application (Update-AIOffice.ps1 -Phase Deploy -SkipFetch)
so Core API picks up the flag. Invites then email members a "choose your password" link
(needs the mail settings from Configure-KeycloakEmail.ps1).
#>
param(
    [string]$EnvironmentFile = "C:\AIOffice\secrets\.env.production",
    [string]$KeycloakUrl
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'Setup-Helpers.ps1')

function Read-EnvFile([string]$Path) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding utf8) {
        $index = $line.IndexOf('=')
        if ($line.TrimStart().StartsWith('#') -or $index -lt 1) { continue }
        $values[$line.Substring(0, $index).Trim()] = ConvertFrom-SetupEnvValue $line.Substring($index + 1)
    }
    return $values
}

function Read-SecretFile([string]$Path, [string]$What) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing $What file." }
    $value = (Get-Content -LiteralPath $Path -Raw -Encoding utf8).Trim()
    if (-not $value -or $value -like '*replace-with-*') { throw "$What file is empty or a placeholder." }
    return $value
}

$environment = Read-EnvFile $EnvironmentFile
if ($environment['AIOFFICE_KEYCLOAK_ENABLED'] -cne 'true') { throw 'AIOFFICE_KEYCLOAK_ENABLED must be true first.' }
$publicOrigin = $environment['AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN']
if (-not $publicOrigin) { throw 'AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN is missing in the env file.' }
$browserClientId = if ($environment['AIOFFICE_BROWSER_OIDC_CLIENT_ID']) { $environment['AIOFFICE_BROWSER_OIDC_CLIENT_ID'] } else { 'aioffice-browser' }
if (-not $KeycloakUrl) {
    $kcPort = if ($environment['AIOFFICE_KEYCLOAK_PORT']) { $environment['AIOFFICE_KEYCLOAK_PORT'] } else { '8081' }
    $KeycloakUrl = "http://127.0.0.1:$kcPort"
}
$KeycloakUrl = $KeycloakUrl.TrimEnd('/')

# Provisioning client secret: reuse the protected file when present, else create it.
$secretFile = $environment['AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE']
if (-not $secretFile -or $secretFile -eq '/dev/null') {
    $secretFile = Join-Path (Split-Path -Parent $EnvironmentFile) 'keycloak-provisioning-client-secret'
}
$secret = $null
if (Test-Path -LiteralPath $secretFile -PathType Leaf) {
    $existing = (Get-Content -LiteralPath $secretFile -Raw -Encoding utf8).Trim()
    if ($existing.Length -ge 32 -and $existing -notlike '*replace-with-*') { $secret = $existing }
}
if (-not $secret) {
    $secret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
    [IO.File]::WriteAllText($secretFile, $secret, [Text.UTF8Encoding]::new($false))
}

$adminUser = Read-SecretFile $environment['AIOFFICE_KEYCLOAK_ADMIN_USERNAME_FILE'] 'Keycloak admin username'
$adminPassword = Read-SecretFile $environment['AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE'] 'Keycloak admin password'
try {
    $token = Invoke-RestMethod -Method Post -Uri "$KeycloakUrl/realms/master/protocol/openid-connect/token" -Body @{
        client_id = 'admin-cli'; grant_type = 'password'; username = $adminUser; password = $adminPassword
    }
}
catch { throw "Could not sign in to Keycloak admin at $KeycloakUrl (is Keycloak running, is the bootstrap admin still there?)." }
$headers = @{ Authorization = "Bearer $($token.access_token)" }
$realmUrl = "$KeycloakUrl/admin/realms/aioffice"
# Invoke-RestMethod hands a JSON array down the pipeline as one object; unwrap it.
function Get-First($Items) { foreach ($item in @($Items)) { foreach ($inner in @($item)) { return $inner } } }
function Send-Json([string]$Method, [string]$Uri, $Body) {
    Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 6 -AsArray:($Body -is [array])))) | Out-Null
}

# 1. Provisioning client: enabled, confidential, service account only, our secret.
$client = (Get-First (Invoke-RestMethod -Uri "$realmUrl/clients?clientId=aioffice-member-provisioner" -Headers $headers))
if (-not $client) { throw 'Client aioffice-member-provisioner is missing from the aioffice realm.' }
$client.enabled = $true
$client | Add-Member -Force -NotePropertyName secret -NotePropertyValue $secret
Send-Json Put "$realmUrl/clients/$($client.id)" $client
$check = Invoke-RestMethod -Uri "$realmUrl/clients/$($client.id)/client-secret" -Headers $headers
if ($check.value -cne $secret) { throw 'Keycloak did not keep the provisioning client secret.' }

# 2. Service account may manage users (create accounts, send setup emails).
$serviceUser = Invoke-RestMethod -Uri "$realmUrl/clients/$($client.id)/service-account-user" -Headers $headers
$management = (Get-First (Invoke-RestMethod -Uri "$realmUrl/clients?clientId=realm-management" -Headers $headers))
$assigned = @(Invoke-RestMethod -Uri "$realmUrl/users/$($serviceUser.id)/role-mappings/clients/$($management.id)" -Headers $headers | ForEach-Object { $_ })
if (-not ($assigned | Where-Object name -eq 'manage-users')) {
    $role = Invoke-RestMethod -Uri "$realmUrl/clients/$($management.id)/roles/manage-users" -Headers $headers
    Send-Json Post "$realmUrl/users/$($serviceUser.id)/role-mappings/clients/$($management.id)" @(@{ id = $role.id; name = $role.name })
}

# 3. Browser client base URL for Keycloak's "back to application" link after a password is set.
$browser = (Get-First (Invoke-RestMethod -Uri "$realmUrl/clients?clientId=$([Uri]::EscapeDataString($browserClientId))" -Headers $headers))
if (-not $browser) { throw "Browser client $browserClientId is missing from the aioffice realm." }
$baseUrl = $publicOrigin.TrimEnd('/') + '/'
if ($browser.baseUrl -ne $baseUrl) {
    $browser | Add-Member -Force -NotePropertyName baseUrl -NotePropertyValue $baseUrl
    Send-Json Put "$realmUrl/clients/$($browser.id)" $browser
}

# 4. Env file flags for Core API.
$lines = [System.Collections.Generic.List[string]]::new([string[]](Get-Content -LiteralPath $EnvironmentFile -Encoding utf8))
Set-EnvValue $lines 'AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED' 'true'
Set-EnvValue $lines 'AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE' $secretFile
[IO.File]::WriteAllLines($EnvironmentFile, $lines, [Text.UTF8Encoding]::new($false))

$realm = Invoke-RestMethod -Uri $realmUrl -Headers $headers
if (-not $realm.smtpServer.host) { Write-Warning 'No mail settings yet: run Configure-KeycloakEmail.ps1, or invites fall back to a temporary password.' }
Write-Host "Member account creation is on (secret file: $secretFile). Redeploy the application so Core API uses it."
