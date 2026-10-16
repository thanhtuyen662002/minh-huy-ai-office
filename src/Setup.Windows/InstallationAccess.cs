using System.Security.AccessControl;
using System.Security.Principal;
using Setup.Core;

namespace Setup.Windows;

internal static class InstallationAccess
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void ProtectPrivateRoot(string root) =>
        ProtectDirectory(root, FileSystemRights.Traverse, InheritanceFlags.None);

    public static void ProtectInstallerDirectory(string root, string installerDirectory)
    {
        var installers = Path.Combine(root, "Installers");
        PathSafety.RejectLinks(installerDirectory);
        if (!Path.GetFullPath(installerDirectory).StartsWith(
                Path.GetFullPath(installers) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The helper must remain inside the installation's Installers directory.");
        ProtectDirectory(installers, FileSystemRights.Traverse, InheritanceFlags.None);
        ProtectDirectory(installerDirectory, FileSystemRights.Traverse, InheritanceFlags.None);
    }

    public static void ProtectInstallerFile(string path) => ProtectFile(path, administratorRead: true);

    public static void ProtectPrivateFile(string path) => ProtectFile(path, administratorRead: false);

    private static void ProtectFile(string path, bool administratorRead)
    {
        PathSafety.RejectLinks(path);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new SetupFailure("Không xác định được tài khoản cài đặt.", SetupFailureCode.Configuration);
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        if (administratorRead)
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
    private static void ProtectDirectory(string path, FileSystemRights administratorRights,
        InheritanceFlags administratorInheritance)
    {
        PathSafety.RejectLinks(path);
        Directory.CreateDirectory(path);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ??
            throw new SetupFailure("Không xác định được tài khoản cài đặt.", SetupFailureCode.Configuration);
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, administratorRights,
            administratorInheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
