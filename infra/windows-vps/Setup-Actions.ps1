# Machine operations, loaded only by the real wizard. Never called by -WhatIf.
function Set-SetupDirectoryAcl {
    param([string]$Path)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $sids = @('S-1-5-32-544', 'S-1-5-18', [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    foreach ($sid in $sids) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Initialize-SetupDirectory {
    param([string]$Path)
    $null = New-Item -ItemType Directory -Path $Path -Force
    if ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Không dùng thư mục cài đặt chuyển hướng.' }
    Set-SetupDirectoryAcl $Path
}

function Write-SetupOption {
    param([string]$Path, [hashtable]$Options)
    Assert-SetupOption $Options
    $safe = @{}
    foreach ($key in @('WebHost', 'ApiHost', 'IdentityHost', 'LanPrefix', 'CertificateEmail', 'SqlHost', 'SqlPort',
            'AiBaseUrl', 'AiModel', 'CompanyCode', 'CompanyName', 'AdminEmail', 'TenantId', 'CompanyId', 'AutoSignIn')) {
        $safe[$key] = $Options[$key]
    }
    foreach ($key in @('TenantId', 'CompanyId')) {
        $id = [Guid]::Empty
        if (-not [Guid]::TryParse($safe[$key], [ref]$id) -or $id -eq [Guid]::Empty) { throw 'Mã định danh cài đặt không hợp lệ.' }
    }
    [IO.File]::WriteAllText("$Path.tmp", (ConvertTo-Json $safe), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move("$Path.tmp", $Path, $true)
}

function Invoke-SetupNative {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Lệnh của bước cài đặt thất bại.' }
}

function Test-SetupTcp {
    param([string]$HostName, [int]$Port)
    $client = [Net.Sockets.TcpClient]::new()
    try { $client.ConnectAsync($HostName, $Port).WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() }
    finally { $client.Dispose() }
}

function Test-SetupSqlLogin {
    param([string]$ConnectionString)
    # Windows PowerShell provides System.Data.SqlClient on a clean Windows VM.
    # Only static code is in argv. The connection exists only in the child's private
    # environment. Drain both output streams without displaying or persisting them.
    $start = New-SetupSqlStartInfo $ConnectionString $env:windir
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'SQL timeout.' }
        $null = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'SQL login failed.' }
    } finally { $start.Environment.Remove('AIOFFICE_SETUP_SQL') | Out-Null; $process.Dispose() }
}

function Assert-SetupSqlConnection {
    param([hashtable]$Options, [hashtable]$Secrets)
    $connection = ConvertFrom-SetupSecureString $Secrets.DatabaseConnection
    $endpoint = Get-SetupSqlEndpoint $connection
    Test-SetupTcp $endpoint.HostName $endpoint.Port
    Test-SetupSqlLogin $connection
    Test-SetupTcp $Options.SqlHost ([int]$Options.SqlPort)
    $builder = [Data.Common.DbConnectionStringBuilder]::new()
    $builder['Server'] = 'tcp:' + $Options.SqlHost + ',' + $Options.SqlPort
    $builder['Database'] = 'AIOffice_Identity'
    $builder['User ID'] = ConvertFrom-SetupSecureString $Secrets.SqlLogin
    $builder['Password'] = ConvertFrom-SetupSecureString $Secrets.SqlPassword
    $builder['Encrypt'] = 'true'
    $builder['TrustServerCertificate'] = 'false'
    $builder['Connect Timeout'] = '10'
    Test-SetupSqlLogin $builder.get_ConnectionString()
}

function Assert-SetupPublicIngress {
    param([hashtable]$Options)
    $publicIp = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 15).Trim()
    $parsedIp = $null
    if (-not [Net.IPAddress]::TryParse($publicIp, [ref]$parsedIp) -or $parsedIp.AddressFamily -ne 'InterNetwork') { throw 'Public IPv4 unavailable.' }
    foreach ($name in @($Options.WebHost, $Options.ApiHost, $Options.IdentityHost)) {
        # Require every public A to be this office, and reject AAAA (IPv4-only ingress).
        $addresses = @([Net.Dns]::GetHostAddresses($name))
        if (-not $addresses.Count -or @($addresses | Where-Object { $_.ToString() -ne $publicIp }).Count) { throw 'DNS does not match.' }
        # A connection from this VM is NAT loopback, not evidence of outside reachability.
        $request = Invoke-RestMethod -Uri ("https://check-host.net/check-tcp?host=" + [Uri]::EscapeDataString("${name}:80") + '&max_nodes=3') -Headers @{ Accept = 'application/json' } -TimeoutSec 15
        if ($request.ok -ne 1 -or $request.request_id -cnotmatch '\A[a-zA-Z0-9_-]+\z') { throw 'External probe unavailable.' }
        $reachable = $false
        for ($attempt = 0; $attempt -lt 15; $attempt++) {
            Start-Sleep -Seconds 2
            $result = Invoke-RestMethod -Uri ("https://check-host.net/check-result/" + $request.request_id) -Headers @{ Accept = 'application/json' } -TimeoutSec 15
            $success = @($result.PSObject.Properties | Where-Object {
                $row = @($_.Value)[0]
                $null -ne $row -and $null -ne $row.time -and -not $row.error -and $row.address -eq $publicIp
            })
            if ($success.Count -ge 2) { $reachable = $true; break }
        }
        if (-not $reachable) { throw 'External TCP 80 unreachable.' }
    }
}

