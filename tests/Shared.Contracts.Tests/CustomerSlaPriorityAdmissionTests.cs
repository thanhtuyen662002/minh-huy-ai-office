using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerSlaPriorityAdmissionTests
{
    private static readonly CustomerSlaAuthority Authority = new("tenant-a", "company-a", "user-a", 7);
    private static readonly CustomerSlaPolicy Policy = new(
        "sla-standard",
        3,
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["standard"] = 20,
            ["urgent"] = 80,
        },
        80);

    [Fact]
    public void Decide_MapsAllowedServiceClassToAuthoritativePriority()
    {
        var request = new CustomerSlaPriorityAdmissionRequest("admission-1", Authority, Policy, "urgent");

        var result = CustomerSlaPriorityAdmission.Decide(request);

        Assert.Equal(80, result.SchedulerPriority);
        Assert.Equal(80, result.PriorityCeiling);
        Assert.Equal(Authority, result.Authority);
        Assert.Equal("sla-standard", result.PolicyId);
        Assert.Equal(3, result.PolicyVersion);
    }

    [Fact]
    public void Decide_RejectsUnknownServiceClass()
    {
        var request = new CustomerSlaPriorityAdmissionRequest("admission-2", Authority, Policy, "vip");

        Assert.Throws<UnauthorizedAccessException>(() => CustomerSlaPriorityAdmission.Decide(request));
    }

    [Fact]
    public void Decide_RejectsPriorityAbovePolicyCeiling()
    {
        var invalid = Policy with
        {
            ServiceClassPriorities = new Dictionary<string, int> { ["urgent"] = 81 },
        };
        var request = new CustomerSlaPriorityAdmissionRequest("admission-3", Authority, invalid, "urgent");

        Assert.Throws<InvalidOperationException>(() => CustomerSlaPriorityAdmission.Decide(request));
    }

    [Theory]
    [InlineData("tenant-b", "company-a", "user-a", 7)]
    [InlineData("tenant-a", "company-b", "user-a", 7)]
    [InlineData("tenant-a", "company-a", "user-b", 7)]
    [InlineData("tenant-a", "company-a", "user-a", 0)]
    public void Decide_RejectsConflictingOrInvalidAuthority(string tenant, string company, string user, long version)
    {
        var request = new CustomerSlaPriorityAdmissionRequest(
            "admission-4",
            new CustomerSlaAuthority(tenant, company, user, version),
            Policy,
            "standard");
        var existing = new CustomerSlaPriorityAdmissionEvidence(
            "admission-4", Authority, Policy.PolicyId, Policy.PolicyVersion, "standard", 20)
        {
            PriorityCeiling = Policy.PriorityCeiling,
        };

        Assert.ThrowsAny<Exception>(() => CustomerSlaPriorityAdmission.Decide(request, existing));
    }

    [Fact]
    public void Decide_ExactReplayIsIdempotent()
    {
        var request = new CustomerSlaPriorityAdmissionRequest("admission-5", Authority, Policy, "standard");
        var first = CustomerSlaPriorityAdmission.Decide(request);

        var replay = CustomerSlaPriorityAdmission.Decide(request, first);

        Assert.Same(first, replay);
    }

    [Fact]
    public void Decide_RejectsStalePolicyReplay()
    {
        var request = new CustomerSlaPriorityAdmissionRequest("admission-6", Authority, Policy, "standard");
        var stale = new CustomerSlaPriorityAdmissionEvidence(
            "admission-6", Authority, Policy.PolicyId, Policy.PolicyVersion - 1, "standard", 20)
        {
            PriorityCeiling = Policy.PriorityCeiling,
        };

        Assert.Throws<InvalidOperationException>(() => CustomerSlaPriorityAdmission.Decide(request, stale));
    }

    [Fact]
    public void Decide_RejectsReplayWhenAuthoritativeCeilingChanged()
    {
        var request = new CustomerSlaPriorityAdmissionRequest("admission-7", Authority, Policy, "standard");
        var existing = CustomerSlaPriorityAdmission.Decide(request);
        var changedPolicy = Policy with { PriorityCeiling = 90 };
        var changedRequest = request with { Policy = changedPolicy };

        Assert.Throws<InvalidOperationException>(() => CustomerSlaPriorityAdmission.Decide(changedRequest, existing));
    }
}
