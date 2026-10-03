[CmdletBinding()]
param([string]$DataDirectory)

$ErrorActionPreference = 'Stop'
if (-not $DataDirectory) {
    if (-not $env:LOCALAPPDATA) { throw 'Specify DataDirectory outside the repository on this host.' }
    $DataDirectory = Join-Path $env:LOCALAPPDATA 'MinhHuyAIoffice\LocalRuntime'
}
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$envPath = & (Join-Path $PSScriptRoot 'initialize-local-config.ps1') -DataDirectory $DataDirectory
& docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker engine must be ready. The Windows dependency installer is not shipped yet.' }
Push-Location $repository
try {
    & docker compose --env-file $envPath -f compose.local.yaml up --build -d
    if ($LASTEXITCODE -ne 0) { throw 'Local runtime start failed. Installation configuration and data have been retained.' }
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    do {
        try {
            $api = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/health' -UseBasicParsing -TimeoutSec 5
            $web = Invoke-WebRequest -Uri 'http://127.0.0.1:3000/' -UseBasicParsing -TimeoutSec 5
            if ($api.StatusCode -eq 200 -and $web.StatusCode -eq 200) {
                Write-Output 'Local API and FE ready: http://127.0.0.1:3000/'
                Write-Output ('Initial owner username: owner. Credentials are retained in the protected installation state: ' + $DataDirectory)
                return
            }
        } catch { }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Local readiness deadline exceeded. Configuration and database volumes have been retained for recovery.'
} finally { Pop-Location }
