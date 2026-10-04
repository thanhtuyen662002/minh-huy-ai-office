using System.Security.Cryptography;
using Setup.Core;

namespace Setup.Windows;

internal static class CachedInstaller
{
    public static string Ensure(string source, string destination, bool repair)
    {
        PathSafety.RejectLinks(source);
        PathSafety.RejectLinks(destination);
        var matches = false;
        if (File.Exists(destination))
        {
            using (var existing = File.OpenRead(destination))
            using (var original = File.OpenRead(source))
                matches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(original));
            if (!matches)
            {
                if (!repair) throw new SetupFailure("Bộ cài lưu trên máy đã thay đổi. Chọn Sửa chữa để lưu lại bản hỏng và khôi phục bộ cài.",
                    SetupFailureCode.BundleIntegrity);
                InstallationAccess.ProtectPrivateFile(destination);
                File.Move(destination, destination + ".retained-" + Guid.NewGuid().ToString("N"), overwrite: false);
            }
        }
        if (!matches)
        {
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".setup-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, destination, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        InstallationAccess.ProtectInstallerFile(destination);
        return destination;
    }
}
