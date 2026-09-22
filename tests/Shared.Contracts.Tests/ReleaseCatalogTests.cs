using MinhHuy.AIOffice.Shared.Contracts.Releases;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class ReleaseCatalogTests
{
    [Fact]
    public void Manifest_RequiresEveryAttributionDimension()
    {
        Assert.Throws<ArgumentException>(() => (Manifest() with { SourceCommit = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (Manifest() with { SchemaContractVersion = " schema-v1" }).Validate());
        Assert.Throws<ArgumentException>(() => (Manifest() with { WorkflowVersion = "" }).Validate());
        Assert.Throws<ArgumentException>(() => (Manifest() with { SkillVersion = " " }).Validate());
    }

    [Fact]
    public void TaskPin_PreservesReleaseAndTenantCompanyAuthority()
    {
        var pin = TaskReleasePin.Create("tenant", "company", "task-1", Manifest());

        Assert.Equal("release-2", pin.ReleaseId);
        pin.AssertAuthority("tenant", "company");
        Assert.Throws<InvalidOperationException>(() => pin.AssertAuthority("tenant", "other-company"));
    }

    [Fact]
    public void Rollout_RequiresReleaseGateBeforeCanary()
    {
        var rollout = ReleaseRollout.Register(Manifest(), "release-1");

        Assert.Throws<InvalidOperationException>(() => rollout.StartCanary(false));
        Assert.Equal(ReleaseRolloutStage.Canary, rollout.StartCanary(true).Stage);
    }

    [Fact]
    public void Rollout_ProgressesCanaryPromoteDrainStableDeterministically()
    {
        var rollout = ReleaseRollout.Register(Manifest(), "release-1")
            .StartCanary(true)
            .Promote()
            .BeginPreviousDrain()
            .CompleteDrain();

        Assert.Equal(ReleaseRolloutStage.Stable, rollout.Stage);
        Assert.Throws<InvalidOperationException>(() => rollout.Promote());
    }

    [Fact]
    public void Rollback_AllowsOnlyExactPreviousReleaseDuringActiveRollout()
    {
        var rollout = ReleaseRollout.Register(Manifest(), "release-1").StartCanary(true);

        Assert.Throws<InvalidOperationException>(() => rollout.Rollback("release-0"));
        Assert.Equal(ReleaseRolloutStage.RolledBack, rollout.Rollback("release-1").Stage);
    }

    [Fact]
    public void FirstRelease_BecomesStableWithoutInventingPreviousDrain()
    {
        var rollout = ReleaseRollout.Register(Manifest(), null)
            .StartCanary(true)
            .Promote()
            .BeginPreviousDrain();

        Assert.Equal(ReleaseRolloutStage.Stable, rollout.Stage);
    }

    private static ReleaseManifest Manifest() =>
        new("release-2", "abc123", "api-v2", "schema-v3", "workflow-v4", "skill-v5");
}
