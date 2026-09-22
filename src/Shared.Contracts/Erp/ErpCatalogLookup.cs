namespace MinhHuy.AiOffice.Shared.Contracts.Erp;

public static class ErpCatalogLookup
{
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
        return catalog.Capabilities.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.Key, key));
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
        return catalog.Features.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.Key, key));
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
