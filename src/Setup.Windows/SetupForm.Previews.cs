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
        var scenarios = new[] { "default", "minimum", "expanded", "minimum-again", "large-font-minimum" };
        var verified = new List<object>();
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
            var expanded = new Size(Math.Max(form.Width, form.MinimumSize.Width + 300),
                Math.Max(form.Height, form.MinimumSize.Height + 240));
            foreach (var scenario in scenarios)
            {
                if (scenario is "minimum" or "minimum-again" or "large-font-minimum") form.Size = form.MinimumSize;
                if (scenario == "expanded") form.Size = expanded;
                if (scenario == "large-font-minimum") form.Font = new Font("Segoe UI", 14);
                form.ActiveControl = form.license;
                form.layout.AutoScrollPosition = Point.Empty;
                SettlePreview(form);
                VerifyPreviewLayout(form);
                SavePreview(form, directory, state + (scenario == "default" ? "" : "-" + scenario));
                if (state == "ready")
                {
                    form.ActiveControl = form.reveal;
                    form.layout.ScrollControlIntoView(form.reveal);
                    SettlePreview(form);
                    var passwordBounds = form.layout.RectangleToClient(form.password.RectangleToScreen(form.password.ClientRectangle));
                    var revealBounds = form.layout.RectangleToClient(form.reveal.RectangleToScreen(form.reveal.ClientRectangle));
                    if (!form.password.UseSystemPasswordChar || !form.layout.ClientRectangle.Contains(passwordBounds) ||
                        !form.layout.ClientRectangle.Contains(revealBounds))
                        throw new VerificationFailure("preview-onboarding-privacy-layout");
                    VerifyPreviewLayout(form);
                    SavePreview(form, directory, "ready-onboarding" + (scenario == "default" ? "" : "-" + scenario));
                }
                verified.Add(new { state, scenario, dpi = form.DeviceDpi, width = form.Width, height = form.Height });
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
            states = new[] { "initial", "failed", "reboot", "ready" },
            scenarios,
            checks = new[] { "no-horizontal-scroll", "complete-text-height", "buttons-fit", "resize-cycle", "masked-password-reachable" },
            verified
        }));
    }

    private static void SettlePreview(SetupForm form)
    {
        for (var round = 0; round < 2; round++)
        {
            Application.DoEvents();
            form.PerformLayout();
            form.content.PerformLayout();
        }
        RequireOffscreen(form);
    }

    private static void VerifyPreviewLayout(SetupForm form)
    {
        if (form.layout.HorizontalScroll.Visible)
            throw new VerificationFailure("preview-horizontal-clipping");
        foreach (Control control in form.content.Controls)
        {
            if (!control.Visible) continue;
            if (control.Right + control.Margin.Right > form.content.ClientSize.Width)
                throw new VerificationFailure("preview-control-width");
            if (control is Label or CheckBox)
            {
                // Independently measure the rendered text at its actual width.
                // MaximumSize/GetPreferredSize alone miss clipped checkboxes.
                var textWidth = control.ClientSize.Width;
                if (control is CheckBox)
                {
                    using var graphics = control.CreateGraphics();
                    textWidth -= CheckBoxRenderer.GetGlyphSize(graphics,
                        System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width + (int)Math.Ceiling(6d * form.DeviceDpi / 96);
                }
                var measured = TextRenderer.MeasureText(control.Text, control.Font,
                    new Size(Math.Max(1, textWidth), int.MaxValue), TextFormatFlags.WordBreak);
                if (control.ClientSize.Height < measured.Height)
                    throw new VerificationFailure("preview-incomplete-text-height");
            }
            if (control is FlowLayoutPanel buttons)
            {
                foreach (Control button in buttons.Controls)
                {
                    if (button.Visible && (button.Right + button.Margin.Right > buttons.ClientSize.Width ||
                        button.Bottom + button.Margin.Bottom > buttons.ClientSize.Height ||
                        button.Size.Width < button.GetPreferredSize(Size.Empty).Width ||
                        button.Size.Height < button.GetPreferredSize(Size.Empty).Height))
                        throw new VerificationFailure("preview-button-clipping");
                }
            }
        }
    }

    private static void SavePreview(SetupForm form, string directory, string name)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var path = Path.Combine(directory, name + ".png");
        PathSafety.RejectLinks(path);
        bitmap.Save(path, ImageFormat.Png);
    }
}
