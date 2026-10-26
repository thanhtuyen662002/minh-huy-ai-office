[CmdletBinding()]
param([switch]$SkipAnalyzer)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$files = @(Get-ChildItem -LiteralPath (Join-Path $root 'infra/windows-vps') -Filter '*.ps1')
$failed = $false
foreach ($file in $files) {
    $parseErrors = $null; $tokens = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    foreach ($errorRecord in $parseErrors) {
        Write-Output "$($file.Name):$($errorRecord.Extent.StartLineNumber): parse error"
        $failed = $true
    }
    if (-not $SkipAnalyzer) {
        $findings = @(Invoke-ScriptAnalyzer -Path $file.FullName -Settings (Join-Path $PSScriptRoot 'PSScriptAnalyzerSettings.psd1') -Severity Warning, Error)
        foreach ($finding in $findings) {
            Write-Output "$($file.Name):$($finding.Line): $($finding.RuleName)"
            $failed = $true
        }
    }
}
foreach ($relative in @('infra/bootstrap-company.ps1', 'infra/migrate-platform-database.ps1', 'infra/validate-production-env.ps1')) {
    $parseErrors = $null; $tokens = $null
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $relative), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { Write-Output "$relative : parse error"; $failed = $true }
}
if ($failed) { throw 'VPS PowerShell verification failed.' }
Write-Output "PASS: $($files.Count) VPS scripts parsed$(if (-not $SkipAnalyzer) { ' and analyzed' }); 3 shared scripts parsed."
