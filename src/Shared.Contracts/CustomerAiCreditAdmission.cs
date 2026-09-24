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
    long ActiveReservedAiCredits,
    long RequestedAiCredits,
    long ProjectedAiCredits,
    bool Allowed);

public static class CustomerAiCreditAdmission
{
    public static CustomerAiCreditAdmissionDecision Decide(
        CustomerAiCreditAdmissionRequest request,
        CustomerAiCreditProjection usage,
        CustomerAiCreditActiveReservationProjection activeReservations)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(activeReservations);
        ValidateAuthority(request.Authority);
        ValidatePricing(request.Pricing);
        ArgumentNullException.ThrowIfNull(request.Allowance);

        if (request.Allowance.CreditLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(request.Allowance.CreditLimit));
        if (request.RequestedMaxTokenEquivalent < 0)
            throw new ArgumentOutOfRangeException(nameof(request.RequestedMaxTokenEquivalent));
        if (usage.UsedAiCredits < 0 || usage.TotalTokenEquivalent < 0)
            throw new ArgumentException("Usage projection cannot contain negative usage.", nameof(usage));
        if (activeReservations.ActiveReservedAiCredits < 0)
            throw new ArgumentException("Active reservation projection cannot contain negative reserved credits.", nameof(activeReservations));

        ValidateProjectionScope(usage.Authority, usage.PricingPolicyId, usage.PricingPolicyVersion, request, "Usage");
        ValidateProjectionScope(activeReservations.Authority, activeReservations.PricingPolicyId, activeReservations.PricingPolicyVersion, request, "Active reservation");

        var requestedCredits = CreditsFor(request.RequestedMaxTokenEquivalent, request.Pricing.TokenEquivalentPerCredit);
        var committedCredits = checked(usage.UsedAiCredits + activeReservations.ActiveReservedAiCredits);
        var projectedCredits = checked(committedCredits + requestedCredits);

        return new CustomerAiCreditAdmissionDecision(
            request.Authority,
            request.Pricing.PolicyId,
            request.Pricing.PolicyVersion,
            request.Allowance.CreditLimit,
            usage.UsedAiCredits,
            activeReservations.ActiveReservedAiCredits,
            requestedCredits,
            projectedCredits,
            projectedCredits <= request.Allowance.CreditLimit);
    }

    private static void ValidateProjectionScope(
        CustomerBillingAuthority authority,
        string pricingPolicyId,
        long pricingPolicyVersion,
        CustomerAiCreditAdmissionRequest request,
        string projectionKind)
    {
        if (authority != request.Authority)
            throw new UnauthorizedAccessException($"{projectionKind} projection is outside the authoritative billing scope or version.");
        if (!string.Equals(pricingPolicyId, request.Pricing.PolicyId, StringComparison.Ordinal) ||
            pricingPolicyVersion != request.Pricing.PolicyVersion)
        {
            throw new InvalidOperationException($"{projectionKind} projection pricing policy is stale or mismatched.");
        }
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
