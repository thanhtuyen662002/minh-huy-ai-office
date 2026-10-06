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
    public string Root { get; }
    public string RuntimeDirectory => Path.Combine(Root, "LocalRuntime");
    private string Docker => WindowsPrerequisites.Docker;
    private string? source;
    private string? environment;
    private bool diagnosticEnabled;
    private SetupFailureCode failureContext = SetupFailureCode.Unexpected;
    public SetupDiagnostic Diagnostic { get; private set; }

    public SetupEngine(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinhHuyAIoffice"));
        Diagnostic = new SetupDiagnostic(1, bundle.Revision, InstallPhase.Inspecting, SetupFailureCode.None,
            DateTimeOffset.UtcNow, null, []);
    }

    public void RecordFailure(SetupFailureCode code)
    {
        Diagnostic = Diagnostic with { Failure = code, UpdatedAt = DateTimeOffset.UtcNow };
        if (diagnosticEnabled) new DiagnosticStore(Root).Save(Diagnostic);
    }

    private void RecordStage(InstallPhase phase, SetupFailureCode context)
    {
        failureContext = context;
        Diagnostic = Diagnostic with { Phase = phase, Failure = SetupFailureCode.None, UpdatedAt = DateTimeOffset.UtcNow };
        if (diagnosticEnabled) new DiagnosticStore(Root).Save(Diagnostic);
    }

    public bool AcceptedLicense => new ProgressStore(Root).Load()?.DockerLicenseAccepted == true;

    public async Task<bool> InstallAsync(bool licenseAccepted, bool repair, bool startOnly, Action<string> report)
    {
        Diagnostic = Diagnostic with { Machine = null, Services = [] };
        source = null;
        environment = null;
        RecordStage(InstallPhase.Inspecting, SetupFailureCode.MissingLicense);
        try { return await InstallCoreAsync(licenseAccepted, repair, startOnly, report); }
        catch (Exception error)
        {
            if (Diagnostic.Failure == SetupFailureCode.None)
            {
                var code = error is CorruptProgressException or UnsupportedProgressException ? SetupFailureCode.InvalidProgress :
                    error is SetupFailure { Code: not SetupFailureCode.Unexpected } typed ? typed.Code : failureContext;
                RecordFailure(code);
            }
            throw;
        }
    }

    private async Task<bool> InstallCoreAsync(bool licenseAccepted, bool repair, bool startOnly, Action<string> report)
    {
        if (!licenseAccepted) throw new SetupFailure("Cần chấp nhận điều khoản Docker trước khi cài môi trường.");
        report("Kiểm tra Windows, ảo hóa và tài nguyên…");
        RecordStage(InstallPhase.Inspecting, SetupFailureCode.UnsupportedMachine);
        var machine = await prerequisites.InspectAsync(value => Diagnostic = Diagnostic with
        {
            Machine = new MachineDiagnostic(value.Build, value.Virtualization, value.Hypervisor, value.Memory, 0)
        });
        RecordStage(InstallPhase.Inspecting, SetupFailureCode.InsufficientDisk);
        Diagnostic = Diagnostic with
        {
            Machine = new MachineDiagnostic(machine.Build, machine.Virtualization, machine.Hypervisor, machine.Memory,
                new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace)
        };
        if (!Directory.Exists(RuntimeDirectory) && Diagnostic.Machine!.FreeDiskBytes < 30L * 1024 * 1024 * 1024)
            throw new SetupFailure("Cần ít nhất 30 GB dung lượng trống cho Docker images và dữ liệu lần cài đầu.");
        RecordStage(InstallPhase.Inspecting, SetupFailureCode.Configuration);
        PathSafety.RejectLinks(Root);
        Directory.CreateDirectory(Root);
        await ProtectDirectoryAsync();
        RecordStage(InstallPhase.Inspecting, SetupFailureCode.AlreadyRunning);
        var leasePath = Path.Combine(Root, ".setup.lock");
        PathSafety.RejectLinks(leasePath);
        using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var progress = new ProgressStore(Root);
        diagnosticEnabled = true;
        var progressWritten = false;
        var resumeRegistered = false;
        string? installer = null;
        try
        {
            RecordStage(InstallPhase.Inspecting, SetupFailureCode.InvalidProgress);
            var previous = progress.LoadOrRepair(repair);
            RecordStage(InstallPhase.Inspecting, SetupFailureCode.BundleIntegrity);
            installer = PreserveInstaller(repair);
            progress.Save(new InstallProgress(ProgressStore.CurrentSchemaVersion, bundle.Revision, InstallPhase.Inspecting, licenseAccepted));
            progressWritten = true;
            RecordStage(InstallPhase.Inspecting, SetupFailureCode.PrerequisitePreparation);
            if (await prerequisites.NeedsInstallationAsync(machine))
            {
                RecordStage(InstallPhase.Inspecting, SetupFailureCode.ShortcutConflict);
                ApplicationLinks.RegisterResume(installer);
                resumeRegistered = true;
                progress.Save(new InstallProgress(ProgressStore.CurrentSchemaVersion, bundle.Revision, InstallPhase.AwaitingReboot, licenseAccepted));
                report("Windows sẽ hỏi quyền quản trị để chuẩn bị WSL và Docker. Tiến trình đã được lưu để tiếp tục sau reboot.");
                RecordStage(InstallPhase.Inspecting, SetupFailureCode.PrerequisitePreparation);
                var result = await prerequisites.ElevateAsync(installer);
                if (result == 1223) throw new System.ComponentModel.Win32Exception(1223);
                if (result == 3010)
                {
                    RecordStage(InstallPhase.AwaitingReboot, SetupFailureCode.None);
                    report("Windows cần khởi động lại. Bộ cài sẽ tự tiếp tục khi bạn đăng nhập lại.");
                    return false;
                }
                if (result != 0) throw new SetupFailure("Chưa hoàn tất chuẩn bị môi trường (mã " + result + "). Chạy lại bộ cài để tiếp tục; dữ liệu được giữ nguyên.");
            }
            report("Kiểm tra bundle và chuẩn bị cấu hình riêng cho máy…");
            progress.Save(new InstallProgress(ProgressStore.CurrentSchemaVersion, bundle.Revision, InstallPhase.PreparingRuntime, licenseAccepted));
            RecordStage(InstallPhase.PreparingRuntime, SetupFailureCode.BundleIntegrity);
            source = bundle.Extract(Path.Combine(Root, "Versions"), repair);
            RecordStage(InstallPhase.PreparingRuntime, SetupFailureCode.Configuration);
            var initialize = await runner.RunAsync(WindowsPrerequisites.PowerShell,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(source, "infra", "initialize-local-config.ps1"),
                "-DataDirectory", RuntimeDirectory], TimeSpan.FromMinutes(1));
            if (initialize.ExitCode != 0) throw new SetupFailure("Không tạo được cấu hình bảo mật. Cấu hình hiện có được giữ nguyên.");
            environment = Path.Combine(RuntimeDirectory, "local.env");
            if (!File.Exists(environment)) throw new SetupFailure("Cấu hình dịch vụ chưa sẵn sàng.");
            report("Khởi động Docker Desktop…");
            RecordStage(InstallPhase.StartingRuntime, SetupFailureCode.DockerUnavailable);
            await EnsureDockerAsync();
            progress.Save(new InstallProgress(ProgressStore.CurrentSchemaVersion, bundle.Revision, InstallPhase.StartingRuntime, licenseAccepted));
            report("Tải tài nguyên, chạy FE/BE và áp dụng migration. Lần đầu có thể mất nhiều phút…");
            var plan = RuntimeStartPlan.Create(previous, bundle.Revision, startOnly);
            if (plan.Drain)
            {
                // Select the secured helper before migration; old startup links
                // must never restart pre-guard services after an interrupted upgrade.
                RecordStage(InstallPhase.StartingRuntime, SetupFailureCode.ShortcutConflict);
                ApplicationLinks.RegisterApplication(installer);
                RecordStage(InstallPhase.StartingRuntime, SetupFailureCode.RuntimeStart);
                report("Dừng ứng dụng cũ trước khi nâng cấp bảo mật…");
                var drained = await ComposeAsync(["stop", "web", "core-api", "agent-worker"], TimeSpan.FromMinutes(2));
                if (drained.ExitCode != 0)
                    throw new SetupFailure("Chưa dừng được ứng dụng cũ; migration chưa được khởi động.", SetupFailureCode.RuntimeStart);
            }
            RecordStage(InstallPhase.StartingRuntime, SetupFailureCode.RuntimeStart);
            var started = await ComposeAsync(plan.Rebuild ? ["up", "--build", "-d"] : ["up", "-d"], TimeSpan.FromMinutes(45));
            if (started.ExitCode != 0) throw new SetupFailure("Chưa khởi động được dịch vụ. Kiểm tra kết nối mạng, dung lượng và chạy Sửa chữa để tiếp tục.");
            report("Chờ các dịch vụ, API và giao diện sẵn sàng…");
            RecordStage(InstallPhase.StartingRuntime, SetupFailureCode.RuntimeReadiness);
            await WaitForRuntimeAsync();
            RecordStage(InstallPhase.StartingRuntime, SetupFailureCode.ShortcutConflict);
            ApplicationLinks.RegisterApplication(installer);
            progress.Save(new InstallProgress(ProgressStore.CurrentSchemaVersion, bundle.Revision, InstallPhase.Ready, licenseAccepted));
            RecordStage(InstallPhase.Ready, SetupFailureCode.None);
            report("Ứng dụng đã sẵn sàng. Bạn có thể mở từ Start Menu; FE/BE sẽ tự chạy khi đăng nhập Windows.");
            return true;
        }
        catch (Exception error)
        {
            var cancelled = error is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 };
            var code = cancelled ? SetupFailureCode.ElevationCancelled :
                error is CorruptProgressException or UnsupportedProgressException ? SetupFailureCode.InvalidProgress :
                error is SetupFailure { Code: not SetupFailureCode.Unexpected } typed ? typed.Code : failureContext;
            if (cancelled && resumeRegistered && installer is not null)
            {
                try { ApplicationLinks.RemoveResume(installer); }
                catch (Exception removalError) when (removalError is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            }
            if (code is SetupFailureCode.RuntimeStart or SetupFailureCode.RuntimeReadiness)
                await CaptureRuntimeDiagnosticAsync();
            try
            {
                RecordFailure(code);
                if (progressWritten) progress.Save(new InstallProgress(ProgressStore.CurrentSchemaVersion, bundle.Revision, InstallPhase.Failed, licenseAccepted));
            }
            catch (Exception recordingError) when (recordingError is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally { diagnosticEnabled = false; }
    }

    public string InitialOwnerPassword()
    {
        var path = Path.Combine(RuntimeDirectory, "installation.json");
        PathSafety.RejectLinks(path);
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        return manifest.RootElement.GetProperty("AIOFFICE_OWNER_PASSWORD").GetString()!;
    }

    private Task ProtectDirectoryAsync()
    {
        InstallationAccess.ProtectPrivateRoot(Root);
        return Task.CompletedTask;
    }
    private string PreserveInstaller(bool repair)
    {
        var current = Environment.ProcessPath ?? throw new SetupFailure("Không tìm được bộ cài hiện tại.");
        var directory = Path.Combine(Root, "Installers", bundle.Revision);
        InstallationAccess.ProtectInstallerDirectory(Root, directory);
        return CachedInstaller.Ensure(current, Path.Combine(directory, "Setup.exe"), repair);
    }
    private async Task EnsureDockerAsync()
    {
        Process.Start(new ProcessStartInfo(WindowsPrerequisites.DockerDesktop)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })?.Dispose();
        var deadline = DateTime.UtcNow.AddMinutes(8);
        do
        {
            var endpoint = await runner.RunAsync(Docker, ["context", "inspect", "desktop-linux", "--format", "{{.Endpoints.docker.Host}}"], TimeSpan.FromSeconds(20),
                removeEnvironmentPrefixes: ["DOCKER_"]);
            if (endpoint.ExitCode == 0)
            {
                if (!endpoint.Output.Trim().Equals("npipe:////./pipe/dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase))
                    throw new SetupFailure("Docker context không trỏ tới engine Linux cục bộ được hỗ trợ.", SetupFailureCode.DockerContext);
                var engine = await runner.RunAsync(Docker, ["--context", "desktop-linux", "info", "--format", "{{.OSType}}"], TimeSpan.FromSeconds(20),
                    removeEnvironmentPrefixes: ["DOCKER_"]);
                if (engine.ExitCode == 0 && engine.Output.Trim() == "linux") return;
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        } while (DateTime.UtcNow < deadline);
        throw new SetupFailure("Docker Linux engine chưa sẵn sàng. Có thể cần reboot Windows rồi chạy lại ứng dụng.");
    }

    private string ComposeProjectName()
    {
        var path = Path.Combine(RuntimeDirectory, "installation.json");
        PathSafety.RejectLinks(path);
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        if (!Guid.TryParse(manifest.RootElement.GetProperty("AIOFFICE_INSTALLATION_ID").GetString(), out var identity))
            throw new SetupFailure("Định danh cài đặt không hợp lệ. Dữ liệu hiện có được giữ nguyên.", SetupFailureCode.Configuration);
        return "aioffice-" + identity.ToString("N");
    }
    private Task<CommandResult> ComposeAsync(IEnumerable<string> arguments, TimeSpan timeout)
    {
        return runner.RunAsync(Docker, new[] { "--context", "desktop-linux", "compose", "--project-name", ComposeProjectName(), "--project-directory", source!,
            "--env-file", environment!, "-f", Path.Combine(source!, "compose.local.yaml") }.Concat(arguments), timeout, removeEnvironmentPrefixes: ["AIOFFICE_", "COMPOSE_", "DOCKER_"]);
    }

    private async Task CaptureRuntimeDiagnosticAsync()
    {
        if (source is null || environment is null) return;
        try
        {
            var services = await ComposeAsync(["ps", "--all", "--format", "json"], TimeSpan.FromSeconds(20));
            Diagnostic = Diagnostic with { Services = RuntimeDiagnostics.Parse(services.ExitCode == 0 ? services.Output : "") };
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
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
                Diagnostic = Diagnostic with { Services = RuntimeDiagnostics.Parse(services.ExitCode == 0 ? services.Output : "") };
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
