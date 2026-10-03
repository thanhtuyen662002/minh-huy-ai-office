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
