using Setup.Core;

namespace Setup.Windows;

internal static class ApplicationLinks
{
    private const string ResumeName = "MinhHuy AI Office Resume.lnk";
    private const string Description = "MinhHuy AI Office";

    public static void RegisterResume(string installer, string? startupDirectory = null)
    {
        var startup = startupDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        StartupContinuation.Suspend(installer, startup);
        try { Write(Path.Combine(startup, ResumeName), installer, "--resume"); }
        catch { StartupContinuation.Restore(installer, startup); throw; }
    }

    public static void RemoveResume(string installer, string? startupDirectory = null, bool allowPreviousRevision = false)
    {
        var startup = startupDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var path = Path.Combine(startup, ResumeName);
        PathSafety.RejectLinks(path);
        if (!File.Exists(path) || !IsOwned(WindowsShortcut.Read(path), installer, "--resume", allowPreviousRevision)) return;
        File.Delete(path);
        StartupContinuation.Restore(installer, startup);
    }

    public static void RegisterApplication(string installer, string? programsDirectory = null, string? startupDirectory = null)
    {
        var programs = Path.Combine(programsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), Description);
        var startup = startupDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        PathSafety.RejectLinks(programs);
        Directory.CreateDirectory(programs);
        Write(Path.Combine(programs, Description + ".lnk"), installer, "--start");
        Write(Path.Combine(startup, Description + ".lnk"), installer, "--start --background");
        StartupContinuation.Complete(installer);
        RemoveResume(installer, startup, allowPreviousRevision: true);
    }

    internal static bool IsOwned(ShortcutSnapshot shortcut, string installer, string arguments, bool allowPreviousRevision)
    {
        try
        {
            if (shortcut.Arguments != arguments || shortcut.Description != Description) return false;
            var target = Path.GetFullPath(shortcut.TargetPath);
            if (string.Equals(target, Path.GetFullPath(installer), StringComparison.OrdinalIgnoreCase)) return true;
            if (!allowPreviousRevision) return false;
            var installerRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(installer)))!;
            var targetDirectory = Path.GetDirectoryName(target)!;
            return string.Equals(Path.GetDirectoryName(targetDirectory), installerRoot, StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(targetDirectory), @"\A[a-f0-9]{40}\z") &&
                string.Equals(Path.GetFileName(target), "Setup.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static void Write(string path, string target, string arguments)
    {
        PathSafety.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && !IsOwned(WindowsShortcut.Read(path), target, arguments, allowPreviousRevision: true))
            throw new SetupFailure("Một lối tắt cùng tên thuộc ứng dụng khác. Hãy đổi tên lối tắt đó rồi Sửa chữa.",
                SetupFailureCode.ShortcutConflict);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            WindowsShortcut.Write(temporary, target, arguments, Description);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
