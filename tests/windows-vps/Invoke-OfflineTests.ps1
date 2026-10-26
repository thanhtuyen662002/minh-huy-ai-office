# The assertions use Pester 3/4 syntax so the Windows inbox module can run them
# offline. CI pins Pester 4.10.1 and separately enforces PSScriptAnalyzer.
$ErrorActionPreference = 'Stop'
Import-Module 'C:\Program Files\WindowsPowerShell\Modules\Pester\3.4.0\Pester.psd1'
$result = Invoke-Pester -Script (Join-Path $PSScriptRoot 'Setup.Tests.ps1') -PassThru
if ($result.FailedCount -or $result.PassedCount -eq 0) { throw 'Offline Pester verification failed.' }
