using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class CustomerAiCreditUsageTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", 7);
    private static readonly CustomerCreditPricing Pricing = new("credits-v1", 3, 1_000_000);

    [Fact]
    public void Project_IsDeterministicAndIdempotentForIdenticalReplay()
    {
        var first = Evidence(Usage("usage-b", 600_000, "provider-a", "model-a"));
        var second = Evidence(Usage("usage-a", 600_000, "provider-b", "model-b"));

        var result = CustomerAiCreditUsage.Project(Authority, Pricing, [first, second, first]);

        Assert.Equal(1_200_000, result.TotalTokenEquivalent);
        Assert.Equal(2, result.UsedAiCredits);
        Assert.Equal(["usage-a", "usage-b"], result.UsageEntryIds);
        Assert.Equal("credits-v1", result.PricingPolicyId);
        Assert.Equal(3, result.PricingPolicyVersion);
    }

    [Fact]
    public void Project_RejectsCrossCompanyEvidence()
    {
        var entry = Usage("usage-a", 10, "provider-a", "model-a") with
        {
            Scope = new AiUsageScope("tenant-a", "company-b", "task-a", "agent-a", "request-a")
        };

        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditUsage.Project(Authority, Pricing, [Evidence(entry)]));
    }

    [Fact]
    public void Project_RejectsStaleAuthorityVersionEvidence()
    {
        var evidence = Evidence(Usage("usage-a", 10, "provider-a", "model-a")) with { AuthorityVersion = 6 };
        Assert.Throws<UnauthorizedAccessException>(() => CustomerAiCreditUsage.Project(Authority, Pricing, [evidence]));
    }

    [Fact]
    public void Project_RejectsConflictingReplay()
    {
        var first = Evidence(Usage("usage-a", 10, "provider-a", "model-a"));
        var conflicting = Evidence(Usage("usage-a", 11, "provider-a", "model-a"));

        Assert.Throws<InvalidOperationException>(() => CustomerAiCreditUsage.Project(Authority, Pricing, [first, conflicting]));
    }

    [Fact]
    public void Project_IsProviderNeutralForCustomerCredits()
    {
        var cheap = Evidence(Usage("usage-a", 500_000, "provider-a", "model-a", 0.01m));
        var expensive = Evidence(Usage("usage-b", 500_000, "provider-z", "model-z", 99m));

        var result = CustomerAiCreditUsage.Project(Authority, Pricing, [cheap, expensive]);

        Assert.Equal(1, result.UsedAiCredits);
        Assert.DoesNotContain("provider", string.Join(',', result.UsageEntryIds), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Project_RejectsInvalidAuthorityVersion(long version)
    {
        var authority = Authority with { AuthorityVersion = version };
        Assert.Throws<ArgumentOutOfRangeException>(() => CustomerAiCreditUsage.Project(authority, Pricing, []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Project_RejectsInvalidPricingVersion(long version)
    {
        var pricing = Pricing with { PolicyVersion = version };
        Assert.Throws<ArgumentOutOfRangeException>(() => CustomerAiCreditUsage.Project(Authority, pricing, []));
    }

    [Fact]
    public void Project_FailsClosedOnCreditRoundingOverflow()
    {
        var pricing = Pricing with { TokenEquivalentPerCredit = long.MaxValue };
        var entry = Evidence(Usage("usage-a", long.MaxValue, "provider-a", "model-a"));

        Assert.Throws<OverflowException>(() => CustomerAiCreditUsage.Project(Authority, pricing, [entry]));
    }

    private static CustomerUsageEvidence Evidence(AiUsageEntry entry) => new(Authority.AuthorityVersion, entry);

    private static AiUsageEntry Usage(
        string entryId,
        long totalTokens,
        string provider,
        string model,
        decimal providerCost = 1m) =>
        new(
            entryId,
            new AiUsageScope("tenant-a", "company-a", "task-a", "agent-a", "request-a"),
            provider,
            model,
            AiCapability.Reasoning,
            new AiUsageAmount(totalTokens, 0, totalTokens, providerCost),
            DateTimeOffset.UnixEpoch);
}
