namespace MinhHuy.AiOffice.Shared.Contracts.Erp;

public static class ErpCatalogLookup
{
    public static ErpCatalogItem? FindItem(
        this ErpCatalog catalog,
        string tenantId,
        string companyId,
        string dataSourceId,
        ErpCatalogItemKind kind,
        string key)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Validate();
        catalog.AssertAuthority(tenantId, companyId, dataSourceId);
        RequireCanonical(key, nameof(key));

        foreach (var item in catalog.Items)
            if (item.Kind == kind && StringComparer.Ordinal.Equals(item.Key, key))
                return item;

        return null;
    }

    public static ErpCapability? FindCapability(
        this ErpCatalog catalog,
        string tenantId,
        string companyId,
        string dataSourceId,
        string key)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Validate();
        catalog.AssertAuthority(tenantId, companyId, dataSourceId);
        RequireCanonical(key, nameof(key));

        foreach (var capability in catalog.Capabilities)
            if (StringComparer.Ordinal.Equals(capability.Key, key))
                return capability;

        return null;
    }

    public static ErpFeature? FindFeature(
        this ErpCatalog catalog,
        string tenantId,
        string companyId,
        string dataSourceId,
        string key)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Validate();
        catalog.AssertAuthority(tenantId, companyId, dataSourceId);
        RequireCanonical(key, nameof(key));

        foreach (var feature in catalog.Features)
            if (StringComparer.Ordinal.Equals(feature.Key, key))
                return feature;

        return null;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
