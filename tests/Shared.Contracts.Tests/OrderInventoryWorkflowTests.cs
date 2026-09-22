using MinhHuy.AIOffice.Shared.Contracts.Erp;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class OrderInventoryWorkflowTests
{
    [Fact]
    public void Idempotency_key_is_stable_for_normalized_business_identity()
    {
        var first = Preview(order: "SO-001", warehouse: "WH-A");
        var second = first with { OrderReference = "so-001", WarehouseReference = "wh-a" };

        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void Result_crossing_company_authority_is_rejected()
    {
        var preview = Preview();
        var result = Result(preview) with { CompanyId = Guid.NewGuid() };

        Assert.Throws<UnauthorizedAccessException>(() => OrderInventoryContract.ValidateResult(preview, result));
    }

    [Fact]
    public void Stale_schema_result_is_rejected()
    {
        var preview = Preview();
        var result = Result(preview) with { SchemaSnapshotVersion = "schema-previous" };

        Assert.Throws<InvalidOperationException>(() => OrderInventoryContract.ValidateResult(preview, result));
    }

    [Fact]
    public void Mismatch_must_be_derived_from_actual_quantity_evidence()
    {
        var preview = Preview();
        var result = Result(preview) with
        {
            ActualQuantities = new Dictionary<string, decimal> { ["SKU-1"] = 1m },
            ReconciliationState = OrderInventoryReconciliationState.Matched
        };

        Assert.Throws<InvalidOperationException>(() => OrderInventoryContract.ValidateResult(preview, result));

        var mismatch = result with { ReconciliationState = OrderInventoryReconciliationState.Mismatch };
        OrderInventoryContract.ValidateResult(preview, mismatch);
    }

    [Fact]
    public void Missing_evidence_is_rejected()
    {
        var preview = Preview();
        var result = Result(preview) with { EvidenceReference = "" };

        Assert.Throws<ArgumentException>(() => OrderInventoryContract.ValidateResult(preview, result));
    }

    private static OrderInventoryPreview Preview(string order = "SO-001", string warehouse = "WH-A") =>
        new(
            new OrderInventoryAuthority(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "order.inventory.execute",
                "erp-2026.09",
                "schema-42",
                "catalog-7"),
            order,
            warehouse,
            [new OrderInventoryLine("SKU-1", 2m, "EA")],
            "audit-97");

    private static OrderInventoryExecutionResult Result(OrderInventoryPreview preview) =>
        new(
            preview.Authority.TenantId,
            preview.Authority.CompanyId,
            preview.Authority.DataSourceId,
            preview.IdempotencyKey,
            "execution-1",
            "evidence-1",
            preview.Authority.InstalledErpVersion,
            preview.Authority.SchemaSnapshotVersion,
            preview.Authority.CatalogVersion,
            new Dictionary<string, decimal> { ["SKU-1"] = 2m },
            OrderInventoryReconciliationState.Matched,
            preview.AuditCorrelationId);
}
