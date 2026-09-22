using MinhHuy.AiOffice.Shared.Contracts.Erp;

namespace MinhHuy.AiOffice.Shared.Contracts.Tests;

public sealed class ErpCatalogTests
{
    [Fact]
    public void Valid_catalog_preserves_company_scope_and_semantic_capabilities()
    {
        var catalog = ValidCatalog().Validate();

        catalog.AssertAuthority("tenant-a", "company-a", "erp-main");
        Assert.Equal("sales.order", catalog.Features.Single().Key);
        Assert.Equal("inventory.read", catalog.Capabilities.Single().Key);
    }

    [Fact]
    public void Authority_mismatch_fails_closed()
    {
        var catalog = ValidCatalog().Validate();

        Assert.Throws<InvalidOperationException>(() => catalog.AssertAuthority("tenant-a", "company-b", "erp-main"));
        Assert.Throws<InvalidOperationException>(() => catalog.AssertAuthority("tenant-b", "company-a", "erp-main"));
        Assert.Throws<InvalidOperationException>(() => catalog.AssertAuthority("tenant-a", "company-a", "erp-other"));
    }

    [Fact]
    public void Missing_capability_reference_fails_closed()
    {
        var catalog = ValidCatalog() with
        {
            Features = [new ErpFeature("sales.order", "1", ["inventory.write"])]
        };

        Assert.Throws<InvalidOperationException>(() => catalog.Validate());
    }

    [Fact]
    public void Duplicate_registry_keys_are_rejected()
    {
        var catalog = ValidCatalog() with
        {
            Capabilities = [new ErpCapability("inventory.read", "1"), new ErpCapability("inventory.read", "2")]
        };

        Assert.Throws<InvalidOperationException>(() => catalog.Validate());
    }

    [Fact]
    public void Noncanonical_authority_and_versions_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => (ValidCatalog() with { CompanyId = " company-a" }).Validate());
        Assert.Throws<ArgumentException>(() => (ValidCatalog() with { InstalledVersion = " " }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (ValidCatalog() with { SchemaSnapshotVersion = 0 }).Validate());
    }

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
