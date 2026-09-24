namespace MinhHuy.AIOffice.Shared.Contracts.Erp;

/// <summary>
/// Server-derived operation policy fence applied immediately before an ERP operation executes.
/// Policy versions are monotonic authority metadata; callers must not derive or override them.
/// </summary>
public sealed record DataSourceFailoverOperationPolicy(long Version)
{
    public DataSourceFailoverOperationPolicy Validate()
    {
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
