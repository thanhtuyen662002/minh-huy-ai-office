using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AiOffice.Shared.Contracts.Tests;

public sealed class AccountingPostingTests
{
    [Fact]
    public void Balanced_preview_is_bound_to_authoritative_catalog_version()
    {
        var catalog = Catalog();
        var preview = Preview(catalog);

        Assert.Same(preview, preview.Validate(catalog));
    }

    [Fact]
    public void Cross_company_preview_fails_closed()
    {
        var catalog = Catalog();
        var preview = Preview(catalog) with { CompanyId = "other-company" };

        Assert.Throws<InvalidOperationException>(() => preview.Validate(catalog));
    }

    [Fact]
    public void Unbalanced_preview_is_rejected_before_execution()
    {
        var catalog = Catalog();
        var preview = Preview(catalog) with
        {
            Lines =
            [
                new AccountingPostingLine("111", 100m, 0m, "Cash"),
                new AccountingPostingLine("511", 0m, 99m, "Revenue")
            ]
        };

        Assert.Throws<InvalidOperationException>(() => preview.Validate(catalog));
    }

    [Fact]
    public void Stale_schema_preview_is_rejected()
    {
        var catalog = Catalog();
        var preview = Preview(catalog) with { SchemaSnapshotVersion = catalog.SchemaSnapshotVersion - 1 };

        Assert.Throws<InvalidOperationException>(() => preview.Validate(catalog));
    }

    [Fact]
    public void Reconciliation_result_cannot_switch_authority_or_idempotency_identity()
    {
        var catalog = Catalog();
        var preview = Preview(catalog).Validate(catalog);
        var result = new AccountingPostingExecutionResult(
            preview.TenantId,
            "other-company",
            preview.DataSourceId,
            preview.IdempotencyKey,
            "posting-001",
            [new AccountingEvidence("dbo.GL", "gl:001", "Journal entry 001")],
            AccountingReconciliationState.Balanced);

        Assert.Throws<UnauthorizedAccessException>(() => result.Validate(preview));
    }

    private static ErpCatalog Catalog() => new(
        "tenant-1",
        "company-1",
        "erp-main",
        "minh-huy-erp",
        "2026.09",
        12,
        [new ErpCatalogItem(ErpCatalogItemKind.DatabaseObject, "dbo.GL", "12", "Table:dbo.GL")],
        [new ErpCapability("accounting.post", "1")],
        [new ErpFeature("accounting.posting", "1", ["accounting.post"])]);

    private static AccountingPostingPreview Preview(ErpCatalog catalog) => new(
        catalog.TenantId,
        catalog.CompanyId,
        catalog.DataSourceId,
        catalog.InstalledVersion,
        catalog.SchemaSnapshotVersion,
        "accounting.post",
        "post:invoice:2026-0001",
        [
            new AccountingPostingLine("111", 100m, 0m, "Cash"),
            new AccountingPostingLine("511", 0m, 100m, "Revenue")
        ]);
}
