using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static class GroupNoteRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        var clock = new OwnedClock(TimeProvider.System.GetUtcNow()); var effect = new EffectEvidence(scope, clock);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new FlushProbe(effect), new RollbackProbe(effect)).Options);
        var batch = await db.GroupBatchAllocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation)
            .Select(x => x.Id).SingleAsync(token);
        var original = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .OrderByDescending(x => x.Epoch).FirstAsync(token);
        var expected = mode switch { "note-expiry" => 6, "note-key-expiry" => 7, "note-commit" => 8, _ => throw new InvalidOperationException() };
        var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, token);
        if (original.Epoch != expected || state.Epoch != expected
            || (expected == 6 ? state.ExpiryObservedAtUtc is not null : state.ExpiryObservedAtUtc != original.ExpiresAtUtc)) throw new InvalidOperationException();
        if (clock.Current <= original.ExpiresAtUtc) clock.Current = original.ExpiresAtUtc.AddTicks(1);
        var claimed = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, batch, Guid.NewGuid(), Guid.NewGuid(),
            TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        if (claimed.WasAlreadyClaimed || claimed.CurrentHandle is null || claimed.Receipt.Epoch != expected + 1) throw new InvalidOperationException();
        var handle = claimed.CurrentHandle; var keys = new CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var classified = await db.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .Select(x => x.MessageId).ToArrayAsync(token);
        var remaining = await db.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch && !classified.Contains(x.MessageId))
            .Select(x => x.MessageId).Distinct().ToArrayAsync(token);
        if (remaining.Length != 1 || classified.Length != 1) throw new InvalidOperationException();
        var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, remaining, token));
        var source = preparation.Candidates.Single();
        var request = await db.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.Id).SingleAsync(token);
        var glossary = await db.GroupGlossaryEntries.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.Id).SingleAsync(token);
        var dependencies = await brain.ReadAsync(handle, [request], [glossary], token);
        var proposal = Proposal(false); var effectOperation = Guid.NewGuid();
        var store = new GroupNoteCommitStore(db, worker, clock, sources, brain, keys, new());
        if (keys.Reads != 2 || keys.Writes != 0 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        if (mode is "note-expiry" or "note-key-expiry")
        {
            if (mode == "note-expiry")
            { effect.Operation = effectOperation; effect.Expires = handle.Receipt.ExpiresAtUtc; effect.Armed = true; }
            else keys.BeforeWrite = () => clock.Current = handle.Receipt.ExpiresAtUtc;
            await DeniedAsync(() => store.CommitAsync(proposal, dependencies, effectOperation, token));
            if (mode == "note-expiry" && (!effect.Flushed || !effect.RolledBack || effect.SavepointChecks < 1)) throw new InvalidOperationException();
            if (mode == "note-key-expiry" && (effect.Flushed || effect.RolledBack || effect.SavepointChecks != 0)) throw new InvalidOperationException();
            if (keys.Reads != 2 || keys.Writes != 1 || db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null
                || await TargetRowsAsync(db, scope, effectOperation, token) != 0 || db.ChangeTracker.Entries().Any(x =>
                    x.Entity is GroupWorkCommitReceiptRecord or GroupWorkSourceDispositionRecord or GroupCustomerRequestRecord
                    or GroupRequestRevisionRecord or GroupRequestEvidenceRecord or GroupNotesCommittedOutboxRecord or GroupNotesCommittedItemRecord))
                throw new InvalidOperationException();
            state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, token);
            if (state.Epoch != expected + 1 || state.ExpiryObservedAtUtc != handle.Receipt.ExpiresAtUtc) throw new InvalidOperationException();
            clock.Current = handle.Receipt.IssuedAtUtc;
            await DeniedAsync(() => store.CommitAsync(proposal, dependencies, effectOperation, token));
            if (keys.Reads != 2 || keys.Writes != 1 || db.ChangeTracker.HasChanges() || await TargetRowsAsync(db, scope, effectOperation, token) != 0)
                throw new InvalidOperationException();
            Console.WriteLine(mode == "note-expiry"
                ? "PASS owned note runtime eleven flushed SQL effects rollback with source lock retained clean detach witness only and clock rollback denial"
                : "PASS owned note runtime configured write key outside SQL expiry witness only without effects and clock rollback denial");
            return;
        }
        var committed = await store.CommitAsync(proposal, dependencies, effectOperation, token);
        if (committed.WasAlreadyCommitted || committed.RequestIds.Count != 2 || committed.Scope != scope || committed.BatchId != batch
            || await TargetRowsAsync(db, scope, effectOperation, token) != 11) throw new InvalidOperationException();
        var readback = await brain.ReadAsync(handle, committed.RequestIds, [], token);
        for (var index = 0; index < committed.RequestIds.Count; index++)
        {
            var item = readback.Items.Single(x => x.RecordId == committed.RequestIds[index]);
            var clear = GroupBrainPayloadCodec.DecodeAiNote(item.Content);
            if (item.Revision != 1 || item.IsItConfirmed || item.Content != GroupBrainPayloadCodec.EncodeAiNote(proposal.Notes[index])
                || clear.Evidence.Count != 1 || clear.Evidence[0].MessageId != source.MessageId || clear.Evidence[0].Revision != source.Revision
                || clear.Evidence[0].Quote != source.Text || item.BusinessStatus != (index == 0 ? GroupNoteBusinessStatus.New : GroupNoteBusinessStatus.NeedsClarification))
                throw new InvalidOperationException();
        }
        var replay = await store.CommitAsync(proposal, dependencies, effectOperation, token);
        if (!replay.WasAlreadyCommitted || replay.Scope != committed.Scope || replay.BatchId != committed.BatchId
            || replay.OperationId != committed.OperationId || replay.OutboxId != committed.OutboxId || replay.CommittedAtUtc != committed.CommittedAtUtc
            || !replay.RequestIds.SequenceEqual(committed.RequestIds)) throw new InvalidOperationException();
        await RefuseAsync(() => store.CommitAsync(Proposal(true), dependencies, effectOperation, token));
        var reads = keys.Reads; var writes = keys.Writes;
        await RefuseAsync(() => store.CommitAsync(proposal, dependencies, Guid.NewGuid(), token));
        if (keys.Reads != reads || keys.Writes != writes || writes != 1 || reads != 5 || db.ChangeTracker.HasChanges()
            || db.Database.CurrentTransaction is not null || await TargetRowsAsync(db, scope, effectOperation, token) != 11
            || await db.GroupCustomerRequests.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.OriginOperationId == effectOperation
                && (x.AssignedToUserId != null || x.CommittedDueAtUtc != null || x.ConfirmedByUserId != null || x.ConfirmedAtUtc != null), token))
            throw new InvalidOperationException();
        Console.WriteLine("PASS owned note runtime protected two notes literal evidence atomic NotesCommitted exact original replay changed proposal and new nonce refusal");

        GroupGroundedWorkProposal Proposal(bool changed) => GroupGroundedWorkProposal.Parse(preparation, JsonSerializer.Serialize(new
        {
            version = GroupGroundedWorkProposal.FormatVersion,
            // Fixed synthetic interpretations; this is not evaluated model output.
            notes = new[] { Note("request", changed ? "owned changed interpretation" : "owned synthetic request", []),
                Note("needs_clarification", "owned synthetic clarification", ["owned missing detail"]) },
            source_dispositions = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, disposition = "work" } }
        }));
        object Note(string type, string title, string[] missing) => new
        {
            type,
            title,
            problem = "owned synthetic interpretation",
            outcome = "owned synthetic requested outcome",
            source_refs = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, quote = source.Text } },
            missing_fields = missing,
            requested_deadline_text = (string?)null,
            suggested_relation = (string?)null
        };
    }
    private static async Task DeniedAsync(Func<Task<GroupNoteCommitResult>> action)
    {
        var denied = false; try { await action(); } catch (UnauthorizedAccessException) { denied = true; }
        if (!denied) throw new InvalidOperationException();
    }
    private static async Task RefuseAsync(Func<Task<GroupNoteCommitResult>> action)
    {
        var refused = false;
        try { await action(); } catch (InvalidOperationException error) when (error.Message == "Group note commit is unavailable." && error.InnerException is null) { refused = true; }
        if (!refused) throw new InvalidOperationException();
    }
    private static async Task<int> TargetRowsAsync(PlatformDbContext db, GroupScope scope, Guid operation, CancellationToken token)
    {
        var ids = await db.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.OriginOperationId == operation).Select(x => x.Id).ToArrayAsync(token);
        var outboxes = await db.GroupNotesCommittedOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.OperationId == operation).Select(x => x.Id).ToArrayAsync(token);
        return ids.Length + outboxes.Length
            + await db.GroupWorkCommitReceipts.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token)
            + await db.GroupWorkSourceDispositions.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token)
            + await db.GroupRequestRevisions.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && ids.Contains(x.RequestId), token)
            + await db.GroupRequestEvidence.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && ids.Contains(x.RequestId), token)
            + await db.GroupNotesCommittedItems.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && outboxes.Contains(x.OutboxId), token);
    }
    private sealed class OwnedClock(DateTimeOffset now) : TimeProvider
    { internal DateTimeOffset Current = now; public override DateTimeOffset GetUtcNow() => Current; }
    private sealed class EffectEvidence(GroupScope scope, OwnedClock clock)
    {
        internal bool Armed, Flushed, RolledBack;
        internal int SavepointChecks;
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
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken token = default)
        {
            if (!effect.Armed || effect.Flushed || eventData.Context is not PlatformDbContext db
                || !db.ChangeTracker.Entries<GroupWorkCommitReceiptRecord>().Any(x => x.Entity.OperationId == effect.Operation)) return result;
            var receipt = db.ChangeTracker.Entries<GroupWorkCommitReceiptRecord>().Single(x => x.Entity.OperationId == effect.Operation).Entity;
            if (await TargetRowsAsync(db, new(receipt.TenantId, receipt.CompanyId, receipt.BindingId), effect.Operation, token) != 11) throw new InvalidOperationException();
            effect.Flushed = true; effect.Advance(); return result;
        }
    }
    private sealed class RollbackProbe(EffectEvidence effect) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken token = default)
        { if (effect.Armed) { await effect.RequireLockAsync(transaction, token); effect.SavepointChecks++; } return result; }
        public override async Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData data, CancellationToken token = default)
        {
            if (!effect.Armed || !effect.Flushed || data.Context is not PlatformDbContext db) return;
            await effect.RequireLockAsync(transaction, token);
            var receipt = db.ChangeTracker.Entries<GroupWorkCommitReceiptRecord>().Single(x => x.Entity.OperationId == effect.Operation).Entity;
            if (await TargetRowsAsync(db, new(receipt.TenantId, receipt.CompanyId, receipt.BindingId), effect.Operation, token) != 0) throw new InvalidOperationException();
            effect.RolledBack = true;
        }
    }
    private sealed class CountedKeys : IGroupSourceKeyProvider
    {
        private readonly PlatformDbContext db; private readonly GroupScope scope; private readonly ConfiguredGroupSourceKeyProvider configured;
        internal int Reads, Writes; internal Action? BeforeWrite;
        internal CountedKeys(PlatformDbContext db, GroupScope scope)
        {
            this.db = db; this.scope = scope;
            configured = new(new CompositeSecretResolver([new EnvironmentVariableSecretResolver()]),
                [new(scope, "owned-native-source-v1", SecretReference.Parse("secretref://env/AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY"), true)]);
        }
        private void RequireOutsideSql(GroupScope source)
        { if (source != scope || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException(); }
        public async ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken token = default)
        {
            RequireOutsideSql(source); Writes++; var written = await configured.ResolveWriteAsync(source, token); BeforeWrite?.Invoke(); return written;
        }
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string id, CancellationToken token = default)
        {
            RequireOutsideSql(source); if (id != "owned-native-source-v1") throw new InvalidOperationException();
            Reads++; return await configured.ResolveReadAsync(source, id, token);
        }
    }
}
