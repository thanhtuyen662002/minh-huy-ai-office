# Verification only. Downloads pinned test modules into a disposable directory;
# does not install prerequisites or execute any real setup/deployment action.
[CmdletBinding()]
param([string]$ModulePath = (Join-Path ([IO.Path]::GetTempPath()) 'aioffice-310-test-modules'))
$ErrorActionPreference = 'Stop'
foreach ($module in @(@{ Name = 'Pester'; Version = '4.10.1' }, @{ Name = 'PSScriptAnalyzer'; Version = '1.24.0' })) {
    $directory = Join-Path $ModulePath ($module.Name + '/' + $module.Version)
    $manifest = Join-Path $directory ($module.Name + '.psd1')
    if (-not (Test-Path -LiteralPath $manifest)) {
        $null = New-Item -ItemType Directory -Path $directory -Force
        $zip = Join-Path $ModulePath ($module.Name + '.zip')
        Invoke-WebRequest -Uri ("https://www.powershellgallery.com/api/v2/package/" + $module.Name + '/' + $module.Version) -OutFile $zip
        Expand-Archive -LiteralPath $zip -DestinationPath $directory -Force
    }
    Import-Module $manifest -Force
}
& (Join-Path $PSScriptRoot 'Test-Scripts.ps1')
$result = Invoke-Pester -Script (Join-Path $PSScriptRoot 'Setup.Tests.ps1') -Show All -PassThru
if ($result.FailedCount -or $result.PassedCount -eq 0) { throw 'Pester verification failed.' }
