using System.Runtime.InteropServices;
using Setup.Core;

namespace Setup.Windows;

internal static class ApplicationLinks
{
    public static void RegisterResume(string installer)
    {
        Write(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MinhHuy AI Office Setup.lnk"), installer, "--resume");
    }

    public static void RegisterApplication(string installer)
    {
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MinhHuy AI Office");
        Directory.CreateDirectory(programs);
        Write(Path.Combine(programs, "MinhHuy AI Office.lnk"), installer, "--start");
        Write(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MinhHuy AI Office.lnk"), installer, "--start --background");
        var resume = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MinhHuy AI Office Setup.lnk");
        PathSafety.RejectLinks(resume);
        if (File.Exists(resume)) File.Delete(resume);
    }

    private static void Write(string path, string installer, string arguments)
    {
        PathSafety.RejectLinks(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".lnk");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = ((dynamic)shell!).CreateShortcut(temporary);
            ((dynamic)shortcut).TargetPath = installer;
            ((dynamic)shortcut).Arguments = arguments;
            ((dynamic)shortcut).WorkingDirectory = Path.GetDirectoryName(installer);
            ((dynamic)shortcut).Description = "MinhHuy AI Office";
            ((dynamic)shortcut).WindowStyle = 7;
            ((dynamic)shortcut).Save();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
