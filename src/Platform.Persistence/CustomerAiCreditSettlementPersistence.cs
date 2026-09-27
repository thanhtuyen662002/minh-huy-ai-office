using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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
    // This coalesces concurrent/exact replays in the same process. The SQL store remains
    // the durable source of truth, and the release callback must still be idempotent by
    // SettlementId so a process restart cannot duplicate an external side effect.
    private readonly ConcurrentDictionary<string, Lazy<Task<CustomerAiCreditSettlementResult>>> executions = new(StringComparer.Ordinal);

    public async Task<CustomerAiCreditSettlementResult> SettleAndReleaseAsync(
        string settlementId,
        CustomerAiCreditReservationDecision reservation,
        long settledAiCredits,
        Func<CustomerAiCreditSettlementEvidence, CancellationToken, Task> releaseReservation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(releaseReservation);
        ArgumentNullException.ThrowIfNull(reservation.Reservation);
        ArgumentNullException.ThrowIfNull(reservation.Reservation.Authority);

        // Include every authoritative input in the local coalescing key. A conflicting
        // replay with the same SettlementId must execute independently and fail closed in
        // CustomerAiCreditSettlement.Decide rather than inherit another call's result.
        var identity = BuildIdentity(
            settlementId,
            reservation.Reservation.ReservationId,
            reservation.Reservation.Authority.TenantId,
            reservation.Reservation.Authority.CompanyId,
            reservation.Reservation.Authority.AuthorityVersion,
            reservation.Reservation.PricingPolicyId,
            reservation.Reservation.PricingPolicyVersion,
            reservation.UsedAiCredits,
            reservation.ActiveReservedAiCredits,
            reservation.ProjectedAiCredits,
            reservation.CreditLimit,
            reservation.Allowed,
            settledAiCredits);
        var candidate = new Lazy<Task<CustomerAiCreditSettlementResult>>(
            () => ExecuteOnceAsync(settlementId, reservation, settledAiCredits, releaseReservation, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var execution = executions.GetOrAdd(identity, candidate);
        var replay = !ReferenceEquals(candidate, execution);

        try
        {
            var result = await execution.Value;
            return replay && !result.IsReplay ? result with { IsReplay = true } : result;
        }
        catch
        {
            // A failed release (including a worker crash/cancellation) must be retried;
            // successful exact replays remain coalesced and do not invoke release twice.
            executions.TryRemove(new KeyValuePair<string, Lazy<Task<CustomerAiCreditSettlementResult>>>(identity, execution));
            throw;
        }
    }

    private async Task<CustomerAiCreditSettlementResult> ExecuteOnceAsync(
        string settlementId,
        CustomerAiCreditReservationDecision reservation,
        long settledAiCredits,
        Func<CustomerAiCreditSettlementEvidence, CancellationToken, Task> releaseReservation,
        CancellationToken cancellationToken)
    {
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

    private static string BuildIdentity(params object?[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>";
            builder.Append(text.Length).Append(':').Append(text).Append(';');
        }

        return builder.ToString();
    }
}
