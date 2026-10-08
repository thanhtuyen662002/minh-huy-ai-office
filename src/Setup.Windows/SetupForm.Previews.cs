using System.Drawing.Imaging;
using System.Text.Json;
using Setup.Core;

namespace Setup.Windows;

internal sealed partial class SetupForm
{
    public static void VerifyFailureControls(string path)
    {
        PathSafety.RejectLinks(path);
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "isolated-form-state");
        PathSafety.RejectLinks(root);
        Directory.CreateDirectory(root);
        var progress = Path.Combine(root, "setup-progress.json");
        PathSafety.RejectLinks(progress);
        File.WriteAllText(progress, "synthetic-invalid-progress");
        var before = File.ReadAllBytes(progress);
        using (var form = new SetupForm([], setupEngine: new SetupEngine(root)) { skipInitialInspection = true })
        {
            PrepareOffscreen(form);
            form.license.Checked = false;
            form.RunAsync().GetAwaiter().GetResult();
            Application.DoEvents();
            RequireOffscreen(form);
            if (!form.export.Enabled || !form.install.Enabled || !form.license.Enabled || form.open.Enabled ||
                form.busy || form.engine.Diagnostic.Failure != SetupFailureCode.MissingLicense)
                throw new VerificationFailure("failure-form-export-enabled");
            form.ExportDiagnosticToFile(path);
            VerifyExportedFailure(path, SetupFailureCode.MissingLicense);
            if (!before.SequenceEqual(File.ReadAllBytes(progress)))
                throw new VerificationFailure("failure-form-state-changed");
            form.Hide();
        }
        VerifyInitialProgressFailure(root, progress, "synthetic-invalid-progress", "corrupt");
        VerifyInitialProgressFailure(root, progress, "{\"SchemaVersion\":4}", "unsupported");
    }

    private static void VerifyInitialProgressFailure(string root, string progress, string fixture, string name)
    {
        File.WriteAllText(progress, fixture);
        var before = File.ReadAllBytes(progress);
        using var form = new SetupForm([], setupEngine: new SetupEngine(root));
        PrepareOffscreen(form);
        form.Show();
        Application.DoEvents();
        RequireOffscreen(form);
        if (form.engine.Diagnostic.Failure != SetupFailureCode.InvalidProgress ||
            !form.export.Enabled || form.open.Enabled || form.busy || form.engine.Diagnostic.Machine is not null)
            throw new VerificationFailure(name + "-progress-classification");
        var exported = Path.Combine(root, name + "-diagnostic.json");
        form.ExportDiagnosticToFile(exported);
        VerifyExportedFailure(exported, SetupFailureCode.InvalidProgress);
        if (!before.SequenceEqual(File.ReadAllBytes(progress)))
            throw new VerificationFailure(name + "-progress-fixture-changed");
        form.Hide();
    }

    private static void VerifyExportedFailure(string path, SetupFailureCode expected)
    {
        using var exported = JsonDocument.Parse(File.ReadAllText(path));
        if (!Enum.TryParse<SetupFailureCode>(exported.RootElement.GetProperty(nameof(SetupDiagnostic.Failure)).GetString(),
                ignoreCase: true, out var failure) || failure != expected ||
            exported.RootElement.GetProperty(nameof(SetupDiagnostic.Phase)).GetString() != nameof(InstallPhase.Inspecting))
            throw new VerificationFailure("failure-form-typed-report");
    }

    private static void PrepareOffscreen(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Opacity = 0;
        var screen = SystemInformation.VirtualScreen;
        form.Location = new Point(screen.Left - 4 * form.Width - 1000, screen.Top - 4 * form.Height - 1000);
    }

    private static void RequireOffscreen(Form form)
    {
        if (form.Opacity != 0 || form.ShowInTaskbar || form.Bounds.IntersectsWith(SystemInformation.VirtualScreen))
            throw new VerificationFailure("failure-form-not-offscreen");
    }

    public static void RenderPreviews(string directory)
    {
        PathSafety.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        foreach (var state in new[] { "initial", "failed", "reboot", "ready" })
        {
            using var form = new SetupForm([], preview: true);
            form.Text += " — MÔ PHỎNG";
            PrepareOffscreen(form);
            form.license.Checked = state != "initial";
            form.status.Text = state switch
            {
                "initial" => "Chấp nhận điều khoản rồi chọn Cài đặt / Sửa chữa để bắt đầu.",
                "failed" => "Các dịch vụ chưa sẵn sàng. Dữ liệu và mật khẩu được giữ nguyên để Sửa chữa tiếp tục. Có thể xuất báo cáo chẩn đoán.",
                "reboot" => "Windows cần khởi động lại. Bộ cài sẽ tự tiếp tục khi bạn đăng nhập lại.",
                _ => "Ứng dụng đã sẵn sàng. Bạn có thể mở từ Start Menu; FE/BE sẽ tự chạy khi đăng nhập Windows."
            };
            form.restart.Visible = state == "reboot";
            form.open.Enabled = state == "ready";
            if (state == "ready")
            {
                form.owner.Visible = true;
                form.password.Text = "synthetic-preview-only";
                form.password.Visible = true;
                form.reveal.Visible = true;
            }
            form.Show();
            Application.DoEvents();
            RequireOffscreen(form);
            form.PerformLayout();
            if (form.layout.HorizontalScroll.Visible)
                throw new VerificationFailure("preview-horizontal-clipping");
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var output = Path.Combine(directory, state + ".png");
            PathSafety.RejectLinks(output);
            bitmap.Save(output, ImageFormat.Png);
            if (state == "ready")
            {
                form.layout.ScrollControlIntoView(form.password);
                Application.DoEvents();
                if (form.layout.HorizontalScroll.Visible || !form.password.UseSystemPasswordChar)
                    throw new VerificationFailure("preview-onboarding-privacy-layout");
                using var onboarding = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(onboarding, new Rectangle(Point.Empty, onboarding.Size));
                var onboardingPath = Path.Combine(directory, "ready-onboarding.png");
                PathSafety.RejectLinks(onboardingPath);
                onboarding.Save(onboardingPath, ImageFormat.Png);
            }
            form.Hide();
        }
        PathSafety.RejectLinks(Path.Combine(directory, "preview-proof.json"));
        File.WriteAllText(Path.Combine(directory, "preview-proof.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            simulated = true,
            offscreen = true,
            installationVerified = false,
            privilegedOperations = false,
            states = new[] { "initial", "failed", "reboot", "ready" }
        }));
    }
}
