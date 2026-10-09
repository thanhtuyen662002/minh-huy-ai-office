param([Parameter(Mandatory = $true)][string]$Directory)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Minh Huy tiếng Việt ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$installer = Join-Path $testDirectory 'Setup.exe'
Copy-Item -LiteralPath (Join-Path $Directory 'Setup.exe') -Destination $installer
$proof = Join-Path $testDirectory 'bundle-proof.json'
$info = [Diagnostics.ProcessStartInfo]::new($installer)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.Environment['DOTNET_ROOT'] = 'Z:\missing-runtime'
$info.Environment['DOTNET_ROOT_X64'] = 'Z:\missing-runtime'
$info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$info.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
$info.ArgumentList.Add('--verify-bundle')
$info.ArgumentList.Add($proof)
$process = [Diagnostics.Process]::Start($info)
if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'Executable bundle validation timed out.' }
if ($process.ExitCode -ne 0) { throw 'Executable bundle validation failed.' }
$verified = Get-Content -LiteralPath $proof -Raw | ConvertFrom-Json
if (!$verified.verified -or $verified.revision -ne $manifest.revision -or $verified.sha256 -ne $manifest.bundleSha256) { throw 'Bundle proof does not match the pinned source.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $manifest.installerSha256) { throw 'Installer artifact hash mismatch.' }
Copy-Item -LiteralPath $proof -Destination (Join-Path $Directory 'bundle-proof.json')
Write-Output ('PASS self-contained Windows executable and pinned bundle: ' + $verified.revision)

function Invoke-SetupInspection([string]$Mode, [string]$Argument) {
    $inspectionInfo = [Diagnostics.ProcessStartInfo]::new()
    $inspectionInfo.FileName = [IO.Path]::GetFullPath($installer)
    $inspectionInfo.UseShellExecute = $false
    $inspectionInfo.CreateNoWindow = $true
    $inspectionInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $inspectionInfo.ArgumentList.Add($Mode)
    $inspectionInfo.ArgumentList.Add([IO.Path]::GetFullPath($Argument))
    $inspectionProcess = [Diagnostics.Process]::Start($inspectionInfo)
    try {
        if (!$inspectionProcess.WaitForExit(45000)) {
            $inspectionProcess.Kill($true)
            throw ('Setup inspection timed out: ' + $Mode)
        }
        if ($inspectionProcess.ExitCode -ne 0) {
            throw ('Setup inspection failed: ' + $Mode + ' exit ' + $inspectionProcess.ExitCode)
        }
    } finally { $inspectionProcess.Dispose() }
}

$integrationProof = Join-Path $Directory 'windows-integration-proof.json'
Invoke-SetupInspection '--verify-windows-integration' $integrationProof
$integration = Get-Content -LiteralPath $integrationProof -Raw | ConvertFrom-Json
if ($integration.schemaVersion -ne 1 -or !$integration.verified -or !$integration.isolated -or
    $integration.usedRealStartupFolders -or $integration.privilegedOperations -or $integration.installationVerified -or
    $integration.alternateAdminLoginVerified) { throw 'Invalid isolated Windows integration proof.' }
foreach ($requiredCheck in @('actual-helper-acl', 'administrator-traverse-only', 'private-config-source-acl',
    'installing-user-full-control', 'actual-start-menu-shortcut', 'actual-sign-in-shortcut', 'unicode-space-paths',
    'repeat-shortcut-registration', 'cancel-owned-continuation', 'cancel-preserves-other-revision',
    'cancel-preserves-other-command', 'cancel-preserves-foreign-shortcut', 'foreign-shortcut-conflict-preserved',
    'configuration-identity-role-volume-fixtures-retained', 'missing-license-before-machine-operations',
    'typed-diagnostic-export', 'raw-command-values-excluded', 'failure-preserves-active-phase',
    'typed-service-state-health-exit', 'failure-form-export-enabled',
    'damaged-cache-rejected-without-repair', 'repair-restores-helper',
    'repair-retains-damaged-helper', 'quarantined-helper-private-acl', 'repeat-cache-repair-preserves-quarantine',
    'pause-owned-sign-in-during-continuation', 'repeat-continuation-keeps-paused-sign-in',
    'cancel-keeps-paused-other-revision', 'cancel-restores-owned-sign-in',
    'ready-clears-paused-continuation', 'foreign-sign-in-conflict-preserved',
    'foreign-paused-sign-in-conflict-preserved', 'resume-conflict-restores-owned-sign-in',
    'cancellation-preserves-foreign-active-sign-in')) {
    if ($integration.checks -notcontains $requiredCheck) { throw ('Missing Windows integration check: ' + $requiredCheck) }
}
$previewDirectory = Join-Path $Directory 'ui-preview'
Invoke-SetupInspection '--preview-ui' $previewDirectory
$preview = Get-Content -LiteralPath (Join-Path $previewDirectory 'preview-proof.json') -Raw | ConvertFrom-Json
if ($preview.schemaVersion -ne 1 -or !$preview.simulated -or !$preview.offscreen -or $preview.installationVerified -or
    $preview.privilegedOperations) { throw 'UI previews must be marked as simulations.' }
foreach ($state in @('initial', 'failed', 'reboot', 'ready')) {
    if ($preview.states -notcontains $state -or !(Test-Path -LiteralPath (Join-Path $previewDirectory ($state + '.png')))) {
        throw ('Missing simulated UI preview: ' + $state)
    }
    foreach ($scenario in @('default', 'minimum', 'expanded', 'minimum-again', 'large-font-minimum')) {
        $scenarioName = if ($scenario -eq 'default') { $state } else { $state + '-' + $scenario }
        if ($preview.scenarios -notcontains $scenario -or
            @($preview.verified | Where-Object { $_.state -eq $state -and $_.scenario -eq $scenario }).Count -ne 1 -or
            !(Test-Path -LiteralPath (Join-Path $previewDirectory ($scenarioName + '.png')))) {
            throw ('Missing responsive UI verification: ' + $scenarioName)
        }
    }
}
foreach ($requiredCheck in @('no-horizontal-scroll', 'complete-text-height', 'buttons-fit', 'resize-cycle', 'masked-password-reachable')) {
    if ($preview.checks -notcontains $requiredCheck) { throw ('Missing UI layout check: ' + $requiredCheck) }
}
Write-Output 'PASS isolated real Windows shortcuts/ACLs, typed export, failure UI controls, simulated offscreen previews.'
Write-Output 'Clean Windows installation, reboot, UAC and alternate-administrator login acceptance remain pending.'
