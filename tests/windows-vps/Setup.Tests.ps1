. {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    . (Join-Path $repo 'infra/windows-vps/Setup-Helpers.ps1')
    . (Join-Path $repo 'infra/windows-vps/Setup-Actions.ps1')
    function New-TestOptions {
        return @{
            WebHost = 'ai.example.test'; ApiHost = 'api.example.test'; IdentityHost = 'login.example.test'
            LanPrefix = '192.168.68.'; CertificateEmail = 'ops@example.test'; SqlHost = 'sql.office.test'; SqlPort = '1433'
            CompanyCode = 'MINHHUY'; CompanyName = 'Minh Huy'; AdminEmail = 'admin@example.test'
            AiBaseUrl = ''; AiModel = ''; TenantId = '11111111-1111-1111-1111-111111111111'
            CompanyId = '22222222-2222-2222-2222-222222222222'; AutoSignIn = $false
        }
    }
}

Describe 'Resumable ordered phase machine' {
    It 'has exactly the required thirteen phases' {
        (@(Get-SetupPhase) -join ',') | Should Be 'prerequisites,reboot,docker-ready,secrets,firewall,iis,certificates,migrate,keycloak,deploy,health,first-admin,open-browser'
    }
    It 'round trips only phase names, including empty state' {
        $path = Join-Path $TestDrive 'state.json'
        Write-SetupState $path @()
        @(Read-SetupState $path).Count | Should Be 0
        Write-SetupState $path @('prerequisites', 'reboot')
        [IO.File]::ReadAllText($path) | Should Be '["prerequisites","reboot"]'
        @(Get-SetupPlan -Completed @(Read-SetupState $path))[0] | Should Be 'docker-ready'
        Test-Path "$path.tmp" | Should Be $false
    }
    It 'refuses unknown, reordered, duplicate, skipped and malformed state' {
        foreach ($completed in @(@('secret-sentinel'), @('reboot'), @('prerequisites', 'prerequisites'), @('prerequisites', 'docker-ready'))) {
            { Write-SetupState (Join-Path $TestDrive 'invalid.json') $completed } | Should Throw ' '
        }
        foreach ($json in @('{}', 'null', '[1]', '["reboot"]', 'invalid')) {
            $path = Join-Path $TestDrive 'bad.json'
            [IO.File]::WriteAllText($path, $json)
            { Read-SetupState $path } | Should Throw ' '
        }
        Test-Path (Join-Path $TestDrive 'invalid.json') | Should Be $false
    }
    It 'checkpoints successful actions, stops on failure, and retries only unfinished actions' {
        $path = Join-Path $TestDrive 'resume.json'; $log = Join-Path $TestDrive 'resume.log'
        $calls = [Collections.Generic.List[string]]::new(); $actions = @{}
        foreach ($phase in @(Get-SetupPhase)) { $actions[$phase] = { $calls.Add($phase) }.GetNewClosure() }
        $actions['docker-ready'] = { throw 'injected failure' }
        { Invoke-SetupPlan -Actions $actions -StatePath $path -LogPath $log } | Should Throw 'docker-ready'
        (@(Read-SetupState $path) -join ',') | Should Be 'prerequisites,reboot'
        $actions['docker-ready'] = { $calls.Add('docker-ready') }.GetNewClosure()
        Invoke-SetupPlan -Actions $actions -Completed @(Read-SetupState $path) -StatePath $path -LogPath $log
        $calls.Count | Should Be 13
        @(Read-SetupState $path).Count | Should Be 13
        Invoke-SetupPlan -Actions $actions -Completed @(Read-SetupState $path) -StatePath $path -LogPath $log
        $calls.Count | Should Be 13
    }
    It 'does not acknowledge a requested reboot in the old Windows boot' {
        $path = Join-Path $TestDrive 'reboot.json'; $log = Join-Path $TestDrive 'reboot.log'
        $actions = @{ prerequisites = {}; reboot = { throw [OperationCanceledException]::new('RebootPending') } }
        Invoke-SetupPlan -Actions $actions -StatePath $path -LogPath $log
        (@(Read-SetupState $path) -join ',') | Should Be 'prerequisites'
        [IO.File]::ReadAllText($log) | Should Match 'reboot paused'
    }
    It 'keeps update progress independent of installation state' {
        (@(Get-SetupPhase -Update) -join ',') | Should Be 'migrate,keycloak,deploy,health'
        $calls = [Collections.Generic.List[string]]::new(); $actions = @{}
        foreach ($phase in @(Get-SetupPhase -Update)) { $actions[$phase] = { $calls.Add($phase) }.GetNewClosure() }
        Invoke-SetupPlan -Update -Actions $actions
        ($calls -join ',') | Should Be 'migrate,keycloak,deploy,health'
    }
}

