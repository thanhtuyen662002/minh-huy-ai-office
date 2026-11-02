#Requires -Version 7.0
<#
Turns on "Quên mật khẩu?" (password reset by email) for the aioffice realm and sets the
mailbox Keycloak sends from. Safe to run again; it only changes resetPasswordAllowed and
the reset-link lifetime and smtpServer on the live realm (the realm file is imported once, so later edits to it do
not reach an existing realm).

Mail settings live in a private file next to the env file (never in Git, never in chat):

  C:\AIOffice\secrets\keycloak-smtp.env
    SMTP_HOST=smtp.gmail.com
    SMTP_PORT=587
    SMTP_USER=quantri@example.com
    SMTP_FROM=quantri@example.com
    SMTP_FROM_NAME=AI Office Minh Huy
    SMTP_PASSWORD=<app password of that mailbox>

Port 587 uses STARTTLS, port 465 uses SSL; SMTP_SECURITY=starttls|ssl|none overrides that. To test, press "Quên mật khẩu?" on the sign-in
page with an account that has an email.
#>
param(
    [string]$EnvironmentFile = "C:\AIOffice\secrets\.env.production",
    [string]$SmtpFile,
    [string]$KeycloakUrl
)

$ErrorActionPreference = "Stop"

function Read-KeyValueFile([string]$Path) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding utf8) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        $index = $trimmed.IndexOf('=')
        if ($index -lt 1) { continue }
        $value = $trimmed.Substring($index + 1).Trim()
        if ($value.Length -ge 2 -and $value[0] -eq $value[-1] -and $value[0] -in @('"', "'")) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$trimmed.Substring(0, $index).Trim()] = $value
    }
    return $values
}

function Read-SecretFile([string]$Path, [string]$What) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing $What file." }
    $value = (Get-Content -LiteralPath $Path -Raw -Encoding utf8).Trim()
    if (-not $value -or $value -like '*replace-with-*') { throw "$What file is empty or a placeholder." }
    return $value
}

$environment = Read-KeyValueFile $EnvironmentFile
if (-not $SmtpFile) { $SmtpFile = Join-Path (Split-Path -Parent $EnvironmentFile) 'keycloak-smtp.env' }
if (-not (Test-Path -LiteralPath $SmtpFile -PathType Leaf)) { throw "Missing mail settings file: $SmtpFile" }
$smtp = Read-KeyValueFile $SmtpFile
foreach ($key in 'SMTP_HOST', 'SMTP_USER', 'SMTP_PASSWORD') {
    if (-not $smtp[$key] -or $smtp[$key] -like '*<*' -or $smtp[$key] -like '*replace-with-*') {
        throw "$key is missing in $SmtpFile."
    }
}
$port = if ($smtp['SMTP_PORT']) { [int]$smtp['SMTP_PORT'] } else { 587 }
$from = if ($smtp['SMTP_FROM']) { $smtp['SMTP_FROM'] } else { $smtp['SMTP_USER'] }
$security = if ($smtp['SMTP_SECURITY']) { $smtp['SMTP_SECURITY'].ToLowerInvariant() } elseif ($port -eq 465) { 'ssl' } else { 'starttls' }
if ($security -notin @('starttls', 'ssl', 'none')) { throw 'SMTP_SECURITY must be starttls, ssl or none.' }
$fromName = if ($smtp['SMTP_FROM_NAME']) { $smtp['SMTP_FROM_NAME'] } else { 'AI Office' }

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

$smtpServer = [ordered]@{
    host            = $smtp['SMTP_HOST']
    port            = [string]$port
    from            = $from
    fromDisplayName = $fromName
    replyTo         = $from
    auth            = 'true'
    user            = $smtp['SMTP_USER']
    password        = $smtp['SMTP_PASSWORD']
    starttls        = ([string]($security -eq 'starttls')).ToLowerInvariant()
    ssl             = ([string]($security -eq 'ssl')).ToLowerInvariant()
}
# Reset links stay valid 15 minutes (Keycloak's default 5 is short for a slow inbox).
$update = @{ resetPasswordAllowed = $true; actionTokenGeneratedByUserLifespan = 900; smtpServer = $smtpServer } | ConvertTo-Json -Depth 4
Invoke-RestMethod -Method Put -Uri $realmUrl -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($update)) | Out-Null

$realm = Invoke-RestMethod -Method Get -Uri $realmUrl -Headers $headers
if (-not $realm.resetPasswordAllowed -or $realm.smtpServer.host -ne $smtp['SMTP_HOST']) {
    throw 'Keycloak did not keep the mail settings.'
}
Write-Host "Password reset by email is on. Mail is sent from $fromName <$from> via $($smtp['SMTP_HOST']):$port."

# Accounts without an email cannot use "Quên mật khẩu?".
$users = Invoke-RestMethod -Method Get -Uri "$realmUrl/users?briefRepresentation=true&max=500" -Headers $headers
$missing = @($users | Where-Object { -not $_.email -and -not $_.serviceAccountClientId -and $_.username -notlike 'service-account-*' })
if ($missing.Count) {
    Write-Warning "These accounts have no email, so they cannot reset their password themselves: $(($missing | ForEach-Object username) -join ', ')"
}

