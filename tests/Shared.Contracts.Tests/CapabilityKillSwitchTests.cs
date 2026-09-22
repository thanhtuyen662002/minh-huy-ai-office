using MinhHuy.AIOffice.Shared.Contracts.Releases;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class CapabilityKillSwitchTests
{
    [Fact]
    public void Isolation_DeniesOnlyScopedCapabilityDispatch()
    {
        var isolation = CapabilityIsolation.Enabled("tenant", "company-a", "posting")
            .Isolate("tenant", "company-a", "incident-1", "release-bad");

        Assert.Equal(CapabilityIsolationState.Isolated, isolation.State);
        Assert.Throws<InvalidOperationException>(() => isolation.AssertDispatchAllowed("tenant", "company-a"));

        var unrelated = CapabilityIsolation.Enabled("tenant", "company-a", "reporting");
        unrelated.AssertDispatchAllowed("tenant", "company-a");
    }

    [Fact]
    public void Isolation_FailsClosedAcrossCompanyAuthority()
    {
        var isolation = CapabilityIsolation.Enabled("tenant", "company-a", "posting");

        Assert.Throws<InvalidOperationException>(() =>
            isolation.Isolate("tenant", "company-b", "incident-1", "release-bad"));
    }

    [Fact]
    public void Resume_RequiresMatchingIncidentGreenGatesAndDifferentRelease()
    {
        var isolation = CapabilityIsolation.Enabled("tenant", "company", "posting")
            .Isolate("tenant", "company", "incident-1", "release-bad");

        Assert.Throws<InvalidOperationException>(() =>
            isolation.ResumeAfterHotfix("tenant", "company", "incident-other", Manifest("release-good"), true));
        Assert.Throws<InvalidOperationException>(() =>
            isolation.ResumeAfterHotfix("tenant", "company", "incident-1", Manifest("release-good"), false));
        Assert.Throws<InvalidOperationException>(() =>
            isolation.ResumeAfterHotfix("tenant", "company", "incident-1", Manifest("release-bad"), true));

        var resumed = isolation.ResumeAfterHotfix("tenant", "company", "incident-1", Manifest("release-good"), true);
        Assert.Equal(CapabilityIsolationState.Enabled, resumed.State);
        resumed.AssertDispatchAllowed("tenant", "company");
    }

    [Fact]
    public void WaitingTaskReleasePin_RemainsUnchangedDuringIsolation()
    {
        var pin = TaskReleasePin.Create("tenant", "company", "task-1", Manifest("release-bad"));
        var isolation = CapabilityIsolation.Enabled("tenant", "company", "posting")
            .Isolate("tenant", "company", "incident-1", "release-bad");

        Assert.Equal("release-bad", pin.ReleaseId);
        Assert.Equal(CapabilityIsolationState.Isolated, isolation.State);
        pin.AssertAuthority("tenant", "company");
    }

    private static ReleaseManifest Manifest(string releaseId) =>
        new(releaseId, "abc123", "api-v2", "schema-v3", "workflow-v4", "skill-v5");
}
