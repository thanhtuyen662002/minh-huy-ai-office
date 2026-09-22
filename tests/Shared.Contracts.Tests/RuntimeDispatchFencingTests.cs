using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class RuntimeDispatchFencingTests
{
    private readonly RuntimeDispatchIdentity identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    [Fact]
    public void Initial_claim_starts_first_fencing_epoch()
    {
        var claim = RuntimeDispatchFencing.Claim(identity, "node-a", DateTimeOffset.UnixEpoch, Lease);
        Assert.Equal(1, claim.Epoch);
        Assert.Equal(identity.StableIdentity, claim.Identity.StableIdentity);
    }

    [Fact]
    public void Concurrent_claim_cannot_steal_active_lease()
    {
        var now = DateTimeOffset.UnixEpoch;
        var active = RuntimeDispatchFencing.Claim(identity, "node-a", now, Lease);
        Assert.Throws<InvalidOperationException>(() => RuntimeDispatchFencing.Claim(identity, "node-b", now.AddSeconds(30), Lease, active));
    }

    [Fact]
    public void Expired_claim_can_be_reclaimed_with_new_epoch()
    {
        var now = DateTimeOffset.UnixEpoch;
        var first = RuntimeDispatchFencing.Claim(identity, "node-a", now, Lease);
        var second = RuntimeDispatchFencing.Claim(identity, "node-b", first.ExpiresAt, Lease, first);
        Assert.Equal(2, second.Epoch);
        Assert.Equal("node-b", second.WorkerId);
        Assert.Equal(first.Identity.StableIdentity, second.Identity.StableIdentity);
    }

    [Fact]
    public void Stale_worker_cannot_renew_or_complete_after_failover()
    {
        var now = DateTimeOffset.UnixEpoch;
        var first = RuntimeDispatchFencing.Claim(identity, "node-a", now, Lease);
        var second = RuntimeDispatchFencing.Claim(identity, "node-b", first.ExpiresAt, Lease, first);
        Assert.Throws<InvalidOperationException>(() => RuntimeDispatchFencing.Renew(second, identity, first.Epoch, "node-a", second.ClaimedAt, Lease));
        Assert.Throws<InvalidOperationException>(() => RuntimeDispatchFencing.Complete(second, identity, first.Epoch, "node-a", "old", second.ClaimedAt));
    }

    [Fact]
    public void Cross_company_authority_fails_closed()
    {
        var now = DateTimeOffset.UnixEpoch;
        var active = RuntimeDispatchFencing.Claim(identity, "node-a", now, Lease);
        var foreign = identity with { CompanyId = Guid.NewGuid() };
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeDispatchFencing.Renew(active, foreign, active.Epoch, "node-a", now.AddSeconds(1), Lease));
    }

    [Fact]
    public void Duplicate_exact_completion_is_idempotent_but_conflict_is_rejected()
    {
        var now = DateTimeOffset.UnixEpoch;
        var active = RuntimeDispatchFencing.Claim(identity, "node-a", now, Lease);
        var first = RuntimeDispatchFencing.Complete(active, identity, active.Epoch, "node-a", "evidence-1", now.AddSeconds(1));
        var duplicate = RuntimeDispatchFencing.Complete(active, identity, active.Epoch, "node-a", "evidence-1", now.AddSeconds(2), first);
        Assert.Same(first, duplicate);
        Assert.Throws<InvalidOperationException>(() => RuntimeDispatchFencing.Complete(active, identity, active.Epoch, "node-a", "evidence-2", now.AddSeconds(2), first));
    }

    [Fact]
    public void Broker_settlement_requires_persisted_completion_for_exact_epoch()
    {
        var now = DateTimeOffset.UnixEpoch;
        var active = RuntimeDispatchFencing.Claim(identity, "node-a", now, Lease);
        Assert.Throws<InvalidOperationException>(() => RuntimeDispatchFencing.AuthorizeBrokerSettlement(active, null, identity, active.Epoch));
        var persisted = RuntimeDispatchFencing.Complete(active, identity, active.Epoch, "node-a", "evidence", now.AddSeconds(1));
        RuntimeDispatchFencing.AuthorizeBrokerSettlement(active, persisted, identity, active.Epoch);
        Assert.Throws<InvalidOperationException>(() => RuntimeDispatchFencing.AuthorizeBrokerSettlement(active, persisted, identity, active.Epoch + 1));
    }

    [Fact]
    public void Malformed_identity_and_lease_fail_closed()
    {
        var invalid = identity with { TaskId = Guid.Empty };
        Assert.Throws<InvalidOperationException>(() => RuntimeDispatchFencing.Claim(invalid, "node-a", DateTimeOffset.UnixEpoch, Lease));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeDispatchFencing.Claim(identity, "node-a", DateTimeOffset.UnixEpoch, TimeSpan.Zero));
    }
}
