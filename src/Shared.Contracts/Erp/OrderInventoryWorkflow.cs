namespace MinhHuy.AIOffice.Shared.Contracts.Erp;

public sealed record OrderInventoryAuthority(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string Capability,
    string InstalledErpVersion,
    string SchemaSnapshotVersion,
    string CatalogVersion);

public sealed record OrderInventoryLine(string ItemKey, decimal Quantity, string Unit);

public sealed record OrderInventoryPreview(
    OrderInventoryAuthority Authority,
    string OrderReference,
    string WarehouseReference,
    IReadOnlyList<OrderInventoryLine> Lines,
    string AuditCorrelationId)
{
    public string IdempotencyKey => OrderInventoryContract.GetIdempotencyKey(this);
}

public enum OrderInventoryReconciliationState
{
    Matched,
    Mismatch
}

public sealed record OrderInventoryExecutionResult(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string IdempotencyKey,
    string ExecutionReference,
    string EvidenceReference,
    string InstalledErpVersion,
    string SchemaSnapshotVersion,
    string CatalogVersion,
    IReadOnlyDictionary<string, decimal> ActualQuantities,
    OrderInventoryReconciliationState ReconciliationState,
    string AuditCorrelationId);

public static class OrderInventoryContract
{
    public static string GetIdempotencyKey(OrderInventoryPreview preview)
    {
        ValidatePreview(preview);
        return string.Join(':',
            preview.Authority.TenantId,
            preview.Authority.CompanyId,
            preview.Authority.DataSourceId,
            Canonical(preview.Authority.Capability),
            Canonical(preview.OrderReference),
            Canonical(preview.WarehouseReference),
            preview.Authority.SchemaSnapshotVersion.Trim(),
            preview.Authority.CatalogVersion.Trim());
    }

    public static void ValidatePreview(OrderInventoryPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var authority = preview.Authority ?? throw new ArgumentException("Order/inventory authority is required.", nameof(preview));
        if (authority.TenantId == Guid.Empty || authority.CompanyId == Guid.Empty || authority.DataSourceId == Guid.Empty)
            throw new ArgumentException("Tenant, company and data-source authority are required.", nameof(preview));
        Require(authority.Capability, nameof(authority.Capability));
        Require(authority.InstalledErpVersion, nameof(authority.InstalledErpVersion));
        Require(authority.SchemaSnapshotVersion, nameof(authority.SchemaSnapshotVersion));
        Require(authority.CatalogVersion, nameof(authority.CatalogVersion));
        Require(preview.OrderReference, nameof(preview.OrderReference));
        Require(preview.WarehouseReference, nameof(preview.WarehouseReference));
        Require(preview.AuditCorrelationId, nameof(preview.AuditCorrelationId));
        ArgumentNullException.ThrowIfNull(preview.Lines);
        if (preview.Lines.Count == 0) throw new InvalidOperationException("Order/inventory preview requires at least one line.");
        foreach (var line in preview.Lines)
        {
            Require(line.ItemKey, nameof(line.ItemKey));
            Require(line.Unit, nameof(line.Unit));
            if (line.Quantity <= 0) throw new InvalidOperationException("Order/inventory quantity must be positive.");
        }
        if (preview.Lines.GroupBy(x => Canonical(x.ItemKey), StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Order/inventory preview requires one deterministic line per item key.");
    }

    public static void ValidateResult(OrderInventoryPreview preview, OrderInventoryExecutionResult result)
    {
        ValidatePreview(preview);
        ArgumentNullException.ThrowIfNull(result);
        var authority = preview.Authority;
        if (result.TenantId != authority.TenantId || result.CompanyId != authority.CompanyId || result.DataSourceId != authority.DataSourceId)
            throw new UnauthorizedAccessException("Order/inventory result crossed authoritative tenant/company/data-source scope.");
        if (!StringComparer.Ordinal.Equals(result.IdempotencyKey, preview.IdempotencyKey))
            throw new InvalidOperationException("Order/inventory result idempotency identity does not match the authorized preview.");
        if (!StringComparer.Ordinal.Equals(result.InstalledErpVersion, authority.InstalledErpVersion)
            || !StringComparer.Ordinal.Equals(result.SchemaSnapshotVersion, authority.SchemaSnapshotVersion)
            || !StringComparer.Ordinal.Equals(result.CatalogVersion, authority.CatalogVersion))
            throw new InvalidOperationException("Order/inventory result is stale for the authoritative ERP/schema/catalog version.");
        Require(result.ExecutionReference, nameof(result.ExecutionReference));
        Require(result.EvidenceReference, nameof(result.EvidenceReference));
        if (!StringComparer.Ordinal.Equals(result.AuditCorrelationId, preview.AuditCorrelationId))
            throw new InvalidOperationException("Order/inventory result audit correlation does not match the authorized preview.");
        ArgumentNullException.ThrowIfNull(result.ActualQuantities);

        var expected = preview.Lines.ToDictionary(x => Canonical(x.ItemKey), x => x.Quantity, StringComparer.Ordinal);
        var actual = result.ActualQuantities.ToDictionary(x => Canonical(x.Key), x => x.Value, StringComparer.Ordinal);
        var matched = expected.Count == actual.Count && expected.All(x => actual.TryGetValue(x.Key, out var quantity) && quantity == x.Value);
        var expectedState = matched ? OrderInventoryReconciliationState.Matched : OrderInventoryReconciliationState.Mismatch;
        if (result.ReconciliationState != expectedState)
            throw new InvalidOperationException("Order/inventory reconciliation state must be derived from deterministic ERP quantity evidence.");
    }

    private static string Canonical(string value) => value.Trim().ToUpperInvariant();

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
