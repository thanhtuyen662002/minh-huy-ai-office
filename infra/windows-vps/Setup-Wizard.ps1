#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..'),
    [string]$Root = 'C:\AIOffice',
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Setup-Helpers.ps1')

# No GUI, admin check, file access, network, process launch, action import or
# ShouldProcess callback in this branch. CI exercises the complete phase walk.
if ($WhatIfPreference) {
    Invoke-SetupPlan -DryRun -Update:$Update
    return
}
if (-not $PSCmdlet.ShouldProcess('Windows VPS', 'Cài đặt / cập nhật AI Office')) { return }

try {
    . (Join-Path $PSScriptRoot 'Setup-Actions.ps1')
    $admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator required.' }
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    Initialize-SetupDirectory $Root
    Initialize-SetupDirectory (Join-Path $Root 'logs')
    Initialize-SetupDirectory (Join-Path $Root 'secrets')
    $lock = [IO.File]::Open((Join-Path $Root 'setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $statePath = Join-Path $Root 'setup-state.json'
    $optionsPath = Join-Path $Root 'secrets\setup-options.json'
    $completed = @(Read-SetupState $statePath)
    if ($Update -and $completed.Count -ne @(Get-SetupPhase).Count) { throw 'Complete setup before updating.' }
    $savedOptions = if (Test-Path -LiteralPath $optionsPath) { Get-Content -LiteralPath $optionsPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
    if ($savedOptions.Count) { Assert-SetupOption $savedOptions }
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()

    $form = [Windows.Forms.Form]::new()
    $form.Text = if ($Update) { 'Cập nhật AI Office' } else { 'Cài đặt AI Office' }
    $form.Size = [Drawing.Size]::new(850, 850)
    $form.MinimumSize = [Drawing.Size]::new(760, 650)
    $form.StartPosition = 'CenterScreen'
    $form.AutoScaleMode = 'Dpi'
    $layout = [Windows.Forms.TableLayoutPanel]::new()
    $layout.Dock = 'Fill'; $layout.RowCount = 5; $layout.ColumnCount = 1
    $layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Percent', 100))
    $layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', 185))
    $layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', 75))
    $layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', 45))
    $layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', 35))
    $form.Controls.Add($layout)
    $fields = [Windows.Forms.TableLayoutPanel]::new()
    $fields.Dock = 'Fill'; $fields.AutoScroll = $true; $fields.ColumnCount = 2
    $fields.ColumnStyles.Add([Windows.Forms.ColumnStyle]::new('Percent', 43))
    $fields.ColumnStyles.Add([Windows.Forms.ColumnStyle]::new('Percent', 57))
    $layout.Controls.Add($fields, 0, 0)
    $inputs = @{}
    $defaults = @{ WebHost = 'ai.bms79.com'; ApiHost = 'api.bms79.com'; IdentityHost = 'login.bms79.com'; SqlPort = '1433'; CompanyCode = 'MINHHUY'; CompanyName = 'Minh Huy'; LanPrefix = '' }
    $lan = Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1
    $localAddress = $lan.IPv4Address.IPAddress | Select-Object -First 1
    if ($localAddress) { $defaults.LanPrefix = ($localAddress.Split('.')[0..2] -join '.') + '.' }
    $definitions = @(
        @('WebHost', 'Tên miền Web'), @('ApiHost', 'Tên miền API'), @('IdentityHost', 'Tên miền đăng nhập'),
        @('LanPrefix', 'Tiền tố LAN (ví dụ 192.168.68.)'), @('CertificateEmail', "Email Let's Encrypt"),
        @('DatabaseConnection', 'Kết nối SQL AI_Office', $true), @('SqlHost', 'Máy SQL AIOffice_Identity'), @('SqlPort', 'Cổng SQL Keycloak'),
        @('SqlLogin', 'Tài khoản SQL Keycloak', $true), @('SqlPassword', 'Mật khẩu SQL Keycloak', $true),
        @('AiBaseUrl', 'AI tùy chọn: URL HTTPS'), @('AiModel', 'AI tùy chọn: model'), @('AiApiKey', 'AI tùy chọn: API key', $true),
        @('CompanyCode', 'Mã công ty'), @('CompanyName', 'Tên công ty'), @('AdminEmail', 'Email quản trị đầu tiên')
    )
    foreach ($definition in $definitions) {
        $label = [Windows.Forms.Label]::new(); $label.Text = $definition[1]; $label.AutoSize = $true; $label.Anchor = 'Left'
        $box = [Windows.Forms.TextBox]::new(); $box.Dock = 'Top'; $box.UseSystemPasswordChar = [bool]$definition[2]
        if (-not $box.UseSystemPasswordChar) { $box.Text = if ($savedOptions.ContainsKey($definition[0])) { [string]$savedOptions[$definition[0]] } else { [string]$defaults[$definition[0]] } }
        $fields.Controls.Add($label); $fields.Controls.Add($box)
        $inputs[$definition[0]] = $box
    }
    $autoSignIn = [Windows.Forms.CheckBox]::new()
    $autoSignIn.Text = 'Tùy chọn: mở Autologon để tự đăng nhập tài khoản vận hành'
    $autoSignIn.AutoSize = $true
    $fields.Controls.Add($autoSignIn); $fields.SetColumnSpan($autoSignIn, 2)
    $notice = [Windows.Forms.Label]::new(); $notice.AutoSize = $true
    $notice.MaximumSize = [Drawing.Size]::new(700, 0)
    $notice.Text = 'Docker Desktop cần phiên đăng nhập. Bật Start Docker Desktop when you sign in. Tự đăng nhập giúp phục hồi sau Windows Update nhưng người có quyền truy cập VM có thể dùng phiên này; quản trị viên có thể lấy mật khẩu LSA. Nếu chọn, nhập mật khẩu Windows trực tiếp trong Autologon của Microsoft rồi bấm Enable. Không chọn: đăng nhập thủ công sau reboot. Cổng 80 được kiểm tra qua check-host.net (tên miền/IP công khai).'
    $fields.Controls.Add($notice); $fields.SetColumnSpan($notice, 2)
    $progress = [Windows.Forms.ListView]::new(); $progress.Dock = 'Fill'; $progress.View = 'Details'
    $null = $progress.Columns.Add('Bước', 260); $null = $progress.Columns.Add('Trạng thái', 420)
    $labels = @{ 'prerequisites' = 'Chuẩn bị Windows'; 'reboot' = 'Khởi động lại'; 'docker-ready' = 'Docker / WSL2'; 'secrets' = 'Kiểm tra SQL và lưu bí mật'; 'firewall' = 'Tường lửa'; 'iis' = 'IIS'; 'certificates' = 'DNS, cổng 80, HTTPS'; 'migrate' = 'Migration SQL'; 'keycloak' = 'Keycloak'; 'deploy' = 'Triển khai'; 'health' = 'Kiểm tra sức khỏe'; 'first-admin' = 'Quản trị đầu tiên'; 'open-browser' = 'Lối tắt và mở Web' }
    $rows = @{}
    foreach ($phase in @(Get-SetupPhase -Update:$Update)) {
        $row = [Windows.Forms.ListViewItem]::new($labels[$phase]); $null = $row.SubItems.Add('Chờ')
        if (-not $Update -and $phase -cin $completed) { $row.SubItems[1].Text = 'Đã xong' }
        $null = $progress.Items.Add($row); $rows[$phase] = $row
    }
    $layout.Controls.Add($progress, 0, 1)
    $message = [Windows.Forms.TextBox]::new(); $message.Multiline = $true; $message.ReadOnly = $true; $message.Dock = 'Fill'; $message.ScrollBars = 'Vertical'
    $message.Text = 'Lần đầu: chuẩn bị Windows và khởi động lại trước khi nhập thông tin xác thực. Sau khi đăng nhập, wizard tự mở lại (UAC).'
    $layout.Controls.Add($message, 0, 2)
    $button = [Windows.Forms.Button]::new(); $button.Dock = 'Fill'
    $layout.Controls.Add($button, 0, 3)
    $helpLinks = [Windows.Forms.FlowLayoutPanel]::new(); $helpLinks.Dock = 'Fill'; $helpLinks.Enabled = $false
    foreach ($text in @('Bot / ERP / mời thành viên', 'Quản trị tài khoản Keycloak')) {
        $link = [Windows.Forms.LinkLabel]::new(); $link.Text = $text; $link.AutoSize = $true
        $link.Add_LinkClicked({
            param($linkLabel)
            try {
                $url = if ($linkLabel.Text -eq 'Quản trị tài khoản Keycloak') { "https://$($script:savedOptions.IdentityHost)/admin/" } else { "https://$($script:savedOptions.WebHost)/" }
                Start-Process $url
            } catch { $message.Text = 'Không mở được trình duyệt. Dùng lối tắt Mở AI Office trên Desktop.' }
        })
        $helpLinks.Controls.Add($link)
    }
    $layout.Controls.Add($helpLinks, 0, 4)
    $session = @{ Worker = $null; Handle = $null; Preparing = $false; Queue = [Collections.Concurrent.ConcurrentQueue[object]]::new(); LastPhase = ''; Failed = $false }
    $refresh = {
        $script:completed = @(Read-SetupState $statePath)
        $script:savedOptions = if (Test-Path -LiteralPath $optionsPath) { Get-Content -LiteralPath $optionsPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
        $script:configReady = Test-Path -LiteralPath (Join-Path $Root 'secrets\.env.production')
        $prepared = $script:completed.Count -ge 2
        $fields.Enabled = $prepared -and -not $Update -and -not $script:configReady
        $helpLinks.Enabled = $script:completed.Count -eq @(Get-SetupPhase).Count
        $button.Text = if ($Update) { 'Cập nhật (có migration)' } elseif (-not $prepared) { 'Chuẩn bị máy và khởi động lại' } elseif ($script:completed.Count -eq @(Get-SetupPhase).Count) { 'Đã cài xong — mở AI Office' } else { 'Cài đặt / tiếp tục' }
    }
    & $refresh
    $timer = [Windows.Forms.Timer]::new(); $timer.Interval = 150
    $timer.Add_Tick({
        $progressEvent = $null
        while ($session.Queue.TryDequeue([ref]$progressEvent)) {
            $session.LastPhase = $progressEvent.Phase
            $translated = @{ running = 'Đang chạy'; completed = 'Đã xong'; skipped = 'Đã xong (bỏ qua)'; failed = 'Lỗi — xem hướng dẫn'; paused = 'Đợi khởi động lại' }
            $rows[$progressEvent.Phase].SubItems[1].Text = $translated[$progressEvent.Status]
            $rows[$progressEvent.Phase].EnsureVisible()
            if ($progressEvent.Status -eq 'failed') { $session.Failed = $true; $message.Text = $labels[$progressEvent.Phase] + ': ' + (Get-SetupFix $progressEvent.Phase) }
        }
        if ($session.Handle -and $session.Handle.IsCompleted) {
            try { $null = $session.Worker.EndInvoke($session.Handle) }
            catch { $session.Failed = $true; $message.Text = Get-SetupFix $session.LastPhase; $Error.Clear() }
            finally { $session.Worker.Dispose(); $session.Worker = $null; $session.Handle = $null; $button.Enabled = $true; & $refresh }
            if (-not $session.Failed) {
                $message.Text = if ($session.Preparing) { 'Windows đã sẵn sàng. Nhập thông tin và tiếp tục.' } else { 'AI Office sẵn sàng. Tạo/đăng nhập tài khoản Keycloak cùng email quản trị và nhận lời mời công ty. Các mục trong ứng dụng: cài đặt bot, kết nối ERP và mời thành viên.' }
            }
        }
    })
    $button.Add_Click({
        $secrets = @{}
        try {
        if ($session.Handle) { return }
        $session.Failed = $false
        $session.Preparing = -not $Update -and $script:completed.Count -lt 2
        if ($session.Preparing -and $script:completed.Count -eq 0) {
            $answer = [Windows.Forms.MessageBox]::Show('Máy sẽ khởi động lại sau khi cài thành phần Windows. Lưu công việc trước khi tiếp tục. Chưa cần nhập mật khẩu.', 'Chuẩn bị AI Office', 'OKCancel', 'Information')
            if ($answer -ne 'OK') { return }
        }
        if (-not $Update -and $script:completed.Count -eq @(Get-SetupPhase).Count) { Start-Process "https://$($savedOptions.WebHost)/"; return }
        $options = @{}; $secrets = @{}
        if (-not $session.Preparing) {
            if ($script:configReady) { $options = $savedOptions.Clone() }
            else {
                foreach ($definition in $definitions) {
                    $box = $inputs[$definition[0]]
                    if ($box.UseSystemPasswordChar) {
                        if ($box.Text) {
                            $secureValue = [Security.SecureString]::new()
                            foreach ($character in $box.Text.ToCharArray()) { $secureValue.AppendChar($character) }
                            $secureValue.MakeReadOnly()
                            $secrets[$definition[0]] = $secureValue
                        }
                    } else { $options[$definition[0]] = $box.Text }
                }
                $options.TenantId = if ($savedOptions.TenantId) { $savedOptions.TenantId } else { [Guid]::NewGuid().ToString() }
                $options.CompanyId = if ($savedOptions.CompanyId) { $savedOptions.CompanyId } else { [Guid]::NewGuid().ToString() }
                $options.AutoSignIn = $autoSignIn.Checked
            }
            try {
                Assert-SetupOption $options
                if (-not $script:configReady) { Assert-SetupSecretInput $options $secrets; $null = Get-SetupSqlEndpoint (ConvertFrom-SetupSecureString $secrets.DatabaseConnection) }
            }
            catch { $message.Text = 'Kiểm tra tên miền, LAN, email, công ty và AI tùy chọn; không nhập ký tự điều khiển. Chuỗi SQL cần Database=AI_Office, SQL login và Encrypt=true.' + [Environment]::NewLine + 'Chi tiết: ' + $_.Exception.Message; return }
        }
        $button.Enabled = $false; $fields.Enabled = $false
        $logPath = Join-Path $Root ('logs\setup-' + [Guid]::NewGuid().ToString('N') + '.log')
        $workerCode = {
            param($Scripts, $RootPath, $Repo, $Options, $Secrets, $Completed, $State, $Log, $Queue, $Preparing, $IsUpdate)
            $ErrorActionPreference = 'Stop'
            $env:PATH = (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin') + ';' + (Join-Path $env:ProgramFiles 'Git\cmd') + ';' + (Join-Path $env:ProgramFiles 'dotnet') + ';' + $env:PATH
            . (Join-Path $Scripts 'Setup-Helpers.ps1')
            . (Join-Path $Scripts 'Setup-Actions.ps1')
            $actions = New-SetupAction $RootPath $Repo $Options $Secrets -Update:$IsUpdate
            $callback = { param($phase, $status) $Queue.Enqueue([pscustomobject]@{ Phase = $phase; Status = $status }) }
            $argsForPlan = @{ Actions = $actions; LogPath = $Log; ProgressCallback = $callback; Update = $IsUpdate }
            if (-not $IsUpdate) { $argsForPlan.Completed = [string[]]$Completed; $argsForPlan.StatePath = $State }
            if ($Preparing) { $argsForPlan.StopAfter = 'reboot' }
            Push-Location $Repo
            try { Invoke-SetupPlan @argsForPlan }
            finally {
                Pop-Location
                foreach ($value in $Secrets.Values) { if ($value -is [Security.SecureString]) { $value.Dispose() } }
                $Secrets.Clear()
            }
        }
        $session.Worker = [PowerShell]::Create()
        $null = $session.Worker.AddScript($workerCode.ToString()).AddArgument($PSScriptRoot).AddArgument($Root).AddArgument($RepositoryRoot).AddArgument($options).AddArgument($secrets).AddArgument($script:completed).AddArgument($statePath).AddArgument($logPath).AddArgument($session.Queue).AddArgument($session.Preparing).AddArgument([bool]$Update)
        $session.Handle = $session.Worker.BeginInvoke()
        foreach ($definition in $definitions) { if ($definition[2]) { $inputs[$definition[0]].Clear() } }
        } catch {
            $Error.Clear()
            $message.Text = 'Không bắt đầu được bước cài đặt. Kiểm tra thông tin nhập và thử lại; không gửi mật khẩu hoặc chuỗi kết nối trong báo lỗi.'
            $button.Enabled = $true
            if (-not $session.Handle -and $session.Worker) { $session.Worker.Dispose(); $session.Worker = $null }
            & $refresh
        } finally {
            if (-not $session.Handle) {
                foreach ($value in $secrets.Values) { if ($value -is [Security.SecureString]) { $value.Dispose() } }
            }
        }
    })
    $form.Add_FormClosing({ param($closingForm, $closingArgs) if ($session.Handle -and -not $session.Handle.IsCompleted) { $closingArgs.Cancel = $true; $message.Text = 'Đợi bước hiện tại kết thúc trước khi đóng cửa sổ.' } })
    $form.Add_Shown({ if ($script:completed.Count -eq 1 -and -not $Update) { $button.PerformClick() } })
    $timer.Start()
    $null = $form.ShowDialog()
    $timer.Stop(); $timer.Dispose(); $form.Dispose()
} catch {
    # No raw ErrorRecord, stack, configuration or native diagnostics on any stream.
    $Error.Clear()
    Write-Output 'Không mở được cài đặt. Chạy Setup-AIOffice.cmd bằng quyền quản trị, chỉ mở một cửa sổ và kiểm tra trạng thái cùng các tệp cấu hình được bảo vệ.'
    exit 1
} finally { if ($lock) { $lock.Dispose() } }
