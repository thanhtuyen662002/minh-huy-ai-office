using System.Diagnostics;
using Setup.Core;

namespace Setup.Windows;

internal sealed partial class SetupForm : Form
{
    private readonly SetupEngine engine;
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(610, 0) };
    private readonly CheckBox license = new() { Text = "Tôi chấp nhận điều khoản Docker cho việc cài đặt và sử dụng.", AutoSize = true };
    private readonly Button install = new() { Text = "Cài đặt / Sửa chữa", Width = 185, Height = 38 };
    private readonly Button open = new() { Text = "Mở ứng dụng", Width = 150, Height = 38, Enabled = false };
    private readonly Button restart = new() { Text = "Khởi động lại Windows", Width = 190, Height = 38, Visible = false };
    private readonly TextBox password = new() { ReadOnly = true, UseSystemPasswordChar = true, Width = 400, Visible = false };
    private readonly CheckBox reveal = new() { Text = "Hiện mật khẩu ban đầu", AutoSize = true, Visible = false };
    private readonly Button export = new() { Text = "Xuất chẩn đoán", Width = 155, Height = 34 };
    private readonly Label owner = new()
    {
        Text = "Tài khoản ban đầu: owner. Trong ứng dụng, chọn Đăng nhập rồi nhập tài khoản và mật khẩu này trên trang xác thực.",
        AutoSize = true,
        MaximumSize = new Size(610, 0),
        Margin = new Padding(0, 18, 0, 4),
        Visible = false
    };
    private readonly bool preview;
    private bool skipInitialInspection;
    private bool busy;
    private readonly bool background;
    private readonly bool startOnly;
    private readonly FlowLayoutPanel layout;

    public SetupForm(string[] arguments, bool preview = false, SetupEngine? setupEngine = null)
    {
        this.preview = preview;
        engine = setupEngine ?? new SetupEngine();
        Text = "MinhHuy AI Office";
        ClientSize = new Size(700, 530);
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 570);
        background = arguments.Contains("--background", StringComparer.Ordinal);
        startOnly = arguments.Contains("--start", StringComparer.Ordinal);
        layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(24)
        };
        if (preview) layout.Controls.Add(new Label { Text = "MÔ PHỎNG GIAO DIỆN — KHÔNG PHẢI BẰNG CHỨNG CÀI ĐẶT", AutoSize = true, ForeColor = Color.DarkRed });
        layout.Controls.Add(new Label { Text = "Cài đặt và chạy MinhHuy AI Office", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "Bộ cài chuẩn bị Docker/WSL, dịch vụ và ứng dụng.\nCấu hình và dữ liệu được giữ nguyên khi chạy lại hoặc sửa chữa.", AutoSize = true, Margin = new Padding(0, 12, 0, 12) });
        var terms = new LinkLabel { Text = "Xem điều khoản Docker", AutoSize = true };
        terms.LinkClicked += (_, _) => { if (!preview) OpenUrl("https://www.docker.com/legal/docker-subscription-service-agreement/"); };
        layout.Controls.Add(terms);
        layout.Controls.Add(license);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 12, 0, 12)
        };
        foreach (var button in new[] { install, open, restart, export }) button.AutoSize = true;
        buttons.Controls.AddRange([install, open, restart]);
        layout.Controls.Add(buttons);
        layout.Controls.Add(status);
        layout.Controls.Add(export);
        layout.Controls.Add(new Label { Text = "Báo cáo chỉ chứa trạng thái máy và dịch vụ; không chứa mật khẩu hoặc nội dung cấu hình.", MaximumSize = new Size(610, 0), AutoSize = true });
        layout.Controls.Add(owner);
        layout.Controls.Add(password);
        layout.Controls.Add(reveal);
        Controls.Add(layout);
        layout.SizeChanged += (_, _) => FitLayoutWidth();
        Shown += (_, _) => FitLayoutWidth();
        FitLayoutWidth();
        install.Click += async (_, _) => await RunAsync();
        open.Click += (_, _) => { if (!preview) OpenUrl("http://127.0.0.1:3000/"); };
        export.Click += (_, _) => ExportDiagnostic();
        reveal.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !reveal.Checked;
        restart.Click += (_, _) =>
        {
            if (preview) return;
            if (MessageBox.Show(this, "Lưu công việc đang mở trước khi Windows khởi động lại. Tiếp tục?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 15") { UseShellExecute = false, CreateNoWindow = true });
        };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        Shown += async (_, _) =>
        {
            try
            {
                if (preview || skipInitialInspection) return;
                license.Checked = engine.AcceptedLicense;
                if (startOnly || arguments.Contains("--resume", StringComparer.Ordinal))
                {
                    if (background) Hide();
                    if (license.Checked) await RunAsync();
                    else Show();
                }
            }
            catch (Exception error)
            {
                this.engine.RecordFailure(error is CorruptProgressException or UnsupportedProgressException
                    ? SetupFailureCode.InvalidProgress : SetupFailureCode.Configuration);
                Show();
                status.Text = "Trạng thái cài đặt không hợp lệ. Dữ liệu hiện có được giữ nguyên; cần kiểm tra trạng thái trước khi tiếp tục.";
            }
        };
    }

    private void FitLayoutWidth()
    {
        // FlowLayoutPanel does not automatically wrap a top-down child's text.
        // Reserve the vertical scrollbar even before it appears, so changes in
        // status/owner text cannot cause a horizontal scrollbar at higher DPI.
        var available = Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal
            - SystemInformation.VerticalScrollBarWidth - 8);
        foreach (Control control in layout.Controls)
        {
            var width = Math.Max(1, available - control.Margin.Horizontal);
            if (control is Label or CheckBox or FlowLayoutPanel)
                control.MaximumSize = new Size(width, 0);
            else if (control.Width > width) control.Width = width;
        }
    }

    private async Task RunAsync()
    {
        if (busy || preview) return;
        busy = true;
        install.Enabled = false;
        open.Enabled = false;
        export.Enabled = false;
        owner.Visible = false;
        reveal.Checked = false;
        license.Enabled = false;
        restart.Visible = false;
        password.Clear();
        password.Visible = false;
        reveal.Visible = false;
        try
        {
            var ready = await engine.InstallAsync(license.Checked, repair: true, startOnly, message => status.Text = message);
            open.Enabled = ready;
            restart.Visible = !ready;
            if (ready)
            {
                if (background) { busy = false; Close(); return; }
                password.Text = engine.InitialOwnerPassword();
                owner.Visible = true;
                password.Visible = true;
                reveal.Visible = true;
                OpenUrl("http://127.0.0.1:3000/");
            }
            else Show();
        }
        catch (SetupFailure failure) { Show(); status.Text = failure.Message; }
        catch (System.ComponentModel.Win32Exception failure) when (failure.NativeErrorCode == 1223)
        {
            Show();
            status.Text = "Bạn chưa cấp quyền quản trị. Chạy lại bộ cài khi muốn tiếp tục; dữ liệu đã được giữ nguyên.";
        }
        catch
        {
            Show();
            status.Text = "Bước cài đặt chưa hoàn tất. Trạng thái và dữ liệu đã được giữ lại. Có thể chạy lại để tiếp tục.";
        }
        finally { busy = false; export.Enabled = true; install.Enabled = true; license.Enabled = true; }
    }

    private void ExportDiagnosticToFile(string path) => DiagnosticStore.Export(path, engine.Diagnostic);

    private void ExportDiagnostic()
    {
        if (busy || preview) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Xuất báo cáo chẩn đoán",
            Filter = "Báo cáo JSON (*.json)|*.json",
            FileName = "MinhHuy-AI-Office-diagnostic.json",
            DefaultExt = "json",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ExportDiagnosticToFile(dialog.FileName);
            MessageBox.Show(this, "Đã xuất báo cáo chẩn đoán. Báo cáo không chứa mật khẩu hoặc nội dung cấu hình.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, "Không ghi được báo cáo. Hãy chọn thư mục mà bạn có quyền ghi.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
