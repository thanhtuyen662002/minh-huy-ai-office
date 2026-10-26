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
    [switch]$Migrate
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Push-Location $RepositoryRoot
try {
    Invoke-Checked git @("fetch", "--prune", "origin")
    Invoke-Checked git @("checkout", "--detach", $Ref)
    Write-Host "Deploying $(git rev-parse --short HEAD) ($Ref)"

    if ($Migrate) {
        Invoke-Checked dotnet @("tool", "restore")
        & (Join-Path $RepositoryRoot "infra\migrate-platform-database.ps1") -EnvironmentFile $EnvironmentFile
    }

    $keycloakEnabled = Select-String -LiteralPath $EnvironmentFile -Pattern '^AIOFFICE_KEYCLOAK_ENABLED=true$' -Quiet
    if ($keycloakEnabled) {
        Invoke-Checked docker @("compose", "--env-file", $EnvironmentFile, "-f", "compose.identity.yaml", "up", "-d", "--wait", "keycloak")
    }

    & (Join-Path $RepositoryRoot "infra\deploy-backend.ps1") -EnvironmentFile $EnvironmentFile -AdditionalComposeFile "compose.vps.yaml"
}
finally {
    Pop-Location
}
