using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Separate owned actual-Core501 history fixture. Only500 revisions are
// reserved; the last Edit remains an honest pending suffix, not terminal work.
internal static class GroupRawHistoryRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("raw-history-prepare" or "raw-history-expiry" or "raw-history-commit"))
            throw new InvalidOperationException();
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var effect = new EffectEvidence(scope, clock);
        var dependencyProof = new GroupAutomaticDependencyRuntimeProof.Evidence(scope);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new FlushProbe(effect), new RollbackProbe(effect), new GroupAutomaticDependencyRuntimeProof.ReadProbe(dependencyProof)).Options);
        var references = await db.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.CommittedSequence).Take(502).ToArrayAsync(token);
        RequireReferences(scope, operation, references);
        var suffix = await db.GroupMessageRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.CommittedSequence == 501)
            .Select(x => new { x.MessageId, x.Revision, x.CommittedAtUtc }).SingleAsync(token);
        if (suffix.MessageId != references[0].MessageId || suffix.Revision != 500
            || suffix.CommittedAtUtc.Offset != TimeSpan.Zero) throw new InvalidOperationException();
        if (mode == "raw-history-prepare")
        {
            var inbox = new GroupIngressInboxStore(db, worker, clock);
            foreach (var value in references)
                await inbox.ReceiveAsync(new(1, scope, value.Id, value.MessageId, value.Revision, value.CommittedSequence), token);
            var last = await db.GroupSourceStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.LastPendingAtUtc).SingleAsync(token)
                ?? throw new InvalidOperationException();
            clock.Current = last.AddMinutes(3);
            var allocation = await new GroupBatchAllocationStore(db, worker, new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)), clock)
                .AllocateDueAsync(scope, operation, token) ?? throw new InvalidOperationException();
            if (allocation.WasAlreadyAllocated || allocation.AfterSequence != 0 || allocation.AllocatedThroughSequence != 500 || allocation.ObservedCommittedThroughSequence != 501
                || allocation.Revisions.Count != 500 || await db.GroupBatchClaimStates.AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
            await RequireEmptyAsync();
            await RequireReservationAsync();
            Console.WriteLine("PASS owned raw history actual501 inbox references allocation500 pending suffix501 two selected cutoff heads no claims effects model");
            return;
        }
        var allocated = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        await RequireReservationAsync();
        var previous = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocated.Id)
            .OrderByDescending(x => x.Epoch).FirstOrDefaultAsync(token);
        var epoch = mode == "raw-history-expiry" ? 1 : 2;
        if ((previous?.Epoch ?? 0) != epoch - 1) throw new InvalidOperationException();
        if (previous is not null)
        {
            var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocated.Id, token);
            if (state.Epoch != previous.Epoch || state.ExpiryObservedAtUtc != previous.ExpiresAtUtc) throw new InvalidOperationException();
            if (clock.Current <= previous.ExpiresAtUtc) clock.Current = previous.ExpiresAtUtc.AddTicks(1);
        }
        else if (clock.Current < allocated.AllocatedAtUtc) clock.Current = allocated.AllocatedAtUtc;
        await RequireEmptyAsync();
        var claim = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, allocated.Id, Guid.NewGuid(), Guid.NewGuid(),
            TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        if (claim.WasAlreadyClaimed || claim.CurrentHandle is null || claim.Receipt.Epoch != epoch) throw new InvalidOperationException();
        var handle = claim.CurrentHandle;
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, references.Take(2).Select(x => x.MessageId).ToArray(), token));
        var plan = GroupAutomaticNotePlan.Create(preparation, null);
        if (preparation.Candidates.Count != 0 || preparation.Receipts.Count != 2
            || preparation.Receipts.Any(x => x.HasUnsupportedMedia || x.QuarantineReason is not null)
            || !preparation.Receipts.Any(x => x.MessageId == references[0].MessageId && x.Revision == 499
                && x.Disposition == GroupSourcePreparationDisposition.ChangedAfterCutoff)
            || !preparation.Receipts.Any(x => x.MessageId == references[1].MessageId && x.Revision == 1
                && x.Disposition == GroupSourcePreparationDisposition.EmptyText)
            || plan.HasCoverageGap || plan.NoteCount != 0 || plan.SourceDispositions.Count != 2
            || plan.SourceDispositions.Any(x => x.HasHostAttention)
            || !plan.SourceDispositions.Any(x => x.MessageId == references[0].MessageId && x.Revision == 499
                && x.Outcome == GroupWorkSourceOutcome.ChangedAfterCutoff)
            || !plan.SourceDispositions.Any(x => x.MessageId == references[1].MessageId && x.Revision == 1
                && x.Outcome == GroupWorkSourceOutcome.NoWork)) throw new InvalidOperationException();
        var dependencies = await brain.ReadAsync(handle, [], [], token);
        if (keys.Reads != 1 || keys.Writes != 0 || dependencies.Items.Count != 0) throw new InvalidOperationException();
        var store = new GroupNoWorkCommitStore(db, worker, clock, sources, brain);
        var effectOperation = Guid.NewGuid();
        if (mode == "raw-history-expiry")
        {
            effect.Operation = effectOperation; effect.Expires = handle.Receipt.ExpiresAtUtc; effect.Armed = true;
            await DeniedAsync(() => store.CommitAutomaticAsync(plan, dependencies, effectOperation, token));
            if (!effect.Staged || !effect.Flushed || !effect.RolledBack || effect.Savepoints < 1) throw new InvalidOperationException();
            await RequireEmptyAsync();
            var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocated.Id, token);
            if (state.Epoch != epoch || state.ExpiryObservedAtUtc != handle.Receipt.ExpiresAtUtc) throw new InvalidOperationException();
            clock.Current = handle.Receipt.IssuedAtUtc;
            await DeniedAsync(() => store.CommitAutomaticAsync(plan, dependencies, effectOperation, token));
            await RequireEmptyAsync();
            if (keys.Reads != 1 || keys.Writes != 0) throw new InvalidOperationException();
            await RequireReservationAsync();
            Console.WriteLine("PASS owned raw history five-hundred raw plus three effects flushed same SQL savepoint rollback detach witness only pending501 retained clock rollback denied");
            return;
        }
        var committed = await store.CommitAutomaticAsync(plan, dependencies, effectOperation, token);
        if (committed.WasAlreadyCommitted || committed.Scope != scope || committed.BatchId != allocated.Id || committed.SelectedMessageCount != 2
            || await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, effectOperation, token) != 3) throw new InvalidOperationException();
        var receipt = await db.GroupWorkCommitReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == effectOperation, token);
        var dispositions = await db.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == effectOperation).ToArrayAsync(token);
        if (receipt.Outcome != GroupWorkCommitOutcome.NoWork || receipt.NoteCount != 0 || dispositions.Length != 2
            || dispositions.Any(x => !plan.SourceDispositions.Any(value =>
                value.MessageId == x.MessageId && value.Revision == x.MessageRevision && value.Outcome == x.Outcome))) throw new InvalidOperationException();
        var graph = await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, effectOperation, token);
        var rawGraph = await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, effectOperation, 500, token);
        var manifestGraph = await GroupAutomaticManifestRuntimeProof.RequireAsync(db, scope, effectOperation, token);
        var effectExpectation = await GroupAutomaticEffectExpectationRuntimeProof.RequireAsync(db, scope, effectOperation, token);
        await RequireDependenciesAsync();
        var replay = await store.CommitAutomaticAsync(plan, dependencies, effectOperation, token);
        if (!replay.WasAlreadyCommitted || committed != replay with { WasAlreadyCommitted = false }) throw new InvalidOperationException();
        await RequireOriginalAsync();
        await RequireDependenciesAsync();
        var refused = false;
        try { await store.CommitAutomaticAsync(plan, dependencies, Guid.NewGuid(), token); }
        catch (InvalidOperationException error) when (error.Message == "Group work commit is unavailable." && error.InnerException is null) { refused = true; }
        if (!refused || keys.Reads != 1 || keys.Writes != 0) throw new InvalidOperationException();
        await RequireOriginalAsync();
        await GroupAutomaticRawRuntimeProof.RequireImmutableAsync(db, scope, effectOperation, token);
        await GroupAutomaticManifestRuntimeProof.RequireImmutableAsync(db, scope, effectOperation, token);
        await GroupAutomaticEffectExpectationRuntimeProof.RequireImmutableAsync(db, scope, effectOperation, token);
        await RequireOriginalAsync();
        await RequireDependenciesAsync();
        if (dependencyProof.Completed != 3) throw new InvalidOperationException();
        Console.WriteLine("PASS owned raw history atomic500 raw rows cutoff499 ChangedAfterCutoff and empty1 NoWork original replay new nonce refused immutable runtime rights pending501 unchanged");
        Console.WriteLine("PASS owned raw history whole dependency SQL three current reconstruction checks original graphs claim keys unchanged serializable source lock");
        Console.WriteLine("PASS owned raw history version1 effect expectation32 same atomic graph rollback replay two immutable columns denied no completion or model");
        Console.WriteLine("PASS owned raw history original effect graph SQL three current digest comparisons original acquisition six effect queries ciphertext preflight unchanged original graph no completion or model");

        Task RequireDependenciesAsync() => GroupAutomaticDependencyRuntimeProof.RequireAsync(db, handle, worker, clock, sources, brain,
            keys, dependencyProof, RequireOriginalAsync, token);

        async Task RequireOriginalAsync()
        {
            if (await GroupNoteRuntimeProof.TargetRowsAsync(db, scope, effectOperation, token) != 3
                || await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, effectOperation, 500, token) != rawGraph
                || await GroupAutomaticManifestRuntimeProof.RequireAsync(db, scope, effectOperation, token) != manifestGraph
                || await GroupAutomaticEffectExpectationRuntimeProof.RequireAsync(db, scope, effectOperation, token) != effectExpectation
                || await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, effectOperation, token) != graph) throw new InvalidOperationException();
            await RequireReservationAsync();
            RequireClean(db);
        }
        async Task RequireReservationAsync()
        {
            var state = await db.GroupSourceStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
            if (state.CommittedSequence != 501 || state.ScheduledThroughSequence != 500
                || state.FirstPendingAtUtc != suffix.CommittedAtUtc || state.LastPendingAtUtc is null
                || state.LastPendingAtUtc < state.FirstPendingAtUtc || state.LastPendingAtUtc.Value.Offset != TimeSpan.Zero
                || await db.GroupIngressInbox.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token) != 501)
                throw new InvalidOperationException();
            var batches = await db.GroupBatchAllocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Take(2).ToArrayAsync(token);
            if (batches.Length != 1 || batches[0].OperationId != operation || batches[0].AfterSequence != 0
                || batches[0].AllocatedThroughSequence != 500 || batches[0].ObservedCommittedThroughSequence != 501
                || batches[0].RawRevisionCount != 500) throw new InvalidOperationException();
        }
        async Task RequireEmptyAsync()
        {
            await GroupAutomaticRawRuntimeProof.RequireEmptyAsync(db, scope, token);
            if (await db.GroupWorkCommitReceipts.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupWorkSourceDispositions.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupCustomerRequests.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupRequestRevisions.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupRequestEvidence.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupNotesCommittedOutbox.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await db.GroupNotesCommittedItems.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
            RequireClean(db);
        }
    }
    internal static void RequireReferences(GroupScope scope, Guid operation, IReadOnlyList<GroupIngressOutboxRecord> references)
    {
        if (operation == Guid.Empty || references.Count != 501 || references[0].Id != operation
            || references[0].MessageId == references[1].MessageId || references.Select(x => x.Id).Distinct().Count() != 501
            || references.Where((x, index) => x.TenantId != scope.TenantId || x.CompanyId != scope.CompanyId
                || x.BindingId != scope.SourceBindingId || x.Id == Guid.Empty || x.MessageId == Guid.Empty
                || x.CommittedSequence != index + 1 || x.Revision != (index < 2 ? 1 : index)
                || x.MessageId != references[index == 1 ? 1 : 0].MessageId).Any()) throw new InvalidOperationException();
    }
    private static void RequireClean(PlatformDbContext db)
    {
        GroupAutomaticRawRuntimeProof.RequireDetached(db);
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null || db.ChangeTracker.Entries().Any(x =>
            x.Entity is GroupWorkCommitReceiptRecord or GroupWorkSourceDispositionRecord or GroupCustomerRequestRecord
                or GroupRequestRevisionRecord or GroupRequestEvidenceRecord or GroupNotesCommittedOutboxRecord or GroupNotesCommittedItemRecord))
            throw new InvalidOperationException();
    }
    private static async Task DeniedAsync(Func<Task<GroupNoWorkCommitResult>> action)
    {
        var denied = false; try { await action(); } catch (UnauthorizedAccessException) { denied = true; }
        if (!denied) throw new InvalidOperationException();
    }
    private sealed class EffectEvidence(GroupScope scope, GroupNoteRuntimeProof.OwnedClock clock)
    {
        internal bool Armed, Staged, Flushed, RolledBack;
        internal int Savepoints;
        internal Guid Operation;
        internal DateTimeOffset Expires;
        internal void Advance() => clock.Current = Expires;
        internal async Task RequireLockAsync(DbTransaction transaction, CancellationToken token)
        {
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 5;
            command.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}"; command.Parameters.Add(parameter);
            if (!string.Equals((string?)await command.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
        }
    }
    private sealed class FlushProbe(EffectEvidence effect) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (!effect.Armed || data.Context is not PlatformDbContext db
                || !db.ChangeTracker.Entries<GroupWorkCommitReceiptRecord>().Any(x => x.Entity.OperationId == effect.Operation)) return ValueTask.FromResult(result);
            var entries = db.ChangeTracker.Entries().Where(x => x.Entity is GroupWorkCommitReceiptRecord or GroupWorkSourceDispositionRecord).ToArray();
            if (entries.Length != 3 || entries.Any(x => x.State != EntityState.Added)
                || entries.Count(x => x.Entity is GroupWorkCommitReceiptRecord) != 1) throw new InvalidOperationException();
            var raw = db.ChangeTracker.Entries<GroupWorkRawDispositionRecord>().ToArray();
            if (raw.Length != 500 || raw.Any(x => x.State != EntityState.Added || x.Entity.OperationId != effect.Operation))
                throw new InvalidOperationException();
            effect.Staged = true; return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (!effect.Armed || effect.Flushed || data.Context is not PlatformDbContext db) return result;
            var receipt = db.ChangeTracker.Entries<GroupWorkCommitReceiptRecord>().SingleOrDefault(x => x.Entity.OperationId == effect.Operation)?.Entity;
            if (receipt is null) return result;
            if (!effect.Staged || await GroupNoteRuntimeProof.TargetRowsAsync(db, new(receipt.TenantId, receipt.CompanyId, receipt.BindingId), effect.Operation, token) != 3)
                throw new InvalidOperationException();
            await GroupAutomaticRawRuntimeProof.RequireAsync(db, new(receipt.TenantId, receipt.CompanyId, receipt.BindingId), effect.Operation, 500, token);
            await GroupAutomaticManifestRuntimeProof.RequireAsync(db, new(receipt.TenantId, receipt.CompanyId, receipt.BindingId), effect.Operation, token);
            await GroupAutomaticEffectExpectationRuntimeProof.RequireAsync(db, new(receipt.TenantId, receipt.CompanyId, receipt.BindingId), effect.Operation, token);
            effect.Flushed = true; effect.Advance(); return result;
        }
    }
    private sealed class RollbackProbe(EffectEvidence effect) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken token = default)
        {
            if (effect.Armed) { await effect.RequireLockAsync(transaction, token); effect.Savepoints++; }
            return result;
        }
        public override async Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData data, CancellationToken token = default)
        {
            if (!effect.Armed || !effect.Flushed || data.Context is not PlatformDbContext db) return;
            await effect.RequireLockAsync(transaction, token);
            var receipt = db.ChangeTracker.Entries<GroupWorkCommitReceiptRecord>().Single(x => x.Entity.OperationId == effect.Operation).Entity;
            if (await GroupNoteRuntimeProof.TargetRowsAsync(db, new(receipt.TenantId, receipt.CompanyId, receipt.BindingId), effect.Operation, token) != 0)
                throw new InvalidOperationException();
            await GroupAutomaticRawRuntimeProof.RequireEmptyAsync(db, new(receipt.TenantId, receipt.CompanyId, receipt.BindingId), token);
            effect.RolledBack = true;
        }
    }
}
