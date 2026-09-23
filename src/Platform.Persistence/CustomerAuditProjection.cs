namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record CustomerAuditQuery(Guid TenantId, Guid CompanyId, Guid UserId, int Offset = 0, int Limit = 50);

public sealed record CustomerAuditItem(
    Guid AuditId,
    Guid TaskId,
    string Resource,
    string Action,
    string Risk,
    bool Authorized,
    string DecisionReason,
    DateTimeOffset OccurredAtUtc,
    string? ExecutionId);

public interface ICustomerAuditStore
{
    Task<IReadOnlyList<ToolExecutionAuditEntry>> ListAsync(
        Guid tenantId,
        Guid companyId,
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces the customer-visible audit projection only from server-derived authority.
/// The projection deliberately excludes tenant/company/user identifiers and any credential material.
/// </summary>
public sealed class CustomerAuditProjection(ICustomerAuditStore store)
{
    public async Task<IReadOnlyList<CustomerAuditItem>> ListAsync(
        CustomerAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.TenantId == Guid.Empty || query.CompanyId == Guid.Empty || query.UserId == Guid.Empty)
            throw new UnauthorizedAccessException("Customer audit authority must be complete.");
        if (query.Offset < 0 || query.Limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(query), "Offset must be non-negative and limit must be between 1 and 100.");

        var entries = await store.ListAsync(query.TenantId, query.CompanyId, query.UserId, cancellationToken);

        // Defense in depth: a faulty store must never widen the caller's server-derived authority.
        if (entries.Any(entry => entry.TenantId != query.TenantId || entry.CompanyId != query.CompanyId || entry.UserId != query.UserId))
            throw new UnauthorizedAccessException("Audit store returned evidence outside the authorized scope.");

        return entries
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.AuditId)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(entry => new CustomerAuditItem(
                entry.AuditId,
                entry.TaskId,
                entry.Resource,
                entry.Action,
                entry.Risk.ToString(),
                entry.Authorized,
                entry.DecisionReason,
                entry.OccurredAtUtc,
                entry.ExecutionId))
            .ToArray();
    }
}
