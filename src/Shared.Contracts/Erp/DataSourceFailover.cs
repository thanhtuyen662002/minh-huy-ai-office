using System.Security.Cryptography;
using System.Text;

namespace MinhHuy.AIOffice.Shared.Contracts.Erp;

public enum DataSourceFailoverRole { Primary = 0, Fallback = 1 }
public enum DataSourceAccessMode { ReadOnly = 0, ReadWrite = 1 }
public enum DataSourceOperationKind { Read = 0, Write = 1 }
public enum DataSourceHealthState { Unhealthy = 0, Healthy = 1 }
public enum DataSourceFailoverExecutionOutcome { Denied = 0, Authorized = 1 }

public sealed record DataSourceFailoverAuthority(Guid TenantId, Guid CompanyId, Guid DataSourceId, string RegistryVersion, string SchemaVersion, string CatalogVersion, TimeSpan MaxHealthEvidenceAge);

public sealed record DataSourceFailoverCandidate(
    Guid TenantId, Guid CompanyId, Guid DataSourceId, string EndpointId, string CredentialReference,
    DataSourceFailoverRole Role, DataSourceAccessMode AccessMode, int Priority,
    string RegistryVersion, string SchemaVersion, string CatalogVersion);

public sealed record DataSourceHealthEvidence(
    Guid TenantId, Guid CompanyId, Guid DataSourceId, string EndpointId,
    string RegistryVersion, string SchemaVersion, string CatalogVersion,
    DataSourceHealthState State, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt,
    string EvidenceReference);

public sealed record DataSourceFailoverDecision(
    Guid TenantId, Guid CompanyId, Guid DataSourceId, string EndpointId,
    string RegistryVersion, string SchemaVersion, string CatalogVersion,
    DataSourceOperationKind Operation, string EvidenceReference, DateTimeOffset ObservedAt, string Reason);

public sealed record DataSourceFailoverExecutionEvidence(
    string ExecutionEvidenceId,
    string DecisionIdentity,
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string EndpointId,
    string RegistryVersion,
    string SchemaVersion,
    string CatalogVersion,
    DataSourceOperationKind Operation,
    string HealthEvidenceReference,
    DateTimeOffset HealthObservedAt,
    DateTimeOffset RevalidatedAt,
    DataSourceFailoverExecutionOutcome Outcome,
    string Reason);

public static class DataSourceFailoverContract
{
    public static DataSourceFailoverDecision Select(
        DataSourceFailoverAuthority authority,
        DataSourceOperationKind operation,
        IReadOnlyCollection<DataSourceFailoverCandidate> candidates,
        IReadOnlyCollection<DataSourceHealthEvidence> healthEvidence,
        DateTimeOffset decisionAt)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(healthEvidence);
        ValidateAuthority(authority);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (candidates.Count == 0) throw new InvalidOperationException("No failover candidates are available.");

        var evidenceByReference = new Dictionary<string, DataSourceHealthEvidence>(StringComparer.Ordinal);
        var evidenceByEndpoint = new Dictionary<string, DataSourceHealthEvidence>(StringComparer.Ordinal);
        foreach (var evidence in healthEvidence)
        {
            ValidateEvidence(evidence, authority, decisionAt);
            if (evidenceByReference.TryGetValue(evidence.EvidenceReference, out var replay))
            {
                if (replay != evidence)
                    throw new InvalidOperationException("Health evidence reference was reused with conflicting evidence.");
                continue;
            }
            evidenceByReference.Add(evidence.EvidenceReference, evidence);

            if (!evidenceByEndpoint.TryGetValue(evidence.EndpointId, out var current) || evidence.ObservedAt > current.ObservedAt)
            {
                evidenceByEndpoint[evidence.EndpointId] = evidence;
                continue;
            }

            if (evidence.ObservedAt == current.ObservedAt && evidence != current)
                throw new InvalidOperationException("Health evidence contains conflicting observations at the same endpoint timestamp.");
        }

        var eligible = new List<(DataSourceFailoverCandidate Candidate, DataSourceHealthEvidence Evidence)>();
        var endpointIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            ValidateCandidate(candidate);
            if (!endpointIds.Add(candidate.EndpointId))
                throw new InvalidOperationException("Failover candidates contain a duplicate endpoint identity.");
            ValidateCandidateAuthority(candidate, authority);

