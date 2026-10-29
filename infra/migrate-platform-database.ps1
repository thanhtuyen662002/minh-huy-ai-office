param(
    [string]$EnvironmentFile = ".env.production",
    [string]$TargetMigration = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'windows-vps/Setup-Helpers.ps1')

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath $EnvironmentFile)) {
    throw "Missing $EnvironmentFile. Copy .env.production.example and populate it from approved secret sources."
}

# The runtime login must not own the schema (Core.Api's startup permission proof rejects
# db_owner), so migrations prefer a separate owner connection when one is configured.
$databaseConnection = $null
foreach ($name in @("AIOFFICE_DB_MIGRATION_CONNECTION", "AIOFFICE_DB_CONNECTION")) {
    foreach ($line in Get-Content -LiteralPath $EnvironmentFile) {
        $trimmed = $line.Trim()
        if ($trimmed -and -not $trimmed.StartsWith("#") -and $trimmed.StartsWith("$name=")) {
            $databaseConnection = $trimmed.Substring("$name=".Length).Trim()
            break
        }
    }
    if ($databaseConnection) { break }
}

$missingDatabaseConnection = [string]::IsNullOrWhiteSpace($databaseConnection)
$placeholderDatabaseConnection = $databaseConnection -match "(?i)replace-with-"

if ($missingDatabaseConnection -or $placeholderDatabaseConnection) {
    throw "AIOFFICE_DB_CONNECTION must be populated in the ignored environment file before migration."
}

if ((($databaseConnection.StartsWith('"') -and $databaseConnection.EndsWith('"')) -or ($databaseConnection.StartsWith("'") -and $databaseConnection.EndsWith("'")))) {
    $databaseConnection = ConvertFrom-SetupEnvValue $databaseConnection
}

# Keep the connection string in the child process environment. It is deliberately not passed as
# a command-line argument, logged, or written to a generated file.
$previousConnection = $env:AIOFFICE_DB_CONNECTION
try {
    $env:AIOFFICE_DB_CONNECTION = $databaseConnection

    Invoke-Checked -Command "dotnet" -Arguments @("tool", "restore")
    # A fresh checkout has no project.assets.json; dotnet ef needs the project restored first.
    Invoke-Checked -Command "dotnet" -Arguments @("restore", "src/Platform.Persistence/Platform.Persistence.csproj")

    $efArguments = @(
        "ef", "database", "update",
        "--project", "src/Platform.Persistence/Platform.Persistence.csproj",
        "--startup-project", "src/Platform.Persistence/Platform.Persistence.csproj",
        "--context", "MinhHuy.AIOffice.Platform.Persistence.PlatformDbContext"
    )
    if (-not [string]::IsNullOrWhiteSpace($TargetMigration)) {
        $efArguments += $TargetMigration
    }

    Invoke-Checked -Command "dotnet" -Arguments $efArguments
    Write-Host "Platform database migration completed without printing the database credential."
} finally {
    $env:AIOFFICE_DB_CONNECTION = $previousConnection
}
