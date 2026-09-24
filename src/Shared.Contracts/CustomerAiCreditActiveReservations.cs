namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerAiCreditActiveReservationProjection(
    CustomerBillingAuthority Authority,
    string PricingPolicyId,
    long PricingPolicyVersion,
    IReadOnlyList<CustomerAiCreditReservationEvidence> ActiveReservations,
    long ActiveReservedAiCredits);

public static class CustomerAiCreditActiveReservations
{
    public static CustomerAiCreditActiveReservationProjection Project(
        CustomerBillingAuthority authority,
        string pricingPolicyId,
        long pricingPolicyVersion,
        IEnumerable<CustomerAiCreditReservationEvidence> reservations,
        IEnumerable<CustomerAiCreditSettlementEvidence> settlements)
    {
        ValidateAuthority(authority);
        ValidateCanonical(pricingPolicyId, nameof(pricingPolicyId));
        if (pricingPolicyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(pricingPolicyVersion));
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(settlements);

        var uniqueReservations = new Dictionary<string, CustomerAiCreditReservationEvidence>(StringComparer.Ordinal);
        foreach (var reservation in reservations)
        {
            ArgumentNullException.ThrowIfNull(reservation);
            ValidateReservation(reservation, authority, pricingPolicyId, pricingPolicyVersion);
            if (uniqueReservations.TryGetValue(reservation.ReservationId, out var duplicate))
            {
                if (duplicate != reservation)
                    throw new InvalidOperationException("Conflicting reservation replay evidence.");
                continue;
            }
            uniqueReservations.Add(reservation.ReservationId, reservation);
        }

        var uniqueSettlements = new Dictionary<string, CustomerAiCreditSettlementEvidence>(StringComparer.Ordinal);
        var settledReservations = new Dictionary<string, CustomerAiCreditSettlementEvidence>(StringComparer.Ordinal);
        foreach (var settlement in settlements)
        {
            ArgumentNullException.ThrowIfNull(settlement);
            ValidateSettlementShape(settlement, authority, pricingPolicyId, pricingPolicyVersion);
            if (!uniqueReservations.TryGetValue(settlement.ReservationId, out var reservation))
                throw new InvalidOperationException("Settlement references an unknown reservation.");
            if (checked(settlement.SettledAiCredits + settlement.ReleasedAiCredits) != reservation.ReservedAiCredits)
                throw new InvalidOperationException("Settlement evidence does not reconcile the reservation.");

            if (uniqueSettlements.TryGetValue(settlement.SettlementId, out var duplicate))
            {
                if (duplicate != settlement)
                    throw new InvalidOperationException("Conflicting settlement identity replay evidence.");
                continue;
            }
            uniqueSettlements.Add(settlement.SettlementId, settlement);

            if (settledReservations.TryGetValue(settlement.ReservationId, out var existing))
            {
                if (existing != settlement)
                    throw new InvalidOperationException("Reservation has conflicting settlement evidence.");
                continue;
            }
            settledReservations.Add(settlement.ReservationId, settlement);
        }

        var active = uniqueReservations.Values
            .Where(x => !settledReservations.ContainsKey(x.ReservationId))
            .OrderBy(x => x.ReservationId, StringComparer.Ordinal)
            .ToArray();

        long total = 0;
        foreach (var reservation in active)
            total = checked(total + reservation.ReservedAiCredits);

        return new(authority, pricingPolicyId, pricingPolicyVersion, active, total);
    }

    private static void ValidateReservation(
        CustomerAiCreditReservationEvidence reservation,
        CustomerBillingAuthority authority,
        string pricingPolicyId,
        long pricingPolicyVersion)
    {
        ValidateCanonical(reservation.ReservationId, nameof(reservation.ReservationId));
        ValidateAuthority(reservation.Authority);
        ValidateCanonical(reservation.PricingPolicyId, nameof(reservation.PricingPolicyId));
        if (reservation.PricingPolicyVersion <= 0 || reservation.ReservedAiCredits < 0)
            throw new ArgumentException("Reservation evidence contains invalid credit or policy values.", nameof(reservation));
        ValidateScope(reservation.Authority, reservation.PricingPolicyId, reservation.PricingPolicyVersion, authority, pricingPolicyId, pricingPolicyVersion, "Reservation");
    }

    private static void ValidateSettlementShape(
        CustomerAiCreditSettlementEvidence settlement,
        CustomerBillingAuthority authority,
        string pricingPolicyId,
        long pricingPolicyVersion)
    {
        ValidateCanonical(settlement.SettlementId, nameof(settlement.SettlementId));
        ValidateCanonical(settlement.ReservationId, nameof(settlement.ReservationId));
        ValidateAuthority(settlement.Authority);
        ValidateCanonical(settlement.PricingPolicyId, nameof(settlement.PricingPolicyId));
        if (settlement.PricingPolicyVersion <= 0 || settlement.SettledAiCredits < 0 || settlement.ReleasedAiCredits < 0)
            throw new ArgumentException("Settlement evidence contains invalid credit or policy values.", nameof(settlement));
        ValidateScope(settlement.Authority, settlement.PricingPolicyId, settlement.PricingPolicyVersion, authority, pricingPolicyId, pricingPolicyVersion, "Settlement");
    }

    private static void ValidateScope(
        CustomerBillingAuthority evidenceAuthority,
        string evidencePolicyId,
        long evidencePolicyVersion,
        CustomerBillingAuthority authority,
        string pricingPolicyId,
        long pricingPolicyVersion,
        string evidenceKind)
    {
        if (evidenceAuthority != authority)
            throw new UnauthorizedAccessException($"{evidenceKind} evidence is outside the authoritative billing scope or version.");
        if (!string.Equals(evidencePolicyId, pricingPolicyId, StringComparison.Ordinal) || evidencePolicyVersion != pricingPolicyVersion)
            throw new InvalidOperationException($"{evidenceKind} evidence pricing policy is stale or mismatched.");
    }

    private static void ValidateAuthority(CustomerBillingAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ValidateCanonical(authority.TenantId, nameof(authority.TenantId));
        ValidateCanonical(authority.CompanyId, nameof(authority.CompanyId));
        if (authority.AuthorityVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(authority.AuthorityVersion));
    }

    private static void ValidateCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be a canonical non-empty identifier.", name);
    }
}
