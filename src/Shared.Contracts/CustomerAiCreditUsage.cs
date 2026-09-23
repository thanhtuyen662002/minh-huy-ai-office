namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerBillingAuthority(string TenantId, string CompanyId, long AuthorityVersion);

public sealed record CustomerCreditPricing(string PolicyId, long PolicyVersion, long TokenEquivalentPerCredit);

public sealed record CustomerUsageEvidence(long AuthorityVersion, AiUsageEntry Entry);

public sealed record CustomerAiCreditProjection(
    CustomerBillingAuthority Authority,
    string PricingPolicyId,
    long PricingPolicyVersion,
    long TotalTokenEquivalent,
    long UsedAiCredits,
    IReadOnlyList<string> UsageEntryIds);

public static class CustomerAiCreditUsage
{
    public static CustomerAiCreditProjection Project(
        CustomerBillingAuthority authority,
        CustomerCreditPricing pricing,
        IEnumerable<CustomerUsageEvidence> evidence)
    {
        ValidateAuthority(authority);
        ValidatePricing(pricing);
        ArgumentNullException.ThrowIfNull(evidence);

        var byId = new Dictionary<string, AiUsageEntry>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.AuthorityVersion != authority.AuthorityVersion)
                throw new UnauthorizedAccessException("Usage evidence authority version is stale or mismatched.");

            var validated = AiUsageLedger.Validate(item.Entry);
            if (!string.Equals(validated.Scope.TenantId, authority.TenantId, StringComparison.Ordinal) ||
                !string.Equals(validated.Scope.CompanyId, authority.CompanyId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Usage evidence is outside the authoritative billing scope.");
            }

            if (byId.TryGetValue(validated.EntryId, out var existing))
            {
                if (existing != validated)
                    throw new InvalidOperationException("Conflicting usage evidence shares the same durable entry identity.");
                continue;
            }

            byId.Add(validated.EntryId, validated);
        }

        long totalTokens = 0;
        foreach (var entry in byId.Values)
            totalTokens = checked(totalTokens + entry.Usage.TotalTokenEquivalent);

        var usedCredits = totalTokens == 0
            ? 0
            : checked((totalTokens + pricing.TokenEquivalentPerCredit - 1) / pricing.TokenEquivalentPerCredit);

        return new CustomerAiCreditProjection(
            authority,
            pricing.PolicyId,
            pricing.PolicyVersion,
            totalTokens,
            usedCredits,
            byId.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
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
