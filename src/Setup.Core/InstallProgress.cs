using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Setup.Core;

public enum InstallPhase { Inspecting, AwaitingReboot, PreparingRuntime, StartingRuntime, Ready, Failed }

// Deliberately excludes credentials, command output and arbitrary exception messages.
public sealed record InstallProgress(int SchemaVersion, string Revision, InstallPhase Phase, bool DockerLicenseAccepted = false);

public sealed partial class ProgressStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<InstallPhase>(allowIntegerValues: false) }
    };
    private readonly string path;

    public ProgressStore(string directory)
    {
        path = Path.Combine(Path.GetFullPath(directory), "setup-progress.json");
    }

    public InstallProgress? Load()
    {
        if (!File.Exists(path)) return null;
        PathSafety.RejectLinks(path);
        if (new FileInfo(path).Length > 4096) throw new CorruptProgressException();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
                throw new CorruptProgressException();
            if (version != 1) throw new UnsupportedProgressException();
            if (!root.TryGetProperty("Phase", out var phase) || phase.ValueKind != JsonValueKind.String)
                throw new CorruptProgressException();
            if (!Enum.TryParse<InstallPhase>(phase.GetString(), out var parsed) || !Enum.IsDefined(parsed))
                throw new UnsupportedProgressException();
            var progress = root.Deserialize<InstallProgress>(Options) ?? throw new CorruptProgressException();
            Validate(progress);
            return progress;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new CorruptProgressException();
        }
    }

    public InstallProgress? LoadOrRepair(bool repair)
    {
        try { return Load(); }
        catch (CorruptProgressException) when (repair)
        {
            PathSafety.RejectLinks(path);
            var retained = Path.Combine(Path.GetDirectoryName(path)!, $".progress-retained-{Guid.NewGuid():N}.json");
            File.Move(path, retained, overwrite: false);
            return null;
        }
    }

    public void Save(InstallProgress progress)
    {
        Validate(progress);
        AtomicArtifact.Write(path, JsonSerializer.SerializeToUtf8Bytes(progress, Options));
    }

    public static void ValidateRevision(string revision)
    {
        if (revision is null || !RevisionPattern().IsMatch(revision))
            throw new InvalidDataException("Invalid bundle revision.");
    }

    private static void Validate(InstallProgress progress)
    {
        ValidateRevision(progress.Revision);
        if (progress.SchemaVersion != 1 || !Enum.IsDefined(progress.Phase))
            throw new UnsupportedProgressException();
    }

    [GeneratedRegex("\\A[a-f0-9]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();
}

public sealed class CorruptProgressException() : IOException("Installation progress is damaged. Existing data is retained.");
public sealed class UnsupportedProgressException() : IOException("Installation progress requires a supported installer version. Existing data is retained.");
