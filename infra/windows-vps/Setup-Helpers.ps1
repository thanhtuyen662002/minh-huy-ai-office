# Pure setup policy and file helpers. Dot-sourcing this file has no side effects.
function Get-SetupPhase {
    param([switch]$Update)
    if ($Update) { return @('migrate', 'keycloak', 'deploy', 'health') }
    return @('prerequisites', 'reboot', 'docker-ready', 'secrets', 'firewall', 'iis',
        'certificates', 'migrate', 'keycloak', 'deploy', 'health', 'first-admin', 'open-browser')
}

function Get-SetupPlan {
    param([string[]]$Completed = @(), [switch]$Update)
    $phases = @(Get-SetupPhase -Update:$Update)
    if ($Completed.Count -gt $phases.Count) { throw 'Trạng thái cài đặt không hợp lệ.' }
    for ($i = 0; $i -lt $Completed.Count; $i++) {
        if ($Completed[$i] -cne $phases[$i]) { throw 'Trạng thái cài đặt không hợp lệ.' }
    }
    return $phases | Select-Object -Skip $Completed.Count
}

function Read-SetupState {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    try {
        $json = [IO.File]::ReadAllText($Path)
        if (-not $json.Trim().StartsWith('[')) { throw 'Invalid state.' }
        $completed = @(ConvertFrom-Json -InputObject $json -ErrorAction Stop)
        foreach ($item in $completed) { if ($item -isnot [string]) { throw 'Invalid state.' } }
        $null = Get-SetupPlan -Completed $completed
        return $completed
    } catch { throw 'Không đọc được trạng thái cài đặt. Khôi phục bản sao trạng thái; không xóa thư mục secrets.' }
}

