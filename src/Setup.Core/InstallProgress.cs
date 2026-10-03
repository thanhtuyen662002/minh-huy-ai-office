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
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Invalid installation progress.");
        var progress = JsonSerializer.Deserialize<InstallProgress>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Invalid installation progress.");
        Validate(progress);
        return progress;
    }

    public void Save(InstallProgress progress)
    {
        Validate(progress);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        PathSafety.RejectLinks(directory);
        PathSafety.RejectLinks(path);
        var temporary = Path.Combine(directory, $".progress-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, progress, Options);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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
            throw new InvalidDataException("Unsupported installation progress. Existing data is retained.");
    }

    [GeneratedRegex("\\A[a-f0-9]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();
}
