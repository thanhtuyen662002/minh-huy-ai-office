using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Setup.Core;

namespace Setup.Windows;

internal sealed record MachineState(int Build, bool Virtualization, bool Hypervisor, ulong Memory);

internal sealed partial class WindowsPrerequisites
{
    private readonly CommandRunner runner = new();
    public static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string Wsl => Path.Combine(Environment.SystemDirectory, "wsl.exe");
    public static string DockerDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker");
    public static string Docker => Path.Combine(DockerDirectory, "resources", "bin", "docker.exe");
    public static string DockerDesktop => Path.Combine(DockerDirectory, "Docker Desktop.exe");

    public async Task<MachineState> InspectAsync(Action<MachineState>? observe = null)
    {
        if (!Environment.Is64BitOperatingSystem || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new SetupFailure("Windows 11 x64 là môi trường được hỗ trợ bởi bộ cài này.");
        const string script = "$ErrorActionPreference='Stop'; $os=Get-CimInstance Win32_OperatingSystem; $cpu=Get-CimInstance Win32_Processor | Select-Object -First 1; $computer=Get-CimInstance Win32_ComputerSystem; @{ Build=[int]$os.BuildNumber; Virtualization=[bool]$cpu.VirtualizationFirmwareEnabled; Hypervisor=[bool]$computer.HypervisorPresent; Memory=[ulong]$computer.TotalPhysicalMemory } | ConvertTo-Json -Compress";
        var result = await runner.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromMinutes(1));
        if (result.ExitCode != 0) throw new SetupFailure("Không đọc được thông tin môi trường Windows.");
        var machine = JsonSerializer.Deserialize<MachineState>(result.Output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new SetupFailure("Thông tin môi trường Windows không hợp lệ.");
        observe?.Invoke(machine);
        if (machine.Build < 22631) throw new SetupFailure("Cần Windows 11 23H2 trở lên. Bộ cài giữ nguyên dữ liệu hiện có.");
        if (!machine.Virtualization) throw new SetupFailure("Máy chưa bật ảo hóa trong firmware. Cần bật trước khi Docker/WSL có thể chạy.");
        if (machine.Memory < 8UL * 1024 * 1024 * 1024) throw new SetupFailure("Cần ít nhất 8 GB RAM để chạy Docker và các dịch vụ.");
        return machine;
    }

    public async Task<bool> NeedsInstallationAsync(MachineState machine)
    {
        if (!machine.Hypervisor || !File.Exists(Docker) || !File.Exists(DockerDesktop)) return true;
        var wsl = await runner.RunAsync(Wsl, ["--status"], TimeSpan.FromMinutes(1));
        if (wsl.ExitCode != 0) return true;
        var version = await runner.RunAsync(Wsl, ["--version"], TimeSpan.FromMinutes(1));
        var parsed = WslVersion().Match(version.Output.Replace("\0", ""));
        return version.ExitCode != 0 || !parsed.Success || new Version(parsed.Value) < new Version(2, 1, 5);
    }

    public async Task<int> ElevateAsync(string installer)
    {
        var info = new ProcessStartInfo(installer)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("--install-prerequisites");
        info.ArgumentList.Add("--docker-license-accepted");
        using var process = Process.Start(info) ?? throw new SetupFailure("Không khởi động được bước chuẩn bị môi trường.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public async Task<int> InstallElevatedAsync(bool licenseAccepted)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!licenseAccepted || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 1223;
        var machine = await InspectAsync();
        var restart = !machine.Hypervisor;
        foreach (var feature in new[] { "Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform" })
        {
            var result = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "dism.exe"),
                ["/Online", "/Enable-Feature", "/FeatureName:" + feature, "/All", "/NoRestart"], TimeSpan.FromMinutes(15));
            if (result.ExitCode != 0 && result.ExitCode != 3010) return 10;
            restart |= result.ExitCode == 3010;
        }
        if (!machine.Hypervisor)
        {
            var boot = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
                ["/set", "hypervisorlaunchtype", "auto"], TimeSpan.FromMinutes(1));
            if (boot.ExitCode != 0) return 11;
        }
        if (restart) return 3010;
        var initialStatus = await runner.RunAsync(Wsl, ["--status"], TimeSpan.FromMinutes(1));
        if (initialStatus.ExitCode != 0)
        {
            var installWsl = await runner.RunAsync(Wsl, ["--install", "--no-distribution", "--web-download"], TimeSpan.FromMinutes(20));
            if (installWsl.ExitCode == 3010) return 3010;
            if (installWsl.ExitCode != 0) return 12;
        }
        var update = await runner.RunAsync(Wsl, ["--update", "--web-download"], TimeSpan.FromMinutes(20));
        if (update.ExitCode != 0) return 13;
        var status = await runner.RunAsync(Wsl, ["--status"], TimeSpan.FromMinutes(1));
        if (status.ExitCode != 0) return 3010;
        var defaultVersion = await runner.RunAsync(Wsl, ["--set-default-version", "2"], TimeSpan.FromMinutes(1));
        if (defaultVersion.ExitCode != 0) return 14;
        if (!File.Exists(DockerDesktop) || !File.Exists(Docker))
        {
            var download = await DownloadDockerAsync();
            var result = await runner.RunAsync(download,
                ["install", "--quiet", "--accept-license", "--backend=wsl-2", "--always-run-service"], TimeSpan.FromMinutes(30));
            if (result.ExitCode != 0 && result.ExitCode != 3010) return 15;
            if (result.ExitCode == 3010) return 3010;
        }
        return File.Exists(Docker) && File.Exists(DockerDesktop) ? 0 : 16;
    }

    private async Task<string> DownloadDockerAsync()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MinhHuy AI Office", "Prerequisites");
        PathSafety.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Docker-" + Guid.NewGuid().ToString("N") + ".exe");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        var uri = new Uri("https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe");
        for (var redirects = 0; ; redirects++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirects >= 3 || response.Headers.Location is null) throw new InvalidDataException("Invalid prerequisite redirect.");
                uri = new Uri(uri, response.Headers.Location);
                if (uri.Scheme != "https" || uri.Host != "desktop.docker.com") throw new InvalidDataException("Untrusted prerequisite download host.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            const long maximum = 2L * 1024 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Prerequisite is too large.");
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer)) != 0)
            {
                written += count;
                if (written > maximum) throw new InvalidDataException("Prerequisite is too large.");
                await output.WriteAsync(buffer.AsMemory(0, count));
            }
            break;
        }
        var info = CommandRunner.CreateStartInfo(PowerShell);
        info.Environment["AIOFFICE_PREREQUISITE_PATH"] = path;
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$signature=Get-AuthenticodeSignature -LiteralPath $env:AIOFFICE_PREREQUISITE_PATH; if ($signature.Status -ne 'Valid') {exit 1}; @{Subject=$signature.SignerCertificate.Subject} | ConvertTo-Json -Compress" })
            info.ArgumentList.Add(argument);
        using var check = Process.Start(info) ?? throw new InvalidDataException("Cannot validate prerequisite signature.");
        var stdout = check.StandardOutput.ReadToEndAsync();
        var stderr = check.StandardError.ReadToEndAsync();
        await check.WaitForExitAsync();
        await stderr;
        if (check.ExitCode != 0) throw new InvalidDataException("Prerequisite signature is invalid.");
        using var signature = JsonDocument.Parse(await stdout);
        if (!DockerPublisher().IsMatch(signature.RootElement.GetProperty("Subject").GetString()!))
            throw new InvalidDataException("Prerequisite publisher is not Docker Inc.");
        return path;
    }

    [GeneratedRegex(@"(?:^|,\s*)(?:CN|O)=Docker Inc\.?(?:,|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DockerPublisher();
    [GeneratedRegex(@"\b\d+\.\d+\.\d+(?:\.\d+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex WslVersion();
}

internal sealed class SetupFailure(string message, SetupFailureCode code = SetupFailureCode.Unexpected) : Exception(message)
{
    public SetupFailureCode Code { get; } = code;
}
