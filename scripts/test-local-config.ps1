[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$configScript = Join-Path $PSScriptRoot '../infra/initialize-local-config.ps1'
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('aioffice-config-proof-' + [Guid]::NewGuid().ToString('N'))
function Require([bool]$Condition) { if (-not $Condition) { throw 'Local configuration replay proof failed.' } }
function BrowserKey {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $fixture 'local.env'))
    $keyLines = @($lines | Where-Object { $_.StartsWith('AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY=') })
    Require ($keyLines.Count -eq 1)
    return $keyLines[0].Substring('AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY='.Length)
}
try {
    & $configScript -DataDirectory $fixture | Out-Null
    $manifestPath = Join-Path $fixture 'installation.json'
    $original = [System.IO.File]::ReadAllText($manifestPath)
    $key = BrowserKey
    Require ($key -cmatch '\A[A-Za-z0-9_-]{43}\z')
    & $configScript -DataDirectory $fixture | Out-Null
    Require ([System.IO.File]::ReadAllText($manifestPath) -ceq $original)
    Require ((BrowserKey) -ceq $key)
    # Independent Python HMAC/SHA256 reference vector, with synthetic fixture
    # material. The original manifest remains immutable under real replay.
    $synthetic = $original | ConvertFrom-Json
    $synthetic.AIOFFICE_INSTALLATION_ID = '11111111-1111-1111-1111-111111111111'
    $synthetic.AIOFFICE_RUNTIME_PASSWORD = 'Aa1_' + ('B' * 43)
    [System.IO.File]::WriteAllText($manifestPath, ($synthetic | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    $expectedManifest = [System.IO.File]::ReadAllText($manifestPath)
    & $configScript -DataDirectory $fixture | Out-Null
    Require ((BrowserKey) -ceq 'XvKlBy4r40g0Aj69MC6JrzGra4QsMz7Zzyd-wE6Ov5k')
    Require ([System.IO.File]::ReadAllText($manifestPath) -ceq $expectedManifest)
    $synthetic.AIOFFICE_INSTALLATION_ID = '22222222-2222-2222-2222-222222222222'
    [System.IO.File]::WriteAllText($manifestPath, ($synthetic | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    & $configScript -DataDirectory $fixture | Out-Null
    Require ((BrowserKey) -cne 'XvKlBy4r40g0Aj69MC6JrzGra4QsMz7Zzyd-wE6Ov5k')
    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        Require ((& stat -c '%a' (Join-Path $fixture 'local.env')) -eq '600')
        Require ((& stat -c '%a' $fixture) -eq '700')
    } else {
        $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $acl = Get-Acl -LiteralPath (Join-Path $fixture 'local.env')
        Require ($acl.AreAccessRulesProtected -and @($acl.Access | Where-Object {
            $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -ne $currentSid
        }).Count -eq 0)
    }
    Write-Output 'PASS stable private browser key, independent HMAC vector, immutable replay and protected configuration'
} catch { throw 'Local configuration replay proof failed. No private state is emitted.' }
