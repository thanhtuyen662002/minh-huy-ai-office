using System.Diagnostics;
using Setup.Core;
using Xunit;

namespace Setup.Core.Tests;

public sealed class CommandRunnerTests
{
    [Fact]
    public async Task WindowsPowerShellUsesItsOwnModulesAndPreservesParentModulePath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = Environment.GetEnvironmentVariable("PSModulePath");
        var root = Path.Combine(Path.GetTempPath(), "aioffice-module-test-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(root, "Microsoft.PowerShell.Security", "99.0.0");
        Directory.CreateDirectory(module);
        File.WriteAllText(Path.Combine(module, "Microsoft.PowerShell.Security.psd1"),
            "@{ModuleVersion='99.0.0';PowerShellVersion='99.0';RootModule='incompatible.psm1'}");
        Environment.SetEnvironmentVariable("PSModulePath", root);
        try
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var result = await new CommandRunner().RunAsync(executable,
                ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write((Get-Acl -LiteralPath $env:TEMP).GetType().Name)"],
                TimeSpan.FromSeconds(15));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("DirectorySecurity", result.Output);
            Assert.Equal(root, Environment.GetEnvironmentVariable("PSModulePath"));
            // The native prerequisite signature path uses this factory directly.
            var info = CommandRunner.CreateStartInfo(executable);
            info.Environment["AIOFFICE_PREREQUISITE_PATH"] = executable;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Write((Get-AuthenticodeSignature -LiteralPath $env:AIOFFICE_PREREQUISITE_PATH).Status)" })
                info.ArgumentList.Add(argument);
            using var check = Process.Start(info)!;
            var output = check.StandardOutput.ReadToEndAsync();
            var error = check.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await check.WaitForExitAsync(timeout.Token); }
            finally { if (!check.HasExited) check.Kill(entireProcessTree: true); }
            Assert.Equal(0, check.ExitCode);
            Assert.Equal("Valid", await output);
            Assert.Equal("", await error);
            Assert.Equal(root, Environment.GetEnvironmentVariable("PSModulePath"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", original);
            var absolute = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Module fixture cleanup escaped its owned directory.");
            Directory.Delete(absolute, recursive: true);
        }
    }

    [Fact]
    public async Task ProtectedConfigurationOverridesAreRemovedOnlyFromChildProcess()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var configuration = "AIOFFICE_SETUP_TEST_" + suffix;
        var project = "COMPOSE_SETUP_TEST_" + suffix;
        var docker = "dOcKeR_SETUP_TEST_" + suffix;
        var retained = "SETUP_KEEP_TEST_" + suffix;
        Environment.SetEnvironmentVariable(configuration, "fixture-override");
        Environment.SetEnvironmentVariable(project, "foreign-project");
        Environment.SetEnvironmentVariable(docker, "foreign-engine");
        Environment.SetEnvironmentVariable(retained, "fixture-retained");
        try
        {
            var script = OperatingSystem.IsWindows()
                ? $"[Console]::Write($env:{configuration} + '|' + $env:{project} + '|' + $env:{docker} + '|' + $env:{retained})"
                : $"printf '%s|%s|%s|%s' \"${configuration}\" \"${project}\" \"${docker}\" \"${retained}\"";
            var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh";
            string[] arguments = OperatingSystem.IsWindows() ? ["-NoProfile", "-NonInteractive", "-Command", script] : ["-c", script];
            var result = await new CommandRunner().RunAsync(executable, arguments, TimeSpan.FromSeconds(15),
                removeEnvironmentPrefixes: ["AIOFFICE_", "COMPOSE_", "DOCKER_"]);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("|||fixture-retained", result.Output);
            Assert.Equal("fixture-override", Environment.GetEnvironmentVariable(configuration));
            Assert.Equal("foreign-project", Environment.GetEnvironmentVariable(project));
            Assert.Equal("foreign-engine", Environment.GetEnvironmentVariable(docker));
        }
        finally
        {
            Environment.SetEnvironmentVariable(configuration, null);
            Environment.SetEnvironmentVariable(project, null);
            Environment.SetEnvironmentVariable(docker, null);
            Environment.SetEnvironmentVariable(retained, null);
        }
    }
}
