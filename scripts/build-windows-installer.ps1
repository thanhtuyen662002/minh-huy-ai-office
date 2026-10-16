param(
    [Parameter(Mandatory = $true)][string]$BundleDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bundle = [System.IO.Path]::GetFullPath($BundleDirectory)
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $repository
try {
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -cnotmatch '\A[a-f0-9]{40}\z') { throw 'A verified Git revision is required.' }
    if (!$SkipPublish) {
        $changes = @(& git status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the release checkout.' }
        if ($changes.Count -ne 0) { throw 'Release publishing requires a clean committed checkout, including untracked source files.' }
    }
    $archive = Join-Path $bundle 'application.zip'
    & git archive --format=zip "--output=$archive" $revision
    if ($LASTEXITCODE -ne 0) { throw 'Cannot archive the application revision.' }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = @{ schemaVersion = 1; revision = $revision; sha256 = $hash } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $bundle 'manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))
    if ($SkipPublish) { return }
    & dotnet publish src/Setup.Windows/Setup.Windows.csproj --configuration Release --runtime win-x64 --self-contained true "-p:BundleDirectory=$bundle" --output $output
    if ($LASTEXITCODE -ne 0) { throw 'Windows installer publishing failed.' }
    $installer = Join-Path $output 'Setup.exe'
    if (!(Test-Path -LiteralPath $installer)) { throw 'Windows installer artifact is missing.' }
    $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output 'Setup.exe.sha256'), ($installerHash + '  Setup.exe' + [Environment]::NewLine))
    [IO.File]::WriteAllText((Join-Path $output 'release-manifest.json'),
        (@{ schemaVersion = 1; revision = $revision; bundleSha256 = $hash; installerSha256 = $installerHash } | ConvertTo-Json))
} finally { Pop-Location }
