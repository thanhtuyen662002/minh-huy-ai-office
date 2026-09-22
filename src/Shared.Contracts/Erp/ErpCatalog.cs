namespace MinhHuy.AiOffice.Shared.Contracts.Erp;

public enum ErpCatalogItemKind
{
    DatabaseObject,
    Form,
    Report,
    MenuAction
}

public sealed record ErpCatalogItem(
    ErpCatalogItemKind Kind,
    string Key,
    string Version,
    string? SchemaObjectIdentity = null)
{
    public ErpCatalogItem Validate()
    {
        RequireCanonical(Key, nameof(Key));
        RequireCanonical(Version, nameof(Version));
        if (SchemaObjectIdentity is not null) RequireCanonical(SchemaObjectIdentity, nameof(SchemaObjectIdentity));
        if (Kind == ErpCatalogItemKind.DatabaseObject && SchemaObjectIdentity is null)
            throw new InvalidOperationException("Database catalog items require a schema object identity.");
        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}

public sealed record ErpCapability(string Key, string Version)
{
    public ErpCapability Validate()
    {
        RequireCanonical(Key, nameof(Key));
        RequireCanonical(Version, nameof(Version));
        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}

public sealed record ErpFeature(
    string Key,
    string Version,
    IReadOnlyList<string> RequiredCapabilities)
{
    public ErpFeature Validate()
    {
        RequireCanonical(Key, nameof(Key));
        RequireCanonical(Version, nameof(Version));
        ArgumentNullException.ThrowIfNull(RequiredCapabilities);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in RequiredCapabilities)
        {
            RequireCanonical(capability, nameof(RequiredCapabilities));
            if (!seen.Add(capability)) throw new InvalidOperationException("Feature capability keys must be unique.");
        }
        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}

public sealed record ErpCatalog(
    string TenantId,
    string CompanyId,
    string DataSourceId,
    string ErpKey,
    string InstalledVersion,
    long SchemaSnapshotVersion,
    IReadOnlyList<ErpCatalogItem> Items,
    IReadOnlyList<ErpCapability> Capabilities,
    IReadOnlyList<ErpFeature> Features)
{
    public ErpCatalog Validate()
    {
        RequireCanonical(TenantId, nameof(TenantId));
        RequireCanonical(CompanyId, nameof(CompanyId));
        RequireCanonical(DataSourceId, nameof(DataSourceId));
        RequireCanonical(ErpKey, nameof(ErpKey));
        RequireCanonical(InstalledVersion, nameof(InstalledVersion));
        if (SchemaSnapshotVersion <= 0) throw new ArgumentOutOfRangeException(nameof(SchemaSnapshotVersion));
        ArgumentNullException.ThrowIfNull(Items);
        ArgumentNullException.ThrowIfNull(Capabilities);
        ArgumentNullException.ThrowIfNull(Features);

        ValidateUnique(Items.Select(item => { item.Validate(); return $"{item.Kind}:{item.Key}"; }), "catalog item");
        ValidateUnique(Capabilities.Select(item => { item.Validate(); return item.Key; }), "capability");
        ValidateUnique(Features.Select(item => { item.Validate(); return item.Key; }), "feature");

        var capabilityKeys = Capabilities.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var required in Features.SelectMany(x => x.RequiredCapabilities))
            if (!capabilityKeys.Contains(required)) throw new InvalidOperationException("Feature references an unavailable capability.");
        return this;
    }

    public void AssertAuthority(string tenantId, string companyId, string dataSourceId)
    {
        RequireCanonical(tenantId, nameof(tenantId));
        RequireCanonical(companyId, nameof(companyId));
        RequireCanonical(dataSourceId, nameof(dataSourceId));
        if (!StringComparer.Ordinal.Equals(TenantId, tenantId) ||
            !StringComparer.Ordinal.Equals(CompanyId, companyId) ||
            !StringComparer.Ordinal.Equals(DataSourceId, dataSourceId))
            throw new InvalidOperationException("ERP catalog authority does not match server-derived tenant/company/data-source scope.");
    }

    public void AssertSchemaSnapshot(ErpSchemaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate();
        snapshot.Validate();
        AssertAuthority(snapshot.TenantId, snapshot.CompanyId, snapshot.DataSourceId);
        if (SchemaSnapshotVersion != snapshot.Version)
            throw new InvalidOperationException("ERP catalog schema version does not match the authoritative schema snapshot.");

        var identities = snapshot.Objects
            .Select(item => $"{item.Kind}:{item.Schema}.{item.Name}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in Items.Where(item => item.Kind == ErpCatalogItemKind.DatabaseObject))
        {
            if (!identities.Contains(item.SchemaObjectIdentity!))
                throw new InvalidOperationException($"ERP catalog database object is absent from the authoritative schema snapshot: {item.SchemaObjectIdentity}");
        }
    }

    private static void ValidateUnique(IEnumerable<string> keys, string label)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
            if (!seen.Add(key)) throw new InvalidOperationException($"Duplicate ERP {label}: {key}");
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
