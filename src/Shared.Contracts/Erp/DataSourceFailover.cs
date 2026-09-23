namespace MinhHuy.AIOffice.Shared.Contracts.Erp;

public enum DataSourceFailoverRole { Primary = 0, Fallback = 1 }
public enum DataSourceAccessMode { ReadOnly = 0, ReadWrite = 1 }
public enum DataSourceOperationKind { Read = 0, Write = 1 }

public sealed record DataSourceFailoverAuthority(Guid TenantId, Guid CompanyId, Guid DataSourceId, string RegistryVersion, string SchemaVersion, string CatalogVersion);

public sealed record DataSourceFailoverCandidate(
    Guid TenantId, Guid CompanyId, Guid DataSourceId, string EndpointId, string CredentialReference,
    DataSourceFailoverRole Role, DataSourceAccessMode AccessMode, int Priority,
    string RegistryVersion, string SchemaVersion, string CatalogVersion,
    bool Healthy, string HealthEvidenceReference);

public sealed record DataSourceFailoverDecision(
    Guid TenantId, Guid CompanyId, Guid DataSourceId, string EndpointId,
    string RegistryVersion, string SchemaVersion, string CatalogVersion,
    string EvidenceReference, string Reason);

public static class DataSourceFailoverContract
{
    public static DataSourceFailoverDecision Select(
        DataSourceFailoverAuthority authority,
        DataSourceOperationKind operation,
        IReadOnlyCollection<DataSourceFailoverCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateAuthority(authority);
        if (candidates.Count == 0) throw new InvalidOperationException("No failover candidates are available.");

        var eligible = new List<DataSourceFailoverCandidate>();
        foreach (var candidate in candidates)
        {
            ValidateCandidate(candidate);
            if (candidate.TenantId != authority.TenantId || candidate.CompanyId != authority.CompanyId || candidate.DataSourceId != authority.DataSourceId)
                throw new UnauthorizedAccessException("Failover candidate authority does not match the requested data source.");
            if (!StringComparer.Ordinal.Equals(candidate.RegistryVersion, authority.RegistryVersion))
                throw new InvalidOperationException("Failover candidate registry evidence is stale.");
            if (!StringComparer.Ordinal.Equals(candidate.SchemaVersion, authority.SchemaVersion) || !StringComparer.Ordinal.Equals(candidate.CatalogVersion, authority.CatalogVersion))
                throw new InvalidOperationException("Failover candidate is outside the required schema/catalog compatibility fence.");
            if (candidate.Healthy && (operation != DataSourceOperationKind.Write || candidate.AccessMode == DataSourceAccessMode.ReadWrite))
                eligible.Add(candidate);
        }

        var selected = eligible
            .OrderBy(x => x.Role)
            .ThenBy(x => x.Priority)
            .ThenBy(x => x.EndpointId, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(operation == DataSourceOperationKind.Write
                ? "No healthy write-capable data source satisfies the authority and version fences."
                : "No healthy data source satisfies the authority and version fences.");

        return new DataSourceFailoverDecision(
            authority.TenantId, authority.CompanyId, authority.DataSourceId, selected.EndpointId,
            authority.RegistryVersion, authority.SchemaVersion, authority.CatalogVersion,
            selected.HealthEvidenceReference,
            selected.Role == DataSourceFailoverRole.Primary ? "primary-healthy" : "primary-unavailable-fallback-selected");
    }

    private static void ValidateAuthority(DataSourceFailoverAuthority authority)
    {
        if (authority.TenantId == Guid.Empty || authority.CompanyId == Guid.Empty || authority.DataSourceId == Guid.Empty)
            throw new ArgumentException("Failover authority identifiers must be non-empty.");
        RequireCanonical(authority.RegistryVersion, nameof(authority.RegistryVersion));
        RequireCanonical(authority.SchemaVersion, nameof(authority.SchemaVersion));
        RequireCanonical(authority.CatalogVersion, nameof(authority.CatalogVersion));
    }

    private static void ValidateCandidate(DataSourceFailoverCandidate candidate)
    {
        if (candidate.TenantId == Guid.Empty || candidate.CompanyId == Guid.Empty || candidate.DataSourceId == Guid.Empty)
            throw new ArgumentException("Failover candidate authority identifiers must be non-empty.");
        RequireCanonical(candidate.EndpointId, nameof(candidate.EndpointId));
        RequireCanonical(candidate.CredentialReference, nameof(candidate.CredentialReference));
        RequireCanonical(candidate.RegistryVersion, nameof(candidate.RegistryVersion));
        RequireCanonical(candidate.SchemaVersion, nameof(candidate.SchemaVersion));
        RequireCanonical(candidate.CatalogVersion, nameof(candidate.CatalogVersion));
        RequireCanonical(candidate.HealthEvidenceReference, nameof(candidate.HealthEvidenceReference));
        if (candidate.CredentialReference.Contains('=') || candidate.CredentialReference.Contains(';'))
            throw new ArgumentException("CredentialReference must be opaque and must not contain connection-string material.", nameof(candidate.CredentialReference));
        if (candidate.Priority < 0) throw new ArgumentOutOfRangeException(nameof(candidate.Priority));
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
