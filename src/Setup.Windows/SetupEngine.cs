using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Setup.Core;

namespace Setup.Windows;

internal sealed class SetupEngine
{
    private readonly EmbeddedBundle bundle = new();
    private readonly CommandRunner runner = new();
    private readonly WindowsPrerequisites prerequisites = new();
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinhHuyAIoffice");
    public string RuntimeDirectory => Path.Combine(Root, "LocalRuntime");
    private string Docker => WindowsPrerequisites.Docker;
    private string? source;
    private string? environment;

    public bool AcceptedLicense => new ProgressStore(Root).Load()?.DockerLicenseAccepted == true;

    public async Task<bool> InstallAsync(bool licenseAccepted, bool repair, bool startOnly, Action<string> report)
    {
        if (!licenseAccepted) throw new SetupFailure("Cần chấp nhận điều khoản Docker trước khi cài môi trường.");
        report("Kiểm tra Windows, ảo hóa và tài nguyên…");
        var machine = await prerequisites.InspectAsync();
        PathSafety.RejectLinks(Root);
        Directory.CreateDirectory(Root);
        await ProtectDirectoryAsync();
        using var lease = new FileStream(Path.Combine(Root, ".setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var progress = new ProgressStore(Root);
        var previous = progress.Load();
        var installer = PreserveInstaller();
        progress.Save(new InstallProgress(1, bundle.Revision, InstallPhase.Inspecting, licenseAccepted));
        if (await prerequisites.NeedsInstallationAsync(machine))
        {
            ApplicationLinks.RegisterResume(installer);
            progress.Save(new InstallProgress(1, bundle.Revision, InstallPhase.AwaitingReboot, licenseAccepted));
            report("Windows sẽ hỏi quyền quản trị để chuẩn bị WSL và Docker. Tiến trình đã được lưu để tiếp tục sau reboot.");
            var result = await prerequisites.ElevateAsync(installer);
            if (result == 3010)
            {
                report("Windows cần khởi động lại. Bộ cài sẽ tự tiếp tục khi bạn đăng nhập lại.");
                return false;
            }
            if (result != 0) throw new SetupFailure("Chưa hoàn tất chuẩn bị môi trường (mã " + result + "). Chạy lại bộ cài để tiếp tục; dữ liệu được giữ nguyên.");
        }
        if (!Directory.Exists(RuntimeDirectory) && new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < 30L * 1024 * 1024 * 1024)
            throw new SetupFailure("Cần ít nhất 30 GB dung lượng trống cho Docker images và dữ liệu lần cài đầu.");
        report("Kiểm tra bundle và chuẩn bị cấu hình riêng cho máy…");
        progress.Save(new InstallProgress(1, bundle.Revision, InstallPhase.PreparingRuntime, licenseAccepted));
        source = bundle.Extract(Path.Combine(Root, "Versions"), repair);
        var initialize = await runner.RunAsync(WindowsPrerequisites.PowerShell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(source, "infra", "initialize-local-config.ps1"),
                "-DataDirectory", RuntimeDirectory], TimeSpan.FromMinutes(1));
        if (initialize.ExitCode != 0) throw new SetupFailure("Không tạo được cấu hình bảo mật. Cấu hình hiện có được giữ nguyên.");
        environment = Path.Combine(RuntimeDirectory, "local.env");
        if (!File.Exists(environment)) throw new SetupFailure("Cấu hình dịch vụ chưa sẵn sàng.");
        report("Khởi động Docker Desktop…");
        await EnsureDockerAsync();
        progress.Save(new InstallProgress(1, bundle.Revision, InstallPhase.StartingRuntime, licenseAccepted));
        report("Tải tài nguyên, chạy FE/BE và áp dụng migration. Lần đầu có thể mất nhiều phút…");
        var reuse = startOnly && previous?.Phase == InstallPhase.Ready && previous.Revision == bundle.Revision;
        var started = await ComposeAsync(reuse ? ["up", "-d"] : ["up", "--build", "-d"], TimeSpan.FromMinutes(45));
        if (started.ExitCode != 0) throw new SetupFailure("Chưa khởi động được dịch vụ. Kiểm tra kết nối mạng, dung lượng và chạy Sửa chữa để tiếp tục.");
        report("Chờ các dịch vụ, API và giao diện sẵn sàng…");
        await WaitForRuntimeAsync();
        ApplicationLinks.RegisterApplication(installer);
        progress.Save(new InstallProgress(1, bundle.Revision, InstallPhase.Ready, licenseAccepted));
        report("Ứng dụng đã sẵn sàng. Bạn có thể mở từ Start Menu; FE/BE sẽ tự chạy khi đăng nhập Windows.");
        return true;
    }

    public string InitialOwnerPassword()
    {
        var path = Path.Combine(RuntimeDirectory, "installation.json");
        PathSafety.RejectLinks(path);
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        return manifest.RootElement.GetProperty("AIOFFICE_OWNER_PASSWORD").GetString()!;
    }

    private async Task ProtectDirectoryAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var result = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            [Root, "/inheritance:r", "/grant:r", "*" + sid + ":(OI)(CI)F"], TimeSpan.FromMinutes(1));
        if (result.ExitCode != 0) throw new SetupFailure("Không thiết lập được quyền riêng tư cho thư mục cài đặt.");
        var security = new DirectoryInfo(Root).GetAccessControl();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference.Value == sid) continue;
            var remove = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                [Root, "/remove", "*" + rule.IdentityReference.Value], TimeSpan.FromMinutes(1));
            if (remove.ExitCode != 0) throw new SetupFailure("Không thu hồi được quyền truy cập thư mục cài đặt.");
        }
        security = new DirectoryInfo(Root).GetAccessControl();
        if (!security.AreAccessRulesProtected || security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Value != sid))
            throw new SetupFailure("Thư mục cài đặt chưa được bảo vệ.");
    }

    private string PreserveInstaller()
    {
        var current = Environment.ProcessPath ?? throw new SetupFailure("Không tìm được bộ cài hiện tại.");
        var directory = Path.Combine(Root, "Installers", bundle.Revision);
        PathSafety.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "Setup.exe");
        if (File.Exists(destination))
        {
            PathSafety.RejectLinks(destination);
            using var existing = File.OpenRead(destination);
            using var original = File.OpenRead(current);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(original)))
                throw new SetupFailure("Bộ cài lưu trên máy đã thay đổi. Cần kiểm tra tính toàn vẹn trước khi tiếp tục.");
        }
        else
        {
            var temporary = Path.Combine(directory, ".setup-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(current, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: false);
        }
        return destination;
    }

    private async Task EnsureDockerAsync()
    {
        Process.Start(new ProcessStartInfo(WindowsPrerequisites.DockerDesktop)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        })?.Dispose();
        var deadline = DateTime.UtcNow.AddMinutes(8);
        do
        {
            var endpoint = await runner.RunAsync(Docker, ["context", "inspect", "desktop-linux", "--format", "{{.Endpoints.docker.Host}}"], TimeSpan.FromSeconds(20));
            if (endpoint.ExitCode == 0)
            {
                if (!endpoint.Output.Trim().Equals("npipe:////./pipe/dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase))
                    throw new SetupFailure("Docker context không trỏ tới engine Linux cục bộ được hỗ trợ.");
                var engine = await runner.RunAsync(Docker, ["--context", "desktop-linux", "info", "--format", "{{.OSType}}"], TimeSpan.FromSeconds(20));
                if (engine.ExitCode == 0 && engine.Output.Trim() == "linux") return;
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        } while (DateTime.UtcNow < deadline);
        throw new SetupFailure("Docker Linux engine chưa sẵn sàng. Có thể cần reboot Windows rồi chạy lại ứng dụng.");
    }

    private Task<CommandResult> ComposeAsync(IEnumerable<string> arguments, TimeSpan timeout)
    {
        return runner.RunAsync(Docker, new[] { "--context", "desktop-linux", "compose", "--project-directory", source!,
            "--env-file", environment!, "-f", Path.Combine(source!, "compose.local.yaml") }.Concat(arguments), timeout);
    }

    private async Task WaitForRuntimeAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddMinutes(8);
        do
        {
            try
            {
                var services = await ComposeAsync(["ps", "--all", "--format", "json"], TimeSpan.FromSeconds(20));
                if (services.ExitCode == 0 && RuntimeReadiness.IsReady(services.Output))
                {
                    using var api = await client.GetAsync("http://127.0.0.1:8080/health");
                    using var web = await client.GetAsync("http://127.0.0.1:3000/");
                    if (api.IsSuccessStatusCode && web.IsSuccessStatusCode) return;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(TimeSpan.FromSeconds(3));
        } while (DateTime.UtcNow < deadline);
        throw new SetupFailure("Các dịch vụ chưa sẵn sàng. Dữ liệu và mật khẩu được giữ nguyên để Sửa chữa tiếp tục.");
    }
}