Describe 'Dry run never reaches side effects' {
    It 'parses the CMD embedded bootstrap as code without executing the entry point' {
        $entry = [IO.File]::ReadAllText((Join-Path $repo 'Setup-AIOffice.cmd'))
        $commandLine = ($entry -split '\r?\n' | Where-Object { $_.StartsWith('powershell.exe ') })
        $code = $commandLine.Substring($commandLine.IndexOf('-Command "') + 10).TrimEnd('"')
        $parseErrors = $null; $tokens = $null
        $null = [Management.Automation.Language.Parser]::ParseInput($code, [ref]$tokens, [ref]$parseErrors)
        $parseErrors.Count | Should Be 0
        $code | Should Match 'IsInRole'
        $code | Should Match 'RunAs'
        $code | Should Match 'winget install'
        $code | Should Match 'git clone'
    }
    It 'walks every phase without executing actions, callbacks or writing state/logs' {
        $path = Join-Path $TestDrive 'absent/state.json'; $log = Join-Path $TestDrive 'absent/setup.log'
        $actions = @{}
        foreach ($phase in @(Get-SetupPhase)) { $actions[$phase] = { throw 'Must never run.' } }
        $result = @(Invoke-SetupPlan -Actions $actions -DryRun -StatePath $path -LogPath $log -ProgressCallback { throw 'Must never run.' })
        $result.Count | Should Be 13
        @($result | Where-Object Status -ne 'planned').Count | Should Be 0
        Test-Path (Join-Path $TestDrive 'absent') | Should Be $false
    }
    It 'entry WhatIf is safe with absent repo and root and does not import actions or load GUI' {
        # AST verifies the early return precedes *every* executable real-mode action.
        $source = [IO.File]::ReadAllText((Join-Path $repo 'infra/windows-vps/Setup-Wizard.ps1'))
        ($source.IndexOf('if ($WhatIfPreference)') -lt $source.IndexOf(". (Join-Path `$PSScriptRoot 'Setup-Actions.ps1')")) | Should Be $true
        $result = @(& (Join-Path $repo 'infra/windows-vps/Setup-Wizard.ps1') -WhatIf -Root (Join-Path $TestDrive 'never-created') -RepositoryRoot (Join-Path $TestDrive 'no-repo'))
        $result.Count | Should Be 13
        Test-Path (Join-Path $TestDrive 'never-created') | Should Be $false
        $result = @(& (Join-Path $repo 'infra/windows-vps/Setup-Wizard.ps1') -WhatIf -Update)
        $result.Count | Should Be 4
    }
}

