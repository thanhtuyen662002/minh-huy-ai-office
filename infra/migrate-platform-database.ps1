param(
    [string]$EnvironmentFile = ".env.production",
    [string]$TargetMigration = ""
)

$ErrorActionPreference = "Stop"

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

$databaseConnection = $null
foreach ($line in Get-Content -LiteralPath $EnvironmentFile) {
    $trimmed = $line.Trim()
    if ($trimmed -and -not $trimmed.StartsWith("#") -and $trimmed.StartsWith("AIOFFICE_DB_CONNECTION=")) {
        $databaseConnection = $trimmed.Substring("AIOFFICE_DB_CONNECTION=".Length).Trim()
        break
    }
}

$missingDatabaseConnection = [string]::IsNullOrWhiteSpace($databaseConnection)
$placeholderDatabaseConnection = $databaseConnection -match "(?i)replace-with-"

if ($missingDatabaseConnection -or $placeholderDatabaseConnection) {
    throw "AIOFFICE_DB_CONNECTION must be populated in the ignored environment file before migration."
}

if ((($databaseConnection.StartsWith('"') -and $databaseConnection.EndsWith('"')) -or ($databaseConnection.StartsWith("'") -and $databaseConnection.EndsWith("'")))) {
    $databaseConnection = $databaseConnection.Substring(1, $databaseConnection.Length - 2)
}

# Keep the connection string in the child process environment. It is deliberately not passed as
# a command-line argument, logged, or written to a generated file.
$env:AIOFFICE_DB_CONNECTION = $databaseConnection

Invoke-Checked -Command "dotnet" -Arguments @("tool", "restore")

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