            if (!evidenceByEndpoint.TryGetValue(candidate.EndpointId, out var evidence))
                continue;
            if (evidence.State == DataSourceHealthState.Healthy &&
                (operation != DataSourceOperationKind.Write || candidate.AccessMode == DataSourceAccessMode.ReadWrite))
                eligible.Add((candidate, evidence));
        }

        var selected = eligible
            .OrderBy(x => x.Candidate.Role)
            .ThenBy(x => x.Candidate.Priority)
            .ThenBy(x => x.Candidate.EndpointId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected.Candidate is null)
            throw new InvalidOperationException(operation == DataSourceOperationKind.Write
                ? "No healthy write-capable data source satisfies the authority, version and freshness fences."
                : "No healthy data source satisfies the authority, version and freshness fences.");

        return new DataSourceFailoverDecision(
            authority.TenantId, authority.CompanyId, authority.DataSourceId, selected.Candidate.EndpointId,
            authority.RegistryVersion, authority.SchemaVersion, authority.CatalogVersion,
            operation, selected.Evidence.EvidenceReference, selected.Evidence.ObservedAt,
            selected.Candidate.Role == DataSourceFailoverRole.Primary ? "primary-healthy" : "primary-unavailable-fallback-selected");
    }

    public static void ValidateDecision(
        DataSourceFailoverAuthority authority,
        DataSourceOperationKind operation,
        DataSourceFailoverDecision decision)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(decision);
        ValidateAuthority(authority);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (!Enum.IsDefined(decision.Operation)) throw new ArgumentOutOfRangeException(nameof(decision.Operation));
        RequireCanonical(decision.EndpointId, nameof(decision.EndpointId));
        RequireOpaqueReference(decision.EvidenceReference, nameof(decision.EvidenceReference));

        if (decision.TenantId != authority.TenantId || decision.CompanyId != authority.CompanyId || decision.DataSourceId != authority.DataSourceId)
            throw new UnauthorizedAccessException("Failover decision authority does not match the requested data source.");
        if (decision.Operation != operation)
            throw new UnauthorizedAccessException("Failover decision operation does not authorize the requested operation.");
        if (!StringComparer.Ordinal.Equals(decision.RegistryVersion, authority.RegistryVersion) ||
            !StringComparer.Ordinal.Equals(decision.SchemaVersion, authority.SchemaVersion) ||
            !StringComparer.Ordinal.Equals(decision.CatalogVersion, authority.CatalogVersion))
            throw new InvalidOperationException("Failover decision is outside the required registry/schema/catalog version fence.");
    }

    public static void ValidateDecisionForExecution(
        DataSourceFailoverAuthority authority,
        DataSourceOperationKind operation,
        DataSourceFailoverDecision decision,
        DataSourceHealthEvidence authoritativeEvidence,
        DateTimeOffset executionAt)
    {
        ArgumentNullException.ThrowIfNull(authoritativeEvidence);
        ValidateDecision(authority, operation, decision);
        ValidateEvidence(authoritativeEvidence, authority, executionAt);

        if (authoritativeEvidence.State != DataSourceHealthState.Healthy)
            throw new InvalidOperationException("Failover decision health evidence is not healthy at execution time.");
        ValidateBoundObservation(decision, authoritativeEvidence);
    }

    public static DataSourceFailoverExecutionEvidence CreateExecutionEvidence(
        DataSourceFailoverAuthority authority,
        DataSourceOperationKind operation,
        DataSourceFailoverDecision decision,
        DataSourceHealthEvidence authoritativeEvidence,
        DateTimeOffset executionAt)
    {
        ArgumentNullException.ThrowIfNull(authoritativeEvidence);
        ValidateDecision(authority, operation, decision);
        ValidateEvidenceAuthority(authoritativeEvidence, authority);
        ValidateBoundObservation(decision, authoritativeEvidence);

        var denialReason = GetExecutionDenialReason(authority, authoritativeEvidence, executionAt);
        var outcome = denialReason is null ? DataSourceFailoverExecutionOutcome.Authorized : DataSourceFailoverExecutionOutcome.Denied;
        var reason = denialReason ?? "execution-authorized";
        var decisionIdentity = ComputeOpaqueIdentity("decision", decision.TenantId, decision.CompanyId, decision.DataSourceId,
            decision.EndpointId, decision.RegistryVersion, decision.SchemaVersion, decision.CatalogVersion,
            decision.Operation, decision.EvidenceReference, decision.ObservedAt);
        var executionEvidenceId = ComputeOpaqueIdentity("execution", decisionIdentity, authoritativeEvidence.EvidenceReference,
            authoritativeEvidence.ObservedAt, executionAt);

        return new DataSourceFailoverExecutionEvidence(
            executionEvidenceId, decisionIdentity,
            authority.TenantId, authority.CompanyId, authority.DataSourceId, decision.EndpointId,
            authority.RegistryVersion, authority.SchemaVersion, authority.CatalogVersion, operation,
            authoritativeEvidence.EvidenceReference, authoritativeEvidence.ObservedAt, executionAt, outcome, reason);
    }

    public static void ValidateExecutionEvidenceReplay(
        DataSourceFailoverExecutionEvidence persisted,
        DataSourceFailoverExecutionEvidence replay)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(replay);
        RequireOpaqueReference(persisted.ExecutionEvidenceId, nameof(persisted.ExecutionEvidenceId));
        RequireOpaqueReference(replay.ExecutionEvidenceId, nameof(replay.ExecutionEvidenceId));
        if (!StringComparer.Ordinal.Equals(persisted.ExecutionEvidenceId, replay.ExecutionEvidenceId) || persisted != replay)
            throw new InvalidOperationException("Failover execution evidence replay conflicts with persisted evidence.");
    }

    private static string? GetExecutionDenialReason(DataSourceFailoverAuthority authority, DataSourceHealthEvidence evidence, DateTimeOffset executionAt)
    {
        if (evidence.ObservedAt > executionAt) return "health-evidence-future";
        if (evidence.ExpiresAt <= evidence.ObservedAt || evidence.ExpiresAt <= executionAt) return "health-evidence-expired";
        if (executionAt - evidence.ObservedAt > authority.MaxHealthEvidenceAge) return "health-evidence-stale";
        if (evidence.State != DataSourceHealthState.Healthy) return "health-evidence-unhealthy";
        return null;
    }

    private static void ValidateBoundObservation(DataSourceFailoverDecision decision, DataSourceHealthEvidence evidence)
    {
        if (!StringComparer.Ordinal.Equals(evidence.EndpointId, decision.EndpointId))
            throw new InvalidOperationException("Failover decision endpoint does not match the authoritative health evidence.");
        if (!StringComparer.Ordinal.Equals(evidence.EvidenceReference, decision.EvidenceReference) || evidence.ObservedAt != decision.ObservedAt)
            throw new InvalidOperationException("Failover decision is not bound to the authoritative health observation used for execution.");
    }

    private static string ComputeOpaqueIdentity(string prefix, params object[] values)
    {
        var canonical = string.Join("\u001f", values.Select(value => value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O"),
            Enum enumeration => Convert.ToInt64(enumeration).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        }));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}-sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static void ValidateAuthority(DataSourceFailoverAuthority authority)
    {
        if (authority.TenantId == Guid.Empty || authority.CompanyId == Guid.Empty || authority.DataSourceId == Guid.Empty)
            throw new ArgumentException("Failover authority identifiers must be non-empty.");
        RequireCanonical(authority.RegistryVersion, nameof(authority.RegistryVersion));
        RequireCanonical(authority.SchemaVersion, nameof(authority.SchemaVersion));
        RequireCanonical(authority.CatalogVersion, nameof(authority.CatalogVersion));
        if (authority.MaxHealthEvidenceAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(authority.MaxHealthEvidenceAge), "Maximum health evidence age must be positive.");
    }

    private static void ValidateCandidate(DataSourceFailoverCandidate candidate)
    {
        if (candidate.TenantId == Guid.Empty || candidate.CompanyId == Guid.Empty || candidate.DataSourceId == Guid.Empty)
            throw new ArgumentException("Failover candidate authority identifiers must be non-empty.");
        RequireCanonical(candidate.EndpointId, nameof(candidate.EndpointId));
        RequireOpaqueReference(candidate.CredentialReference, nameof(candidate.CredentialReference));
        RequireCanonical(candidate.RegistryVersion, nameof(candidate.RegistryVersion));
        RequireCanonical(candidate.SchemaVersion, nameof(candidate.SchemaVersion));
        RequireCanonical(candidate.CatalogVersion, nameof(candidate.CatalogVersion));
        if (!Enum.IsDefined(candidate.Role)) throw new ArgumentOutOfRangeException(nameof(candidate.Role));
        if (!Enum.IsDefined(candidate.AccessMode)) throw new ArgumentOutOfRangeException(nameof(candidate.AccessMode));
        if (candidate.Priority < 0) throw new ArgumentOutOfRangeException(nameof(candidate.Priority));
    }

    private static void ValidateCandidateAuthority(DataSourceFailoverCandidate candidate, DataSourceFailoverAuthority authority)
    {
        if (candidate.TenantId != authority.TenantId || candidate.CompanyId != authority.CompanyId || candidate.DataSourceId != authority.DataSourceId)
            throw new UnauthorizedAccessException("Failover candidate authority does not match the requested data source.");
        if (!StringComparer.Ordinal.Equals(candidate.RegistryVersion, authority.RegistryVersion))
            throw new InvalidOperationException("Failover candidate registry evidence is stale.");
        if (!StringComparer.Ordinal.Equals(candidate.SchemaVersion, authority.SchemaVersion) || !StringComparer.Ordinal.Equals(candidate.CatalogVersion, authority.CatalogVersion))
            throw new InvalidOperationException("Failover candidate is outside the required schema/catalog compatibility fence.");
    }

    private static void ValidateEvidence(DataSourceHealthEvidence evidence, DataSourceFailoverAuthority authority, DateTimeOffset decisionAt)
    {
        ValidateEvidenceAuthority(evidence, authority);
        if (evidence.ObservedAt > decisionAt)
            throw new InvalidOperationException("Health evidence cannot be observed in the future.");
        if (evidence.ExpiresAt <= evidence.ObservedAt || evidence.ExpiresAt <= decisionAt)
            throw new InvalidOperationException("Health evidence is malformed or expired.");
        if (decisionAt - evidence.ObservedAt > authority.MaxHealthEvidenceAge)
            throw new InvalidOperationException("Health evidence exceeds the authority freshness policy.");
    }

    private static void ValidateEvidenceAuthority(DataSourceHealthEvidence evidence, DataSourceFailoverAuthority authority)
    {
        if (evidence.TenantId == Guid.Empty || evidence.CompanyId == Guid.Empty || evidence.DataSourceId == Guid.Empty)
            throw new ArgumentException("Health evidence authority identifiers must be non-empty.");
        RequireCanonical(evidence.EndpointId, nameof(evidence.EndpointId));
        RequireCanonical(evidence.RegistryVersion, nameof(evidence.RegistryVersion));
        RequireCanonical(evidence.SchemaVersion, nameof(evidence.SchemaVersion));
        RequireCanonical(evidence.CatalogVersion, nameof(evidence.CatalogVersion));
        RequireOpaqueReference(evidence.EvidenceReference, nameof(evidence.EvidenceReference));
        if (!Enum.IsDefined(evidence.State)) throw new ArgumentOutOfRangeException(nameof(evidence.State));
        if (evidence.TenantId != authority.TenantId || evidence.CompanyId != authority.CompanyId || evidence.DataSourceId != authority.DataSourceId)
            throw new UnauthorizedAccessException("Health evidence authority does not match the requested data source.");
        if (!StringComparer.Ordinal.Equals(evidence.RegistryVersion, authority.RegistryVersion) ||
            !StringComparer.Ordinal.Equals(evidence.SchemaVersion, authority.SchemaVersion) ||
            !StringComparer.Ordinal.Equals(evidence.CatalogVersion, authority.CatalogVersion))
            throw new InvalidOperationException("Health evidence is outside the required registry/schema/catalog version fence.");
    }

    private static void RequireOpaqueReference(string value, string name)
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
