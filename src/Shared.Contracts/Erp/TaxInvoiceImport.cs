namespace MinhHuy.AIOffice.Shared.Contracts.Erp;

public sealed record TaxInvoiceImportAuthority(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string Provider,
    string Capability,
    string InstalledErpVersion,
    string SchemaSnapshotVersion,
    string ContractVersion);

public sealed record TaxInvoiceImportRequest(
    TaxInvoiceImportAuthority Authority,
    string ExternalDocumentId,
    DateTimeOffset IssuedAt,
    string Currency,
    decimal NetAmount,
    decimal TaxAmount,
    string SecretReference,
    string AuditCorrelationId);

public sealed record TaxInvoiceImportResult(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string Provider,
    string ExternalDocumentId,
    string ContractVersion,
    string EvidenceReference,
    string AuditCorrelationId);

public static class TaxInvoiceImportContract
{
    public static string GetIdempotencyKey(TaxInvoiceImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        return string.Join(':', request.Authority.TenantId, request.Authority.CompanyId,
            request.Authority.DataSourceId, request.Authority.Provider.Trim().ToUpperInvariant(),
            request.ExternalDocumentId.Trim().ToUpperInvariant(), request.Authority.ContractVersion.Trim());
    }

    public static void ValidateRequest(TaxInvoiceImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authority = request.Authority ?? throw new ArgumentException("Import authority is required.", nameof(request));
        if (authority.TenantId == Guid.Empty || authority.CompanyId == Guid.Empty || authority.DataSourceId == Guid.Empty)
            throw new ArgumentException("Tenant, company and data-source authority are required.", nameof(request));
        Require(authority.Provider, nameof(authority.Provider));
        Require(authority.Capability, nameof(authority.Capability));
        Require(authority.InstalledErpVersion, nameof(authority.InstalledErpVersion));
        Require(authority.SchemaSnapshotVersion, nameof(authority.SchemaSnapshotVersion));
        Require(authority.ContractVersion, nameof(authority.ContractVersion));
        Require(request.ExternalDocumentId, nameof(request.ExternalDocumentId));
        Require(request.Currency, nameof(request.Currency));
        Require(request.SecretReference, nameof(request.SecretReference));
        Require(request.AuditCorrelationId, nameof(request.AuditCorrelationId));
        if (request.NetAmount < 0 || request.TaxAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Invoice amounts cannot be negative.");
    }

    public static void ValidateResult(TaxInvoiceImportRequest request, TaxInvoiceImportResult result)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(result);
        if (result.TenantId != request.Authority.TenantId || result.CompanyId != request.Authority.CompanyId ||
            result.DataSourceId != request.Authority.DataSourceId)
            throw new InvalidOperationException("Tax invoice import result crossed authoritative tenant/company/data-source scope.");
        if (!StringComparer.Ordinal.Equals(result.Provider, request.Authority.Provider) ||
            !StringComparer.Ordinal.Equals(result.ExternalDocumentId, request.ExternalDocumentId) ||
            !StringComparer.Ordinal.Equals(result.ContractVersion, request.Authority.ContractVersion))
            throw new InvalidOperationException("Tax invoice import result does not match the authoritative import identity/version.");
        Require(result.EvidenceReference, nameof(result.EvidenceReference));
        if (!StringComparer.Ordinal.Equals(result.AuditCorrelationId, request.AuditCorrelationId))
            throw new InvalidOperationException("Tax invoice import result audit correlation does not match the request.");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }
}
