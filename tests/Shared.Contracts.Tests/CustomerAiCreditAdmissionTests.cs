using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerAiCreditAdmissionTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", 7);
    private static readonly CustomerCreditPricing Pricing = new("credits-v1", 3, 1_000_000);

    [Fact]
    public void Decide_AllowsExactCreditBoundary()
    {
        var usage = Projection(2);
        var decision = CustomerAiCreditAdmission.Decide(Request(3, 1_000_000), usage);

        Assert.True(decision.Allowed);
        Assert.Equal(1, decision.RequestedAiCredits);
        Assert.Equal(3, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_DeniesWhenProjectedCreditsExceedLimit()
    {
        var decision = CustomerAiCreditAdmission.Decide(Request(2, 1), Projection(2));

        Assert.False(decision.Allowed);
        Assert.Equal(1, decision.RequestedAiCredits);
        Assert.Equal(3, decision.ProjectedAiCredits);
    }

    [Fact]
    public void Decide_ZeroRequestConsumesNoAdditionalCredits()
    {
        var decision = CustomerAiCreditAdmission.Decide(Request(2, 0), Projection(2));

        Assert.True(decision.Allowed);
        Assert.Equal(0, decision.RequestedAiCredits);
        Assert.Equal(2, decision.ProjectedAiCredits);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1_000_000, 1)]
    [InlineData(1_000_001, 2)]
    public void Decide_RoundsRequestedBudgetUp(long tokens, long credits)
    {
        var decision = CustomerAiCreditAdmission.Decide(Request(10, tokens), Projection(0));
        Assert.Equal(credits, decision.RequestedAiCredits);
    }

    [Fact]
    public void Decide_RejectsStaleAuthorityProjection()
    {
        var usage = Projection(1) with { Authority = Authority with { AuthorityVersion = 6 } };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditAdmission.Decide(Request(10, 1), usage));
    }

    [Fact]
    public void Decide_RejectsCrossCompanyProjection()
    {
        var usage = Projection(1) with { Authority = Authority with { CompanyId = "company-b" } };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditAdmission.Decide(Request(10, 1), usage));
    }

    [Fact]
    public void Decide_RejectsPricingPolicyMismatch()
    {
        var usage = Projection(1) with { PricingPolicyVersion = 2 };
        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditAdmission.Decide(Request(10, 1), usage));
    }

    [Fact]
    public void Decide_FailsClosedOnProjectedCreditOverflow()
    {
        Assert.Throws<OverflowException>(() =>
            CustomerAiCreditAdmission.Decide(Request(long.MaxValue, 1), Projection(long.MaxValue)));
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

        var first = CustomerAiCreditAdmission.Decide(Request(2, 1), usage);
        var second = CustomerAiCreditAdmission.Decide(Request(2, 1), usage);

        Assert.Equal(first, second);
        Assert.True(first.Allowed);
        Assert.Equal(2, first.ProjectedAiCredits);
    }

    private static CustomerAiCreditAdmissionRequest Request(long limit, long requestedTokens) =>
        new(Authority, Pricing, new CustomerAiCreditAllowance(limit), requestedTokens);

    private static CustomerAiCreditProjection Projection(long usedCredits) =>
        new(Authority, Pricing.PolicyId, Pricing.PolicyVersion, usedCredits * Pricing.TokenEquivalentPerCredit, usedCredits, []);

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
