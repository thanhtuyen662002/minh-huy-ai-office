using System.Diagnostics;

namespace Setup.Windows;

internal sealed class SetupForm : Form
{
    private readonly SetupEngine engine = new();
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(610, 0) };
    private readonly CheckBox license = new() { Text = "Tôi chấp nhận điều khoản Docker cho việc cài đặt và sử dụng.", AutoSize = true };
    private readonly Button install = new() { Text = "Cài đặt / Sửa chữa", Width = 185, Height = 38 };
    private readonly Button open = new() { Text = "Mở ứng dụng", Width = 150, Height = 38, Enabled = false };
    private readonly Button restart = new() { Text = "Khởi động lại Windows", Width = 190, Height = 38, Visible = false };
    private readonly TextBox password = new() { ReadOnly = true, UseSystemPasswordChar = true, Width = 400, Visible = false };
    private readonly CheckBox reveal = new() { Text = "Hiện mật khẩu ban đầu", AutoSize = true, Visible = false };
    private bool busy;
    private readonly bool background;
    private readonly bool startOnly;

    public SetupForm(string[] arguments)
    {
        Text = "MinhHuy AI Office";
        ClientSize = new Size(700, 450);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 490);
        background = arguments.Contains("--background", StringComparer.Ordinal);
        startOnly = arguments.Contains("--start", StringComparer.Ordinal);
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(24)
        };
        layout.Controls.Add(new Label { Text = "Cài đặt và chạy MinhHuy AI Office", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "Bộ cài chuẩn bị Docker/WSL, dịch vụ và ứng dụng.\nCấu hình và dữ liệu được giữ nguyên khi chạy lại hoặc sửa chữa.", AutoSize = true, Margin = new Padding(0, 12, 0, 12) });
        var terms = new LinkLabel { Text = "Xem điều khoản Docker", AutoSize = true };
        terms.LinkClicked += (_, _) => OpenUrl("https://www.docker.com/legal/docker-subscription-service-agreement/");
        layout.Controls.Add(terms);
        layout.Controls.Add(license);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 12) };
        buttons.Controls.AddRange([install, open, restart]);
        layout.Controls.Add(buttons);
        layout.Controls.Add(status);
        layout.Controls.Add(new Label { Text = "Tài khoản ban đầu: owner", AutoSize = true, Margin = new Padding(0, 18, 0, 4) });
        layout.Controls.Add(password);
        layout.Controls.Add(reveal);
        Controls.Add(layout);
        install.Click += async (_, _) => await RunAsync();
        open.Click += (_, _) => OpenUrl("http://127.0.0.1:3000/");
        reveal.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !reveal.Checked;
        restart.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Lưu công việc đang mở trước khi Windows khởi động lại. Tiếp tục?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 15") { UseShellExecute = false, CreateNoWindow = true });
        };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        Shown += async (_, _) =>
        {
            try
            {
                license.Checked = engine.AcceptedLicense;
                if (startOnly || arguments.Contains("--resume", StringComparer.Ordinal))
                {
                    if (background) Hide();
                    if (license.Checked) await RunAsync();
                    else Show();
                }
            }
            catch
            {
                Show();
                status.Text = "Trạng thái cài đặt không hợp lệ. Dữ liệu hiện có được giữ nguyên; cần kiểm tra trạng thái trước khi tiếp tục.";
            }
        };
    }

    private async Task RunAsync()
    {
        if (busy) return;
        busy = true;
        install.Enabled = false;
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
        finally { busy = false; install.Enabled = true; license.Enabled = true; }
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
