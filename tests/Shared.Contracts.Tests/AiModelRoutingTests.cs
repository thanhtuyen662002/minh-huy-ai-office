using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class AiModelRoutingTests
{
    [Fact]
    public void Select_RoutesByCapabilityTierBudgetLatencyAndHealth()
    {
        var router = new DeterministicAiModelRouter(new[]
        {
            Model("provider-a", "fast", AiCapability.Reasoning, tier: 1, cost: 4, latency: 100),
            Model("provider-b", "cheap", AiCapability.Reasoning, tier: 1, cost: 2, latency: 200),
            Model("provider-c", "unhealthy", AiCapability.Reasoning, tier: 0, cost: 1, latency: 50, healthy: false)
        });

        var selected = router.Select(Request(AiCapability.Reasoning));

        Assert.Equal("provider-b", selected.ProviderId);
        Assert.Equal("cheap", selected.ModelId);
    }

    [Fact]
    public void Select_FallbackCanFenceFailedProvider()
    {
        var router = new DeterministicAiModelRouter(new[]
        {
            Model("provider-a", "primary", AiCapability.Reasoning, 1, 1, 100),
            Model("provider-b", "fallback", AiCapability.Reasoning, 1, 2, 120)
        });

        var selected = router.Select(Request(AiCapability.Reasoning) with { ExcludedProviderId = "provider-a" });

        Assert.Equal("provider-b", selected.ProviderId);
    }

    [Fact]
    public void Select_FailsClosedWhenNoModelSatisfiesConstraints()
    {
        var router = new DeterministicAiModelRouter(new[]
        {
            Model("provider-a", "vision", AiCapability.Vision, 1, 1, 100)
        });

        Assert.Throws<InvalidOperationException>(() => router.Select(Request(AiCapability.Reasoning)));
    }

    [Fact]
    public void Select_EnforcesMinimumTierAndBudget()
    {
        var router = new DeterministicAiModelRouter(new[]
        {
            Model("provider-a", "low", AiCapability.Reasoning, 1, 1, 100),
            Model("provider-b", "high", AiCapability.Reasoning, 2, 3, 100)
        });

        var selected = router.Select(Request(AiCapability.Reasoning) with { MinimumTier = 2, MaxCostPerMillionTokens = 3 });

        Assert.Equal("high", selected.ModelId);
    }

    [Fact]
    public void Constructor_RejectsDuplicateProviderModelIdentity()
    {
        var models = new[]
        {
            Model("provider-a", "same", AiCapability.Reasoning, 1, 1, 100),
            Model("provider-a", "same", AiCapability.Vision, 1, 1, 100)
        };

        Assert.Throws<InvalidOperationException>(() => new DeterministicAiModelRouter(models));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" tenant")]
    [InlineData("tenant ")]
    public void RouteRequest_RejectsNonCanonicalTenantIdentity(string tenantId)
    {
        Assert.Throws<ArgumentException>(() => (Request(AiCapability.Reasoning) with { TenantId = tenantId }).Validate());
    }

    private static AiRouteRequest Request(AiCapability capability) =>
        new("tenant-a", "company-a", "task-a", capability);

    private static AiModelProfile Model(
        string provider,
        string model,
        AiCapability capability,
        int tier,
        decimal cost,
        int latency,
        bool healthy = true) =>
        new(provider, model, new HashSet<AiCapability> { capability }, tier, cost, latency, healthy);
}
