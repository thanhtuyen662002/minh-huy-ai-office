using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CustomerAiCreditSettlementPersistenceTests
{
    private static readonly CustomerBillingAuthority Authority = new("tenant-a", "company-a", 7);

    [Fact]
    public async Task SettleAndRelease_PersistsBeforeRelease()
    {
        var events = new List<string>();
        var store = new FakeStore(events);
        var service = new CustomerAiCreditSettlementPersistenceService(store);

        var result = await service.SettleAndReleaseAsync("set-a", Reservation(5), 2, (e, _) =>
        {
            Assert.NotNull(store.BySettlement(e.SettlementId));
            events.Add("release");
            return Task.CompletedTask;
        });

        Assert.Equal(["persist", "release"], events);
        Assert.Equal(2, result.Evidence.SettledAiCredits);
        Assert.Equal(3, result.Evidence.ReleasedAiCredits);
        Assert.True(result.Released);
    }

    [Fact]
    public async Task SettleAndRelease_DoesNotReleaseWhenPersistenceFails()
    {
        var events = new List<string>();
        var store = new FakeStore(events) { FailPersist = true };
        var service = new CustomerAiCreditSettlementPersistenceService(store);
        var released = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SettleAndReleaseAsync("set-a", Reservation(5), 2, (_, _) =>
            {
                released = true;
                return Task.CompletedTask;
            }));

        Assert.False(released);
        Assert.Equal(["persist"], events);
    }

    [Fact]
    public async Task SettleAndRelease_CrashAfterPersistenceRetriesAsExactReplay()
    {
        var events = new List<string>();
        var store = new FakeStore(events);
        var service = new CustomerAiCreditSettlementPersistenceService(store);

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            service.SettleAndReleaseAsync("set-a", Reservation(5), 2, (_, _) => throw new SimulatedCrashException()));

        var releases = 0;
        var retry = await service.SettleAndReleaseAsync("set-a", Reservation(5), 2, (_, _) =>
        {
            releases++;
            return Task.CompletedTask;
        });

        Assert.True(retry.IsReplay);
        Assert.Equal(1, releases);
        Assert.Equal(2, events.Count(x => x == "persist"));
    }

    [Fact]
    public async Task SettleAndRelease_RejectsConflictingReplayBeforeRelease()
    {
        var events = new List<string>();
        var store = new FakeStore(events);
        var service = new CustomerAiCreditSettlementPersistenceService(store);
        await service.SettleAndReleaseAsync("set-a", Reservation(5), 2, (_, _) => Task.CompletedTask);
        var releases = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SettleAndReleaseAsync("set-a", Reservation(5), 3, (_, _) =>
            {
                releases++;
                return Task.CompletedTask;
            }));

        Assert.Equal(0, releases);
    }

    [Fact]
    public async Task SettleAndRelease_RejectsCrossAuthorityEvidence()
    {
        var events = new List<string>();
        var store = new FakeStore(events);
        store.Seed(new CustomerAiCreditSettlementEvidence(
            "set-a", "res-other", Authority with { CompanyId = "company-b" }, "credits-v1", 3, 1, 1));
        var service = new CustomerAiCreditSettlementPersistenceService(store);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SettleAndReleaseAsync("set-a", Reservation(5), 2, (_, _) => Task.CompletedTask));
    }

    private static CustomerAiCreditReservationDecision Reservation(long reserved)
    {
        var evidence = new CustomerAiCreditReservationEvidence("res-a", Authority, "credits-v1", 3, reserved);
        return new(evidence, 2, 1, checked(3 + reserved), 20, true, false);
    }

    private sealed class FakeStore(List<string> events) : ICustomerAiCreditSettlementStore
    {
        private readonly Dictionary<string, CustomerAiCreditSettlementEvidence> settlements = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CustomerAiCreditSettlementEvidence> reservations = new(StringComparer.Ordinal);
        public bool FailPersist { get; init; }

        public CustomerAiCreditSettlementEvidence? BySettlement(string id) => settlements.GetValueOrDefault(id);
        public void Seed(CustomerAiCreditSettlementEvidence evidence)
        {
            settlements[evidence.SettlementId] = evidence;
            reservations[evidence.ReservationId] = evidence;
        }

        public Task<CustomerAiCreditSettlementEvidence?> FindBySettlementIdAsync(string settlementId, CancellationToken cancellationToken = default) =>
            Task.FromResult(settlements.GetValueOrDefault(settlementId));

        public Task<CustomerAiCreditSettlementEvidence?> FindByReservationIdAsync(string reservationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(reservations.GetValueOrDefault(reservationId));

        public Task<CustomerAiCreditSettlementEvidence> PersistAsync(CustomerAiCreditSettlementEvidence evidence, CancellationToken cancellationToken = default)
        {
            events.Add("persist");
            if (FailPersist) throw new InvalidOperationException("simulated persistence failure");
            if (settlements.TryGetValue(evidence.SettlementId, out var bySettlement) && bySettlement != evidence)
                throw new InvalidOperationException("conflicting settlement");
            if (reservations.TryGetValue(evidence.ReservationId, out var byReservation) && byReservation != evidence)
                throw new InvalidOperationException("conflicting reservation");
            Seed(evidence);
            return Task.FromResult(evidence);
        }
    }

    private sealed class SimulatedCrashException : Exception;
}
