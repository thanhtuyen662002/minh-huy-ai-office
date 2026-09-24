using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerAiCreditAdmissionTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", 7);
    private static readonly CustomerCreditPricing Pricing = new("credits-v1", 3, 1_000_000);

    [Fact]
    public void Decide_AllowsExactCreditBoundaryIncludingActiveHolds()
    {
        var decision = CustomerAiCreditAdmission.Decide(Request(4, 1_000_000), Projection(2), Active(1));
        Assert.True(decision.Allowed);
        Assert.Equal(1, decision.ActiveReservedAiCredits);
        Assert.Equal(1, decision.RequestedAiCredits);
        Assert.Equal(4, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_DeniesWhenActiveHoldConsumesRemainingCapacity()
    {
        var decision = CustomerAiCreditAdmission.Decide(Request(3, 1), Projection(2), Active(1));
        Assert.False(decision.Allowed);
        Assert.Equal(2, decision.UsedAiCredits);
        Assert.Equal(1, decision.ActiveReservedAiCredits);
        Assert.Equal(1, decision.RequestedAiCredits);
        Assert.Equal(4, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_ZeroActiveHoldsPreserveAdmissionSemantics()
    {
        var decision = CustomerAiCreditAdmission.Decide(Request(3, 1_000_000), Projection(2), Active(0));
        Assert.True(decision.Allowed);
        Assert.Equal(0, decision.ActiveReservedAiCredits);
        Assert.Equal(3, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_ZeroRequestConsumesNoAdditionalCredits()
    {
        var decision = CustomerAiCreditAdmission.Decide(Request(3, 0), Projection(2), Active(1));
        Assert.True(decision.Allowed);
        Assert.Equal(0, decision.RequestedAiCredits);
        Assert.Equal(3, decision.ProjectedAiCredits);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1_000_000, 1)]
    [InlineData(1_000_001, 2)]
    public void Decide_RoundsRequestedBudgetUp(long tokens, long credits)
    {
        var decision = CustomerAiCreditAdmission.Decide(Request(10, tokens), Projection(0), Active(0));
        Assert.Equal(credits, decision.RequestedAiCredits);
    }

    [Fact]
    public void Decide_RejectsStaleAndCrossCompanyProjections()
    {
        var staleUsage = Projection(1) with { Authority = Authority with { AuthorityVersion = 6 } };
        var staleActive = Active(1) with { Authority = Authority with { AuthorityVersion = 6 } };
        var foreignActive = Active(1) with { Authority = Authority with { CompanyId = "company-b" } };

        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditAdmission.Decide(Request(10, 1), staleUsage, Active(0)));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditAdmission.Decide(Request(10, 1), Projection(1), staleActive));
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditAdmission.Decide(Request(10, 1), Projection(1), foreignActive));
    }

    [Fact]
    public void Decide_RejectsPricingPolicyMismatchAcrossEitherProjection()
    {
        var staleUsage = Projection(1) with { PricingPolicyVersion = 2 };
        var staleActive = Active(1) with { PricingPolicyVersion = 2 };

        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditAdmission.Decide(Request(10, 1), staleUsage, Active(0)));
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditAdmission.Decide(Request(10, 1), Projection(1), staleActive));
    }

    [Fact]
    public void Decide_FailsClosedOnCommittedOrProjectedCreditOverflow()
    {
        var maxUsage = new CustomerAiCreditProjection(
            Authority, Pricing.PolicyId, Pricing.PolicyVersion, 0, long.MaxValue, []);

        Assert.Throws<OverflowException>(() => CustomerAiCreditAdmission.Decide(Request(long.MaxValue, 0), maxUsage, Active(1)));
        Assert.Throws<OverflowException>(() => CustomerAiCreditAdmission.Decide(Request(long.MaxValue, 1), Projection(long.MaxValue), Active(0)));
    }

    [Fact]
    public void Decide_RejectsNegativeRequestedBudgetLimitAndActiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CustomerAiCreditAdmission.Decide(Request(1, -1), Projection(0), Active(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CustomerAiCreditAdmission.Decide(Request(-1, 0), Projection(0), Active(0)));
        Assert.Throws<ArgumentException>(() => CustomerAiCreditAdmission.Decide(Request(1, 0), Projection(0), Active(-1)));
    }

    [Fact]
    public void Decide_IsDeterministicAndProviderNeutral()
    {
        var evidence = new[]
        {
            new CustomerUsageEvidence(7, Usage("usage-a", 500_000, "provider-cheap", "model-a", 0.01m)),
            new CustomerUsageEvidence(7, Usage("usage-b", 500_000, "provider-expensive", "model-z", 99m))
        };
        var usage = CustomerAiCreditUsage.Project(Authority, Pricing, evidence);

        var first = CustomerAiCreditAdmission.Decide(Request(3, 1), usage, Active(1));
        var second = CustomerAiCreditAdmission.Decide(Request(3, 1), usage, Active(1));

        Assert.Equal(first, second);
        Assert.True(first.Allowed);
        Assert.Equal(3, first.ProjectedAiCredits);
    }

    private static CustomerAiCreditAdmissionRequest Request(long limit, long requestedTokens) =>
        new(Authority, Pricing, new CustomerAiCreditAllowance(limit), requestedTokens);

    private static CustomerAiCreditProjection Projection(long usedCredits) =>
        new(Authority, Pricing.PolicyId, Pricing.PolicyVersion, checked(usedCredits * Pricing.TokenEquivalentPerCredit), usedCredits, []);

    private static CustomerAiCreditActiveReservationProjection Active(long reservedCredits) =>
        new(Authority, Pricing.PolicyId, Pricing.PolicyVersion, [], reservedCredits);

    private static AiUsageEntry Usage(string id, long tokens, string provider, string model, decimal cost) =>
        new(
            id,
            new AiUsageScope("tenant-a", "company-a", "task-a", "agent-a", "request-a"),
            provider,
            model,
            AiCapability.Reasoning,
            new AiUsageAmount(tokens, 0, tokens, cost),
            DateTimeOffset.UnixEpoch);
}
