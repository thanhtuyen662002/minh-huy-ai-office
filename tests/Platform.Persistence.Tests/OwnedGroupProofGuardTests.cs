extern alias GroupRuntimeProof;
using Guard = GroupRuntimeProof::MinhHuy.AIOffice.GroupIntake.RuntimeProof.OwnedGroupProofGuard;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedGroupProofGuardTests
{
    [Theory]
    [InlineData("CI", "false")]
    [InlineData("GITHUB_ACTIONS", "false")]
    [InlineData("AIOFFICE_OWNED_GROUP_SPOOL_PROOF", "false")]
    [InlineData("RUNNER_TEMP", "")]
    [InlineData("RUNNER_TEMP", "relative")]
    public void UnownedEnvironmentCannotResolveAResourceRoot(string key, string value)
    {
        var env = Environment(); env[key] = value;
        Assert.Throws<InvalidOperationException>(() => Guard.RequireOwned(name => env.GetValueOrDefault(name)));
    }
    [Fact]
    public void FilesystemRootIsNeverAnOwnedFixtureRoot()
    {
        var env = Environment(); env["RUNNER_TEMP"] = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<InvalidOperationException>(() => Guard.RequireOwned(name => env.GetValueOrDefault(name)));
    }
    [Fact]
    public void OwnedEnvironmentDerivesOnlyTheFixedEphemeralChild()
    {
        var env = Environment();
        Assert.Equal(Path.Combine(Path.GetFullPath(env["RUNNER_TEMP"]), "aioffice-local", "group-spool-proof"), Guard.RequireOwned(name => env.GetValueOrDefault(name)));
    }
    private static Dictionary<string, string> Environment() => new()
    {
        ["CI"] = "true",
        ["GITHUB_ACTIONS"] = "true",
        ["AIOFFICE_OWNED_GROUP_SPOOL_PROOF"] = "true",
        ["RUNNER_TEMP"] = Path.Combine(Path.GetTempPath(), "group-guard-no-resources")
    };

    [Theory]
    [InlineData("CI", "false")]
    [InlineData("GITHUB_ACTIONS", "false")]
    [InlineData("AIOFFICE_OWNED_GROUP_SPOOL_PROOF", "false")]
    [InlineData("AIOFFICE_OWNED_GROUP_RECOVERY_PROOF", "false")]
    [InlineData("RUNNER_TEMP", "")]
    [InlineData("RUNNER_TEMP", "relative")]
    public void ManagedRecoveryHasItsOwnOptInAndCannotResolveUnownedResources(string key, string value)
    {
        var env = Environment(); env["AIOFFICE_OWNED_GROUP_RECOVERY_PROOF"] = "true"; env[key] = value;
        Assert.Throws<InvalidOperationException>(() => Guard.RequireOwnedRecovery(name => env.GetValueOrDefault(name)));
    }

    [Fact]
    public void ManagedRecoveryUsesSeparateFixedOwnedRoot()
    {
        var env = Environment(); env["AIOFFICE_OWNED_GROUP_RECOVERY_PROOF"] = "true";
        var actual = Guard.RequireOwnedRecovery(name => env.GetValueOrDefault(name));
        Assert.Equal(Path.Combine(Path.GetFullPath(env["RUNNER_TEMP"]), "aioffice-local", "group-recovery-proof"), actual);
        Assert.NotEqual(Guard.RequireOwned(name => env.GetValueOrDefault(name)), actual);
        env["RUNNER_TEMP"] = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<InvalidOperationException>(() => Guard.RequireOwnedRecovery(name => env.GetValueOrDefault(name)));
    }
}
