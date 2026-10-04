using System.Runtime.InteropServices;
using Setup.Core;

namespace Setup.Windows;

internal static class StartupContinuation
{
    public static string BackupPath(string installer)
    {
        var revision = Path.GetDirectoryName(Path.GetFullPath(installer))!;
        var installers = Path.GetDirectoryName(revision)!;
        if (!string.Equals(Path.GetFileName(installers), "Installers", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The continuation must use an installed helper.");
        return Path.Combine(Path.GetDirectoryName(installers)!, ".pending-startup.lnk");
    }

    public static void Suspend(string installer, string startup)
    {
        var active = Path.Combine(startup, "MinhHuy AI Office.lnk");
        var backup = BackupPath(installer);
        PathSafety.RejectLinks(active);
        PathSafety.RejectLinks(backup);
        if (File.Exists(backup)) RequireOwned(backup, installer);
        if (!File.Exists(active)) return;
        RequireOwned(active, installer);
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(active, backup, overwrite: false);
    }

    public static void Restore(string installer, string startup)
    {
        var active = Path.Combine(startup, "MinhHuy AI Office.lnk");
        var backup = BackupPath(installer);
        PathSafety.RejectLinks(active);
        PathSafety.RejectLinks(backup);
        if (!File.Exists(backup)) return;
        RequireOwned(backup, installer);
        if (!File.Exists(active) && !Directory.Exists(active)) File.Move(backup, active, overwrite: false);
    }

    public static void Complete(string installer)
    {
        var backup = BackupPath(installer);
        PathSafety.RejectLinks(backup);
        if (!File.Exists(backup)) return;
        RequireOwned(backup, installer);
        File.Delete(backup);
    }

    private static void RequireOwned(string path, string installer)
    {
        if (!ApplicationLinks.IsOwned(WindowsShortcut.Read(path), installer, "--start --background", allowPreviousRevision: true))
            throw new SetupFailure("Một lối tắt cùng tên thuộc ứng dụng khác. Hãy đổi tên lối tắt đó rồi Sửa chữa.",
                SetupFailureCode.ShortcutConflict);
    }
}
