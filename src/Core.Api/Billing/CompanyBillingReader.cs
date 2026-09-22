using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api.Billing;

public interface ICompanyBillingPlanSource
{
    ValueTask<CompanyPlan?> GetAsync(
        CompanyBillingAuthority authority,
        CancellationToken cancellationToken = default);
}

public sealed class CompanyBillingReader(ICompanyBillingPlanSource source)
{
    public async ValueTask<CompanyPlan?> GetCurrentAsync(
        IRequestAuthorizationContextAccessor accessor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        var context = accessor.Current?.Context
            ?? throw new UnauthorizedAccessException("Server-derived authorization context is required.");
        var authority = new CompanyBillingAuthority(context.TenantId, context.CompanyId);
        authority.Validate();

        var plan = await source.GetAsync(authority, cancellationToken);
        if (plan is null)
        {
            return null;
        }

        plan.DemandAuthority(authority);
        return plan;
    }
}
