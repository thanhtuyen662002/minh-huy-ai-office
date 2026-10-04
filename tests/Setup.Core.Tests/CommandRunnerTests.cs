using Setup.Core;
using Xunit;

namespace Setup.Core.Tests;

public sealed class CommandRunnerTests
{
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