function Write-SetupState {
    param([string]$Path, [string[]]$Completed)
    $null = Get-SetupPlan -Completed $Completed
    # Only an ordered array of known phase names can reach disk. Atomic replacement.
    $json = ConvertTo-Json -InputObject @($Completed) -Compress
    [IO.File]::WriteAllText("$Path.tmp", $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move("$Path.tmp", $Path, $true)
}

function Get-SetupFix {
    param([string]$Phase)
    switch ($Phase) {
        'prerequisites' { 'Cài App Installer (winget), kiểm tra Internet và chạy lại bằng quyền quản trị.' }
        'reboot' { 'Khởi động lại Windows, đăng nhập đúng tài khoản vận hành rồi chạy lại Setup-AIOffice.cmd.' }
        'docker-ready' { 'Nhờ quản trị máy chủ bật nested virtualization / CPU type host. Mở Docker Desktop, dùng WSL2 và bật Start Docker Desktop when you sign in.' }
        'secrets' { 'Kiểm tra kết nối TCP và đăng nhập SQL cho cả hai DB; SQL cần chứng chỉ TLS tin cậy. Kiểm tra các ô nhập. Không xóa hoặc tái tạo secrets đã có.' }
        'firewall' { 'Chạy bằng quyền quản trị; chỉ chuyển tiếp TCP 80/443, không mở RDP hay SQL ra Internet.' }
        'iis' { 'Kiểm tra IIS, URL Rewrite và ARR đã được cài; chạy lại bước chuẩn bị nếu thiếu.' }
        'certificates' { 'Tạo DNS A cho cả ba tên trỏ về IP công cộng, bỏ DNS AAAA sai và nhờ quản trị chuyển tiếp TCP 80/443 về VM. Cho phép api.ipify.org và check-host.net; không bỏ qua kiểm tra từ Internet.' }
        'migrate' { 'Kiểm tra bản sao lưu, quyền SQL và các migration đã phiên bản hóa rồi thử lại.' }
        'keycloak' { 'Kiểm tra DB AIOffice_Identity và chứng chỉ SQL tin cậy với đúng tên máy; giữ trustServerCertificate=false.' }
        'deploy' { 'Kiểm tra Docker Desktop, Internet, Git sạch và cấu hình production; thử lại cùng phiên bản.' }
        'health' { 'Kiểm tra HTTPS, chứng chỉ, DNS, Core /health, web và discovery của realm aioffice.' }
        'first-admin' { 'Kiểm tra công ty, email quản trị và SQL; giữ nguyên mã định danh khi thử lại. Tạo tài khoản Keycloak qua quy trình quản trị hiện có.' }
        'open-browser' { 'Mở địa chỉ web bằng trình duyệt. Kiểm tra quyền tạo lối tắt trên Desktop.' }
        default { 'Kiểm tra cấu hình cài đặt và thử lại.' }
    }
}

function Write-SetupEvent {
    param([string]$Phase, [string]$Status, [string]$LogPath, [scriptblock]$ProgressCallback)
    if ($Phase -cnotin @(Get-SetupPhase) -or $Status -cnotin @('running', 'completed', 'failed', 'skipped', 'planned', 'paused')) {
        throw 'Sự kiện cài đặt không hợp lệ.'
    }
    # Deliberately no arbitrary message/exception/configuration parameter.
    if ($LogPath) { [IO.File]::AppendAllText($LogPath, "$Phase $Status`n") }
    if ($ProgressCallback) { & $ProgressCallback $Phase $Status | Out-Null }
}

function Invoke-SetupPlan {
    param([hashtable]$Actions = @{}, [string[]]$Completed = @(), [string]$StatePath,
        [string]$LogPath, [scriptblock]$ProgressCallback, [switch]$DryRun,
        [switch]$Update, [string]$StopAfter)
    $null = Get-SetupPlan -Completed $Completed -Update:$Update
    foreach ($phase in @(Get-SetupPhase -Update:$Update)) {
        if ($DryRun) {
            # This branch must stay above ALL reads, writes, callbacks and actions.
            [pscustomobject]@{ Phase = $phase; Status = 'planned' }
            continue
        }
        if ($phase -cin $Completed) {
            Write-SetupEvent $phase 'skipped' $LogPath $ProgressCallback
        } else {
            Write-SetupEvent $phase 'running' $LogPath $ProgressCallback
            try {
                if (-not $Actions.ContainsKey($phase)) { throw 'Missing action.' }
                # Suppress all six streams, including native stderr and Write-Host.
                & $Actions[$phase] *> $null
                $Completed += $phase
                if ($StatePath) { Write-SetupState $StatePath $Completed }
            } catch {
                if ($phase -ceq 'reboot' -and $_.Exception -is [OperationCanceledException]) {
                    Write-SetupEvent $phase 'paused' $LogPath $ProgressCallback
                    $Error.Clear()
                    return
                }
                Write-SetupEvent $phase 'failed' $LogPath $ProgressCallback
                # Never rethrow the original ErrorRecord or retain it as InnerException.
                $Error.Clear()
                throw [InvalidOperationException]::new("$phase : $(Get-SetupFix $phase)")
            }
            Write-SetupEvent $phase 'completed' $LogPath $ProgressCallback
        }
        if ($phase -ceq $StopAfter) { break }
    }
}

function ConvertTo-SetupEnvValue {
    param([AllowEmptyString()][string]$Value)
    if ($Value -match '[\x00-\x1f\x7f]') { throw 'Giá trị phải nằm trên một dòng, không có ký tự điều khiển.' }
    # Ambiguous backslash/quote sequences use Compose's double-quoted escaping.
    if ($Value.EndsWith('\') -or $Value.Contains("\'")) {
        return '"' + $Value.Replace('\', '\\').Replace('"', '\"').Replace('$', '\$') + '"'
    }
    # Compose single quotes suppress $ interpolation and # comments. Escape apostrophes.
    return "'" + $Value.Replace("'", "\'") + "'"
}

function ConvertFrom-SetupEnvValue {
    param([AllowEmptyString()][string]$Value)
    $valueText = $Value.Trim()
    if ($valueText.StartsWith("'") -and $valueText.EndsWith("'") -and $valueText.Length -ge 2) {
        return $valueText.Substring(1, $valueText.Length - 2).Replace("\'", "'")
    }
    if ($valueText.StartsWith('"') -and $valueText.EndsWith('"') -and $valueText.Length -ge 2) {
        $body = $valueText.Substring(1, $valueText.Length - 2).Replace('\"', '"')
        return [regex]::Replace($body, '\\([\\$nrt])|\$\$', {
            param($match)
            switch ($match.Value) {
                '\\' { '\' }
                '\$' { '$' }
                '$$' { '$' }
                '\n' { "`n" }
                '\r' { "`r" }
                '\t' { "`t" }
            }
        })
    }
    return $valueText
}

function Set-EnvValue {
    param([System.Collections.Generic.List[string]]$Lines, [string]$Name, [AllowEmptyString()][string]$Value)
    if ($Name -cnotmatch '\A[A-Z][A-Z0-9_]*\z') { throw 'Tên cấu hình không hợp lệ.' }
    $encoded = ConvertTo-SetupEnvValue $Value
    # Keep simple values unquoted for backward compatibility with manual tooling.
    if ($Value -cmatch '\A[A-Za-z0-9_./:@+=,-]*\z') { $encoded = $Value }
    $prefix = "$Name="
    for ($i = $Lines.Count - 1; $i -ge 0; $i--) {
        if ($Lines[$i].StartsWith($prefix, [StringComparison]::Ordinal)) { $Lines.RemoveAt($i) }
    }
    $Lines.Add($prefix + $encoded)
}

function Assert-SetupHost {
    param([string]$Value)
    if ($Value.Length -gt 253 -or $Value -cnotmatch '\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\z') {
        throw 'Tên máy chỉ gồm các nhãn DNS hợp lệ; không nhập URL, cổng hoặc thuộc tính SQL.'
    }
}

function Assert-SetupOption {
    param([hashtable]$Options)
    foreach ($key in @('WebHost', 'ApiHost', 'IdentityHost', 'SqlHost')) { Assert-SetupHost $Options[$key] }
    if (@($Options.WebHost, $Options.ApiHost, $Options.IdentityHost | Sort-Object -Unique).Count -ne 3) { throw 'Ba tên miền phải khác nhau.' }
    if ($Options.SqlPort -notmatch '\A[1-9][0-9]{0,4}\z' -or [int]$Options.SqlPort -gt 65535) { throw 'Cổng SQL phải từ 1 đến 65535.' }
    if ($Options.LanPrefix -notmatch '\A(?:[0-9]{1,3}\.){3}\z' -or
        @($Options.LanPrefix.TrimEnd('.').Split('.') | Where-Object { [int]$_ -gt 255 -or ($_ -ne '0' -and $_.StartsWith('0')) }).Count) {
        throw 'Tiền tố LAN gồm ba octet IPv4 và dấu chấm cuối, ví dụ 192.168.68.'
    }
    foreach ($key in @('CertificateEmail', 'AdminEmail')) {
        if ($Options[$key] -notmatch '\A[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+\z') { throw 'Email không hợp lệ.' }
    }
    foreach ($key in @('CompanyCode', 'CompanyName')) {
        $limit = if ($key -eq 'CompanyCode') { 100 } else { 200 }
        if ([string]::IsNullOrWhiteSpace($Options[$key]) -or $Options[$key].Length -gt $limit -or $Options[$key] -ne $Options[$key].Trim()) { throw 'Mã hoặc tên công ty không hợp lệ.' }
    }
    foreach ($value in $Options.Values) { $null = ConvertTo-SetupEnvValue ([string]$value) }
    $providerFields = @('AiBaseUrl', 'AiModel' | Where-Object { $Options[$_] })
    if ($providerFields.Count) {
        $uri = $null
        if ($providerFields.Count -ne 2 -or -not [Uri]::TryCreate($Options.AiBaseUrl, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'AI tùy chọn cần URL HTTPS và model đầy đủ.' }
    }
}

function Get-SetupSqlEndpoint {
    param([string]$ConnectionString)
    try {
        $builder = [Data.Common.DbConnectionStringBuilder]::new()
        $builder.set_ConnectionString($ConnectionString)
        $server = if ($builder.ContainsKey('Server')) { $builder['Server'] } else { $builder['Data Source'] }
        $db = if ($builder.ContainsKey('Database')) { $builder['Database'] } else { $builder['Initial Catalog'] }
        $user = if ($builder.ContainsKey('User ID')) { $builder['User ID'] } else { $builder['UID'] }
        $password = if ($builder.ContainsKey('Password')) { $builder['Password'] } else { $builder['PWD'] }
        if ($db -cne 'AI_Office' -or -not $user -or -not $password -or $builder['Integrated Security'] -match 'true|sspi' -or
            $builder['Encrypt'] -notmatch '\A(?i:true|mandatory)\z') { throw 'Invalid SQL.' }
        # TrustServerCertificate=true is an explicit operator opt-in for a self-signed SQL
        # certificate. It is carried to the Keycloak connection; encryption stays mandatory.
        $trust = $builder.ContainsKey('TrustServerCertificate') -and $builder['TrustServerCertificate'] -match '\A(?i:true|yes)\z'
        $parts = ($server -replace '\Atcp:', '') -split ','
        Assert-SetupHost $parts[0]
        $port = if ($parts.Count -eq 2) { $parts[1] } else { '1433' }
        if ($parts.Count -gt 2 -or $port -notmatch '\A[1-9][0-9]{0,4}\z' -or [int]$port -gt 65535) { throw 'Invalid SQL.' }
        return @{ HostName = $parts[0]; Port = [int]$port; TrustServerCertificate = $trust }
    } catch { throw 'Chuỗi kết nối cần SQL login, Database=AI_Office, Server=tên,cổng và Encrypt=true (TrustServerCertificate=true chỉ khi SQL dùng chứng chỉ tự ký).' }
}

function New-SetupSqlStartInfo {
    param([string]$ConnectionString, [string]$WindowsDirectory)
    $code = '$ErrorActionPreference="Stop"; $c=New-Object System.Data.SqlClient.SqlConnection; try {$c.ConnectionString=$env:AIOFFICE_SETUP_SQL; $c.Open(); exit 0} catch {exit 1} finally {$c.Dispose()}'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $WindowsDirectory 'System32\WindowsPowerShell\v1.0\powershell.exe'
    foreach ($arg in @('-NoProfile', '-NonInteractive', '-Command', $code)) { $start.ArgumentList.Add($arg) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['AIOFFICE_SETUP_SQL'] = $ConnectionString
    return $start
}

function ConvertFrom-SetupSecureString {
    param([Security.SecureString]$Value)
    if ($null -eq $Value -or $Value.Length -eq 0) { throw 'Thiếu thông tin xác thực.' }
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Assert-SetupSecretInput {
    param([hashtable]$Options, [hashtable]$Secrets)
    foreach ($key in @('DatabaseConnection', 'SqlLogin', 'SqlPassword')) {
        if ($Secrets[$key] -isnot [Security.SecureString] -or $Secrets[$key].Length -eq 0) { throw 'Nhập đủ thông tin xác thực SQL.' }
        $null = ConvertTo-SetupEnvValue (ConvertFrom-SetupSecureString $Secrets[$key])
    }
    $hasKey = $Secrets.AiApiKey -is [Security.SecureString] -and $Secrets.AiApiKey.Length -gt 0
    if ([bool]$Options.AiBaseUrl -ne $hasKey) { throw 'AI tùy chọn cần URL, model và API key cùng lúc, hoặc để trống cả ba.' }
    if ($hasKey) { $null = ConvertTo-SetupEnvValue (ConvertFrom-SetupSecureString $Secrets.AiApiKey) }
}

function Get-SetupRedisConfiguration {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Constructs a protected Redis secret file in memory; this value never enters logs, argv or exceptions.')]
    [CmdletBinding()]
    param([string]$Password)
    $null = ConvertTo-SetupEnvValue $Password
    if (-not $Password) { throw 'Thiếu mật khẩu Redis.' }
    $escaped = $Password.Replace('\', '\\').Replace('"', '\"')
    return @"
port 0
tls-port 6380
tls-cert-file /tls/session-redis.crt
tls-key-file /tls/session-redis.key
tls-ca-cert-file /tls/ca.crt
tls-auth-clients no
requirepass "$escaped"
save ""
appendonly no
"@
}

function Initialize-SetupRedisConfiguration {
    param([string]$EnvironmentFile)
    $lines = [Collections.Generic.List[string]]::new()
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($EnvironmentFile)) {
        $lines.Add($line)
        if ($line -match '\A([A-Z][A-Z0-9_]*)=(.*)\z') { $values[$Matches[1]] = ConvertFrom-SetupEnvValue $Matches[2] }
    }
    if ($values.AIOFFICE_VPS_REDIS_CONFIG_FILE) {
        if (-not (Test-Path -LiteralPath $values.AIOFFICE_VPS_REDIS_CONFIG_FILE -PathType Leaf)) { throw 'Thiếu tệp Redis được bảo vệ; khôi phục bản sao lưu.' }
        return
    }
    # Expand existing PR309 installs without rotating any secret. The new file
    # inherits the already-protected env directory's ACL, outside the checkout.
    $path = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EnvironmentFile))) 'session-redis.conf'
    if (-not (Test-Path -LiteralPath $path)) {
        [IO.File]::WriteAllText("$path.tmp", (Get-SetupRedisConfiguration $values.AIOFFICE_SESSION_REDIS_PASSWORD), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move("$path.tmp", $path)
    }
    Set-EnvValue $lines 'AIOFFICE_VPS_REDIS_CONFIG_FILE' $path
    [IO.File]::WriteAllLines("$EnvironmentFile.tmp", $lines, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move("$EnvironmentFile.tmp", $EnvironmentFile, $true)
}
