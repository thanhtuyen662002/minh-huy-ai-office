namespace MinhHuy.AIOffice.Shared.Contracts.Erp;

/// <summary>
/// Server-derived operation policy fence applied immediately before an ERP operation executes.
/// Policy scope, operation and versions are authority metadata; callers must not derive or override them.
/// </summary>
public sealed record DataSourceFailoverOperationPolicy(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    DataSourceOperationKind Operation,
    long Version)
{
    public DataSourceFailoverOperationPolicy Validate()
    {
        if (TenantId == Guid.Empty)
            throw new ArgumentException("Failover operation policy tenant scope is required.", nameof(TenantId));
        if (CompanyId == Guid.Empty)
            throw new ArgumentException("Failover operation policy company scope is required.", nameof(CompanyId));
        if (DataSourceId == Guid.Empty)
            throw new ArgumentException("Failover operation policy data-source scope is required.", nameof(DataSourceId));
        if (!Enum.IsDefined(Operation))
            throw new ArgumentOutOfRangeException(nameof(Operation), "Failover operation policy operation must be defined.");
        if (Version <= 0)
            throw new ArgumentOutOfRangeException(nameof(Version), "Failover operation policy version must be positive.");
        return this;
    }
}

public sealed record DataSourceFailoverOperationAuthorization(
    DataSourceFailoverExecutionEvidence Evidence,
    long OperationPolicyVersion);

public static class DataSourceFailoverOperationPolicyContract
{
    public static void ValidateForExecution(
        DataSourceFailoverAuthority authority,
        DataSourceOperationKind operation,
        DataSourceFailoverDecision decision,
        DataSourceFailoverOperationPolicy authoritativePolicy,
        DataSourceFailoverOperationAuthorization authorization,
        DateTimeOffset operationAt)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(authoritativePolicy);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(authorization.Evidence);
        authoritativePolicy.Validate();

        if (authoritativePolicy.TenantId != authority.TenantId ||
            authoritativePolicy.CompanyId != authority.CompanyId ||
            authoritativePolicy.DataSourceId != authority.DataSourceId)
            throw new UnauthorizedAccessException("Failover operation policy is outside the server-derived authority scope.");
        if (authoritativePolicy.Operation != operation)
            throw new UnauthorizedAccessException("Failover operation policy does not authorize the requested operation.");

        if (authorization.OperationPolicyVersion <= 0)
            throw new InvalidOperationException("Persisted failover authorization has an invalid operation policy version.");
        if (authorization.OperationPolicyVersion != authoritativePolicy.Version)
            throw new UnauthorizedAccessException("Persisted failover authorization is outside the authoritative operation policy version fence.");

        DataSourceFailoverExecutionAuthorization.ValidateAtOperationTime(
            authority,
            operation,
            decision,
            authorization.Evidence,
            operationAt);
    }
}
