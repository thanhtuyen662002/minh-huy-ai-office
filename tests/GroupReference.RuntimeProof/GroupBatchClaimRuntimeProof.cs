using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static class GroupBatchClaimRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        await using var db = new PlatformDbContext(options);
        var batch = await db.GroupBatchAllocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation)
            .Select(x => x.Id).SingleAsync(token);
        var lifetime = TimeSpan.FromSeconds(10);
        var store = new GroupBatchClaimStore(db, worker, TimeProvider.System);
        if (mode is "claim-deny" or "claim-unsafe" or "claim-rollback")
        {
            var refused = false;
            try { await store.TryAcquireAsync(scope, batch, operation, operation, lifetime, token); }
            catch (Exception error) when (OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, error)) { refused = true; }
            if (!refused || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned claim runtime refusal " + mode); return;
        }
        GroupBatchClaimResult original;
        if (mode == "claim-crash")
        {
            var creators = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var creator = new PlatformDbContext(options);
                return await new GroupBatchClaimStore(creator, worker, TimeProvider.System)
                    .TryAcquireAsync(scope, batch, operation, operation, lifetime, token) ?? throw new InvalidOperationException();
            }));
            original = creators.Single(x => !x.WasAlreadyClaimed);
            if (creators.Any(x => x.Receipt != original.Receipt || x.CurrentHandle is null)) throw new InvalidOperationException();
            if (original.Receipt.Epoch != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            // Guarded owned proof process abruptly dies after SQL commit and
            // competing contexts reconcile, before any external receipt reply.
            Console.WriteLine("CHECKPOINT owned claim committed before receipt delivery"); Console.Out.Flush();
            Process.GetCurrentProcess().Kill();
            await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException();
        }
        original = await store.TryAcquireAsync(scope, batch, operation, operation, lifetime, token) ?? throw new InvalidOperationException();
        if (!original.WasAlreadyClaimed || original.Receipt.Epoch != 1) throw new InvalidOperationException();
        if (mode == "claim-replay")
        {
            using var slots = new SemaphoreSlim(4);
            await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
            {
                await slots.WaitAsync(token);
                try
                {
                    await using var restarted = new PlatformDbContext(options);
                    var replay = await new GroupBatchClaimStore(restarted, worker, TimeProvider.System)
                        .TryAcquireAsync(scope, batch, operation, operation, lifetime, token) ?? throw new InvalidOperationException();
                    if (!replay.WasAlreadyClaimed || replay.Receipt != original.Receipt || restarted.ChangeTracker.HasChanges())
                        throw new InvalidOperationException();
                }
                finally { slots.Release(); }
            }));
            Console.WriteLine("PASS owned claim runtime100 concurrent original receipts never renew expired lease"); return;
        }
        if (mode != "claim-fence") throw new InvalidOperationException();
        await WaitExpiredAsync(original.Receipt.ExpiresAtUtc);
        var expired = await store.TryAcquireAsync(scope, batch, operation, operation, lifetime, token) ?? throw new InvalidOperationException();
        if (expired.Receipt != original.Receipt || expired.CurrentHandle is not null) throw new InvalidOperationException();
        var owner2 = Guid.NewGuid(); var operation2 = Guid.NewGuid();
        var second = await store.TryAcquireAsync(scope, batch, owner2, operation2, lifetime, token) ?? throw new InvalidOperationException();
        if (second.Receipt.Epoch != 2 || second.CurrentHandle is null || second.WasAlreadyClaimed) throw new InvalidOperationException();
        await store.RequireCurrentAsync(second.CurrentHandle, token);
        var contenders = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var contender = new PlatformDbContext(options);
            return await new GroupBatchClaimStore(contender, worker, TimeProvider.System)
                .TryAcquireAsync(scope, batch, Guid.NewGuid(), Guid.NewGuid(), lifetime, token);
        }));
        if (contenders.Any(x => x is not null)) throw new InvalidOperationException();
        await WaitExpiredAsync(second.Receipt.ExpiresAtUtc);
        var staleDenied = false;
        try { await store.RequireCurrentAsync(second.CurrentHandle, token); }
        catch (UnauthorizedAccessException) { staleDenied = true; }
        if (!staleDenied) throw new InvalidOperationException();
        var witnessedAt = await db.GroupBatchClaimStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .Select(x => x.ExpiryObservedAtUtc).SingleAsync(token);
        if (witnessedAt is null || witnessedAt < second.Receipt.ExpiresAtUtc || witnessedAt.Value.Offset != TimeSpan.Zero)
            throw new InvalidOperationException();
        // Real System expiry was observed and committed above; a separate
        // owned clock now exercises durable SQL fencing across clock rollback.
        await using (var restarted = new PlatformDbContext(options))
        {
            var backwards = new GroupBatchClaimStore(restarted, worker, new FixedClock(second.Receipt.ExpiresAtUtc.AddTicks(-1)));
            staleDenied = false;
            try { await backwards.RequireCurrentAsync(second.CurrentHandle, token); }
            catch (UnauthorizedAccessException) { staleDenied = true; }
            var replay = await backwards.TryAcquireAsync(scope, batch, owner2, operation2, lifetime, token)
                ?? throw new InvalidOperationException();
            if (!staleDenied || replay.Receipt != second.Receipt || replay.CurrentHandle is not null
                || await backwards.TryAcquireAsync(scope, batch, Guid.NewGuid(), Guid.NewGuid(), lifetime, token) is not null
                || restarted.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            var unchanged = await restarted.GroupBatchClaimStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
                .Select(x => x.ExpiryObservedAtUtc).SingleAsync(token);
            if (unchanged != witnessedAt) throw new InvalidOperationException();
        }
        Console.WriteLine("PASS owned claim runtime durable SQL expiry witness refuses original nonce and handle after clock rollback");
        var third = await store.TryAcquireAsync(scope, batch, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5), token)
            ?? throw new InvalidOperationException();
        if (third.Receipt.Epoch != 3 || third.CurrentHandle is null || third.WasAlreadyClaimed) throw new InvalidOperationException();
        var old = await store.TryAcquireAsync(scope, batch, owner2, operation2, lifetime, token) ?? throw new InvalidOperationException();
        if (old.Receipt != second.Receipt || old.CurrentHandle is not null) throw new InvalidOperationException();
        staleDenied = false;
        try { await store.RequireCurrentAsync(second.CurrentHandle, token); }
        catch (UnauthorizedAccessException) { staleDenied = true; }
        if (!staleDenied) throw new InvalidOperationException();
        await store.RequireCurrentAsync(third.CurrentHandle, token);
        if (await db.GroupBatchClaimReceipts.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.BatchId == batch, token) != 3
            || await db.GroupBatchClaimStates.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.BatchId == batch && x.ExpiryObservedAtUtc != null, token)
            || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        Console.WriteLine("PASS owned claim runtime actual expiry monotone replacement active contention and stale handle refusal");

        async Task WaitExpiredAsync(DateTimeOffset expiry)
        {
            var remaining = expiry - TimeProvider.System.GetUtcNow();
            if (remaining > lifetime) throw new InvalidOperationException();
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(20), token);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
