namespace Setup.Core;

internal static class AtomicArtifact
{
    public static void Write(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        PathSafety.RejectLinks(directory);
        PathSafety.RejectLinks(path);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".artifact-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(content);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
