using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AiOffice.Shared.Contracts.Tests;

public sealed class ErpCatalogLookupTests
{
    [Fact]
    public void Lookup_is_stable_and_ordinal()
    {
        var catalog = ValidCatalog();

        Assert.Equal("12", catalog.FindItem("tenant-a", "company-a", "erp-main", ErpCatalogItemKind.DatabaseObject, "dbo.Inventory")!.Version);
        Assert.Equal("1", catalog.FindCapability("tenant-a", "company-a", "erp-main", "inventory.read")!.Version);
        Assert.Equal("1", catalog.FindFeature("tenant-a", "company-a", "erp-main", "sales.order")!.Version);
        Assert.Null(catalog.FindItem("tenant-a", "company-a", "erp-main", ErpCatalogItemKind.DatabaseObject, "dbo.inventory"));
        Assert.Null(catalog.FindCapability("tenant-a", "company-a", "erp-main", "Inventory.Read"));
        Assert.Null(catalog.FindFeature("tenant-a", "company-a", "erp-main", "Sales.Order"));
    }

    [Fact]
    public void Lookup_rejects_caller_authority_and_noncanonical_keys()
    {
        var catalog = ValidCatalog();

        Assert.Throws<InvalidOperationException>(() => catalog.FindItem("tenant-b", "company-a", "erp-main", ErpCatalogItemKind.DatabaseObject, "dbo.Inventory"));
        Assert.Throws<InvalidOperationException>(() => catalog.FindCapability("tenant-b", "company-a", "erp-main", "inventory.read"));
        Assert.Throws<InvalidOperationException>(() => catalog.FindFeature("tenant-a", "company-b", "erp-main", "sales.order"));
        Assert.Throws<InvalidOperationException>(() => catalog.FindCapability("tenant-a", "company-a", "erp-other", "inventory.read"));
        Assert.Throws<ArgumentException>(() => catalog.FindItem("tenant-a", "company-a", "erp-main", ErpCatalogItemKind.DatabaseObject, " dbo.Inventory"));
        Assert.Throws<ArgumentException>(() => catalog.FindCapability("tenant-a", "company-a", "erp-main", " inventory.read"));
        Assert.Throws<ArgumentException>(() => catalog.FindFeature("tenant-a", "company-a", "erp-main", " "));
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
        [new ErpFeature("sales.order", "1", ["inventory.read"])]).Validate();
}
