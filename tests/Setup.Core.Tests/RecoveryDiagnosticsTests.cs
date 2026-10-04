using System.Text;
using Setup.Core;
using Xunit;

namespace Setup.Core.Tests;

public sealed class RecoveryDiagnosticsTests : IDisposable
{
    private const string Revision = "1234567890abcdef1234567890abcdef12345678";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aioffice-recovery-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ExplicitRepairRetainsProgressAndPreservesIdentity(string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "setup-progress.json");
        File.WriteAllText(path, content);
        var identity = Path.Combine(directory, "installation.json");
        File.WriteAllText(identity, "retained identity");
        var store = new ProgressStore(directory);
        Assert.Throws<CorruptProgressException>(() => store.LoadOrRepair(false));
        Assert.Null(store.LoadOrRepair(true));
        Assert.Equal(content, File.ReadAllText(Assert.Single(Directory.GetFiles(directory, ".progress-retained-*.json"))));
        store.Save(new InstallProgress(1, Revision, InstallPhase.Inspecting, true));
        Assert.Equal("retained identity", File.ReadAllText(identity));
        Assert.True(store.Load()!.DockerLicenseAccepted);
    }

    [Fact]
    public void UnsupportedVersionCannotBeRepairedByAnOlderInstaller()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "setup-progress.json");
        var content = $$"""{"SchemaVersion":2,"Revision":"{{Revision}}","Phase":"Ready"}""";
        File.WriteAllText(path, content);
        Assert.Throws<UnsupportedProgressException>(() => new ProgressStore(directory).LoadOrRepair(true));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(directory, ".progress-retained-*.json"));
    }

    [Fact]
    public void ReportCannotExportRawCommandTextOrArbitraryServiceNames()
    {
        const string secret = "owner-secret-not-for-export";
        var input = $$"""[{"Service":"web","State":"{{secret}}","Health":"{{secret}}","ExitCode":0,"Command":"{{secret}}"},{"Service":"{{secret}}","State":"running","ExitCode":0}]""";
        var report = new SetupDiagnostic(1, Revision, InstallPhase.Failed, SetupFailureCode.RuntimeReadiness,
            DateTimeOffset.UtcNow, null, RuntimeDiagnostics.Parse(input));
        var store = new DiagnosticStore(directory);
        store.Save(report);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(DiagnosticStore.Serialize(store.Load())));
        Assert.Equal(11, store.Load().Services.Count);
        Assert.Equal(ServiceState.Unknown, store.Load().Services.Single(value => value.Service == RuntimeService.Web).State);
        Assert.Throws<InvalidDataException>(() => store.Save(report with { Revision = "../unsafe" }));
        Assert.Equal(report.Revision, store.Load().Revision);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void DiagnosticExportIsTypedAtomicAndDoesNotReadPrivateConfiguration()
    {
        Directory.CreateDirectory(directory);
        var identity = Path.Combine(directory, "installation.json");
        File.WriteAllText(identity, "owner-secret-not-for-export");
        var report = new SetupDiagnostic(1, Revision, InstallPhase.StartingRuntime, SetupFailureCode.DockerUnavailable,
            DateTimeOffset.UtcNow, new MachineDiagnostic(26200, true, false, 16UL * 1024 * 1024 * 1024, 50000000000),
            RuntimeDiagnostics.Parse(""));
        var output = Path.Combine(directory, "Minh Huy tiếng Việt", "report.json");
        DiagnosticStore.Export(output, report);
        var content = File.ReadAllBytes(output);
        Assert.DoesNotContain("owner-secret-not-for-export", Encoding.UTF8.GetString(content));
        Assert.Throws<InvalidDataException>(() => DiagnosticStore.Export(output, report with { Services = [null!] }));
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.Equal("owner-secret-not-for-export", File.ReadAllText(identity));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.tmp"));
    }

    [Fact]
    public void FutureProgressPhaseIsNeverResetByRepair()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "setup-progress.json");
        var content = "{\"SchemaVersion\":1,\"Revision\":\"" + Revision + "\",\"Phase\":\"FuturePhase\",\"DockerLicenseAccepted\":true}";
        File.WriteAllText(path, content);
        Assert.Throws<UnsupportedProgressException>(() => new ProgressStore(directory).LoadOrRepair(repair: true));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(directory, ".progress-retained-*.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
