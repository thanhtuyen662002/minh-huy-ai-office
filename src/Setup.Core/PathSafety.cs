namespace Setup.Core;

public static class PathSafety
{
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installation paths must not contain links or junctions.");
        }
    }

    public static string Child(string root, string relative)
    {
        var directory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(directory, relative));
        if (!result.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bundle entry escapes the installation directory.");
        return result;
    }
}
