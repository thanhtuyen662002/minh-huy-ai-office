using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public interface ICustomerAiCreditSettlementStore
{
    Task<CustomerAiCreditSettlementEvidence?> FindBySettlementIdAsync(string settlementId, CancellationToken cancellationToken = default);
    Task<CustomerAiCreditSettlementEvidence?> FindByReservationIdAsync(string reservationId, CancellationToken cancellationToken = default);
    Task<CustomerAiCreditSettlementEvidence> PersistAsync(CustomerAiCreditSettlementEvidence evidence, CancellationToken cancellationToken = default);
}

public sealed record CustomerAiCreditSettlementResult(CustomerAiCreditSettlementEvidence Evidence, bool IsReplay, bool Released);

public sealed class CustomerAiCreditSettlementPersistenceService(ICustomerAiCreditSettlementStore store)
{
    public async Task<CustomerAiCreditSettlementResult> SettleAndReleaseAsync(
        string settlementId,
        CustomerAiCreditReservationDecision reservation,
        long settledAiCredits,
        Func<CustomerAiCreditSettlementEvidence, CancellationToken, Task> releaseReservation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(releaseReservation);
        var bySettlement = await store.FindBySettlementIdAsync(settlementId, cancellationToken);
        var byReservation = await store.FindByReservationIdAsync(reservation.Reservation.ReservationId, cancellationToken);
        var existing = new[] { bySettlement, byReservation }.OfType<CustomerAiCreditSettlementEvidence>().Distinct().ToArray();
        var decision = CustomerAiCreditSettlement.Decide(settlementId, reservation, settledAiCredits, existing);
        var persisted = await store.PersistAsync(decision.Settlement, cancellationToken);
        if (persisted != decision.Settlement)
            throw new InvalidOperationException("Persisted settlement evidence conflicts with the authoritative decision.");
        await releaseReservation(persisted, cancellationToken);
        return new(persisted, decision.IsReplay, true);
    }
}
