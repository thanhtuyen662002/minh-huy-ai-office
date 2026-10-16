using Xunit;

namespace Setup.Core.Tests;

public sealed class ConfigurationRetentionPlanTests
{
    private const string Revision = "1234567890abcdef1234567890abcdef12345678";

    [Theory]
    [InlineData(1, InstallPhase.Ready)]
    [InlineData(2, InstallPhase.Ready)]
    [InlineData(1, InstallPhase.AwaitingReboot)]
    [InlineData(2, InstallPhase.PreparingRuntime)]
    public void LegacyStateRequiresOriginalConfigurationEvenWithoutAnIdentityCheckpoint(int version, InstallPhase phase)
    {
        var plan = ConfigurationRetentionPlan.Create(new InstallProgress(version, Revision, phase), false, false);
        Assert.True(plan.RequireExistingInstallation);
        Assert.Null(plan.InstallationId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnknownRetainedStateCannotBecomeAFreshInstallation(bool retainedProgress, bool existingConfiguration)
    {
        Assert.True(ConfigurationRetentionPlan.Create(null, retainedProgress, existingConfiguration).RequireExistingInstallation);
    }

    [Fact]
    public void ProvenFreshRebootCanContinueBeforeConfigurationIsCreated()
    {
        var fresh = new InstallProgress(3, Revision, InstallPhase.AwaitingReboot, true);
        Assert.False(ConfigurationRetentionPlan.Create(fresh, false, false).RequireExistingInstallation);
        Assert.False(ConfigurationRetentionPlan.Create(null, false, false).RequireExistingInstallation);
    }

    [Fact]
    public void RetentionAndIdentitySurviveFailureAndPrerequisiteReboot()
    {
        var id = Guid.NewGuid();
        var ready = new InstallProgress(3, Revision, InstallPhase.Ready, true, true, id);
        var plan = ConfigurationRetentionPlan.Create(ready, false, false);
        foreach (var phase in new[] { InstallPhase.Inspecting, InstallPhase.AwaitingReboot, InstallPhase.PreparingRuntime, InstallPhase.Failed })
        {
            var checkpoint = ready with { Phase = phase };
            Assert.Equal(plan, ConfigurationRetentionPlan.Create(checkpoint, false, false));
            Assert.Equal(id, plan.InstallationId);
            Assert.True(plan.RequireExistingInstallation);
        }
    }
}
