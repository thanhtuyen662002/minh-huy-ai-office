using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AiOffice.Shared.Contracts.Tests;

public sealed class AccountingInvestigationTests
{
    [Fact]
    public void Authorized_investigation_requires_available_capability_and_evidence()
    {
        var catalog = ValidCatalog().Validate();
        var request = ValidRequest().Validate(catalog);
        var result = new AccountingInvestigationResult(
            "tenant-a",
            "company-a",
            "erp-main",
            "Inventory evidence confirms the balance.",
            [new AccountingEvidence("dbo.Inventory", "inventory:42", "Balance row 42")],
            "audit-001").Validate(request);

        Assert.Equal("audit-001", result.AuditCorrelationId);
        Assert.Single(result.Evidence);
    }

    [Fact]
    public void Cross_authority_request_fails_closed()
    {
        var catalog = ValidCatalog().Validate();

        Assert.Throws<InvalidOperationException>(() => (ValidRequest() with { CompanyId = "company-b" }).Validate(catalog));
        Assert.Throws<InvalidOperationException>(() => (ValidRequest() with { TenantId = "tenant-b" }).Validate(catalog));
        Assert.Throws<InvalidOperationException>(() => (ValidRequest() with { DataSourceId = "erp-other" }).Validate(catalog));
    }

    [Fact]
    public void Unavailable_capability_fails_closed()
    {
        var catalog = ValidCatalog().Validate();
        var request = ValidRequest() with { RequiredCapabilityKeys = ["inventory.write"] };

        Assert.Throws<InvalidOperationException>(() => request.Validate(catalog));
    }

    [Fact]
    public void Result_without_deterministic_evidence_is_rejected()
    {
        var request = ValidRequest().Validate(ValidCatalog().Validate());
        var result = new AccountingInvestigationResult(
            "tenant-a",
            "company-a",
            "erp-main",
            "Unsupported answer",
            [],
            "audit-002");

        Assert.Throws<InvalidOperationException>(() => result.Validate(request));
    }

    [Fact]
    public void Cross_authority_result_is_rejected()
    {
        var request = ValidRequest().Validate(ValidCatalog().Validate());
        var result = new AccountingInvestigationResult(
            "tenant-a",
            "company-b",
            "erp-main",
            "Answer",
            [new AccountingEvidence("dbo.Inventory", "inventory:42", "Balance row 42")],
            "audit-003");

        Assert.Throws<UnauthorizedAccessException>(() => result.Validate(request));
    }

    private static AccountingInvestigationRequest ValidRequest() => new(
        "tenant-a",
        "company-a",
        "erp-main",
        "What is the current inventory balance?",
        ["inventory.read"]);

    private static ErpCatalog ValidCatalog() => new(
        "tenant-a",
        "company-a",
        "erp-main",
        "minh-huy-erp",
        "2026.09",
        12,
        [new ErpCatalogItem(ErpCatalogItemKind.DatabaseObject, "dbo.Inventory", "12", "Table:dbo.Inventory")],
        [new ErpCapability("inventory.read", "1")],
        [new ErpFeature("sales.order", "1", ["inventory.read"])]);
}
