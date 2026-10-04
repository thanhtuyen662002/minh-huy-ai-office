using System.Runtime.InteropServices;

namespace Setup.Windows;

internal static class ShellObjects
{
    public static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); }
        catch (ArgumentException) { }
        catch (InvalidComObjectException) { }
    }
}
