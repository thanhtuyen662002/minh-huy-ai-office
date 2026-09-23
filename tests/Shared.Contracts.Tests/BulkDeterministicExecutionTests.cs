using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class BulkDeterministicExecutionTests
{
    [Fact]
    public void PlanIdentity_IsStableAcrossItemOrder_AndVersionFenced()
    {
        var plan = Plan();
        var reversed = plan with { Items = plan.Items.Reverse().ToArray() };
        Assert.Equal(plan.PlanIdentity, reversed.PlanIdentity);
        var changed = plan with { Authority = plan.Authority with { SchemaSnapshotVersion = "schema-43" } };
        Assert.NotEqual(plan.PlanIdentity, changed.PlanIdentity);
    }

    [Fact]
    public void ItemIdentity_IsAuthorityOperationAndExpectedResultFenced()
    {
        var plan = Plan();
        var item = plan.Items[0];
        var identity = item.GetExecutionIdentity(plan.Authority);
        Assert.NotEqual(identity, item.GetExecutionIdentity(plan.Authority with { CompanyId = Guid.NewGuid() }));
        Assert.NotEqual(identity, (item with { Operation = "inventory.adjust" }).GetExecutionIdentity(plan.Authority));
        Assert.NotEqual(identity, (item with { ExpectedResultFingerprint = "expected-other" }).GetExecutionIdentity(plan.Authority));
    }

    [Fact]
    public void ResultRejectsCrossCompanyAndIdentityMismatch()
    {
        var plan = Plan();
        var item = plan.Items[0];
        var result = Result(plan, item, BulkExecutionItemState.Succeeded);
        Assert.Throws<UnauthorizedAccessException>(() => BulkDeterministicExecutionContract.ValidateResult(plan, item, result with { CompanyId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => BulkDeterministicExecutionContract.ValidateResult(plan, item, result with { ItemExecutionIdentity = "wrong" }));
    }

    [Fact]
    public void RetryDoesNotReplaySuccessfulReconciledWrites()
    {
        var plan = Plan();
        var succeeded = Result(plan, plan.Items[0], BulkExecutionItemState.Succeeded);
        var failed = Result(plan, plan.Items[1], BulkExecutionItemState.Failed);
        var retry = BulkDeterministicExecutionContract.GetRetryableItems(plan, new[] { succeeded, failed });
        Assert.Single(retry);
        Assert.Equal(plan.Items[1].ItemIdentity, retry[0].ItemIdentity);
    }

    [Fact]
    public void ResultRequiresExactEvidenceAuditAndDerivedReconciliation()
    {
        var plan = Plan();
        var item = plan.Items[0];
        var result = Result(plan, item, BulkExecutionItemState.Exception);
        Assert.Throws<ArgumentException>(() => BulkDeterministicExecutionContract.ValidateResult(plan, item, result with { EvidenceReference = "" }));
        Assert.Throws<InvalidOperationException>(() => BulkDeterministicExecutionContract.ValidateResult(plan, item, result with { AuditCorrelationId = "audit-other" }));
        Assert.Throws<InvalidOperationException>(() => BulkDeterministicExecutionContract.ValidateResult(plan, item, result with { ReconciliationState = BulkReconciliationState.Mismatch }));
    }

    [Fact]
    public void SucceededResultCannotHideErpReconciliationMismatch()
    {
        var plan = Plan();
        var item = plan.Items[0];
        var mismatch = Result(plan, item, BulkExecutionItemState.Succeeded) with
        {
            ActualResultFingerprint = "unexpected-erp-state",
            ReconciliationState = BulkReconciliationState.Mismatch
        };
        Assert.Throws<InvalidOperationException>(() => BulkDeterministicExecutionContract.ValidateResult(plan, item, mismatch));
    }

    [Fact]
    public void PlanRejectsDuplicateNormalizedItemIdentity()
    {
        var plan = Plan();
        var duplicate = plan.Items[0] with { ItemIdentity = plan.Items[0].ItemIdentity.ToLowerInvariant() };
        var invalid = plan with { Items = new[] { plan.Items[0], duplicate } };
        Assert.Throws<InvalidOperationException>(() => BulkDeterministicExecutionContract.ValidatePlan(invalid));
    }

    private static BulkExecutionPlan Plan()
    {
        var authority = new BulkExecutionAuthority(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"), "erp.bulk.write", "workflow-7", "erp-2026.09", "schema-42", "catalog-12", "secret-ref:erp-prod");
        return new BulkExecutionPlan(authority, new[]
        {
            new BulkExecutionItem("ITEM-001", "order.post", "payload-a", "evidence-preview-a", "actual-ITEM-001"),
            new BulkExecutionItem("ITEM-002", "order.post", "payload-b", "evidence-preview-b", "actual-ITEM-002")
        }, "audit-123");
    }

    private static BulkExecutionItemResult Result(BulkExecutionPlan plan, BulkExecutionItem item, BulkExecutionItemState state)
        => new(plan.Authority.TenantId, plan.Authority.CompanyId, plan.Authority.DataSourceId,
            item.GetExecutionIdentity(plan.Authority), state, $"evidence-{item.ItemIdentity}", item.ExpectedResultFingerprint,
            BulkReconciliationState.Matched, plan.AuditCorrelationId);
}
