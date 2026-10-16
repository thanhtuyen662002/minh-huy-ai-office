using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Setup.Core;

namespace Setup.Windows;

internal static class WindowsIntegrationVerification
{
    private const string ApplicationName = "MinhHuy AI Office";
    private const string ResumeName = "MinhHuy AI Office Resume.lnk";
    private static string step = "initial";
    private const string FixtureSecret = "synthetic-verification-secret-never-a-real-credential";
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void Run(string proofPath)
    {
        PathSafety.RejectLinks(proofPath);
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.Combine(temporary, "aioffice-windows-verify-" + Guid.NewGuid().ToString("N") + " đường dẫn");
        var checks = new List<string>();
        step = "private-root";
        try
        {
            InstallationAccess.ProtectPrivateRoot(root);
            step = "private-fixture";
            var runtime = Path.Combine(root, "LocalRuntime");
            var versions = Path.Combine(root, "Versions");
            Directory.CreateDirectory(runtime);
            Directory.CreateDirectory(versions);
            var configuration = Path.Combine(runtime, "installation.json");
            var sourceConfiguration = Path.Combine(versions, "private-fixture.env");
            var volume = Path.Combine(runtime, "volume-fixture.bin");
            File.WriteAllText(configuration, JsonSerializer.Serialize(new { owner = FixtureSecret, roles = new[] { "owner", "staff" }, installationId = "retained-fixture" }));
            File.WriteAllText(sourceConfiguration, "SECRET=" + FixtureSecret);
            File.WriteAllBytes(volume, [1, 2, 3, 5, 8, 13]);
            var before = new[] { configuration, sourceConfiguration, volume }.Select(Hash).ToArray();

            step = "helper-acl";
            var current = Path.Combine(root, "Installers", new string('a', 40));
            var previous = Path.Combine(root, "Installers", new string('b', 40));
            var helper = Path.Combine(current, "Setup.exe");
            var priorHelper = Path.Combine(previous, "Setup.exe");
            InstallationAccess.ProtectInstallerDirectory(root, current);
            InstallationAccess.ProtectInstallerDirectory(root, previous);
            File.Copy(Environment.ProcessPath!, helper);
            File.Copy(Environment.ProcessPath!, priorHelper);
            InstallationAccess.ProtectInstallerFile(helper);
            InstallationAccess.ProtectInstallerFile(priorHelper);
            step = "acl-read";
            Require((Rights(helper, Administrators) & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute, "administrator-helper-read-execute");
            Require((Rights(root, Administrators) & FileSystemRights.Traverse) != 0, "administrator-root-traverse");
            Require((Rights(Path.Combine(root, "Installers"), Administrators) & FileSystemRights.Traverse) != 0, "administrator-installers-traverse");
            var privateRead = FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes;
            foreach (var path in new[] { root, runtime, versions, configuration, sourceConfiguration, volume })
                Require((Rights(path, Administrators) & privateRead) == 0, "no-administrator-secret-read-grant");
            using var identity = WindowsIdentity.GetCurrent();
            foreach (var path in new[] { root, runtime, configuration, sourceConfiguration, volume, helper })
                Require((Rights(path, identity.User!) & FileSystemRights.FullControl) == FileSystemRights.FullControl, "installing-user-retains-access");
            checks.AddRange(["actual-helper-acl", "administrator-traverse-only", "private-config-source-acl", "installing-user-full-control"]);

            step = "cached-installer";
            VerifyCacheRepair(helper, checks);
            step = "shortcuts";
            VerifyShortcuts(root, helper, priorHelper, checks);
            Require(before.SequenceEqual(new[] { configuration, sourceConfiguration, volume }.Select(Hash)), "configuration-identity-role-volume-fixtures-retained");
            checks.Add("configuration-identity-role-volume-fixtures-retained");
            step = "diagnostic";
            VerifyDiagnostic(root, checks);
            step = "failure-form";
            SetupForm.VerifyFailureControls(Path.Combine(root, "form-diagnostic.json"));
            checks.Add("failure-form-export-enabled");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(proofPath))!);
            File.WriteAllText(proofPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                verified = true,
                isolated = true,
                usedRealStartupFolders = false,
                privilegedOperations = false,
                installationVerified = false,
                alternateAdminLoginVerified = false,
                checks
            }));
        }
        catch (Exception error) when (error is not VerificationFailure)
        {
            throw new VerificationFailure(step + "-" + error.GetType().Name, error);
        }
        finally
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(root));
            if (!string.Equals(parent, temporary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Verification cleanup escaped its generated temporary directory.");
            PathSafety.RejectLinks(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private static void VerifyCacheRepair(string helper, List<string> checks)
    {
        var source = Environment.ProcessPath!;
        var original = Hash(source);
        var folder = Path.GetDirectoryName(helper)!;
        File.WriteAllText(helper, FixtureSecret + "-damaged-installer");
        var damaged = Hash(helper);
        var rejected = false;
        step = "cache-reject-damaged";
        try { CachedInstaller.Ensure(source, helper, repair: false); }
        catch (SetupFailure error) when (error.Code == SetupFailureCode.BundleIntegrity) { rejected = true; }
        Require(rejected && Hash(helper) == damaged &&
            Directory.GetFiles(folder, "Setup.exe.retained-*").Length == 0, "damaged-cache-rejected-without-repair");

        step = "cache-repair";
        Require(CachedInstaller.Ensure(source, helper, repair: true) == helper &&
            Hash(helper) == original, "repair-restores-helper");
        var retained = Directory.GetFiles(folder, "Setup.exe.retained-*");
        Require(retained.Length == 1 && Hash(retained[0]) == damaged, "repair-retains-damaged-helper");
        using var identity = WindowsIdentity.GetCurrent();
        Require(new FileInfo(retained[0]).GetAccessControl().AreAccessRulesProtected &&
            Rights(retained[0], Administrators) == 0 &&
            (Rights(retained[0], identity.User!) & FileSystemRights.FullControl) == FileSystemRights.FullControl,
            "quarantined-helper-private-acl");
        Require((Rights(helper, Administrators) & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute,
            "repaired-helper-administrator-read-execute");

        step = "cache-repeat-repair";
        CachedInstaller.Ensure(source, helper, repair: true);
        Require(Hash(helper) == original && Hash(retained[0]) == damaged &&
            Directory.GetFiles(folder, "Setup.exe.retained-*").Length == 1 &&
            Directory.GetFiles(folder, ".setup-*.tmp").Length == 0,
            "repeat-cache-repair-preserves-quarantine");
        checks.AddRange(["damaged-cache-rejected-without-repair", "repair-restores-helper",
            "repair-retains-damaged-helper", "quarantined-helper-private-acl",
            "repeat-cache-repair-preserves-quarantine"]);
    }
    private static void VerifyShortcuts(string root, string helper, string priorHelper, List<string> checks)
    {
        var startup = Path.Combine(root, "Synthetic Startup");
        var programs = Path.Combine(root, "Synthetic Programs");
        Directory.CreateDirectory(startup);
        Directory.CreateDirectory(programs);
        var resume = Path.Combine(startup, ResumeName);
        step = "resume-register";
        ApplicationLinks.RegisterResume(helper, startup);
        step = "shortcut-inspect";
        VerifyShortcut(resume, helper, "--resume");
        step = "resume-remove";
        ApplicationLinks.RemoveResume(helper, startup);
        Require(!File.Exists(resume), "cancellation-removes-current-owned-continuation");

        step = "resume-register";
        ApplicationLinks.RegisterResume(priorHelper, startup);
        var oldBytes = Hash(resume);
        step = "resume-remove";
        ApplicationLinks.RemoveResume(helper, startup);
        Require(File.Exists(resume) && oldBytes == Hash(resume), "cancellation-preserves-other-revision-continuation");
        step = "resume-register";
        ApplicationLinks.RegisterResume(helper, startup);
        step = "shortcut-inspect";
        VerifyShortcut(resume, helper, "--resume");

        step = "fixture-shortcut";
        WriteFixtureShortcut(resume, helper, "--start", ApplicationName);
        var wrongArguments = Hash(resume);
        step = "resume-remove";
        ApplicationLinks.RemoveResume(helper, startup);
        Require(wrongArguments == Hash(resume), "cancellation-preserves-different-command");
        RequireConflict(() => ApplicationLinks.RegisterResume(helper, startup));
        Require(wrongArguments == Hash(resume), "registration-preserves-conflicting-command");
        File.Delete(resume);

        var foreign = Path.Combine(root, "Other fixture application.exe");
        File.Copy(helper, foreign);
        step = "fixture-shortcut";
        WriteFixtureShortcut(resume, foreign, "--resume", "Other fixture application");
        var foreignBytes = Hash(resume);
        step = "resume-remove";
        ApplicationLinks.RemoveResume(helper, startup);
        Require(foreignBytes == Hash(resume), "cancellation-preserves-foreign-continuation");
        RequireConflict(() => ApplicationLinks.RegisterResume(helper, startup));
        Require(foreignBytes == Hash(resume), "registration-preserves-foreign-continuation");
        File.Delete(resume);
        step = "resume-register";
        ApplicationLinks.RegisterResume(helper, startup);
        step = "application-register";
        ApplicationLinks.RegisterApplication(helper, programs, startup);
        var application = Path.Combine(programs, ApplicationName, ApplicationName + ".lnk");
        var signIn = Path.Combine(startup, ApplicationName + ".lnk");
        step = "shortcut-inspect";
        VerifyShortcut(application, helper, "--start");
        step = "shortcut-inspect";
        VerifyShortcut(signIn, helper, "--start --background");
        Require(!File.Exists(resume), "ready-removes-owned-continuation");
        step = "application-register";
        ApplicationLinks.RegisterApplication(helper, programs, startup);
        step = "shortcut-inspect";
        VerifyShortcut(application, helper, "--start");
        step = "shortcut-inspect";
        VerifyShortcut(signIn, helper, "--start --background");
        VerifyStartupContinuation(root, helper, priorHelper, foreign, startup, programs, checks);
        step = "fixture-shortcut";
        WriteFixtureShortcut(application, foreign, "--start", "Other fixture application");
        var foreignApplication = Hash(application);
        RequireConflict(() => ApplicationLinks.RegisterApplication(helper, programs, startup));
        Require(foreignApplication == Hash(application), "start-menu-conflict-preserved");
        checks.AddRange(["actual-start-menu-shortcut", "actual-sign-in-shortcut", "unicode-space-paths",
            "repeat-shortcut-registration", "cancel-owned-continuation", "cancel-preserves-other-revision",
            "cancel-preserves-other-command", "cancel-preserves-foreign-shortcut", "foreign-shortcut-conflict-preserved"]);
    }

    private static void VerifyStartupContinuation(string root, string helper, string priorHelper,
        string foreign, string startup, string programs, List<string> checks)
    {
        var signIn = Path.Combine(startup, ApplicationName + ".lnk");
        var resume = Path.Combine(startup, ResumeName);
        var backup = StartupContinuation.BackupPath(helper);
        Require(string.Equals(Path.GetDirectoryName(backup), root, StringComparison.OrdinalIgnoreCase),
            "paused-sign-in-outside-startup");

        step = "startup-owned-old-revision";
        ApplicationLinks.RegisterApplication(priorHelper, programs, startup);
        var priorSignIn = Hash(signIn);
        step = "startup-suspend";
        ApplicationLinks.RegisterResume(helper, startup);
        Require(!File.Exists(signIn) && File.Exists(backup) && Hash(backup) == priorSignIn,
            "pause-owned-sign-in-during-continuation");
        VerifyShortcut(backup, priorHelper, "--start --background");
        VerifyShortcut(resume, helper, "--resume");
        step = "startup-repeat-continuation";
        ApplicationLinks.RegisterResume(helper, startup);
        Require(!File.Exists(signIn) && Hash(backup) == priorSignIn,
            "repeat-continuation-keeps-paused-sign-in");
        var currentResume = Hash(resume);
        step = "startup-cancel-other-revision";
        ApplicationLinks.RemoveResume(priorHelper, startup);
        Require(!File.Exists(signIn) && Hash(backup) == priorSignIn && Hash(resume) == currentResume,
            "cancel-keeps-paused-other-revision");

        step = "startup-cancel-owned";
        ApplicationLinks.RemoveResume(helper, startup);
        Require(!File.Exists(resume) && !File.Exists(backup) && Hash(signIn) == priorSignIn,
            "cancel-restores-owned-sign-in");
        VerifyShortcut(signIn, priorHelper, "--start --background");
        step = "startup-complete-owned";
        ApplicationLinks.RegisterResume(helper, startup);
        ApplicationLinks.RegisterApplication(helper, programs, startup);
        Require(!File.Exists(resume) && !File.Exists(backup), "ready-clears-paused-continuation");
        VerifyShortcut(signIn, helper, "--start --background");

        step = "startup-foreign-active";
        WriteFixtureShortcut(signIn, foreign, "--start --background", "Other fixture application");
        var foreignActive = Hash(signIn);
        RequireConflict(() => ApplicationLinks.RegisterResume(helper, startup));
        Require(Hash(signIn) == foreignActive && !File.Exists(backup) && !File.Exists(resume),
            "foreign-sign-in-conflict-preserved");
        File.Delete(signIn);
        ApplicationLinks.RegisterApplication(helper, programs, startup);

        step = "startup-foreign-paused";
        var ownedActive = Hash(signIn);
        WriteFixtureShortcut(backup, foreign, "--start --background", "Other fixture application");
        var foreignBackup = Hash(backup);
        RequireConflict(() => ApplicationLinks.RegisterResume(helper, startup));
        RequireConflict(() => StartupContinuation.Complete(helper));
        Require(Hash(signIn) == ownedActive && Hash(backup) == foreignBackup && !File.Exists(resume),
            "foreign-paused-sign-in-conflict-preserved");
        File.Delete(backup);

        step = "startup-conflicting-resume";
        WriteFixtureShortcut(resume, foreign, "--resume", "Other fixture application");
        var foreignResume = Hash(resume);
        RequireConflict(() => ApplicationLinks.RegisterResume(helper, startup));
        Require(Hash(signIn) == ownedActive && Hash(resume) == foreignResume && !File.Exists(backup),
            "resume-conflict-restores-owned-sign-in");
        File.Delete(resume);

        step = "startup-cancel-preserves-foreign-active";
        ApplicationLinks.RegisterResume(helper, startup);
        var pendingOwned = Hash(backup);
        WriteFixtureShortcut(signIn, foreign, "--start --background", "Other fixture application");
        foreignActive = Hash(signIn);
        ApplicationLinks.RemoveResume(helper, startup);
        Require(!File.Exists(resume) && Hash(signIn) == foreignActive && Hash(backup) == pendingOwned,
            "cancellation-preserves-foreign-active-sign-in");
        File.Delete(signIn);
        StartupContinuation.Restore(helper, startup);
        Require(!File.Exists(backup) && Hash(signIn) == ownedActive, "remaining-owned-sign-in-restored");
        ApplicationLinks.RegisterApplication(helper, programs, startup);
        checks.AddRange(["pause-owned-sign-in-during-continuation", "repeat-continuation-keeps-paused-sign-in",
            "cancel-keeps-paused-other-revision", "cancel-restores-owned-sign-in",
            "ready-clears-paused-continuation", "foreign-sign-in-conflict-preserved",
            "foreign-paused-sign-in-conflict-preserved", "resume-conflict-restores-owned-sign-in",
            "cancellation-preserves-foreign-active-sign-in"]);
    }
    private static void VerifyDiagnostic(string root, List<string> checks)
    {
        var engine = new SetupEngine();
        try
        {
            engine.InstallAsync(false, false, false, _ => { }).GetAwaiter().GetResult();
            throw new VerificationFailure("missing-license-was-accepted");
        }
        catch (SetupFailure)
        {
            Require(engine.Diagnostic.Failure == SetupFailureCode.MissingLicense &&
                engine.Diagnostic.Phase == InstallPhase.Inspecting, "missing-license-classification");
        }
        var services = RuntimeDiagnostics.Parse(JsonSerializer.Serialize(new[]
        {
            new { Service = "core-api", State = "exited", Health = "unhealthy", ExitCode = 1, Name = FixtureSecret, Command = FixtureSecret }
        }));
        var report = new SetupDiagnostic(1, new EmbeddedBundle().Revision, InstallPhase.StartingRuntime,
            SetupFailureCode.RuntimeReadiness, DateTimeOffset.UtcNow,
            new MachineDiagnostic(22631, true, true, 8UL * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024), services);
        var path = Path.Combine(root, "setup-diagnostics.json");
        DiagnosticStore.Export(path, report);
        var exported = File.ReadAllText(path);
        Require(!exported.Contains(FixtureSecret, StringComparison.Ordinal) &&
            !exported.Contains("Command", StringComparison.OrdinalIgnoreCase), "typed-export-excludes-raw-command-values");
        var roundTrip = new DiagnosticStore(root).Load()!;
        Require(roundTrip.Phase == InstallPhase.StartingRuntime && roundTrip.Failure == SetupFailureCode.RuntimeReadiness,
            "typed-export-preserves-active-phase");
        Require(roundTrip.Services.Any(value => value.Service == RuntimeService.CoreApi &&
            value.State == ServiceState.Exited && value.Health == ServiceHealth.Unhealthy && value.ExitCode == 1),
            "typed-export-retains-service-status");
        checks.AddRange(["missing-license-before-machine-operations", "typed-diagnostic-export",
            "raw-command-values-excluded", "failure-preserves-active-phase", "typed-service-state-health-exit"]);
    }
    private static FileSystemRights Rights(string path, SecurityIdentifier principal)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(value => value.IdentityReference.Equals(principal));
        var allowed = (FileSystemRights)0;
        var denied = (FileSystemRights)0;
        foreach (var rule in rules)
            if (rule.AccessControlType == AccessControlType.Allow) allowed |= rule.FileSystemRights;
            else denied |= rule.FileSystemRights;
        return allowed & ~denied;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyShortcut(string path, string target, string arguments)
    {
        var shortcut = WindowsShortcut.Read(path);
        Require(!string.IsNullOrWhiteSpace(shortcut.TargetPath), "shortcut-empty-target");
        Require(!string.IsNullOrWhiteSpace(shortcut.WorkingDirectory), "shortcut-empty-working-directory");
        Require(string.Equals(Path.GetFullPath(shortcut.TargetPath), Path.GetFullPath(target),
            StringComparison.OrdinalIgnoreCase), "shortcut-target");
        Require(shortcut.Arguments == arguments, "shortcut-arguments");
        Require(string.Equals(Path.GetFullPath(shortcut.WorkingDirectory), Path.GetDirectoryName(Path.GetFullPath(target)),
            StringComparison.OrdinalIgnoreCase), "shortcut-working-directory");
        Require(shortcut.Description == ApplicationName, "shortcut-description");
    }

    private static void WriteFixtureShortcut(string path, string target, string arguments, string description) =>
        WindowsShortcut.Write(path, target, arguments, description);
    private static void RequireConflict(Action action)
    {
        try { action(); }
        catch (SetupFailure error) when (error.Code == SetupFailureCode.ShortcutConflict) { return; }
        throw new VerificationFailure("foreign-shortcut-was-not-rejected");
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new VerificationFailure(code);
    }
}

internal sealed class VerificationFailure(string code, Exception? inner = null) : Exception(null, inner)
{
    public string Code { get; } = code;
}
