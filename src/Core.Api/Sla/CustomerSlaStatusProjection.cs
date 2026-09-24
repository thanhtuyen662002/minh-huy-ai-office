using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api.Sla;

public sealed record CustomerSlaStatusAuthority(
    Guid TenantId,
    Guid CompanyId,
    Guid UserId,
    long AuthorityVersion);

public sealed record CustomerSlaStatusSnapshot(
    CustomerSlaStatusAuthority Authority,
    string ServiceLabel,
    int SchedulerPriority,
    int PriorityCeiling,
    long PolicyVersion,
    DateTimeOffset EffectiveAt,
    string? AdmissionId = null);

public sealed record CustomerSlaStatusResponse(
    string ServiceLabel,
    int SchedulerPriority,
    int PriorityCeiling,
    long PolicyVersion,
    DateTimeOffset EffectiveAt,
    string? AdmissionId);

public interface ICustomerSlaStatusSource
{
    Task<CustomerSlaStatusSnapshot?> GetCurrentAsync(
        AuthorizationContext authority,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableCustomerSlaStatusSource : ICustomerSlaStatusSource
{
    public Task<CustomerSlaStatusSnapshot?> GetCurrentAsync(
        AuthorizationContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<CustomerSlaStatusSnapshot?>(null);
    }
}

public sealed class CustomerSlaStatusProjection(ICustomerSlaStatusSource source)
{
    public async Task<CustomerSlaStatusResponse?> GetCurrentAsync(
        IRequestAuthorizationContextAccessor accessor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        var current = accessor.Current?.Context
            ?? throw new UnauthorizedAccessException("Authenticated SLA authority is required.");

        var snapshot = await source.GetCurrentAsync(current, cancellationToken);
        if (snapshot is null)
            return null;

        ValidateSnapshot(current, snapshot);
        return new CustomerSlaStatusResponse(
            snapshot.ServiceLabel,
            snapshot.SchedulerPriority,
            snapshot.PriorityCeiling,
            snapshot.PolicyVersion,
            snapshot.EffectiveAt,
            snapshot.AdmissionId);
    }

    private static void ValidateSnapshot(AuthorizationContext current, CustomerSlaStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.Authority);
        var authority = snapshot.Authority;
        if (authority.TenantId != current.TenantId
            || authority.CompanyId != current.CompanyId
            || authority.UserId != current.UserId
            || authority.AuthorityVersion <= 0)
        {
            throw new UnauthorizedAccessException("SLA status authority does not match the authenticated request authority.");
        }

        if (snapshot.PolicyVersion <= 0)
            throw new UnauthorizedAccessException("SLA status policy revision is invalid or stale.");
        if (string.IsNullOrWhiteSpace(snapshot.ServiceLabel)
            || !string.Equals(snapshot.ServiceLabel, snapshot.ServiceLabel.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("SLA status service label is invalid.");
        if (snapshot.PriorityCeiling < 0
            || snapshot.SchedulerPriority < 0
            || snapshot.SchedulerPriority > snapshot.PriorityCeiling)
            throw new InvalidOperationException("SLA status priority is outside its authoritative ceiling.");
        if (snapshot.EffectiveAt == default)
            throw new InvalidOperationException("SLA status effective timestamp is invalid.");
        if (snapshot.AdmissionId is not null
            && (string.IsNullOrWhiteSpace(snapshot.AdmissionId)
                || !string.Equals(snapshot.AdmissionId, snapshot.AdmissionId.Trim(), StringComparison.Ordinal)))
            throw new InvalidOperationException("SLA status admission metadata is invalid.");
    }
}
