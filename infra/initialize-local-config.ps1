[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [switch]$RequireExistingInstallation,
    [string]$ExpectedInstallationId
)

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$directory = [System.IO.Path]::GetFullPath($DataDirectory)
$separator = [System.IO.Path]::DirectorySeparatorChar
if ($directory.Equals($repository, [System.StringComparison]::OrdinalIgnoreCase) -or
    $directory.StartsWith($repository.TrimEnd($separator) + $separator, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Local installation state must be outside the repository.'
}
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
if ($isWindowsHost) {
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    # icacls changes DACL only, without requiring SeSecurityPrivilege for SACL.
    & icacls $directory '/inheritance:r' '/grant:r' ('*' + $sid.Value + ':(OI)(CI)F') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect local installation directory.' }
    foreach ($rule in @((Get-Acl -LiteralPath $directory).Access)) {
        $ruleSid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
        if ($ruleSid -ne $sid.Value) {
            & icacls $directory '/remove' ('*' + $ruleSid) | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Cannot remove inherited installation access.' }
        }
    }
    $verifiedAcl = Get-Acl -LiteralPath $directory
    if (-not $verifiedAcl.AreAccessRulesProtected -or @($verifiedAcl.Access | Where-Object {
        $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -ne $sid.Value
    }).Count -ne 0) { throw 'Installation directory access is not private.' }
} else {
    & chmod 700 -- $directory
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect local installation directory.' }
}

function New-LocalSecret {
    $bytes = New-Object byte[] 32
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) } finally { $random.Dispose() }
    return 'Aa1_' + [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
function Protect-LocalFile([string]$Path) {
    if ($isWindowsHost) {
        & icacls $Path '/inheritance:r' '/grant:r' ('*' + $sid.Value + ':F') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot protect local installation file.' }
        foreach ($rule in @((Get-Acl -LiteralPath $Path).Access)) {
            $ruleSid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            if ($ruleSid -ne $sid.Value) {
                & icacls $Path '/remove' ('*' + $ruleSid) | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Cannot remove installation file access.' }
            }
        }
    } else {
        & chmod 600 -- $Path
        if ($LASTEXITCODE -ne 0) { throw 'Cannot protect local installation file.' }
    }
}
function Write-AtomicFile([string]$Path, [string]$Content, [bool]$Replace) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $backup = $temporary + '.backup'
    try {
        [System.IO.File]::WriteAllText($temporary, $Content, (New-Object System.Text.UTF8Encoding($false)))
        if (-not $isWindowsHost) {
            & chmod 600 -- $temporary
            if ($LASTEXITCODE -ne 0) { throw 'Cannot protect local installation file.' }
        }
        if ($Replace -and [System.IO.File]::Exists($Path)) {
            [System.IO.File]::Replace($temporary, $Path, $backup)
        } else {
            [System.IO.File]::Move($temporary, $Path)
        }
    } finally {
        if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) }
        if ([System.IO.File]::Exists($backup)) { [System.IO.File]::Delete($backup) }
    }
}

$manifestPath = Join-Path $directory 'installation.json'
$envPath = Join-Path $directory 'local.env'
if (($RequireExistingInstallation -or $ExpectedInstallationId) -and -not [System.IO.File]::Exists($manifestPath)) {
    throw 'Retained installation manifest is missing. Restore the original manifest; credentials and identity were not changed.'
}
$idKeys = @('INSTALLATION', 'TENANT', 'COMPANY', 'USER', 'DATA_SOURCE')
$secretKeys = @('SQL', 'RUNTIME', 'READER', 'IDENTITY_ADMIN', 'IDENTITY_DB', 'OWNER', 'RABBITMQ')
if (-not [System.IO.File]::Exists($manifestPath)) {
    if ([System.IO.File]::Exists($envPath)) {
        throw 'Installation manifest is missing but retained configuration exists. Restore the original manifest; credentials and identity were not changed.'
    }
    $newManifest = [ordered]@{ schemaVersion = 1 }
    foreach ($key in $idKeys) { $newManifest["AIOFFICE_${key}_ID"] = [Guid]::NewGuid().ToString() }
    foreach ($key in $secretKeys) { $newManifest["AIOFFICE_${key}_PASSWORD"] = New-LocalSecret }
    Write-AtomicFile $manifestPath ($newManifest | ConvertTo-Json) $false
}
Protect-LocalFile $manifestPath
$manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported local installation manifest. Preserve it for recovery.' }
if ($ExpectedInstallationId) {
    $expected = [Guid]::Empty
    $actual = [Guid]::Empty
    if (-not [Guid]::TryParse($ExpectedInstallationId, [ref]$expected) -or $expected -eq [Guid]::Empty -or
        -not [Guid]::TryParse($manifest.AIOFFICE_INSTALLATION_ID, [ref]$actual) -or $expected -ne $actual) {
        throw 'Installation identity does not match retained progress. Restore the original manifest; configuration was not changed.'
    }
}
$lines = New-Object 'System.Collections.Generic.List[string]'
foreach ($key in $idKeys) {
    $name = "AIOFFICE_${key}_ID"
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($manifest.$name, [ref]$parsed) -or $parsed -eq [Guid]::Empty) {
        throw 'Invalid installation identifier. Restore the original manifest.'
    }
    $lines.Add($name + '=' + $parsed.ToString())
}
foreach ($key in $secretKeys) {
    $name = "AIOFFICE_${key}_PASSWORD"
    if ($manifest.$name -cnotmatch '\A[A-Za-z0-9_-]{32,128}\z') {
        throw 'Invalid generated installation secret. Restore the original manifest.'
    }
    $lines.Add($name + '=' + $manifest.$name)
}
$lines.Add('COMPOSE_PROJECT_NAME=aioffice-' + ([Guid]$manifest.AIOFFICE_INSTALLATION_ID).ToString('N'))
if ([System.IO.File]::Exists($envPath)) { Protect-LocalFile $envPath }
Write-AtomicFile $envPath (($lines -join "`n") + "`n") $true
Protect-LocalFile $envPath
# Return only a path, never credentials. The Windows installer will consume this file.
Write-Output $envPath
