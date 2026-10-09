using System.Text.Json;
using System.Text.Json.Serialization;

namespace Setup.Core;

public enum SetupFailureCode
{
    None, MissingLicense, UnsupportedMachine, InvalidProgress, AlreadyRunning, ElevationCancelled,
    PrerequisitePreparation, InsufficientDisk, BundleIntegrity, Configuration, DockerUnavailable,
    DockerContext, RuntimeStart, RuntimeReadiness, ShortcutConflict, Unexpected
}

public enum RuntimeService { Sql, IdentityDatabase, Identity, RabbitMq, Redis, Jaeger, Telemetry, CoreApi, AgentWorker, Web, Bootstrap }
public enum ServiceState { Missing, Unknown, Created, Running, Exited, Restarting, Paused, Dead, Removing }
public enum ServiceHealth { None, Unknown, Starting, Healthy, Unhealthy }
public sealed record ServiceDiagnostic(RuntimeService Service, ServiceState State, ServiceHealth Health, int ExitCode);
public sealed record MachineDiagnostic(int WindowsBuild, bool FirmwareVirtualization, bool HypervisorPresent, ulong PhysicalMemoryBytes, long FreeDiskBytes);
public sealed record SetupDiagnostic(int SchemaVersion, string Revision, InstallPhase Phase, SetupFailureCode Failure,
    DateTimeOffset UpdatedAt, MachineDiagnostic? Machine, IReadOnlyList<ServiceDiagnostic> Services);

public sealed class DiagnosticStore(string directory)
{
    private readonly string path = Path.Combine(Path.GetFullPath(directory), "setup-diagnostics.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public void Save(SetupDiagnostic diagnostic) => AtomicArtifact.Write(path, Serialize(diagnostic));

    public static void Export(string path, SetupDiagnostic diagnostic) => AtomicArtifact.Write(path, Serialize(diagnostic));

    public SetupDiagnostic Load()
    {
        PathSafety.RejectLinks(path);
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Invalid setup diagnostic.");
        var value = JsonSerializer.Deserialize<SetupDiagnostic>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Invalid setup diagnostic.");
        Validate(value);
        return value;
    }

    public static byte[] Serialize(SetupDiagnostic diagnostic)
    {
        Validate(diagnostic);
        return JsonSerializer.SerializeToUtf8Bytes(diagnostic, Options);
    }

    private static void Validate(SetupDiagnostic diagnostic)
    {
        if (diagnostic is null) throw new InvalidDataException("Invalid setup diagnostic.");
        ProgressStore.ValidateRevision(diagnostic.Revision);
        if (diagnostic.SchemaVersion != 1 || !Enum.IsDefined(diagnostic.Phase) || !Enum.IsDefined(diagnostic.Failure) ||
            diagnostic.UpdatedAt.Offset != TimeSpan.Zero || diagnostic.Services is null || diagnostic.Services.Count > 11 ||
            diagnostic.Services.Any(value => value is null) ||
            diagnostic.Services.Select(value => value.Service).Distinct().Count() != diagnostic.Services.Count ||
            diagnostic.Services.Any(value => !Enum.IsDefined(value.Service) || !Enum.IsDefined(value.State) ||
                !Enum.IsDefined(value.Health) || value.ExitCode < 0 || value.ExitCode > 255) ||
            diagnostic.Machine is { WindowsBuild: < 0 } || diagnostic.Machine is { FreeDiskBytes: < 0 })
            throw new InvalidDataException("Invalid setup diagnostic.");
    }
}
