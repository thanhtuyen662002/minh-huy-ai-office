using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AiOffice.Shared.Contracts.Tests;

public sealed class ErpChangeImpactTests
{
    [Fact]
    public void Analyze_preserves_declared_impact_order_and_requires_migration_for_versioned_affected_change()
    {
        var analyzer = new ErpChangeImpactAnalyzer();
        var catalog = ValidCatalog();
        var changes = new[] { new ErpChangedObject(ErpCatalogItemKind.DatabaseObject, "dbo.Inventory", "12", "13") };
        var bindings = new[]
        {
            new ErpImpactBinding(
                ErpCatalogItemKind.DatabaseObject,
                "dbo.Inventory",
                new[] { "sales.order" },
                new[] { "inventory.read", "inventory.reserve" },
                new[] { "skill.inventory" },
                new[] { "workflow.sales-order" })
        };

        var impact = Assert.Single(analyzer.Analyze(catalog, "tenant-a", "company-a", "erp-main", changes, bindings));

        Assert.Equal(new[] { "sales.order" }, impact.AffectedFeatures);
        Assert.Equal(new[] { "inventory.read", "inventory.reserve" }, impact.AffectedCapabilities);
        Assert.Equal(new[] { "skill.inventory" }, impact.AffectedSkills);
        Assert.Equal(new[] { "workflow.sales-order" }, impact.AffectedWorkflows);
        Assert.Equal(ErpCompatibilityAction.MigrationRequired, impact.RequiredAction);
    }

    [Fact]
    public void Analyze_rejects_cross_authority_requests()
    {
        var analyzer = new ErpChangeImpactAnalyzer();
        var changes = new[] { new ErpChangedObject(ErpCatalogItemKind.DatabaseObject, "dbo.Inventory", "12", "13") };
        var bindings = ValidBindings();

        Assert.Throws<InvalidOperationException>(() => analyzer.Analyze(ValidCatalog(), "tenant-b", "company-a", "erp-main", changes, bindings));
        Assert.Throws<InvalidOperationException>(() => analyzer.Analyze(ValidCatalog(), "tenant-a", "company-b", "erp-main", changes, bindings));
        Assert.Throws<InvalidOperationException>(() => analyzer.Analyze(ValidCatalog(), "tenant-a", "company-a", "erp-other", changes, bindings));
    }

    [Fact]
    public void Analyze_rejects_changed_object_without_explicit_binding()
    {
        var analyzer = new ErpChangeImpactAnalyzer();
        var changes = new[] { new ErpChangedObject(ErpCatalogItemKind.DatabaseObject, "dbo.Inventory", "12", "13") };

        Assert.Throws<InvalidOperationException>(() => analyzer.Analyze(
            ValidCatalog(),
            "tenant-a",
            "company-a",
            "erp-main",
            changes,
            Array.Empty<ErpImpactBinding>()));
    }

    [Fact]
    public void Analyze_rejects_binding_to_unavailable_catalog_capability()
    {
        var analyzer = new ErpChangeImpactAnalyzer();
        var changes = new[] { new ErpChangedObject(ErpCatalogItemKind.DatabaseObject, "dbo.Inventory", "12", "13") };
        var bindings = new[]
        {
            new ErpImpactBinding(
                ErpCatalogItemKind.DatabaseObject,
                "dbo.Inventory",
                new[] { "sales.order" },
                new[] { "inventory.write" },
                Array.Empty<string>(),
                Array.Empty<string>())
        };

        Assert.Throws<InvalidOperationException>(() => analyzer.Analyze(
            ValidCatalog(), "tenant-a", "company-a", "erp-main", changes, bindings));
    }

    [Fact]
    public void Version_change_without_affected_surface_requires_review_not_migration()
    {
        var analyzer = new ErpChangeImpactAnalyzer();
        var changes = new[] { new ErpChangedObject(ErpCatalogItemKind.DatabaseObject, "dbo.Inventory", "12", "13") };
        var bindings = new[]
        {
            new ErpImpactBinding(
                ErpCatalogItemKind.DatabaseObject,
                "dbo.Inventory",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>())
        };

        var impact = Assert.Single(analyzer.Analyze(
            ValidCatalog(), "tenant-a", "company-a", "erp-main", changes, bindings));

        Assert.Equal(ErpCompatibilityAction.Review, impact.RequiredAction);
    }

    private static ErpImpactBinding[] ValidBindings() =>
    [
        new ErpImpactBinding(
            ErpCatalogItemKind.DatabaseObject,
            "dbo.Inventory",
            new[] { "sales.order" },
            new[] { "inventory.read" },
            Array.Empty<string>(),
            Array.Empty<string>())
    ];

    private static ErpCatalog ValidCatalog() => new(
        "tenant-a",
        "company-a",
        "erp-main",
        "minh-huy-erp",
        "2026.09",
        12,
        [new ErpCatalogItem(ErpCatalogItemKind.DatabaseObject, "dbo.Inventory", "12", "Table:dbo.Inventory")],
        [new ErpCapability("inventory.read", "1"), new ErpCapability("inventory.reserve", "1")],
        [new ErpFeature("sales.order", "1", new[] { "inventory.read", "inventory.reserve" })]);
}
