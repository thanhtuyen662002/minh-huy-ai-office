using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MinhHuy.AIOffice.Shared.Contracts.Erp;

public static class DataSourceFailoverExecutionAuthorization
{
    public static void Validate(
        DataSourceFailoverAuthority authority,
        DataSourceOperationKind operation,
        DataSourceFailoverDecision decision,
        DataSourceFailoverExecutionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(evidence);

        DataSourceFailoverContract.ValidateDecision(authority, operation, decision);
        if (!Enum.IsDefined(evidence.Operation)) throw new ArgumentOutOfRangeException(nameof(evidence.Operation));
        if (!Enum.IsDefined(evidence.Outcome)) throw new ArgumentOutOfRangeException(nameof(evidence.Outcome));
        RequireOpaque(evidence.ExecutionEvidenceId, nameof(evidence.ExecutionEvidenceId));
        RequireOpaque(evidence.DecisionIdentity, nameof(evidence.DecisionIdentity));
        RequireCanonical(evidence.EndpointId, nameof(evidence.EndpointId));
        RequireCanonical(evidence.RegistryVersion, nameof(evidence.RegistryVersion));
        RequireCanonical(evidence.SchemaVersion, nameof(evidence.SchemaVersion));
        RequireCanonical(evidence.CatalogVersion, nameof(evidence.CatalogVersion));
        RequireOpaque(evidence.HealthEvidenceReference, nameof(evidence.HealthEvidenceReference));
        RequireCanonical(evidence.Reason, nameof(evidence.Reason));

        if (evidence.Outcome != DataSourceFailoverExecutionOutcome.Authorized)
            throw new UnauthorizedAccessException("Denied failover execution evidence cannot authorize an ERP operation.");
        if (evidence.TenantId != authority.TenantId || evidence.CompanyId != authority.CompanyId || evidence.DataSourceId != authority.DataSourceId)
            throw new UnauthorizedAccessException("Failover execution evidence authority does not match the requested data source.");
        if (evidence.Operation != operation)
            throw new UnauthorizedAccessException("Failover execution evidence does not authorize the requested operation.");
        if (!StringComparer.Ordinal.Equals(evidence.EndpointId, decision.EndpointId))
            throw new InvalidOperationException("Failover execution evidence endpoint does not match the persisted decision.");
        if (!StringComparer.Ordinal.Equals(evidence.RegistryVersion, authority.RegistryVersion) ||
            !StringComparer.Ordinal.Equals(evidence.SchemaVersion, authority.SchemaVersion) ||
            !StringComparer.Ordinal.Equals(evidence.CatalogVersion, authority.CatalogVersion))
            throw new InvalidOperationException("Failover execution evidence is outside the required registry/schema/catalog version fence.");
        if (!StringComparer.Ordinal.Equals(evidence.HealthEvidenceReference, decision.EvidenceReference) || evidence.HealthObservedAt != decision.ObservedAt)
            throw new InvalidOperationException("Failover execution evidence is not bound to the persisted decision health observation.");
        if (evidence.RevalidatedAt < evidence.HealthObservedAt)
            throw new InvalidOperationException("Failover execution evidence revalidation precedes its health observation.");
        if (!StringComparer.Ordinal.Equals(evidence.Reason, "execution-authorized"))
            throw new InvalidOperationException("Authorized failover execution evidence has an invalid reason.");

        var expectedDecisionIdentity = ComputeIdentity("decision", decision.TenantId, decision.CompanyId, decision.DataSourceId,
            decision.EndpointId, decision.RegistryVersion, decision.SchemaVersion, decision.CatalogVersion,
            decision.Operation, decision.EvidenceReference, decision.ObservedAt);
        if (!StringComparer.Ordinal.Equals(evidence.DecisionIdentity, expectedDecisionIdentity))
            throw new InvalidOperationException("Failover execution evidence decision identity is invalid.");

        var expectedExecutionEvidenceId = ComputeIdentity("execution", expectedDecisionIdentity,
            evidence.HealthEvidenceReference, evidence.HealthObservedAt, evidence.RevalidatedAt);
        if (!StringComparer.Ordinal.Equals(evidence.ExecutionEvidenceId, expectedExecutionEvidenceId))
            throw new InvalidOperationException("Failover execution evidence identity is invalid.");
    }

    public static void ValidateAtOperationTime(
        DataSourceFailoverAuthority authority,
        DataSourceOperationKind operation,
        DataSourceFailoverDecision decision,
        DataSourceFailoverExecutionEvidence evidence,
        DateTimeOffset operationAt)
    {
        Validate(authority, operation, decision, evidence);

        if (operationAt < evidence.RevalidatedAt)
            throw new InvalidOperationException("ERP operation time cannot precede failover execution revalidation.");
        if (operationAt - evidence.HealthObservedAt > authority.MaxHealthEvidenceAge)
            throw new UnauthorizedAccessException("Failover execution evidence is stale at ERP operation time.");
    }

    private static string ComputeIdentity(string prefix, params object[] values)
    {
        var canonical = string.Join("\u001f", values.Select(value => value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O"),
            Enum enumeration => Convert.ToInt64(enumeration).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        }));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}-sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static void RequireOpaque(string value, string name)
    {
        RequireCanonical(value, name);
        if (value.Contains('=') || value.Contains(';'))
            throw new ArgumentException($"{name} must be opaque and must not contain connection-string material.", name);
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
