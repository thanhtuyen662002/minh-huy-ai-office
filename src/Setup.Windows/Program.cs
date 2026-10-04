using System.Text.Json;

namespace Setup.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] == "--verify-bundle")
        {
            try
            {
                var bundle = new EmbeddedBundle();
                var root = Path.Combine(Path.GetTempPath(), "aioffice-bundle-verify-" + Guid.NewGuid().ToString("N"));
                bundle.Verify(root);
                File.WriteAllText(arguments[1], JsonSerializer.Serialize(new { verified = true, revision = bundle.Revision, sha256 = bundle.Sha256 }));
                return 0;
            }
            catch { return 20; }
        }
        if (arguments.Length == 2 && arguments[0] == "--verify-windows-integration")
        {
            try { ApplicationConfiguration.Initialize(); WindowsIntegrationVerification.Run(arguments[1]); return 0; }
            catch (Exception error)
            {
                try
                {
                    File.WriteAllText(arguments[1], JsonSerializer.Serialize(new
                    {
                        verified = false,
                        installationVerified = false,
                        failure = error is VerificationFailure verification ? verification.Code : error.GetType().Name,
                        argument = error.InnerException is ArgumentException argument ? argument.ParamName : null,
                        methods = new System.Diagnostics.StackTrace(error.InnerException ?? error, false).GetFrames()
                            .Select(value => value.GetMethod()).Where(value => value?.DeclaringType?.Namespace is "Setup.Windows" or "System.Runtime.InteropServices")
                            .Select(value => value!.DeclaringType!.Name + "." + value.Name).Take(4).ToArray()
                    }));
                }
                catch { }
                return 22;
            }
        }
        if (arguments.Length == 2 && arguments[0] == "--preview-ui")
        {
            try
            {
                ApplicationConfiguration.Initialize();
                SetupForm.RenderPreviews(arguments[1]);
                return 0;
            }
            catch { return 23; }
        }
        if (arguments.Contains("--install-prerequisites", StringComparer.Ordinal))
        {
            try { return new WindowsPrerequisites().InstallElevatedAsync(arguments.Contains("--docker-license-accepted", StringComparer.Ordinal)).GetAwaiter().GetResult(); }
            catch { return 21; }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(arguments));
        return 0;
    }
}
