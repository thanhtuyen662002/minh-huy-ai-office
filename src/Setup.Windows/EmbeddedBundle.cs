using System.Reflection;
using System.Text.Json;
using Setup.Core;

namespace Setup.Windows;

internal sealed class EmbeddedBundle
{
    public string Revision { get; }
    public string Sha256 { get; }

    public EmbeddedBundle()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ApplicationBundleManifest")
            ?? throw new InvalidDataException("Bundle manifest is missing.");
        using var manifest = JsonDocument.Parse(resource);
        if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported bundle manifest.");
        Revision = manifest.RootElement.GetProperty("revision").GetString()!;
        Sha256 = manifest.RootElement.GetProperty("sha256").GetString()!;
        ProgressStore.ValidateRevision(Revision);
    }

    public string Extract(string directory, bool repair)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ApplicationBundle")
            ?? throw new InvalidDataException("Application bundle is missing.");
        using var archive = new MemoryStream();
        resource.CopyTo(archive);
        if (archive.Length > BundleExtractor.MaxExpandedBytes) throw new InvalidDataException("Application bundle is too large.");
        var extractor = new BundleExtractor();
        try { return extractor.Extract(archive, Revision, Sha256, directory); }
        catch (InvalidDataException) when (repair)
        {
            ProgressStore.ValidateRevision(Revision);
            if (Sha256.Length != 64 || Sha256.Any(c => !char.IsAsciiHexDigitLower(c))) throw;
            var target = PathSafety.Child(directory, Revision + "-" + Sha256[..12]);
            PathSafety.RejectLinks(target);
            if (!Directory.Exists(target)) throw;
            // Quarantine this version's source only; identity and Docker volumes are elsewhere.
            Directory.Move(target, target + ".retained-" + Guid.NewGuid().ToString("N"));
            return extractor.Extract(archive, Revision, Sha256, directory);
        }
    }

    public void Verify(string directory)
    {
        var extracted = Extract(directory, repair: false);
        foreach (var path in new[] { "compose.local.yaml", "infra/initialize-local-config.ps1", "apps/web/Dockerfile", "src/Platform.Bootstrap/Dockerfile" })
            if (!File.Exists(Path.Combine(extracted, path)))
                throw new InvalidDataException("Required runtime source is missing from the application bundle.");
    }
}
