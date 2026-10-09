using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Setup.Core;

public sealed partial class BundleExtractor
{
    public const int MaxEntries = 25000;
    public const long MaxExpandedBytes = 256L * 1024 * 1024;

    public string Extract(Stream archive, string revision, string expectedSha256, string directory)
    {
        ProgressStore.ValidateRevision(revision);
        if (expectedSha256 is null || !HashPattern().IsMatch(expectedSha256) || !archive.CanSeek)
            throw new InvalidDataException("Invalid application bundle manifest.");
        archive.Position = 0;
        if (!Convert.ToHexStringLower(SHA256.HashData(archive)).Equals(expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Application bundle integrity check failed.");
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var entries = Validate(zip);
        var root = Path.GetFullPath(directory);
        PathSafety.RejectLinks(root);
        Directory.CreateDirectory(root);
        var target = PathSafety.Child(root, revision + "-" + expectedSha256[..12]);
        if (Directory.Exists(target))
        {
            VerifyExisting(target, entries);
            return target;
        }
        var staging = PathSafety.Child(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (entry, relative, isDirectory) in entries)
            {
                var path = PathSafety.Child(staging, relative);
                if (isDirectory) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                int count;
                long written = 0;
                while ((count = input.Read(buffer)) != 0)
                {
                    written += count;
                    if (written > entry.Length) throw new InvalidDataException("Application bundle entry exceeds its declared size.");
                    output.Write(buffer, 0, count);
                }
                if (output.Length != entry.Length) throw new InvalidDataException("Incomplete application bundle entry.");
            }
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            // Only this invocation's validated staging directory is disposable; never runtime data.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static List<(ZipArchiveEntry Entry, string Relative, bool Directory)> Validate(ZipArchive zip)
    {
        if (zip.Entries.Count == 0 || zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("Application bundle has an invalid entry count.");
        var entries = new List<(ZipArchiveEntry, string, bool)>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var segments = (directory ? name[..^1] : name).Split('/');
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment.Length > 255 || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 ||
                segment.Any(char.IsControl) || ReservedName().IsMatch(segment)))
                throw new InvalidDataException("Unsafe application bundle path.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Application bundle contains a link.");
            var relative = string.Join(Path.DirectorySeparatorChar, segments);
            if (!paths.TryAdd(relative, directory)) throw new InvalidDataException("Duplicate application bundle path.");
            size = checked(size + entry.Length);
            if (size > MaxExpandedBytes || entry.Length > 64L * 1024 * 1024 || (directory && entry.Length != 0))
                throw new InvalidDataException("Application bundle exceeds extraction limits.");
            entries.Add((entry, relative, directory));
        }
        foreach (var path in paths.Keys)
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (paths.TryGetValue(parent, out var isDirectory) && !isDirectory)
                    throw new InvalidDataException("Application bundle file and directory conflict.");
        return entries;
    }

    private static void VerifyExisting(string target, List<(ZipArchiveEntry Entry, string Relative, bool Directory)> entries)
    {
        PathSafety.RejectLinks(target);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, relative, isDirectory) in entries)
        {
            if (isDirectory) directories.Add(relative);
            else files.Add(relative);
            for (var parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                directories.Add(parent);
        }
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.TryPop(out var directory))
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                PathSafety.RejectLinks(path);
                var relative = Path.GetRelativePath(target, path);
                if (System.IO.Directory.Exists(path))
                {
                    if (!directories.Contains(relative)) throw new InvalidDataException("Installed bundle contains unexpected content. Repair is required.");
                    pending.Push(path);
                }
                else if (!files.Contains(relative))
                    throw new InvalidDataException("Installed bundle contains unexpected content. Repair is required.");
            }
        foreach (var (entry, relative, isDirectory) in entries)
        {
            var path = PathSafety.Child(target, relative);
            PathSafety.RejectLinks(path);
            if (isDirectory)
            {
                if (!Directory.Exists(path)) throw new InvalidDataException("Installed bundle is incomplete. Repair is required.");
                continue;
            }
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Length)
                throw new InvalidDataException("Installed bundle is incomplete. Repair is required.");
            using var installed = File.OpenRead(path);
            using var original = entry.Open();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(installed), SHA256.HashData(original)))
                throw new InvalidDataException("Installed bundle was modified. Repair is required.");
        }
    }

    [GeneratedRegex("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
    [GeneratedRegex("\\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|\\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