Describe 'Literal environment file writer' {
    It 'round trips dollars, hashes, quotes, backslashes, equals, whitespace and Vietnamese' {
        foreach ($value in @('', '$TOKEN ${OTHER} # value', 'pass"word', "pass'word", 'C:\sql\path', 'C:\sql\', "slash\'quote", 'literal\n\t\r\', 'literal\"$#\', 'a=b;c=d', ' space ', 'Mật khẩu')) {
            ConvertFrom-SetupEnvValue (ConvertTo-SetupEnvValue $value) | Should BeExactly $value
        }
    }
    It 'replaces duplicate entries without changing unrelated lines' {
        $lines = [Collections.Generic.List[string]]::new()
        @('# template', 'KEY=old', 'OTHER=value', 'KEY=duplicate') | ForEach-Object { $lines.Add($_) }
        Set-EnvValue $lines 'KEY' '$secret # value'
        @($lines | Where-Object { $_.StartsWith('KEY=') }).Count | Should Be 1
        ($lines -contains 'OTHER=value') | Should Be $true
        ConvertFrom-SetupEnvValue ($lines[-1].Substring(4)) | Should BeExactly '$secret # value'
    }
    It 'rejects line injection and invalid variable names without including input in errors' {
        foreach ($value in @("sentinel`nEVIL=1", "sentinel`rnext", "sentinel$([char]0)")) {
            try { ConvertTo-SetupEnvValue $value; throw 'expected rejection' }
            catch { $_.Exception.Message | Should Not Match 'sentinel|EVIL' }
        }
        $lines = [Collections.Generic.List[string]]::new()
        { Set-EnvValue $lines 'BAD=KEY' 'value' } | Should Throw ' '
        $lines.Count | Should Be 0
    }
    It 'existing production validator accepts the same quoted SQL representation' {
        $lines = [Collections.Generic.List[string]]::new()
        $values = @{
            RABBITMQ_DEFAULT_USER = 'aioffice'; RABBITMQ_DEFAULT_PASS = 'fake-only'
            AIOFFICE_DB_CONNECTION = 'Server=sql.test;Database=AI_Office;User ID=test;Password="fake$#''only";Encrypt=true'
            AIOFFICE_AUTHORITY = 'https://login.test/realms/aioffice'; AIOFFICE_AUDIENCE = 'minh-huy-ai-office'
            PILOT_ERP_CONNECTION = 'disabled-local-fixture'; AIOFFICE_SECRET_ENCRYPTION_KEY = [Convert]::ToBase64String([byte[]]::new(32))
        }
        foreach ($key in $values.Keys) { Set-EnvValue $lines $key $values[$key] }
        $path = Join-Path $TestDrive 'fake.env'; [IO.File]::WriteAllLines($path, $lines)
        { & (Join-Path $repo 'infra/validate-production-env.ps1') -EnvironmentFile $path *> $null } | Should Not Throw ' '
    }
    It 'keeps HTTPS, SQL host grammar and protected Keycloak file validation enforced' {
        $values = @{
            RABBITMQ_DEFAULT_USER = 'aioffice'; RABBITMQ_DEFAULT_PASS = 'fake-only'; AIOFFICE_DB_CONNECTION = 'fake-only'
            AIOFFICE_AUTHORITY = 'https://login.example.test/realms/aioffice'; AIOFFICE_AUDIENCE = 'minh-huy-ai-office'
            PILOT_ERP_CONNECTION = 'disabled-local-fixture'; AIOFFICE_SECRET_ENCRYPTION_KEY = [Convert]::ToBase64String([byte[]]::new(32))
            AIOFFICE_KEYCLOAK_ENABLED = 'true'; AIOFFICE_KEYCLOAK_HOSTNAME = 'https://login.example.test'
            AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN = 'https://ai.example.test'; AIOFFICE_BROWSER_OIDC_CLIENT_ID = 'aioffice-browser'
            AIOFFICE_KEYCLOAK_DB_HOST = 'sql.office.test'; AIOFFICE_KEYCLOAK_DB_PORT = '1433'; AIOFFICE_KEYCLOAK_PORT = '8081'
        }
        $issuer = $values.AIOFFICE_AUTHORITY
        $values.AIOFFICE_BROWSER_OIDC_ISSUER = $issuer
        $values.AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT = "$issuer/protocol/openid-connect/auth"
        $values.AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT = "$issuer/protocol/openid-connect/token"
        $values.AIOFFICE_BROWSER_OIDC_JWKS_URI = "$issuer/protocol/openid-connect/certs"
        foreach ($key in @('AIOFFICE_KEYCLOAK_DB_USERNAME_FILE', 'AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE', 'AIOFFICE_KEYCLOAK_ADMIN_USERNAME_FILE', 'AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE')) {
            $values[$key] = Join-Path $TestDrive ($key + '.fixture')
            [IO.File]::WriteAllText($values[$key], 'fake-only')
        }
        $path = Join-Path $TestDrive 'keycloak-fixture.env'
        foreach ($mode in @('valid', 'http', 'sql-injection', 'missing-file')) {
            $fixture = $values.Clone()
            if ($mode -eq 'http') { $fixture.AIOFFICE_KEYCLOAK_HOSTNAME = 'http://login.example.test' }
            if ($mode -eq 'sql-injection') { $fixture.AIOFFICE_KEYCLOAK_DB_HOST = 'sql;trustServerCertificate=true' }
            if ($mode -eq 'missing-file') { $fixture.AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE = Join-Path $TestDrive 'absent.fixture' }
            $lines = [Collections.Generic.List[string]]::new()
            foreach ($key in $fixture.Keys) { Set-EnvValue $lines $key $fixture[$key] }
            [IO.File]::WriteAllLines($path, $lines)
            if ($mode -eq 'valid') { { & (Join-Path $repo 'infra/validate-production-env.ps1') -EnvironmentFile $path *> $null } | Should Not Throw ' ' }
            else { { & (Join-Path $repo 'infra/validate-production-env.ps1') -EnvironmentFile $path *> $null } | Should Throw ' ' }
        }
    }
}

Describe 'Wizard validation and metadata separation' {
    It 'accepts the documented input and requires all-or-none AI settings' {
        $options = New-TestOptions
        { Assert-SetupOption $options } | Should Not Throw ' '
        $options.AiBaseUrl = 'https://provider.test/v1'
        { Assert-SetupOption $options } | Should Throw ' '
        $options.AiModel = 'example-model'
        { Assert-SetupOption $options } | Should Not Throw ' '
    }
    It 'rejects dangerous hosts, duplicate hosts, bad ports, LAN, email, and controls' {
        foreach ($case in @(@('WebHost', 'https://bad.test'), @('IdentityHost', 'ai.example.test'), @('SqlHost', 'sql;trustServerCertificate=true'),
                @('SqlPort', '65536'), @('SqlPort', '0'), @('SqlPort', '01433'), @('LanPrefix', '999.1.1.'), @('LanPrefix', '192.168.'),
                @('AdminEmail', 'invalid'), @('CompanyName', "sentinel`nline"), @('AiBaseUrl', 'http://insecure.test'))) {
            $options = New-TestOptions; $options[$case[0]] = $case[1]
            { Assert-SetupOption $options } | Should Throw ' '
        }
    }
    It 'parses SQL without connecting and never reflects bad credentials in errors' {
        $connection = 'Server=tcp:sql.office.test,1444;Database=AI_Office;User ID=fixture;Password=sentinel;Encrypt=true;TrustServerCertificate=false'
        $endpoint = Get-SetupSqlEndpoint $connection
        $endpoint.HostName | Should Be 'sql.office.test'; $endpoint.Port | Should Be 1444; $endpoint.TrustServerCertificate | Should Be $false
        (Get-SetupSqlEndpoint $connection.Replace('TrustServerCertificate=false', 'TrustServerCertificate=true')).TrustServerCertificate | Should Be $true
        foreach ($bad in @('Password=sentinel;invalid syntax', $connection.Replace('AI_Office', 'master'), $connection.Replace('Encrypt=true', 'Encrypt=false'))) {
            try { $null = Get-SetupSqlEndpoint $bad; throw 'expected rejection' }
            catch { $_.Exception.Message | Should Not Match 'sentinel|expected rejection' }
        }
    }
    It 'persists only whitelisted non-secret metadata with stable IDs' {
        $options = New-TestOptions; $options.SqlPassword = 'SECRET-MUST-NOT-PERSIST'; $options.DatabaseConnection = 'SECRET-MUST-NOT-PERSIST'
        $path = Join-Path $TestDrive 'options.json'
        Write-SetupOption $path $options
        $json = [IO.File]::ReadAllText($path)
        $json | Should Not Match 'SECRET-MUST-NOT-PERSIST|SqlPassword|DatabaseConnection'
        ($json | ConvertFrom-Json).CompanyId | Should Be $options.CompanyId
    }
    It 'requires masked SQL fields and all-or-none provider key without leaking bad input' {
        $options = New-TestOptions; $secrets = @{}
        { Assert-SetupSecretInput $options $secrets } | Should Throw ' '
        foreach ($key in @('DatabaseConnection', 'SqlLogin', 'SqlPassword')) {
            $secure = [Security.SecureString]::new()
            foreach ($character in 'FAKE_SENTINEL_310'.ToCharArray()) { $secure.AppendChar($character) }
            $secrets[$key] = $secure
        }
        { Assert-SetupSecretInput $options $secrets } | Should Not Throw ' '
        $options.AiBaseUrl = 'https://provider.test'; $options.AiModel = 'model'
        { Assert-SetupSecretInput $options $secrets } | Should Throw ' '
        $secrets.AiApiKey = $secrets.SqlPassword
        { Assert-SetupSecretInput $options $secrets } | Should Not Throw ' '
        $options.AiBaseUrl = ''; $options.AiModel = ''
        { Assert-SetupSecretInput $options $secrets } | Should Throw ' '
    }
    It 'upgrades a fake PR309 env to file-backed Redis without rotating credentials or exposing output' {
        $directory = Join-Path $TestDrive 'legacy'; $null = New-Item -ItemType Directory $directory
        $path = Join-Path $directory 'fixture.env'
        [IO.File]::WriteAllText($path, 'AIOFFICE_SESSION_REDIS_PASSWORD=FAKE_REDIS_310')
        $output = Initialize-SetupRedisConfiguration $path *>&1 | Out-String
        $output | Should Be ''
        $config = Join-Path $directory 'session-redis.conf'
        [IO.File]::ReadAllText($config) | Should Match 'requirepass "FAKE_REDIS_310"'
        $original = [IO.File]::ReadAllText($config)
        Initialize-SetupRedisConfiguration $path
        [IO.File]::ReadAllText($config) | Should BeExactly $original
        [IO.File]::ReadAllText($path) | Should Match 'AIOFFICE_VPS_REDIS_CONFIG_FILE='
        Remove-Item -LiteralPath $config
        { Initialize-SetupRedisConfiguration $path } | Should Throw ' '
    }
}

Describe 'Sentinel secret leak regression' {
    It 'keeps SQL credentials in the private child environment, never process arguments' {
        $sentinel = 'FAKE_SQL_PROCESS_SECRET_310_$#'
        $start = New-SetupSqlStartInfo $sentinel 'C:\Windows'
        $start.Environment['AIOFFICE_SETUP_SQL'] | Should BeExactly $sentinel
        ($start.ArgumentList -join ' ') | Should Not Match ([regex]::Escape($sentinel))
        $start.FileName | Should Not Match ([regex]::Escape($sentinel))
        $start.RedirectStandardError | Should Be $true
        $start.RedirectStandardOutput | Should Be $true
        $start.CreateNoWindow | Should Be $true
    }
    It 'discards secrets emitted on every stream and thrown by an action, then greps logs and state' {
        $sentinels = @('FAKE_SQL_PASSWORD_310_$#', 'FAKE_CONNECTION_310', 'FAKE_AI_KEY_310', 'FAKE_KC_LOGIN_310')
        $path = Join-Path $TestDrive 'sentinel-state.json'; $log = Join-Path $TestDrive 'sentinel-setup.log'
        $actions = @{ prerequisites = {}; reboot = {}; 'docker-ready' = {}; secrets = {
            foreach ($sentinel in $sentinels) {
                Write-Output $sentinel; Write-Warning $sentinel; Write-Verbose $sentinel -Verbose; Write-Debug $sentinel -Debug
                Write-Information $sentinel -InformationAction Continue; Write-Host $sentinel; Write-Error $sentinel -ErrorAction Continue
            }
            throw ($sentinels -join ' ')
        }.GetNewClosure() }
        $output = & {
            try { Invoke-SetupPlan -Actions $actions -StatePath $path -LogPath $log }
            catch { $_.Exception.ToString() }
        } *>&1 | Out-String
        $output | Should Match 'secrets'
        $artifacts = [IO.File]::ReadAllText($path) + [IO.File]::ReadAllText($log) + $output
        foreach ($sentinel in $sentinels) { $artifacts | Should Not Match ([regex]::Escape($sentinel)) }
        @(Read-SetupState $path).Count | Should Be 3
    }
    It 'rejects arbitrary log messages and state payloads before creating artifacts' {
        $log = Join-Path $TestDrive 'injected.log'
        { Write-SetupEvent 'secrets' 'FAKE_PASSWORD_310' $log } | Should Throw ' '
        Test-Path $log | Should Be $false
    }
    It 'does not put the Redis password in container command arguments' {
        $overlay = [IO.File]::ReadAllText((Join-Path $repo 'compose.vps.yaml'))
        $overlay | Should Not Match '--requirepass|AIOFFICE_SESSION_REDIS_PASSWORD'
        $overlay | Should Match '/run/secrets/session-redis.conf'
    }
}
