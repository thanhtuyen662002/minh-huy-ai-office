namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerAiCreditReservationEvidence(
    string ReservationId,
    CustomerBillingAuthority Authority,
    string PricingPolicyId,
    long PricingPolicyVersion,
    long ReservedAiCredits);

public sealed record CustomerAiCreditReservationDecision(
    CustomerAiCreditReservationEvidence Reservation,
    long UsedAiCredits,
    long ActiveReservedAiCredits,
    long ProjectedAiCredits,
    long CreditLimit,
    bool Allowed,
    bool IsReplay);

public static class CustomerAiCreditReservation
{
    public static CustomerAiCreditReservationDecision Decide(
        string reservationId,
        CustomerAiCreditAdmissionDecision admission,
        IEnumerable<CustomerAiCreditReservationEvidence> activeReservations)
    {
        ValidateCanonical(reservationId, nameof(reservationId));
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(activeReservations);
        ValidateAuthority(admission.Authority);
        ValidateCanonical(admission.PricingPolicyId, nameof(admission.PricingPolicyId));
        if (admission.PricingPolicyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(admission.PricingPolicyVersion));
        if (admission.CreditLimit < 0 || admission.UsedAiCredits < 0 || admission.ActiveReservedAiCredits < 0 || admission.RequestedAiCredits < 0 || admission.ProjectedAiCredits < 0)
            throw new ArgumentException("Admission contains negative credit values.", nameof(admission));
        if (checked(checked(admission.UsedAiCredits + admission.ActiveReservedAiCredits) + admission.RequestedAiCredits) != admission.ProjectedAiCredits)
            throw new InvalidOperationException("Admission projection is inconsistent.");
        if (admission.Allowed != (admission.ProjectedAiCredits <= admission.CreditLimit))
            throw new InvalidOperationException("Admission decision is inconsistent.");
        if (!admission.Allowed)
            throw new InvalidOperationException("A denied admission cannot reserve credits.");

        var requested = new CustomerAiCreditReservationEvidence(reservationId, admission.Authority, admission.PricingPolicyId, admission.PricingPolicyVersion, admission.RequestedAiCredits);
        var unique = new Dictionary<string, CustomerAiCreditReservationEvidence>(StringComparer.Ordinal);
        foreach (var evidence in activeReservations)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            ValidateEvidence(evidence, admission);
            if (unique.TryGetValue(evidence.ReservationId, out var existing))
            {
                if (existing != evidence) throw new InvalidOperationException("Conflicting reservation replay evidence.");
                continue;
            }
            unique.Add(evidence.ReservationId, evidence);
        }

        var isReplay = unique.TryGetValue(reservationId, out var replay);
        if (isReplay && replay != requested) throw new InvalidOperationException("Reservation identity was reused with conflicting evidence.");
        var snapshotEvidence = isReplay ? unique.Values.Where(x => x.ReservationId != reservationId) : unique.Values;
        var observedActiveReserved = SumReserved(snapshotEvidence);
        if (observedActiveReserved != admission.ActiveReservedAiCredits)
            throw new InvalidOperationException("Active reservation evidence does not match the authoritative admission snapshot.");

        return new(requested, admission.UsedAiCredits, admission.ActiveReservedAiCredits, admission.ProjectedAiCredits, admission.CreditLimit, admission.Allowed, isReplay);
    }

    private static long SumReserved(IEnumerable<CustomerAiCreditReservationEvidence> evidence)
    {
        long total = 0;
        foreach (var item in evidence.OrderBy(x => x.ReservationId, StringComparer.Ordinal)) total = checked(total + item.ReservedAiCredits);
        return total;
    }

    private static void ValidateEvidence(CustomerAiCreditReservationEvidence evidence, CustomerAiCreditAdmissionDecision admission)
    {
        ValidateCanonical(evidence.ReservationId, nameof(evidence.ReservationId));
        ValidateAuthority(evidence.Authority);
        ValidateCanonical(evidence.PricingPolicyId, nameof(evidence.PricingPolicyId));
        if (evidence.PricingPolicyVersion <= 0 || evidence.ReservedAiCredits < 0) throw new ArgumentException("Reservation evidence contains invalid credit or policy values.", nameof(evidence));
        if (evidence.Authority != admission.Authority) throw new UnauthorizedAccessException("Reservation evidence is outside the authoritative billing scope or version.");
        if (!string.Equals(evidence.PricingPolicyId, admission.PricingPolicyId, StringComparison.Ordinal) || evidence.PricingPolicyVersion != admission.PricingPolicyVersion)
            throw new InvalidOperationException("Reservation evidence pricing policy is stale or mismatched.");
    }

    private static void ValidateAuthority(CustomerBillingAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ValidateCanonical(authority.TenantId, nameof(authority.TenantId));
        ValidateCanonical(authority.CompanyId, nameof(authority.CompanyId));
        if (authority.AuthorityVersion <= 0) throw new ArgumentOutOfRangeException(nameof(authority.AuthorityVersion));
    }

    private static void ValidateCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal)) throw new ArgumentException($"{name} must be a canonical non-empty identifier.", name);
    }
}
