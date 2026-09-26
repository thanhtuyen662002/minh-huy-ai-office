using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.Erp;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class DataSourceFailoverOperationPolicyStore(PlatformDbContext dbContext)
{
    public async Task<DataSourceFailoverOperationPolicy> ResolveEffectiveAsync(DataSourceFailoverAuthority authority, DataSourceOperationKind operation, DateTimeOffset operationAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (operationAt == default) throw new ArgumentException("Operation timestamp is required.", nameof(operationAt));

        var candidates = await dbContext.DataSourceFailoverOperationPolicies.AsNoTracking()
            .Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId && x.DataSourceId == authority.DataSourceId && x.Operation == operation && x.EffectiveAt <= operationAt)
            .OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.Version).Take(2).ToListAsync(cancellationToken);

        if (candidates.Count == 0) throw new UnauthorizedAccessException("No effective failover operation policy exists for the server-derived authority.");
        if (candidates.Count > 1 && candidates[1].EffectiveAt == candidates[0].EffectiveAt) throw new InvalidOperationException("Ambiguous active failover operation policy revisions.");
        return candidates[0].ToContract().Validate();
    }

    public async Task PersistAsync(DataSourceFailoverOperationPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        var existing = await dbContext.DataSourceFailoverOperationPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == policy.TenantId && x.CompanyId == policy.CompanyId && x.DataSourceId == policy.DataSourceId && x.Operation == policy.Operation && x.Version == policy.Version, cancellationToken);
        if (existing is not null)
        {
            if (existing.EffectiveAt != policy.EffectiveAt) throw new InvalidOperationException("Failover operation policy version is immutable.");
            return;
        }
        dbContext.DataSourceFailoverOperationPolicies.Add(new DataSourceFailoverOperationPolicyRecord { TenantId = policy.TenantId, CompanyId = policy.CompanyId, DataSourceId = policy.DataSourceId, Operation = policy.Operation, Version = policy.Version, EffectiveAt = policy.EffectiveAt });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
