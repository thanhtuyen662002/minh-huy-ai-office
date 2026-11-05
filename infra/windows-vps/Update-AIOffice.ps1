#Requires -Version 7.0
<#
Deploys or updates AI Office on the single Windows host:
  1. checks out the requested Git ref (default: latest origin/main) in a clean tree
  2. optionally applies versioned platform database migrations (-Migrate)
  3. starts/updates Keycloak (compose.identity.yaml) when enabled in the env file
  4. builds and starts the application with the Windows VPS overlay

Expect 1-3 minutes of interruption; run outside working hours.
#>
param(
    [string]$RepositoryRoot = "C:\AIOffice\app",
    [string]$EnvironmentFile = "C:\AIOffice\secrets\.env.production",
    [string]$Ref = "origin/main",
    [switch]$Migrate,
    [ValidateSet('All', 'Migrate', 'Keycloak', 'Tunnel', 'Deploy')][string]$Phase = 'All',
    [switch]$SkipFetch
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'Setup-Helpers.ps1')

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Deployment command failed. Check the current phase and retry.'
    }
}

Push-Location $RepositoryRoot
try {
    if (-not $SkipFetch) {
        $dirty = & git status --porcelain
        if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Update requires a clean Git working tree.' }
        Invoke-Checked git @("fetch", "--prune", "origin")
        Invoke-Checked git @("checkout", "--detach", $Ref)
    }
    Write-Host "Deploying $(git rev-parse --short HEAD) ($Ref)"

    if ($Migrate -and $Phase -in @('All', 'Migrate')) {
        Invoke-Checked dotnet @("tool", "restore")
        & (Join-Path $RepositoryRoot "infra\migrate-platform-database.ps1") -EnvironmentFile $EnvironmentFile
    }

    $keycloakEnabled = Select-String -LiteralPath $EnvironmentFile -Pattern '^AIOFFICE_KEYCLOAK_ENABLED=true$' -Quiet
    if ($keycloakEnabled -and $Phase -in @('All', 'Keycloak')) {
        # Keycloak caches themes; a new revision recreates the container so wording changes show up.
        $themeFiles = Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'infra\keycloak\themes') -Recurse -File | Sort-Object FullName
        $themeHashes = ($themeFiles | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join ''
        $env:AIOFFICE_KEYCLOAK_THEME_REV = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($themeHashes))) -Algorithm SHA256).Hash.Substring(0, 12)
        Invoke-Checked docker @("compose", "--env-file", $EnvironmentFile, "-f", "compose.identity.yaml", "up", "-d", "--wait", "keycloak")
        & (Join-Path $PSScriptRoot 'Configure-KeycloakSessions.ps1') -EnvironmentFile $EnvironmentFile
        # Optional: password reset by email, enabled by the private mail settings file next to the env file.
        $smtpFile = Join-Path (Split-Path -Parent $EnvironmentFile) 'keycloak-smtp.env'
        if (Test-Path -LiteralPath $smtpFile -PathType Leaf) {
            & (Join-Path $PSScriptRoot 'Configure-KeycloakEmail.ps1') -EnvironmentFile $EnvironmentFile -SmtpFile $smtpFile
        }
    }

    # Optional Cloudflare Tunnel ingress, enabled by the protected token file next to the env file.
    $tunnelToken = Join-Path (Split-Path -Parent $EnvironmentFile) 'cloudflare-tunnel-token'
    if ($Phase -in @('All', 'Tunnel') -and (Test-Path -LiteralPath $tunnelToken -PathType Leaf)) {
        $env:AIOFFICE_CLOUDFLARE_TUNNEL_TOKEN_FILE = $tunnelToken
        Invoke-Checked docker @("compose", "-f", "compose.tunnel.yaml", "up", "-d", "cloudflared")
    }

    if ($Phase -in @('All', 'Deploy')) {
        # Core API mails "you were added" notices from the same private mailbox file, when present.
        $mailFile = Join-Path (Split-Path -Parent $EnvironmentFile) 'keycloak-smtp.env'
        if (-not (Test-Path -LiteralPath $mailFile -PathType Leaf)) {
            # Docker needs a real file for the secret; an empty one means "no notices".
            $mailFile = Join-Path (Split-Path -Parent $EnvironmentFile) 'invitation-mail-none.env'
            if (-not (Test-Path -LiteralPath $mailFile -PathType Leaf)) { Set-Content -LiteralPath $mailFile -Value '' -Encoding utf8 }
        }
        $env:AIOFFICE_KEYCLOAK_SMTP_FILE = $mailFile
        Initialize-SetupRedisConfiguration $EnvironmentFile
        $overlays = @("compose.vps.yaml")
        if (Test-Path -LiteralPath $tunnelToken -PathType Leaf) { $overlays += "compose.vps-tunnel.yaml" }
        & (Join-Path $RepositoryRoot "infra\deploy-backend.ps1") -EnvironmentFile $EnvironmentFile -AdditionalComposeFile $overlays
    }
}
finally {
    Pop-Location
}
