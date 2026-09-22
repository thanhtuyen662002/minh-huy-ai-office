namespace MinhHuy.AIOffice.Shared.Contracts;

public sealed record CompanyBillingAuthority(Guid TenantId, Guid CompanyId)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant authority is required.", nameof(TenantId));
        }

        if (CompanyId == Guid.Empty)
        {
            throw new ArgumentException("Company authority is required.", nameof(CompanyId));
        }
    }
}

public sealed record CompanyPlan(
    CompanyBillingAuthority Authority,
    string PlanCode,
    long IncludedAiCredits,
    long UsedAiCredits)
{
    public long RemainingAiCredits => Math.Max(0, IncludedAiCredits - UsedAiCredits);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Authority);
        Authority.Validate();

        if (string.IsNullOrWhiteSpace(PlanCode))
        {
            throw new ArgumentException("Plan code is required.", nameof(PlanCode));
        }

        if (IncludedAiCredits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(IncludedAiCredits));
        }

        if (UsedAiCredits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(UsedAiCredits));
        }
    }

    public void DemandAuthority(CompanyBillingAuthority caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Validate();
        Validate();

        if (caller.TenantId != Authority.TenantId || caller.CompanyId != Authority.CompanyId)
        {
            throw new UnauthorizedAccessException("Billing data is scoped to the authoritative tenant and company.");
        }
    }
}
