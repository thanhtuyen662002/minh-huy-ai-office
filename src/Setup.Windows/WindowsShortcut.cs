using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Setup.Core;

namespace Setup.Windows;

internal sealed record ShortcutSnapshot(string TargetPath, string Arguments, string WorkingDirectory, string Description);

internal static class WindowsShortcut
{
    public static ShortcutSnapshot Read(string path)
    {
        PathSafety.RejectLinks(path);
        var shell = new ShellLink();
        try
        {
            ((IPersistFile)shell).Load(path, 0);
            var link = (IShellLinkW)shell;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var working = new StringBuilder(32768);
            var description = new StringBuilder(4096);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(working, working.Capacity);
            link.GetDescription(description, description.Capacity);
            return new ShortcutSnapshot(target.ToString(), arguments.ToString(), working.ToString(), description.ToString());
        }
        finally { ShellObjects.Release(shell); }
    }

    public static void Write(string path, string target, string arguments, string description)
    {
        PathSafety.RejectLinks(path);
        var shell = new ShellLink();
        try
        {
            var link = (IShellLinkW)shell;
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            ((IPersistFile)shell).Save(path, true);
        }
        finally { ShellObjects.Release(shell); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maximum, IntPtr findData, int flags);
        void GetIDList(out IntPtr item);
        void SetIDList(IntPtr item);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maximum);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximum);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximum);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int command);
        void SetShowCommand(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int maximum, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
