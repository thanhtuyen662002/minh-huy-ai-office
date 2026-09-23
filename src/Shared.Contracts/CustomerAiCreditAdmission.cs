namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerAiCreditAllowance(long CreditLimit);

public sealed record CustomerAiCreditAdmissionRequest(
    CustomerBillingAuthority Authority,
    CustomerCreditPricing Pricing,
    CustomerAiCreditAllowance Allowance,
    long RequestedMaxTokenEquivalent);

public sealed record CustomerAiCreditAdmissionDecision(
    CustomerBillingAuthority Authority,
    string PricingPolicyId,
    long PricingPolicyVersion,
    long CreditLimit,
    long UsedAiCredits,
    long RequestedAiCredits,
    long ProjectedAiCredits,
    bool Allowed);

public static class CustomerAiCreditAdmission
{
    public static CustomerAiCreditAdmissionDecision Decide(
        CustomerAiCreditAdmissionRequest request,
        CustomerAiCreditProjection usage)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(usage);
        ValidateAuthority(request.Authority);
        ValidatePricing(request.Pricing);
        ArgumentNullException.ThrowIfNull(request.Allowance);

        if (request.Allowance.CreditLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(request.Allowance.CreditLimit));
        if (request.RequestedMaxTokenEquivalent < 0)
            throw new ArgumentOutOfRangeException(nameof(request.RequestedMaxTokenEquivalent));
        if (usage.UsedAiCredits < 0 || usage.TotalTokenEquivalent < 0)
            throw new ArgumentException("Usage projection cannot contain negative usage.", nameof(usage));

        if (usage.Authority != request.Authority)
            throw new UnauthorizedAccessException("Usage projection is outside the authoritative billing scope or version.");
        if (!string.Equals(usage.PricingPolicyId, request.Pricing.PolicyId, StringComparison.Ordinal) ||
            usage.PricingPolicyVersion != request.Pricing.PolicyVersion)
        {
            throw new InvalidOperationException("Usage projection pricing policy is stale or mismatched.");
        }

        var requestedCredits = CreditsFor(request.RequestedMaxTokenEquivalent, request.Pricing.TokenEquivalentPerCredit);
        var projectedCredits = checked(usage.UsedAiCredits + requestedCredits);

        return new CustomerAiCreditAdmissionDecision(
            request.Authority,
            request.Pricing.PolicyId,
            request.Pricing.PolicyVersion,
            request.Allowance.CreditLimit,
            usage.UsedAiCredits,
            requestedCredits,
            projectedCredits,
            projectedCredits <= request.Allowance.CreditLimit);
    }

    private static long CreditsFor(long tokens, long tokensPerCredit)
    {
        if (tokens == 0)
            return 0;
        return checked(((tokens - 1) / tokensPerCredit) + 1);
    }

    private static void ValidateAuthority(CustomerBillingAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ValidateCanonical(authority.TenantId, nameof(authority.TenantId));
        ValidateCanonical(authority.CompanyId, nameof(authority.CompanyId));
        if (authority.AuthorityVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(authority.AuthorityVersion));
    }

    private static void ValidatePricing(CustomerCreditPricing pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        ValidateCanonical(pricing.PolicyId, nameof(pricing.PolicyId));
        if (pricing.PolicyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(pricing.PolicyVersion));
        if (pricing.TokenEquivalentPerCredit <= 0)
            throw new ArgumentOutOfRangeException(nameof(pricing.TokenEquivalentPerCredit));
    }

    private static void ValidateCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be a canonical non-empty identifier.", name);
    }
}
