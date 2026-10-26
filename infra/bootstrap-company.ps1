param(
    [Parameter(Mandatory = $true)][Guid]$TenantId,
    [Parameter(Mandatory = $true)][Guid]$CompanyId,
    [Parameter(Mandatory = $true)][string]$CompanyCode,
    [Parameter(Mandatory = $true)][string]$CompanyName,
    [Parameter(Mandatory = $true)][string]$AdminEmail,
    [string]$EnvironmentFile = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'windows-vps/Setup-Helpers.ps1')
$previousConnection = $env:AIOFFICE_DB_CONNECTION
try {
    if ($EnvironmentFile) {
        $connection = $null
        foreach ($line in Get-Content -LiteralPath $EnvironmentFile) {
            $trimmed = $line.Trim()
            if ($trimmed.StartsWith("AIOFFICE_DB_CONNECTION=")) {
                $connection = $trimmed.Substring("AIOFFICE_DB_CONNECTION=".Length).Trim()
                break
            }
        }
        if ([string]::IsNullOrWhiteSpace($connection) -or $connection -match "(?i)replace-with-") { throw "Missing database connection." }
        if (($connection.StartsWith('"') -and $connection.EndsWith('"')) -or ($connection.StartsWith("'") -and $connection.EndsWith("'"))) {
            $connection = ConvertFrom-SetupEnvValue $connection
        }
        $env:AIOFFICE_DB_CONNECTION = $connection
    }
    if ([string]::IsNullOrWhiteSpace($env:AIOFFICE_DB_CONNECTION) -or $env:AIOFFICE_DB_CONNECTION -match "(?i)replace-with-") { throw "Missing database connection." }
    & dotnet run --project "$PSScriptRoot/../src/Core.Api" -- --bootstrap-company `
        --tenant-id $TenantId --company-id $CompanyId --company-code $CompanyCode --company-name $CompanyName --admin-email $AdminEmail
    if ($LASTEXITCODE -ne 0) { throw "Bootstrap command failed." }
} catch {
    # Never print caught exceptions: configuration/provider diagnostics can contain secrets.
    throw "Company bootstrap failed. Check arguments, migrated database access and existing bootstrap state."
} finally {
    $env:AIOFFICE_DB_CONNECTION = $previousConnection
}
