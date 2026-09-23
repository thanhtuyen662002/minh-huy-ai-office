namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerAiCreditSettlementEvidence(
    string SettlementId,
    string ReservationId,
    CustomerBillingAuthority Authority,
    string PricingPolicyId,
    long PricingPolicyVersion,
    long SettledAiCredits,
    long ReleasedAiCredits);

public sealed record CustomerAiCreditSettlementDecision(
    CustomerAiCreditSettlementEvidence Settlement,
    bool IsReplay);

public static class CustomerAiCreditSettlement
{
    public static CustomerAiCreditSettlementDecision Decide(
        string settlementId,
        CustomerAiCreditReservationDecision reservationDecision,
        long settledAiCredits,
        IEnumerable<CustomerAiCreditSettlementEvidence> existingSettlements)
    {
        ValidateCanonical(settlementId, nameof(settlementId));
        ArgumentNullException.ThrowIfNull(reservationDecision);
        ArgumentNullException.ThrowIfNull(existingSettlements);
        ValidateReservationDecision(reservationDecision);
        if (settledAiCredits < 0 || settledAiCredits > reservationDecision.Reservation.ReservedAiCredits)
            throw new ArgumentOutOfRangeException(nameof(settledAiCredits));

        var reservation = reservationDecision.Reservation;
        var requested = new CustomerAiCreditSettlementEvidence(
            settlementId,
            reservation.ReservationId,
            reservation.Authority,
            reservation.PricingPolicyId,
            reservation.PricingPolicyVersion,
            settledAiCredits,
            checked(reservation.ReservedAiCredits - settledAiCredits));

        var bySettlement = new Dictionary<string, CustomerAiCreditSettlementEvidence>(StringComparer.Ordinal);
        CustomerAiCreditSettlementEvidence? reservationSettlement = null;
        foreach (var evidence in existingSettlements)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            ValidateEvidence(evidence, reservation);
            if (bySettlement.TryGetValue(evidence.SettlementId, out var duplicate))
            {
                if (duplicate != evidence)
                    throw new InvalidOperationException("Conflicting settlement replay evidence.");
                continue;
            }

            bySettlement.Add(evidence.SettlementId, evidence);
            if (!string.Equals(evidence.ReservationId, reservation.ReservationId, StringComparison.Ordinal))
                continue;
            if (reservationSettlement is not null && reservationSettlement != evidence)
                throw new InvalidOperationException("Reservation already has conflicting settlement evidence.");
            reservationSettlement = evidence;
        }

        if (bySettlement.TryGetValue(settlementId, out var replay))
        {
            if (replay != requested)
                throw new InvalidOperationException("Settlement identity was reused with conflicting evidence.");
            return new(requested, true);
        }

        if (reservationSettlement is not null)
            throw new InvalidOperationException("Reservation is already settled.");

        return new(requested, false);
    }

    private static void ValidateReservationDecision(CustomerAiCreditReservationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision.Reservation);
        ValidateEvidenceShape(decision.Reservation);
        if (!decision.Allowed)
            throw new InvalidOperationException("A denied reservation cannot be settled.");
        if (decision.UsedAiCredits < 0 || decision.ActiveReservedAiCredits < 0 || decision.ProjectedAiCredits < 0 || decision.CreditLimit < 0)
            throw new ArgumentException("Reservation decision contains negative credit values.", nameof(decision));
        var expectedProjected = checked(decision.UsedAiCredits + decision.ActiveReservedAiCredits + decision.Reservation.ReservedAiCredits);
        if (expectedProjected != decision.ProjectedAiCredits || decision.ProjectedAiCredits > decision.CreditLimit)
            throw new InvalidOperationException("Reservation decision is inconsistent.");
    }

    private static void ValidateEvidence(CustomerAiCreditSettlementEvidence evidence, CustomerAiCreditReservationEvidence reservation)
    {
        ValidateCanonical(evidence.SettlementId, nameof(evidence.SettlementId));
        ValidateCanonical(evidence.ReservationId, nameof(evidence.ReservationId));
        ValidateAuthority(evidence.Authority);
        ValidateCanonical(evidence.PricingPolicyId, nameof(evidence.PricingPolicyId));
        if (evidence.PricingPolicyVersion <= 0 || evidence.SettledAiCredits < 0 || evidence.ReleasedAiCredits < 0)
            throw new ArgumentException("Settlement evidence contains invalid credit or policy values.", nameof(evidence));
        if (checked(evidence.SettledAiCredits + evidence.ReleasedAiCredits) != reservation.ReservedAiCredits)
            throw new InvalidOperationException("Settlement evidence does not reconcile the reservation.");
        if (evidence.Authority != reservation.Authority)
            throw new UnauthorizedAccessException("Settlement evidence is outside the authoritative billing scope or version.");
        if (!string.Equals(evidence.PricingPolicyId, reservation.PricingPolicyId, StringComparison.Ordinal) || evidence.PricingPolicyVersion != reservation.PricingPolicyVersion)
            throw new InvalidOperationException("Settlement evidence pricing policy is stale or mismatched.");
    }

    private static void ValidateEvidenceShape(CustomerAiCreditReservationEvidence reservation)
    {
        ValidateCanonical(reservation.ReservationId, nameof(reservation.ReservationId));
        ValidateAuthority(reservation.Authority);
        ValidateCanonical(reservation.PricingPolicyId, nameof(reservation.PricingPolicyId));
        if (reservation.PricingPolicyVersion <= 0 || reservation.ReservedAiCredits < 0)
            throw new ArgumentException("Reservation evidence contains invalid credit or policy values.", nameof(reservation));
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
