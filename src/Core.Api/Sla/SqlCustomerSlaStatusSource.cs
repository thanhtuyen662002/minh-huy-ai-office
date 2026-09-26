using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api.Sla;

public sealed class SqlCustomerSlaStatusSource(
    PlatformDbContext dbContext,
    TimeProvider timeProvider) : ICustomerSlaStatusSource
{
    public async Task<CustomerSlaStatusSnapshot?> GetCurrentAsync(
        AuthorizationContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var now = timeProvider.GetUtcNow();
        var candidates = await dbContext.CustomerSlaPolicyRevisions
            .AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId
                && row.CompanyId == authority.CompanyId
                && row.UserId == authority.UserId
                && row.EffectiveAtUtc <= now)
            .OrderByDescending(row => row.EffectiveAtUtc)
            .ThenByDescending(row => row.PolicyVersion)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return null;

        var current = candidates[0];
        if (candidates.Count > 1 && candidates[1].EffectiveAtUtc == current.EffectiveAtUtc)
            throw new InvalidOperationException("Current SLA policy revision is ambiguous.");

        Validate(current);
        return new CustomerSlaStatusSnapshot(
            new CustomerSlaStatusAuthority(
                current.TenantId,
                current.CompanyId,
                current.UserId,
                current.AuthorityVersion),
            current.ServiceLabel,
            current.SchedulerPriority,
            current.PriorityCeiling,
            current.PolicyVersion,
            current.EffectiveAtUtc);
    }

    private static void Validate(CustomerSlaPolicyRevisionRecord row)
    {
        if (row.AuthorityVersion <= 0 || row.PolicyVersion <= 0)
            throw new UnauthorizedAccessException("Current SLA authority or policy revision is invalid.");
        if (string.IsNullOrWhiteSpace(row.ServiceLabel)
            || !string.Equals(row.ServiceLabel, row.ServiceLabel.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Current SLA service label is invalid.");
        if (row.SchedulerPriority < 0
            || row.PriorityCeiling < 0
            || row.SchedulerPriority > row.PriorityCeiling)
            throw new InvalidOperationException("Current SLA priority is outside its authoritative ceiling.");
        if (row.EffectiveAtUtc == default || row.EffectiveAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Current SLA effective timestamp must be canonical UTC.");
    }
}

public static class CustomerSlaStatusServiceCollectionExtensions
{
    public static IServiceCollection AddCustomerSlaStatusSource(
        this IServiceCollection services,
        bool persistenceConfigured)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        if (persistenceConfigured)
            services.AddScoped<ICustomerSlaStatusSource, SqlCustomerSlaStatusSource>();
        else
            services.AddSingleton<ICustomerSlaStatusSource, UnavailableCustomerSlaStatusSource>();
        return services;
    }
}
