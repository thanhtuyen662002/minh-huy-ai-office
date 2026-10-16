using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static class GroupCoverageRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("coverage-prepare" or "coverage-check")) throw new InvalidOperationException();
        await using var db = new PlatformDbContext(options);
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var references = await db.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.CommittedSequence).ToArrayAsync(token);
        if (references.Length != 2 || references[0].Id != operation || references[0].CommittedSequence != 1
            || references[1].CommittedSequence != 2 || references.Any(x => x.Revision != 1)
            || references.Select(x => x.MessageId).Distinct().Count() != 2) throw new InvalidOperationException();
        if (mode == "coverage-prepare")
        {
            var inbox = new GroupIngressInboxStore(db, worker, clock);
            foreach (var value in references)
                await inbox.ReceiveAsync(new(1, scope, value.Id, value.MessageId, value.Revision, value.CommittedSequence), token);
            var last = await db.GroupSourceStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.LastPendingAtUtc).SingleAsync(token)
                ?? throw new InvalidOperationException();
            clock.Current = last.AddMinutes(3);
            var receipt = await new GroupBatchAllocationStore(db, worker, new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)), clock)
                .AllocateDueAsync(scope, operation, token) ?? throw new InvalidOperationException();
            if (receipt.WasAlreadyAllocated || receipt.AfterSequence != 0 || receipt.AllocatedThroughSequence != 2
                || receipt.Revisions.Count != 2 || await db.GroupBatchClaimStates.AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
            Console.WriteLine("PASS owned coverage actual inbox allocation two original text sources zero claims effects");
            return;
        }
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        if (clock.Current < allocation.AllocatedAtUtc) clock.Current = allocation.AllocatedAtUtc;
        var claim = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, allocation.Id,
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        if (claim.WasAlreadyClaimed || claim.CurrentHandle is null || claim.Receipt.Epoch != 1) throw new InvalidOperationException();
        var accountId = await db.GroupBindings.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.Id == scope.SourceBindingId).Select(x => x.ConnectorAccountId).SingleAsync(token);
        var gap = SourceGap();
        db.AddRange(gap, AccountGap("listener-started")); await db.SaveChangesAsync(token);
        var keys = new CoverageKeys(db, scope);
        var reader = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var ids = references.Select(x => x.MessageId).ToArray();
        var original = await ReadAsync(); await reader.RequireCurrentAsync(original, token);
        gap.ReconnectedAtUtc = gap.OpenedAtUtc.AddMilliseconds(1); await db.SaveChangesAsync(token);
        await StaleAsync(() => reader.RequireCurrentAsync(original, token));
        var reconnected = await ReadAsync();
        db.Add(SourceGap()); await db.SaveChangesAsync(token);
        await StaleAsync(() => reader.RequireCurrentAsync(reconnected, token));
        var addedSource = await ReadAsync();
        db.Add(AccountGap("listener-expired")); await db.SaveChangesAsync(token);
        await StaleAsync(() => reader.RequireCurrentAsync(addedSource, token));
        await ReadAsync();
        keys.BeforeRead = async () => { db.Add(SourceGap()); await db.SaveChangesAsync(token); };
        await StaleAsync(async () => { await reader.ReadAsync(claim.CurrentHandle, ids, token); });
        if (keys.Reads != 5 || keys.Writes != 0 || keys.LastMaterial is null) throw new InvalidOperationException();
        RequireDisposed(keys.LastMaterial);
        keys.BeforeRead = null; await ReadAsync();
        for (var index = 3; index < 256; index++) db.Add(SourceGap());
        await db.SaveChangesAsync(token);
        var maximum = await ReadAsync(); await reader.RequireCurrentAsync(maximum, token);
        db.Add(SourceGap()); await db.SaveChangesAsync(token);
        await StaleAsync(async () => { await reader.ReadAsync(claim.CurrentHandle, ids, token); });
        await StaleAsync(() => reader.RequireCurrentAsync(maximum, token));
        if (keys.Reads != 7 || keys.Writes != 0 || db.ChangeTracker.HasChanges()
            || await db.GroupCoverageGaps.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId, token) != 257
            || await db.GroupAccountCoverageGaps.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.ConnectorAccountId == accountId, token) != 2) throw new InvalidOperationException();
        var lease = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        if (lease.Epoch != 1 || lease.ExpiryObservedAtUtc is not null) throw new InvalidOperationException();
        RequireDisposed(keys.LastMaterial);
        Console.WriteLine("PASS owned coverage SQL already incomplete reconnection source account additions key await refusal disposed material exact max256 overflow257 before keys seven reads zero writes unchanged effects");

        async Task<GroupBatchSourceContext> ReadAsync()
        {
            var context = await reader.ReadAsync(claim.CurrentHandle, ids, token);
            if (!context.HasCoverageGap || context.Items.Count != 2
                || context.Items.Any(x => x.Kind != GroupSourceEventKind.NewText || x.Revision != 1
                    || x.Disposition != GroupBatchSourceDisposition.Readable || x.Text != "owned native spool 😀\uFEFF ")) throw new InvalidOperationException();
            return context;
        }
        GroupCoverageGapRecord SourceGap() => new()
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            Id = Guid.NewGuid(),
            AfterCommittedSequence = 2,
            Reason = "owned-coverage-probe",
            OpenedAtUtc = clock.Current
        };
        GroupAccountCoverageGapRecord AccountGap(string reason) => new()
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            ConnectorAccountId = accountId,
            ListenerEpoch = 1,
            Reason = reason,
            OpenedAtUtc = clock.Current,
            RecordedAtUtc = clock.Current
        };
    }

    private static async Task StaleAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (error.InnerException is null
            && error.Message is "Group batch source context is not available." or "Group coverage dependencies are not available.")
        { return; }
        throw new InvalidOperationException();
    }
    private static void RequireDisposed(GroupSourceKeyMaterial material)
    {
        // Fixed test-only fields verify disposal without expanding the production key API.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(GroupSourceKeyMaterial);
        if (type.GetField("disposed", flags)?.GetValue(material) is not true
            || type.GetField("key", flags)?.GetValue(material) is not byte[] { Length: 32 } bytes
            || bytes.Any(value => value != 0)) throw new InvalidOperationException();
    }
    private sealed class CoverageKeys(PlatformDbContext db, GroupScope scope) : IGroupSourceKeyProvider
    {
        private readonly GroupNoteRuntimeProof.CountedKeys configured = new(db, scope);
        internal int Reads => configured.Reads;
        internal int Writes => configured.Writes;
        internal Func<Task>? BeforeRead;
        internal GroupSourceKeyMaterial? LastMaterial;
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken token = default) =>
            configured.ResolveWriteAsync(source, token);
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken token = default)
        {
            var material = await configured.ResolveReadAsync(source, keyId, token); LastMaterial = material;
            try { if (BeforeRead is not null) await BeforeRead(); return material; }
            catch { material.Dispose(); throw; }
        }
    }
}