function Wait-SetupHttpsEndpoint {
    param([hashtable]$Options)
    foreach ($url in @("https://$($Options.WebHost)/", "https://$($Options.ApiHost)/health", "https://$($Options.IdentityHost)/realms/aioffice/.well-known/openid-configuration")) {
        $ready = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try {
                $response = Invoke-WebRequest -Uri $url -TimeoutSec 5 -MaximumRedirection 0
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            } catch { Start-Sleep -Seconds 2 }
        }
        if (-not $ready) { throw 'HTTPS health check failed.' }
    }
}

function Enable-SetupBrowser {
    param([string]$EnvironmentFile)
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($line in [IO.File]::ReadAllLines($EnvironmentFile)) { $lines.Add($line) }
    Set-EnvValue $lines 'AIOFFICE_BROWSER_OIDC_ENABLED' 'true'
    [IO.File]::WriteAllLines("$EnvironmentFile.tmp", $lines, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move("$EnvironmentFile.tmp", $EnvironmentFile, $true)
}

function Open-SetupAutologon {
    param([string]$ToolsDirectory)
    $directory = Join-Path $ToolsDirectory 'autologon'
    $null = New-Item -ItemType Directory -Path $directory -Force
    $zip = Join-Path $directory 'Autologon.zip'
    Invoke-WebRequest -Uri 'https://download.sysinternals.com/files/Autologon.zip' -OutFile $zip
    Expand-Archive -LiteralPath $zip -DestinationPath $directory -Force
    $exe = Join-Path $directory 'Autologon64.exe'
    $signature = Get-AuthenticodeSignature -FilePath $exe
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') { throw 'Invalid Autologon signature.' }
    # Interactive credential dialog only. Never pass a password in argv or Winlogon.
    Start-Process -FilePath $exe -Wait
}

function New-SetupShortcut {
    param([string]$RepositoryRoot, [string]$WebHost)
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $shell = New-Object -ComObject WScript.Shell
    $update = $shell.CreateShortcut((Join-Path $desktop 'Cập nhật AI Office.lnk'))
    $update.TargetPath = Join-Path $RepositoryRoot 'Setup-AIOffice.cmd'
    $update.Arguments = '--update'
    $update.WorkingDirectory = $RepositoryRoot
    $update.Save()
    [IO.File]::WriteAllText((Join-Path $desktop 'Mở AI Office.url'), "[InternetShortcut]`r`nURL=https://$WebHost/`r`n")
}

function Invoke-SetupReboot {
    param([string]$Root, [string]$RepositoryRoot)
    $marker = Join-Path $Root 'reboot-request.json'
    $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
    if ((Test-Path -LiteralPath $marker) -and ([IO.File]::ReadAllText($marker) -cne $boot)) {
        Remove-Item -LiteralPath $marker
        return
    }
    [IO.File]::WriteAllText($marker, $boot)
    # HKLM RunOnce is administrator-logon scoped. ! retains the entry until launch.
    $entry = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce'
    $null = New-Item -Path $entry -Force
    $command = '"' + (Join-Path $RepositoryRoot 'Setup-AIOffice.cmd') + '" --resume'
    $null = New-ItemProperty -Path $entry -Name '!AIOfficeSetup' -Value $command -PropertyType String -Force
    Restart-Computer -Force
    # Never acknowledge reboot in the old boot. Completion is proven on next launch.
    throw [OperationCanceledException]::new('RebootPending')
}

function New-SetupAction {
    param([string]$Root, [string]$RepositoryRoot, [hashtable]$Options, [hashtable]$Secrets, [switch]$Update)
    $scripts = Join-Path $RepositoryRoot 'infra\windows-vps'
    $envFile = Join-Path $Root 'secrets\.env.production'
    $actions = @{}
    $actions['prerequisites'] = { & (Join-Path $scripts 'Install-Prerequisites.ps1') -ToolsDirectory (Join-Path $Root 'tools') }.GetNewClosure()
    $actions['reboot'] = { Invoke-SetupReboot $Root $RepositoryRoot }.GetNewClosure()
    $actions['docker-ready'] = {
        Invoke-SetupNative 'wsl.exe' @('--update')
        Invoke-SetupNative 'wsl.exe' @('--set-default-version', '2')
        $dockerDesktop = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
        # Docker Desktop is session-based. Register startup for this operator only.
        $startup = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $null = New-Item -Path $startup -Force
        $null = New-ItemProperty -Path $startup -Name 'AIOfficeDockerDesktop' -Value ('"' + $dockerDesktop + '"') -PropertyType String -Force
        if (-not (Get-Process 'Docker Desktop' -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $dockerDesktop)) {
            Start-Process -FilePath $dockerDesktop -WindowStyle Hidden
        }
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            & docker info *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Seconds 3
        }
        if (-not $ready) { throw 'Docker unavailable.' }
        Invoke-SetupNative 'wsl.exe' @('--distribution', 'docker-desktop', '--exec', '/bin/true')
        Invoke-SetupNative 'docker' @('run', '--rm', 'hello-world')
    }.GetNewClosure()
    $actions['secrets'] = {
        Assert-SetupOption $Options
        # A completed env file is immutable here; retry after crash reuses it.
        if (Test-Path -LiteralPath $envFile) {
            $saved = Get-Content -LiteralPath (Join-Path $Root 'secrets\setup-options.json') -Raw | ConvertFrom-Json -AsHashtable
            foreach ($key in $saved.Keys) { if ([string]$saved[$key] -cne [string]$Options[$key]) { throw 'Existing installation differs.' } }
            return
        }
        Assert-SetupSecretInput $Options $Secrets
        Assert-SetupSqlConnection $Options $Secrets
        if ($Options.AiBaseUrl) { $null = ConvertFrom-SetupSecureString $Secrets.AiApiKey }
        & (Join-Path $scripts 'New-ProductionSecrets.ps1') -WebHost $Options.WebHost -ApiHost $Options.ApiHost -IdentityHost $Options.IdentityHost `
            -SecretsDirectory (Join-Path $Root 'secrets') -RepositoryRoot $RepositoryRoot -WizardInput $Secrets -SetupOptions $Options
    }.GetNewClosure()
    $actions['firewall'] = { & (Join-Path $scripts 'Configure-Firewall.ps1') }.GetNewClosure()
    # WebAdministration's IIS provider requires Windows PowerShell. All arguments
    # here are validated public configuration; credentials never enter this child.
    $iis = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $scripts 'Configure-IisReverseProxy.ps1'),
        '-WebHost', $Options.WebHost, '-ApiHost', $Options.ApiHost, '-IdentityHost', $Options.IdentityHost,
        '-LanPrefix', $Options.LanPrefix, '-SitesRoot', (Join-Path $Root 'iis'))
    $windowsPowerShell = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $actions['iis'] = { Invoke-SetupNative $windowsPowerShell $iis }.GetNewClosure()
    $actions['certificates'] = {
        Assert-SetupPublicIngress $Options
        Invoke-SetupNative $windowsPowerShell ($iis + @('-RequestCertificates', '-CertificateEmail', $Options.CertificateEmail, '-WinAcmePath', (Join-Path $Root 'tools\win-acme\wacs.exe')))
    }.GetNewClosure()
    $fetch = -not $Update
    $actions['migrate'] = {
        & (Join-Path $scripts 'Update-AIOffice.ps1') -RepositoryRoot $RepositoryRoot -EnvironmentFile $envFile -Phase Migrate -Migrate -SkipFetch:$fetch
    }.GetNewClosure()
    $actions['keycloak'] = {
        & (Join-Path $scripts 'Update-AIOffice.ps1') -RepositoryRoot $RepositoryRoot -EnvironmentFile $envFile -Phase Keycloak -SkipFetch
    }.GetNewClosure()
    $actions['deploy'] = {
        & (Join-Path $scripts 'Update-AIOffice.ps1') -RepositoryRoot $RepositoryRoot -EnvironmentFile $envFile -Phase Deploy -SkipFetch
    }.GetNewClosure()
    $actions['health'] = { Wait-SetupHttpsEndpoint $Options }.GetNewClosure()
    $actions['first-admin'] = {
        & (Join-Path $RepositoryRoot 'infra\bootstrap-company.ps1') -TenantId $Options.TenantId -CompanyId $Options.CompanyId `
            -CompanyCode $Options.CompanyCode -CompanyName $Options.CompanyName -AdminEmail $Options.AdminEmail -EnvironmentFile $envFile
        Enable-SetupBrowser $envFile
        & (Join-Path $scripts 'Update-AIOffice.ps1') -RepositoryRoot $RepositoryRoot -EnvironmentFile $envFile -Phase Deploy -SkipFetch
        Wait-SetupHttpsEndpoint $Options
    }.GetNewClosure()
    $actions['open-browser'] = {
        New-SetupShortcut $RepositoryRoot $Options.WebHost
        if ($Options.AutoSignIn) { Open-SetupAutologon (Join-Path $Root 'tools') }
        Start-Process "https://$($Options.WebHost)/"
    }.GetNewClosure()
    return $actions
}
