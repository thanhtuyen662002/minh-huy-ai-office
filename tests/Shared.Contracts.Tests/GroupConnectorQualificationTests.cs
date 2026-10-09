using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class GroupConnectorQualificationTests
{
    private readonly Guid tenant = Guid.NewGuid(), company = Guid.NewGuid(), account = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly GroupConnectorArtifact Artifact = new("zca-js", "2.2.0", "d22c28fcabd70375c144980e9e310c38f8142590");
    private static GroupConnectorObservation[] Observations(GroupConnectorSupport support = GroupConnectorSupport.Supported) =>
        Enum.GetValues<GroupConnectorCapability>().Select(x => new GroupConnectorObservation(x, support,
            support == GroupConnectorSupport.Unverified ? Guid.Empty : Guid.NewGuid(), Now)).ToArray();
    private GroupConnectorQualification Qualification(GroupQualificationEnvironment environment = GroupQualificationEnvironment.ControlledAccount,
        IReadOnlyList<GroupConnectorObservation>? observations = null) => new(tenant, company, account, "opaque-account ", Artifact, environment, observations ?? Observations());
    private bool Allows(GroupConnectorQualification qualification, GroupConnectorProfile profile = GroupConnectorProfile.Receive,
        DateTimeOffset? now = null) => qualification.AllowsLiveProfile(profile, tenant, company, account, "opaque-account ", Artifact, now ?? Now);

    [Theory]
    [InlineData(GroupConnectorProfile.Receive)]
    [InlineData(GroupConnectorProfile.Send)]
    public void Synthetic_and_documentation_metadata_never_enable_live_profiles(GroupConnectorProfile profile)
    {
        Assert.False(Allows(Qualification(GroupQualificationEnvironment.Synthetic), profile));
        Assert.False(Allows(Qualification(observations: Observations(GroupConnectorSupport.Unverified)), profile));
        Assert.True(Allows(Qualification(), profile));
    }

    [Theory]
    [InlineData(GroupConnectorCapability.GroupTextReceive)]
    [InlineData(GroupConnectorCapability.MessageIdentity)]
    [InlineData(GroupConnectorCapability.SenderIdentity)]
    [InlineData(GroupConnectorCapability.SelfOriginCorrelation)]
    [InlineData(GroupConnectorCapability.ListenerCollisionDetection)]
    [InlineData(GroupConnectorCapability.GapDetection)]
    [InlineData(GroupConnectorCapability.MembershipVerification)]
    public void Every_required_receive_capability_must_be_observed_supported(GroupConnectorCapability capability)
    {
        var observations = Observations();
        var index = Array.FindIndex(observations, x => x.Capability == capability);
        observations[index] = observations[index] with { Support = GroupConnectorSupport.Unsupported };
        Assert.False(Allows(Qualification(observations: observations)));
        Assert.False(Allows(Qualification(observations: observations.Where(x => x.Capability != capability).ToArray())));
    }

    [Theory]
    [InlineData(GroupConnectorCapability.GroupTextSend)]
    [InlineData(GroupConnectorCapability.MembershipVerification)]
    [InlineData(GroupConnectorCapability.ProviderAcceptanceReference)]
    [InlineData(GroupConnectorCapability.UnknownSendReconciliation)]
    public void Every_required_send_capability_must_be_observed_supported(GroupConnectorCapability capability)
    {
        var observations = Observations();
        var index = Array.FindIndex(observations, x => x.Capability == capability);
        observations[index] = observations[index] with { Support = GroupConnectorSupport.Unverified, EvidenceId = Guid.Empty };
        Assert.False(Allows(Qualification(observations: observations), GroupConnectorProfile.Send));
    }

    [Fact]
    public void Optional_unverified_edits_recall_and_history_do_not_fabricate_support()
    {
        var observations = Observations().Where(x => x.Capability is not GroupConnectorCapability.EditEvents
            and not GroupConnectorCapability.RecallEvents and not GroupConnectorCapability.ReplyIdentity).ToArray();
        Assert.True(Allows(Qualification(observations: observations)));
        Assert.DoesNotContain(Qualification(observations: observations).Observations, x => x.Capability == GroupConnectorCapability.EditEvents);
    }

    [Fact]
    public void Qualification_is_bound_to_exact_account_scope_and_artifact()
    {
        var qualification = Qualification();
        Assert.False(qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, Guid.NewGuid(), company, account, "opaque-account ", Artifact, Now));
        Assert.False(qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, tenant, Guid.NewGuid(), account, "opaque-account ", Artifact, Now));
        Assert.False(qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, tenant, company, Guid.NewGuid(), "opaque-account ", Artifact, Now));
        Assert.False(qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, tenant, company, account, "opaque-account", Artifact, Now));
        Assert.False(qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, tenant, company, account, "Opaque-account ", Artifact, Now));
        Assert.False(qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, tenant, company, account, "opaque-account ", Artifact with { PackageVersion = "2.2.1" }, Now));
        Assert.False(qualification.AllowsLiveProfile(GroupConnectorProfile.Receive, tenant, company, account, "opaque-account ", Artifact with { GitCommit = new string('a', 40) }, Now));
    }

    [Fact]
    public void Future_or_stale_evidence_cannot_qualify_an_account()
    {
        Assert.False(Allows(Qualification(), now: Now.AddTicks(-1)));
        Assert.True(Allows(Qualification(), now: Now.AddDays(30)));
        Assert.False(Allows(Qualification(), now: Now.AddDays(30).AddTicks(1)));
    }

    [Fact]
    public void Observed_support_requires_evidence_and_cannot_have_duplicate_capabilities()
    {
        var observation = Observations()[0];
        Assert.Throws<InvalidOperationException>(() => Qualification(observations: [observation with { EvidenceId = Guid.Empty }]));
        Assert.Throws<InvalidOperationException>(() => Qualification(observations: [observation, observation]));
        Assert.Throws<InvalidOperationException>(() => Qualification(observations: [observation with { Capability = (GroupConnectorCapability)999 }]));
        Assert.Throws<InvalidOperationException>(() => Qualification(observations: [observation with { ObservedAtUtc = Now.ToOffset(TimeSpan.FromHours(7)) }]));
        Assert.Throws<InvalidOperationException>(() => Qualification(observations: [observation with { Support = GroupConnectorSupport.Unverified }]));
    }

    [Fact]
    public void Frozen_observations_cannot_be_replaced_after_qualification()
    {
        var observations = Observations();
        var qualification = Qualification(observations: observations);
        observations[0] = observations[0] with { Support = GroupConnectorSupport.Unsupported };
        Assert.True(Allows(qualification));
        Assert.Throws<NotSupportedException>(() => ((IList<GroupConnectorObservation>)qualification.Observations).Clear());
    }
}
