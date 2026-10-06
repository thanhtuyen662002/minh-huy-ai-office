using System.Text.Json;
using Xunit;

namespace Setup.Core.Tests;

public sealed class LocalConfigurationRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aioffice-config-test-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "Minh Huy tiếng Việt");

    [Fact]
    public async Task RepeatInitializationPreservesIdentityCredentialsAndEnvironment()
    {
        var first = await InitializeAsync();
        Assert.True(first.ExitCode == 0, SafeDiagnostic(first));
        var manifest = File.ReadAllBytes(Path.Combine(Data, "installation.json"));
        var environment = File.ReadAllBytes(Path.Combine(Data, "local.env"));
        var repeated = await InitializeAsync();
        Assert.Equal(0, repeated.ExitCode);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(Data, "installation.json")));
        Assert.Equal(environment, File.ReadAllBytes(Path.Combine(Data, "local.env")));
        RequireNoSecretOutput(manifest, first, repeated);
    }

    [Fact]
    public async Task MissingEnvironmentIsRebuiltFromOriginalManifestWithoutRotatingSecrets()
    {
        Assert.Equal(0, (await InitializeAsync()).ExitCode);
        var manifest = File.ReadAllBytes(Path.Combine(Data, "installation.json"));
        var environmentPath = Path.Combine(Data, "local.env");
        var environment = File.ReadAllBytes(environmentPath);
        File.Delete(environmentPath);
        var repaired = await InitializeAsync();
        Assert.Equal(0, repaired.ExitCode);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(Data, "installation.json")));
        Assert.Equal(environment, File.ReadAllBytes(environmentPath));
        RequireNoSecretOutput(manifest, repaired);
    }

    [Fact]
    public async Task MissingManifestCannotReplaceRetainedEnvironmentOrCreateNewIdentity()
    {
        Assert.Equal(0, (await InitializeAsync()).ExitCode);
        var manifestPath = Path.Combine(Data, "installation.json");
        var manifest = File.ReadAllBytes(manifestPath);
        var environmentPath = Path.Combine(Data, "local.env");
        var environment = File.ReadAllBytes(environmentPath);
        File.Delete(manifestPath);
        var rejected = await InitializeAsync();
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("Restore the original manifest", rejected.Error);
        Assert.False(File.Exists(manifestPath));
        Assert.Equal(environment, File.ReadAllBytes(environmentPath));
        RequireNoSecretOutput(manifest, rejected);
    }

    [Fact]
    public async Task LostConfigurationAfterReadyAndRebootCannotCreateANewComposeIdentity()
    {
        Assert.Equal(0, (await InitializeAsync()).ExitCode);
        var manifestPath = Path.Combine(Data, "installation.json");
        var environmentPath = Path.Combine(Data, "local.env");
        var manifest = File.ReadAllBytes(manifestPath);
        var environment = File.ReadAllBytes(environmentPath);
        using var document = JsonDocument.Parse(manifest);
        var id = document.RootElement.GetProperty("AIOFFICE_INSTALLATION_ID").GetGuid();
        var store = new ProgressStore(root);
        var ready = new InstallProgress(3, new string('a', 40), InstallPhase.Ready, true, true, id);
        store.Save(ready with { Phase = InstallPhase.AwaitingReboot });
        File.Delete(manifestPath);
        File.Delete(environmentPath);
        var retention = ConfigurationRetentionPlan.Create(new ProgressStore(root).Load(), false, false);
        var rejected = await InitializeAsync(retention.RequireExistingInstallation, retention.InstallationId);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("Restore the original manifest", rejected.Error);
        Assert.False(File.Exists(manifestPath));
        Assert.False(File.Exists(environmentPath));
        File.WriteAllBytes(manifestPath, manifest);
        var restored = await InitializeAsync(retention.RequireExistingInstallation, retention.InstallationId);
        Assert.Equal(0, restored.ExitCode);
        Assert.Equal(manifest, File.ReadAllBytes(manifestPath));
        Assert.Equal(environment, File.ReadAllBytes(environmentPath));
        RequireNoSecretOutput(manifest, rejected, restored);
    }

    [Fact]
    public async Task DifferentManifestIdentityCannotRetargetAnExistingInstallation()
    {
        Assert.Equal(0, (await InitializeAsync()).ExitCode);
        var manifest = File.ReadAllBytes(Path.Combine(Data, "installation.json"));
        var environment = File.ReadAllBytes(Path.Combine(Data, "local.env"));
        var rejected = await InitializeAsync(true, Guid.NewGuid());
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("identity does not match retained progress", rejected.Error);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(Data, "installation.json")));
        Assert.Equal(environment, File.ReadAllBytes(Path.Combine(Data, "local.env")));
        RequireNoSecretOutput(manifest, rejected);
    }

    [Fact]
    public async Task LegacyUnknownInstallationCannotGenerateConfigurationWhenBothFilesAreAbsent()
    {
        var previous = new InstallProgress(2, new string('a', 40), InstallPhase.Ready, true);
        var retention = ConfigurationRetentionPlan.Create(previous, false, false);
        var rejected = await InitializeAsync(retention.RequireExistingInstallation, retention.InstallationId);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.False(File.Exists(Path.Combine(Data, "installation.json")));
        Assert.False(File.Exists(Path.Combine(Data, "local.env")));
    }

    private Task<CommandResult> InitializeAsync(bool requireExisting = false, Guid? expectedId = null)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "infra", "initialize-local-config.ps1")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var script = Path.Combine(directory.FullName, "infra", "initialize-local-config.ps1");
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "pwsh";
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-DataDirectory", Data };
        if (requireExisting) arguments.Add("-RequireExistingInstallation");
        if (expectedId is not null) arguments.AddRange(["-ExpectedInstallationId", expectedId.Value.ToString()]);
        return new CommandRunner().RunAsync(executable, arguments, TimeSpan.FromSeconds(30));
    }

    private static void RequireNoSecretOutput(byte[] manifest, params CommandResult[] results)
    {
        using var document = JsonDocument.Parse(manifest);
        foreach (var property in document.RootElement.EnumerateObject().Where(property => property.Name.EndsWith("_PASSWORD", StringComparison.Ordinal)))
            foreach (var result in results)
                Assert.DoesNotContain(property.Value.GetString()!, result.Output + result.Error);
    }

    private string SafeDiagnostic(CommandResult result)
    {
        var diagnostic = result.Output + result.Error;
        var manifestPath = Path.Combine(Data, "installation.json");
        if (!File.Exists(manifestPath)) return diagnostic;
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        foreach (var property in document.RootElement.EnumerateObject().Where(property => property.Name.EndsWith("_PASSWORD", StringComparison.Ordinal)))
            diagnostic = diagnostic.Replace(property.Value.GetString()!, "[REDACTED]", StringComparison.Ordinal);
        return diagnostic;
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(root);
        var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Temporary configuration cleanup escaped its owned directory.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
