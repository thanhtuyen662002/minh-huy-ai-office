namespace MinhHuyAiOffice.Shared.Contracts;

public enum RuntimeExceptionDisposition
{
    RetryDeterministicOperation,
    EscalateToSpecialist,
    Reject
}

public sealed record RuntimeExceptionEnvelope(
    string TenantId,
    string CompanyId,
    string TaskId,
    string ExceptionId,
    string SourceOperationId,
    string Capability,
    string FailureCategory,
    string EvidenceRef,
    string AuditRef,
    string ReleaseId,
    string WorkflowVersion,
    string ModelPin);

public sealed record RuntimeExceptionClaim(
    string ExceptionId,
    string ClaimId,
    string TenantId,
    string CompanyId,
    string SpecialistId,
    long LeaseEpoch,
    DateTimeOffset LeaseExpiresAt);

public sealed record RuntimeExceptionResolution(
    string ExceptionId,
    string ClaimId,
    string TenantId,
    string CompanyId,
    string ResolutionEvidenceRef,
    RuntimeExceptionDisposition Disposition);

public static class RuntimeExceptionQueuePolicy
{
    public static string IdempotencyKey(RuntimeExceptionEnvelope envelope)
    {
        Validate(envelope);
        return string.Join(':', envelope.TenantId, envelope.CompanyId, envelope.TaskId, envelope.SourceOperationId);
    }

    public static RuntimeExceptionClaim Claim(
        RuntimeExceptionEnvelope envelope,
        string tenantId,
        string companyId,
        string specialistId,
        string claimId,
        long leaseEpoch,
        DateTimeOffset leaseExpiresAt,
        RuntimeExceptionClaim? currentClaim = null,
        DateTimeOffset? now = null)
    {
        Validate(envelope);
        RequireAuthority(envelope.TenantId, envelope.CompanyId, tenantId, companyId);
        Require(specialistId, nameof(specialistId));
        Require(claimId, nameof(claimId));
        if (leaseEpoch < 0) throw new ArgumentOutOfRangeException(nameof(leaseEpoch));

        var clock = now ?? DateTimeOffset.UtcNow;
        if (leaseExpiresAt <= clock)
            throw new InvalidOperationException("Exception claim lease must expire in the future.");

        if (currentClaim is not null)
        {
            RequireClaimAuthority(envelope, currentClaim);
            if (currentClaim.LeaseExpiresAt > clock)
                throw new InvalidOperationException("Exception already has a live specialist claim.");
            if (leaseEpoch <= currentClaim.LeaseEpoch)
                throw new InvalidOperationException("Reclaimed exception requires a newer lease epoch.");
        }

        return new(envelope.ExceptionId, claimId, envelope.TenantId, envelope.CompanyId,
            specialistId, leaseEpoch, leaseExpiresAt);
    }

    public static RuntimeExceptionDisposition Resolve(
        RuntimeExceptionEnvelope envelope,
        RuntimeExceptionClaim claim,
        RuntimeExceptionResolution resolution,
        string tenantId,
        string companyId,
        long currentLeaseEpoch,
        DateTimeOffset now)
    {
        Validate(envelope);
        RequireAuthority(envelope.TenantId, envelope.CompanyId, tenantId, companyId);
        RequireClaimAuthority(envelope, claim);
        Require(resolution.ResolutionEvidenceRef, nameof(resolution.ResolutionEvidenceRef));

        if (!StringComparer.Ordinal.Equals(resolution.ExceptionId, envelope.ExceptionId) ||
            !StringComparer.Ordinal.Equals(resolution.ClaimId, claim.ClaimId) ||
            !StringComparer.Ordinal.Equals(resolution.TenantId, envelope.TenantId) ||
            !StringComparer.Ordinal.Equals(resolution.CompanyId, envelope.CompanyId))
            throw new InvalidOperationException("Resolution evidence is not bound to the active exception claim.");
        if (claim.LeaseEpoch != currentLeaseEpoch || claim.LeaseExpiresAt <= now)
            throw new InvalidOperationException("Stale specialist claim cannot resolve an exception.");

        return resolution.Disposition;
    }

    private static void Validate(RuntimeExceptionEnvelope value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Require(value.TenantId, nameof(value.TenantId));
        Require(value.CompanyId, nameof(value.CompanyId));
        Require(value.TaskId, nameof(value.TaskId));
        Require(value.ExceptionId, nameof(value.ExceptionId));
        Require(value.SourceOperationId, nameof(value.SourceOperationId));
        Require(value.Capability, nameof(value.Capability));
        Require(value.FailureCategory, nameof(value.FailureCategory));
        Require(value.EvidenceRef, nameof(value.EvidenceRef));
        Require(value.AuditRef, nameof(value.AuditRef));
        Require(value.ReleaseId, nameof(value.ReleaseId));
        Require(value.WorkflowVersion, nameof(value.WorkflowVersion));
        Require(value.ModelPin, nameof(value.ModelPin));
    }

    private static void RequireClaimAuthority(RuntimeExceptionEnvelope envelope, RuntimeExceptionClaim claim)
    {
        if (!StringComparer.Ordinal.Equals(claim.ExceptionId, envelope.ExceptionId) ||
            !StringComparer.Ordinal.Equals(claim.TenantId, envelope.TenantId) ||
            !StringComparer.Ordinal.Equals(claim.CompanyId, envelope.CompanyId))
            throw new InvalidOperationException("Specialist claim authority mismatch.");
    }

    private static void RequireAuthority(string expectedTenant, string expectedCompany, string tenantId, string companyId)
    {
        Require(tenantId, nameof(tenantId));
        Require(companyId, nameof(companyId));
        if (!StringComparer.Ordinal.Equals(expectedTenant, tenantId) || !StringComparer.Ordinal.Equals(expectedCompany, companyId))
            throw new InvalidOperationException("Runtime exception authority mismatch.");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
