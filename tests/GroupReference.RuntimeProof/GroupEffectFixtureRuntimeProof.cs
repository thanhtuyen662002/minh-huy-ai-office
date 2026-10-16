using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Owned SQL effect fixture only. Its listener lease is operator-seeded; it
// cannot qualify real listener gap recovery, model quality or production DI.
internal static class GroupEffectFixtureRuntimeProof
{
    internal static async Task RunAsync(GroupScope scope, Guid operation, GroupExtractionWorkerBinding worker,
        DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        await using var db = new PlatformDbContext(options);
        var account = await db.GroupBindings.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.Id == scope.SourceBindingId).Select(x => x.ConnectorAccountId).SingleAsync(token);
        if (await db.GroupCoverageGaps.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId, token)
            || await db.GroupAccountCoverageGaps.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.ConnectorAccountId == account, token)) throw new InvalidOperationException();
        var references = await db.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.CommittedSequence).ToArrayAsync(token);
        if (references.Length != 2 || references[0].Id != operation
            || references[0].CommittedSequence != 1 || references[1].CommittedSequence != 2) throw new InvalidOperationException();
        var inbox = new GroupIngressInboxStore(db, worker, TimeProvider.System);
        foreach (var value in references)
            await inbox.ReceiveAsync(new(1, scope, value.Id, value.MessageId, value.Revision, value.CommittedSequence), token);
        var last = await db.GroupSourceStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.LastPendingAtUtc).SingleAsync(token)
            ?? throw new InvalidOperationException();
        var clock = new OwnedClock(last.AddMinutes(3));
        var allocated = await new GroupBatchAllocationStore(db, worker, new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)), clock)
            .AllocateDueAsync(scope, operation, token) ?? throw new InvalidOperationException();
        if (allocated.Revisions.Count != 2 || allocated.AfterSequence != 0 || allocated.AllocatedThroughSequence != 2)
            throw new InvalidOperationException();
        var claims = new GroupBatchClaimStore(db, worker, clock);
        for (var epoch = 1; epoch <= 3; epoch++)
        {
            var claim = await claims.TryAcquireAsync(scope, allocated.BatchId, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token)
                ?? throw new InvalidOperationException();
            if (claim.WasAlreadyClaimed || claim.CurrentHandle is null || claim.Receipt.Epoch != epoch) throw new InvalidOperationException();
            clock.Current = claim.Receipt.ExpiresAtUtc;
            var denied = false;
            try { await claims.RequireCurrentAsync(claim.CurrentHandle, token); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            clock.Current = clock.Current.AddTicks(1);
        }
        // Existing guarded fixture acquires epoch4 after the actual epoch3
        // expiry witness, uses configured scoped keys and returns cipher only.
        await GroupBrainRuntimeProof.RunAsync("brain-fixture", scope, operation, worker, options, token);
    }
    private sealed class OwnedClock(DateTimeOffset now) : TimeProvider
    { internal DateTimeOffset Current = now; public override DateTimeOffset GetUtcNow() => Current; }
}
