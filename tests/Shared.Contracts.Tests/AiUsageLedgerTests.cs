using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class AiUsageLedgerTests
{
    private static readonly AiUsageScope Scope = new("tenant-a", "company-a", "task-a", "agent-a", "request-root");

    [Fact]
    public void AggregateExecutionTree_SumsProviderNeutralUsage()
    {
        var entries = new[]
        {
            Entry("root", "request-1", 10, 5, 0.12m),
            Entry("child", "request-2", 4, 6, 0.08m, "root")
        };

        var aggregate = AiUsageLedger.AggregateExecutionTree(Scope, entries);

        Assert.Equal(14, aggregate.InputTokenEquivalent);
        Assert.Equal(11, aggregate.OutputTokenEquivalent);
        Assert.Equal(25, aggregate.TotalTokenEquivalent);
        Assert.Equal(0.20m, aggregate.ProviderCostUsd);
        Assert.Equal(2, aggregate.CallCount);
    }

    [Fact]
    public void AggregateExecutionTree_RejectsDuplicateIdentityToPreventDoubleCharge()
    {
        var entries = new[] { Entry("same", "request-1", 1, 1, 0.01m), Entry("same", "request-2", 1, 1, 0.01m) };
        Assert.Throws<InvalidOperationException>(() => AiUsageLedger.AggregateExecutionTree(Scope, entries));
    }

    [Theory]
    [InlineData("tenant-b", "company-a")]
    [InlineData("tenant-a", "company-b")]
    public void AggregateExecutionTree_RejectsCrossAuthorityScope(string tenantId, string companyId)
    {
        var foreign = new AiUsageScope(tenantId, companyId, "task-a", "agent-a", "request-1");
        Assert.Throws<InvalidOperationException>(() => AiUsageLedger.AggregateExecutionTree(Scope, [Entry("entry", foreign, 1, 1, 0.01m)]));
    }

    [Fact]
    public void AggregateExecutionTree_AllowsDistinctRequestIdsWithinOneExecution()
    {
        var aggregate = AiUsageLedger.AggregateExecutionTree(Scope,
        [
            Entry("root", "request-1", 1, 1, 0.01m),
            Entry("child", "request-2", 1, 1, 0.01m, "root")
        ]);
        Assert.Equal(2, aggregate.CallCount);
    }

    [Fact]
    public void AggregateExecutionTree_RejectsParentOutsideTree()
    {
        Assert.Throws<InvalidOperationException>(() => AiUsageLedger.AggregateExecutionTree(Scope,
            [Entry("child", "request-1", 1, 1, 0.01m, "missing")]));
    }

    [Fact]
    public void Validate_RejectsMalformedTotalsAndNegativeCost()
    {
        var malformed = new AiUsageEntry("entry", Scope, "provider", "model", AiCapability.Reasoning,
            new AiUsageAmount(2, 3, 99, 0.01m), DateTimeOffset.UtcNow);
        var negativeCost = new AiUsageEntry("entry", Scope, "provider", "model", AiCapability.Reasoning,
            new AiUsageAmount(2, 3, 5, -0.01m), DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => AiUsageLedger.Validate(malformed));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiUsageLedger.Validate(negativeCost));
    }

    [Fact]
    public void Validate_RejectsNonCanonicalAuthorityAndNonUtcTimestamp()
    {
        var badScope = new AiUsageScope(" tenant-a", "company-a", "task-a", "agent-a", "request-1");
        Assert.Throws<ArgumentException>(() => AiUsageLedger.Validate(Entry("entry", badScope, 1, 1, 0.01m)));

        var nonUtc = new AiUsageEntry("entry", Scope, "provider", "model", AiCapability.Reasoning,
            new AiUsageAmount(1, 1, 2, 0.01m), new DateTimeOffset(2026, 9, 22, 7, 0, 0, TimeSpan.FromHours(7)));
        Assert.Throws<ArgumentException>(() => AiUsageLedger.Validate(nonUtc));
    }

    private static AiUsageEntry Entry(string id, string requestId, long input, long output, decimal cost, string? parent = null) =>
        Entry(id, Scope with { RequestId = requestId }, input, output, cost, parent);

    private static AiUsageEntry Entry(string id, AiUsageScope scope, long input, long output, decimal cost, string? parent = null) =>
        new(id, scope, "provider", "model", AiCapability.Reasoning,
            new AiUsageAmount(input, output, input + output, cost),
            new DateTimeOffset(2026, 9, 21, 17, 30, 0, TimeSpan.Zero), parent);
}
