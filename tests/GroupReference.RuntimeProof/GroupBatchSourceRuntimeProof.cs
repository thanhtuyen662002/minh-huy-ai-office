using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static class GroupBatchSourceRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        await using var db = new PlatformDbContext(options);
        var batch = await db.GroupBatchAllocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation)
            .Select(x => x.Id).SingleAsync(token);
        var original = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .OrderByDescending(x => x.Epoch).FirstAsync(token);
        var ownedClock = new OwnedClock(TimeProvider.System.GetUtcNow());
        Func<CancellationToken, Task>? afterKey = mode == "source-expiry" ? _ =>
            { ownedClock.Current = original.ExpiresAtUtc; return Task.CompletedTask; }
        : mode == "source-key-revoke" ? async cancellationToken =>
        {
            Console.WriteLine("CHECKPOINT owned source key resolved outside SQL before final fence"); Console.Out.Flush();
            await File.WriteAllTextAsync("/tmp/aioffice-source-proof-awaiting", "ready", cancellationToken);
            while (!File.Exists("/tmp/aioffice-source-proof-release")) await Task.Delay(50, cancellationToken);
        }
        : null;
        var keys = new CountedKeys(db, scope, afterKey);
        var claims = new GroupBatchClaimStore(db, worker, ownedClock);
        GroupBatchClaimResult claim;
        if (mode == "source-deny")
        {
            var denied = false;
            try { await claims.TryAcquireAsync(scope, batch, original.OwnerId, original.OperationId, TimeSpan.FromTicks(original.RequestedLifetimeTicks), token); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied || keys.Reads != 0 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned source runtime current Extract denies before protected keys"); return;
        }
        claim = await claims.TryAcquireAsync(scope, batch, original.OwnerId, original.OperationId,
            TimeSpan.FromTicks(original.RequestedLifetimeTicks), token) ?? throw new InvalidOperationException();
        if (claim.CurrentHandle is null || claim.Receipt.Epoch != 3 || !claim.WasAlreadyClaimed) throw new InvalidOperationException();
        var ids = await db.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .Select(x => x.MessageId).Distinct().OrderBy(x => x).ToArrayAsync(token);
        if (ids.Length != 2) throw new InvalidOperationException();
        var reader = new GroupBatchSourceReader(db, worker, ownedClock, keys, new());
        if (mode == "source-key-revoke")
        {
            var denied = false;
            try { await reader.ReadAsync(claim.CurrentHandle, ids, token); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied || keys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned source runtime current Extract revocation during key await denies private context before decrypt"); return;
        }
        if (mode == "source-foreign")
        {
            var refused = false;
            try { await reader.ReadAsync(claim.CurrentHandle, [Guid.NewGuid()], token); }
            catch (InvalidOperationException error) when (error.Message == "Group batch source context is not available." && error.InnerException is null)
            { refused = true; }
            if (!refused || keys.Reads != 0 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned source runtime foreign selected identity refuses before protected keys"); return;
        }
        if (mode == "source-expiry")
        {
            var before = ownedClock.Current; var denied = false;
            try { await reader.ReadAsync(claim.CurrentHandle, ids, token); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied || keys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            ownedClock.Current = before;
            denied = false;
            try { await reader.ReadAsync(claim.CurrentHandle, ids, token); }
            catch (UnauthorizedAccessException) { denied = true; }
            var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, token);
            if (!denied || keys.Reads != 1 || state.Epoch != 3 || state.ExpiryObservedAtUtc != original.ExpiresAtUtc
                || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned source runtime controlled key-await expiry commits SQL witness and refuses context after clock rollback"); return;
        }
        if (mode != "source-read") throw new InvalidOperationException();
        var context = await reader.ReadAsync(claim.CurrentHandle, ids, token);
        if (context.Scope != scope || context.BatchId != batch || context.Items.Count != 2 || keys.Reads != 1
            || context.Items.Any(x => x.Disposition != GroupBatchSourceDisposition.Readable || x.Text != "owned native spool 😀\uFEFF ")
            || context.ToString().Contains("owned native spool", StringComparison.Ordinal)
            || context.Items.Any(x => x.ToString().Contains("owned native spool", StringComparison.Ordinal))) throw new InvalidOperationException();
        await reader.RequireCurrentAsync(context, token);
        if (keys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        Console.WriteLine("PASS owned source runtime actual Core protected originals scoped configured keys and current context without portal identity");
    }

    private sealed class OwnedClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Current = now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class CountedKeys : IGroupSourceKeyProvider
    {
        private readonly PlatformDbContext db;
        private readonly GroupScope scope;
        private readonly ConfiguredGroupSourceKeyProvider configured;
        private readonly Func<CancellationToken, Task>? afterKey;
        internal int Reads;
        internal CountedKeys(PlatformDbContext db, GroupScope scope, Func<CancellationToken, Task>? afterKey)
        {
            this.db = db; this.scope = scope; this.afterKey = afterKey;
            configured = new(new CompositeSecretResolver([new EnvironmentVariableSecretResolver()]),
                [new(scope, "owned-native-source-v1", SecretReference.Parse("secretref://env/AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY"), true)]);
        }
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default)
        {
            if (source != scope || keyId != "owned-native-source-v1" || db.Database.CurrentTransaction is not null) throw new InvalidOperationException();
            Reads++; var material = await configured.ResolveReadAsync(source, keyId, cancellationToken);
            try { if (afterKey is not null) await afterKey(cancellationToken); return material; }
            catch { material.Dispose(); throw; }
        }
    }
}
