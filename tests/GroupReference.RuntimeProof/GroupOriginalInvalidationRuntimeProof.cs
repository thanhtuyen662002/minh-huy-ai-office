using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Owned, independently enrolled source only. This first foundation covers a
// legacy V2 original with zero contributors; no continuation is activated.
internal static class GroupOriginalInvalidationRuntimeProof
{
    internal const string PrepareLine = "PASS owned original invalidation zero prepare actual missing original current metadata legacy V2 epoch one two candidates empty history zero checkpoint keys effects or provider";
    internal const string CommitLine = "PASS owned original invalidation zero commit actual Core edit beyond original cutoff one immutable checkpoint committed reply loss exact original graph legacy reads and starts refused zero checkpoint keys effects or provider";
    internal const string ReplayLine = "PASS owned original invalidation zero separate process exact epoch one checkpoint replay original bytes digest claims allocation unchanged zero keys effects or provider";
    private const string Profile = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Model = "owned-synthetic-invalidation";
    internal static string Phase { get; private set; } = "setup";
    private static readonly (string Table, string Order)[] OriginalTables =
    [
        ("GroupBatchAllocations", "Id"), ("GroupBatchAllocatedRevisions", "BatchId,CommittedSequence"),
        ("GroupBatchClaimStates", "BatchId"), ("GroupBatchClaimReceipts", "OperationId")
    ];
    private static readonly (string Table, string Order)[] SourceTables =
    [
        ("GroupSourceStates", "BindingId"), ("GroupMessages", "Id"),
        ("GroupMessageRevisions", "MessageId,Revision"), ("GroupIngressReceipts", "MessageId,Revision"),
        ("GroupIngressOutbox", "Id"), ("GroupIngressInbox", "EventId")
    ];
    private static readonly string[] EmptyTables =
    [
        "GroupOriginalAdmissionSidecars", "GroupModelAttemptAdmissions", "GroupModelAttemptStarts",
        "GroupModelAttemptResults", "GroupModelAttemptFailures", "GroupWorkCommitReceipts",
        "GroupWorkSourceDispositions", "GroupWorkRawDispositions", "GroupCustomerRequests",
        "GroupRequestRevisions", "GroupRequestEvidence", "GroupNotesCommittedOutbox", "GroupNotesCommittedItems",
        "GroupBatchTerminalReceipts", "GroupTerminalAdvances", "GroupTerminalAdvanceKeys", "GroupTerminalFrontierStates"
    ];

    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("invalidation-zero-prepare" or "invalidation-zero-commit" or "invalidation-zero-replay")) throw Invalid();
        scope.Validate(); worker.Validate(); token.ThrowIfCancellationRequested();
        var prepare = mode == "invalidation-zero-prepare";
        var commit = mode == "invalidation-zero-commit";
        var observation = new CheckpointObservation(); var refusals = new ActivityRefusalObservation();
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new CheckpointFlushProbe(observation), new CheckpointTransactionProbe(observation, refusals),
                new ActivityReadProbe(refusals, scope)).Options);
        RequireClean(db); Phase = "allocation";
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        if (allocation.AfterSequence != 0 || allocation.AllocatedThroughSequence != 2
            || allocation.ObservedCommittedThroughSequence != 2 || allocation.RawRevisionCount != 2) throw Invalid();
        var members = await db.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id)
            .OrderBy(x => x.CommittedSequence).ToArrayAsync(token);
        if (members.Length != 2 || !members.Select(x => x.CommittedSequence).SequenceEqual([1L, 2L])
            || members.Any(x => x.Revision != 1 || x.Kind != GroupSourceEventKind.NewText)
            || members[0].MessageId == members[1].MessageId) throw Invalid();
        var receipts = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).ToArrayAsync(token);
        if (receipts.Length != (prepare ? 0 : 1)) throw Invalid();
        var clock = new GroupNoteRuntimeProof.OwnedClock(prepare ? allocation.AllocatedAtUtc : receipts[0].IssuedAtUtc);
        var claims = new GroupBatchClaimStore(db, worker, clock); Phase = "claim";
        var claim = await claims.TryAcquireAsync(scope, allocation.Id, prepare ? Guid.NewGuid() : receipts[0].OwnerId,
            prepare ? Guid.NewGuid() : receipts[0].OperationId, TimeSpan.FromMinutes(2), token) ?? throw Invalid();
        if (claim.CurrentHandle is not { } handle || claim.WasAlreadyClaimed == prepare
            || handle.Receipt.Epoch != 1 || handle.Receipt.IssuedAtUtc != clock.Current) throw Invalid();
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var original = new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain);
        var checkpoints = new GroupOriginalInvalidationStore(db, worker, clock, sources, brain);
        await RequireEmptyAsync(db, scope, token);
        await RequireSourceShapeAsync(db, scope, members, !prepare, token);
        var retained = await GraphAsync(db, scope, OriginalTables.Concat(SourceTables), token);
        if (prepare)
        {
            if (await Plans().AnyAsync(token) || await Rows().AnyAsync(token)) throw Invalid();
            Phase = "missing-original";
            if (await checkpoints.CheckpointAsync(handle, token) != GroupOriginalInvalidationOutcome.MissingOriginal
                || keys.Reads != 0 || keys.Writes != 0 || await Plans().AnyAsync(token) || await Rows().AnyAsync(token)) throw Invalid();
            Same(retained, await GraphAsync(db, scope, OriginalTables.Concat(SourceTables), token));
            RequireClean(db); Phase = "private-input";
            var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, members.Select(x => x.MessageId).ToArray(), token));
            var history = await brain.ReadAsync(handle, [], [], token);
            if (preparation.Candidates.Count != 2 || preparation.Receipts.Count != 2 || history.Items.Count != 0
                || preparation.Context.HasCoverageGap || keys.Reads != 1 || keys.Writes != 0) throw Invalid();
            Phase = "legacy-original";
            var plan = GroupAutomaticInputPlan.Create(preparation, history, Profile, Model, 64000, [], token);
            var persisted = await original.PersistAsync(plan, preparation, history, token);
            if (!persisted.Encode().AsSpan().SequenceEqual(plan.Encode())) throw Invalid();
            var row = await Plans().SingleAsync(token); RequireOriginal(row, handle, members);
            Same(retained, await GraphAsync(db, scope, OriginalTables.Concat(SourceTables), token));
            var frozen = await GraphAsync(db, scope, OriginalTables.Concat(SourceTables).Append(("GroupAutomaticInputPlans", "BatchId")), token);
            Phase = "current-metadata";
            if (await checkpoints.CheckpointAsync(handle, token) != GroupOriginalInvalidationOutcome.CurrentMetadata
                || await Rows().AnyAsync(token) || keys.Reads != 1 || keys.Writes != 0) throw Invalid();
            Same(frozen, await GraphAsync(db, scope, OriginalTables.Concat(SourceTables).Append(("GroupAutomaticInputPlans", "BatchId")), token));
            await RequireEmptyAsync(db, scope, token); RequireClean(db); Console.WriteLine(PrepareLine); return;
        }
        var stored = await Plans().SingleAsync(token); var oldPlan = RequireOriginal(stored, handle, members);
        var originalGraph = await GraphAsync(db, scope, OriginalTables.Concat(SourceTables).Append(("GroupAutomaticInputPlans", "BatchId")), token);
        if (commit)
        {
            if (await Rows().AnyAsync(token)) throw Invalid();
            // A genuinely valid current legacy context uses the unchanged second
            // member, not a forged stale original context or a null argument.
            Phase = "private-input";
            var legacyPreparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, [members[1].MessageId], token));
            var history = await brain.ReadAsync(handle, [], [], token);
            if (legacyPreparation.Candidates.Count != 1 || history.Items.Count != 0 || keys.Reads != 1 || keys.Writes != 0) throw Invalid();
            var legacyContext = GroupAutomaticModelContext.Create(legacyPreparation, history, Model, 64000);
            var readCount = keys.Reads; Phase = "checkpoint-commit";
            observation.Begin(scope, handle.Receipt.BatchId, handle.Receipt.OperationId, handle.Receipt.Epoch, oldPlan.Sha256);
            var lost = false;
            try { await checkpoints.CheckpointAsync(handle, token); }
            catch (OwnedCheckpointReplyLost) { lost = true; }
            finally { observation.End(); }
            observation.RequireLostReply(lost);
            if (keys.Reads != readCount || keys.Writes != 0) throw Invalid();
            RequireClean(db); var checkpoint = await Rows().SingleAsync(token);
            RequireCheckpoint(checkpoint, stored, handle, receipts[0], clock.Current);
            var checkpointHash = RowHash(checkpoint);
            Same(originalGraph, await GraphAsync(db, scope, OriginalTables.Concat(SourceTables).Append(("GroupAutomaticInputPlans", "BatchId")), token));
            Phase = "legacy-read-refusal";
            await RefusedAtActivityAsync(() => original.ReadAsync(handle, token), "Group original input plan store is not available.");
            await RefusedAtActivityAsync(() => new GroupModelAttemptJournalStore(db, worker, clock, sources, brain)
                .TryStartAsync(legacyContext, Guid.NewGuid(), token), "Group model attempt journal is not available.");
            if (keys.Reads != readCount || keys.Writes != 0) throw Invalid();
            Phase = "checkpoint-replay";
            await using (var fresh = new PlatformDbContext(options))
            {
                var freshKeys = new GroupNoteRuntimeProof.CountedKeys(fresh, scope);
                var fs = new GroupBatchSourceReader(fresh, worker, clock, freshKeys, new());
                var fb = new GroupBrainCurrentReader(fresh, worker, clock, freshKeys, new());
                var replay = await new GroupBatchClaimStore(fresh, worker, clock).TryAcquireAsync(scope, allocation.Id,
                    handle.Receipt.OwnerId, handle.Receipt.OperationId, TimeSpan.FromMinutes(2), token) ?? throw Invalid();
                if (!replay.WasAlreadyClaimed || replay.CurrentHandle is not { } replayHandle || replayHandle.Receipt != handle.Receipt
                    || await new GroupOriginalInvalidationStore(fresh, worker, clock, fs, fb).CheckpointAsync(replayHandle, token)
                        != GroupOriginalInvalidationOutcome.AlreadyCheckpointed || freshKeys.Reads != 0 || freshKeys.Writes != 0) throw Invalid();
                RequireClean(fresh);
            }
            Same(checkpointHash, RowHash(await Rows().SingleAsync(token)));
        }
        else
        {
            Phase = "checkpoint-replay";
            var checkpoint = await Rows().SingleAsync(token); RequireCheckpoint(checkpoint, stored, handle, receipts[0], clock.Current);
            var checkpointHash = RowHash(checkpoint);
            if (await checkpoints.CheckpointAsync(handle, token) != GroupOriginalInvalidationOutcome.AlreadyCheckpointed
                || keys.Reads != 0 || keys.Writes != 0) throw Invalid();
            Same(checkpointHash, RowHash(await Rows().SingleAsync(token)));
        }
        Phase = "verify";
        Same(originalGraph, await GraphAsync(db, scope, OriginalTables.Concat(SourceTables).Append(("GroupAutomaticInputPlans", "BatchId")), token));
        if (await Rows().CountAsync(token) != 1 || await Plans().CountAsync(token) != 1) throw Invalid();
        await RequireEmptyAsync(db, scope, token); RequireClean(db); Console.WriteLine(commit ? CommitLine : ReplayLine);

        IQueryable<GroupAutomaticInputPlanRecord> Plans() => db.GroupAutomaticInputPlans.AsNoTracking().Where(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id);
        IQueryable<GroupOriginalInvalidationRecord> Rows() => db.GroupOriginalInvalidations.AsNoTracking().Where(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id);
        async Task RefusedAtActivityAsync(Func<Task> action, string expected)
        {
            refusals.Begin(allocation.Id); var refused = false;
            try { await action(); }
            catch (InvalidOperationException error) when (error.GetType() == typeof(InvalidOperationException)
                && error.Message == expected && error.InnerException is null)
            { refused = true; }
            finally { refusals.End(); }
            refusals.RequireRefused(refused); RequireClean(db);
        }
    }

    private static GroupAutomaticInputPlan RequireOriginal(GroupAutomaticInputPlanRecord row, GroupBatchClaimHandle handle,
        GroupBatchAllocatedRevisionRecord[] members)
    {
        var plan = GroupAutomaticInputPlan.Read(row.Plan);
        if (row.FormatVersion != 2 || plan.WireFormatVersion != 2 || row.PlanSha256 != plan.Sha256
            || row.OriginalClaimEpoch != 1 || row.OriginalClaimOperationId != handle.Receipt.OperationId
            || row.CreatedAtUtc != handle.Receipt.IssuedAtUtc || plan.Dependencies.Scope != handle.Receipt.Scope
            || plan.Dependencies.BatchId != handle.Receipt.BatchId || plan.Dependencies.AllocatedThroughSequence != 2
            || plan.HistoryOrder.Count != 0 || plan.Dependencies.Dependencies.Count != 0 || plan.Dependencies.Sources.Count != 2
            || plan.Chunks.Count != 1 || !plan.Chunks[0].RequiresModel || plan.Chunks[0].Members.Count != 2
            || !plan.Dependencies.Sources.Select(x => x.MessageId).Order().SequenceEqual(members.Select(x => x.MessageId).Order())
            || plan.Dependencies.Sources.Any(x => x.Revision != 1)) throw Invalid();
        return plan;
    }

    private static void RequireCheckpoint(GroupOriginalInvalidationRecord row, GroupAutomaticInputPlanRecord original,
        GroupBatchClaimHandle handle, GroupBatchClaimReceiptRecord claim, DateTimeOffset now)
    {
        if (row.ClaimOperationId != handle.Receipt.OperationId || row.ClaimEpoch != 1 || row.CreatedAtUtc != handle.Receipt.IssuedAtUtc) throw Invalid();
        GroupOriginalInvalidationProof.Require(row, handle.Receipt.Scope, handle.Receipt.BatchId,
            new(GroupAutomaticInputPlan.Read(original.Plan), original.OriginalClaimOperationId, original.OriginalClaimEpoch, original.CreatedAtUtc), claim, now);
    }

    private static async Task RequireSourceShapeAsync(PlatformDbContext db, GroupScope scope,
        GroupBatchAllocatedRevisionRecord[] members, bool edited, CancellationToken token)
    {
        var state = await db.GroupSourceStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        var messages = await db.GroupMessages.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.Id).ToArrayAsync(token);
        var revisions = await db.GroupMessageRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.CommittedSequence)
            .Select(x => new { x.MessageId, x.Revision, x.CommittedSequence, x.Kind }).ToArrayAsync(token);
        // Same current-winner precedence as shipping HeadAsync at long.MaxValue:
        // Recall > Edit > ordinary, then greatest revision within that kind.
        var heads = revisions.GroupBy(x => x.MessageId).Select(group => group
            .OrderByDescending(x => x.Kind == GroupSourceEventKind.Recall ? 3 : x.Kind == GroupSourceEventKind.Edit ? 2 : 1)
            .ThenByDescending(x => x.Revision).First()).ToArray();
        if (state.ScheduledThroughSequence != 2 || state.CommittedSequence != (edited ? 3 : 2) || heads.Length != 2 || messages.Length != 2
            || !messages.Order().SequenceEqual(members.Select(x => x.MessageId).Order())
            || revisions.Length != (edited ? 3 : 2) || heads.Single(x => x.MessageId == members[0].MessageId).Revision != (edited ? 2 : 1)
            || heads.Single(x => x.MessageId == members[1].MessageId).Revision != 1
            || !revisions.Take(2).Select(x => x.CommittedSequence).SequenceEqual([1L, 2L])
            || edited && (revisions[2].MessageId != members[0].MessageId || revisions[2].Revision != 2
                || revisions[2].CommittedSequence != 3 || revisions[2].Kind != GroupSourceEventKind.Edit)
            || await db.GroupIngressInbox.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId, token) != 2) throw Invalid();
    }

    private static async Task<string> GraphAsync(PlatformDbContext db, GroupScope scope,
        IEnumerable<(string Table, string Order)> tables, CancellationToken token)
    {
        var hashes = new List<string>();
        foreach (var (table, order) in tables)
        {
            // Identifiers originate solely from the closed lists above. Full
            // ciphertext/metadata graph is hashed, never printed as evidence text.
            var sql = "SELECT (SELECT * FROM aioffice." + table
                + " WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding ORDER BY " + order
                + " FOR JSON PATH,INCLUDE_NULL_VALUES) AS [Value]";
            var json = await db.Database.SqlQueryRaw<string>(sql, new SqlParameter("@tenant", scope.TenantId),
                new SqlParameter("@company", scope.CompanyId), new SqlParameter("@binding", scope.SourceBindingId)).SingleAsync(token);
            hashes.Add(table + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        }
        return string.Join("/", hashes);
    }

    private static async Task RequireEmptyAsync(PlatformDbContext db, GroupScope scope, CancellationToken token)
    {
        foreach (var table in EmptyTables)
        {
            // The identifier is from the closed EmptyTables list, never private
            // input. Keep the fixed query separate and scope values parameterized.
            var query = "SELECT COUNT(*) AS [Value] FROM aioffice." + table
                + " WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding";
            var count = await db.Database.SqlQueryRaw<int>(query, new SqlParameter("@tenant", scope.TenantId),
                new SqlParameter("@company", scope.CompanyId), new SqlParameter("@binding", scope.SourceBindingId)).SingleAsync(token);
            if (count != 0) throw Invalid();
        }
    }

    private static string RowHash(GroupOriginalInvalidationRecord row) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row)));
    private static void Same(string expected, string actual) { if (expected != actual) throw Invalid(); }
    private static void RequireClean(PlatformDbContext db)
    {
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed) throw Invalid();
    }
    private static InvalidOperationException Invalid() => new("Owned original invalidation observation is unavailable.");
    private sealed class OwnedCheckpointReplyLost : IOException;

    internal sealed class CheckpointObservation
    {
        private GroupScope? scope; private Guid batch, operation; private long epoch; private string? plan, rowHash;
        private bool flushed, committing, lost;
        internal bool Armed { get; private set; }
        internal void Begin(GroupScope value, Guid batchId, Guid operationId, long claimEpoch, string planHash)
        {
            if (Armed || scope is not null || batchId == Guid.Empty || operationId == Guid.Empty || claimEpoch != 1) throw Invalid();
            value.Validate(); scope = value; batch = batchId; operation = operationId; epoch = claimEpoch; plan = planHash; Armed = true;
        }
        internal void Saving(GroupOriginalInvalidationRecord row, int changed)
        {
            if (!Armed || flushed || rowHash is not null || changed != 1 || row.TenantId != scope!.TenantId
                || row.CompanyId != scope.CompanyId || row.BindingId != scope.SourceBindingId || row.BatchId != batch
                || row.ClaimOperationId != operation || row.ClaimEpoch != epoch || row.PlanSha256 != plan
                || row.CheckpointSha256 != GroupOriginalInvalidationProof.Digest(row)) throw Invalid();
            rowHash = RowHash(row);
        }
        internal void Saved(GroupOriginalInvalidationRecord row, int changed)
        { if (!Armed || flushed || rowHash is null || changed != 1 || RowHash(row) != rowHash) throw Invalid(); flushed = true; }
        internal void Committing() { if (!Armed || !flushed || committing || lost) throw Invalid(); committing = true; }
        internal void Committed()
        { if (!Armed || !committing || lost) throw Invalid(); lost = true; throw new OwnedCheckpointReplyLost(); }
        internal void End() => Armed = false;
        internal void RequireLostReply(bool caught) { if (!caught || !flushed || !committing || !lost) throw Invalid(); }
    }

    internal sealed class ActivityRefusalObservation
    {
        private DbCommand? command;
        private DbTransaction? transaction;
        private string? dispatchedSql;
        private int dispatches, executions, rows, values, ends, commits;
        private bool used, failed, readerClosed;
        internal bool Armed { get; private set; }
        internal Guid BatchId { get; private set; }
        internal void Begin(Guid batch)
        {
            if (Armed || batch == Guid.Empty) throw Invalid();
            BatchId = batch; command = null; transaction = null; dispatchedSql = null;
            dispatches = executions = rows = values = ends = commits = 0;
            failed = readerClosed = false; used = Armed = true;
        }
        internal void Query(string sql)
        {
            if (!Armed) return;
            if (sql.Contains("FROM [aioffice].[GroupOriginalInvalidations]", StringComparison.Ordinal))
            { if (!sql.Contains("EXISTS", StringComparison.Ordinal)) { failed = true; throw Invalid(); } }
            if (sql.Contains("FROM aioffice.GroupAutomaticInputPlans", StringComparison.Ordinal)
                || sql.Contains("ProtectedContent", StringComparison.Ordinal)) { failed = true; throw Invalid(); }
        }
        internal void Dispatched(DbCommand value, DbTransaction owned)
        {
            if (!Armed || ++dispatches != 1 || !ReferenceEquals(value.Transaction, owned)
                || !value.CommandText.Contains("FROM [aioffice].[GroupOriginalInvalidations]", StringComparison.Ordinal)
                || !value.CommandText.Contains("EXISTS", StringComparison.Ordinal))
            { failed = true; throw Invalid(); }
            command = value; transaction = owned; dispatchedSql = value.CommandText;
        }
        internal DbDataReader Executed(DbCommand value, DbTransaction owned, DbDataReader reader)
        {
            if (!Armed || dispatches != 1 || ++executions != 1 || !ReferenceEquals(command, value)
                || !ReferenceEquals(transaction, owned) || !ReferenceEquals(value.Transaction, owned)
                || value.CommandText != dispatchedSql)
            { failed = true; throw Invalid(); }
            try { if (reader.FieldCount != 1) failed = true; }
            catch { failed = true; throw; }
            // Only a successful ReaderExecuted callback can install this
            // transparent observer. Dispatch never qualifies a scalar result.
            return new ActivityObservedReader(reader, this);
        }
        internal void CommandFailed() { if (Armed) failed = true; }
        internal void Read(bool available)
        {
            if (!Armed || executions != 1 || readerClosed || ends != 0) failed = true;
            if (available) { if (++rows != 1) failed = true; }
            else if (++ends != 1 || rows != 1 || values != 1) failed = true;
        }
        internal void Scalar(int ordinal, object? value)
        {
            if (!Armed || executions != 1 || rows != 1 || ends != 0 || readerClosed
                || ordinal != 0 || value is not true || ++values != 1) failed = true;
        }
        internal void ReadFailed() => failed = true;
        internal void Closed()
        {
            if (rows != 1 || values != 1 || ends != 1) failed = true;
            readerClosed = true;
        }
        internal void Committed() { if (Armed) commits++; }
        internal void End() => Armed = false;
        internal void RequireRefused(bool refused)
        {
            if (!used || !refused || failed || dispatches != 1 || executions != 1 || rows != 1 || values != 1
                || ends != 1 || !readerClosed || commits != 0) throw Invalid();
        }
    }

    // Delegates every operation; no early read, suppressed result, alternate
    // value or swallowed fault can substitute for EF consuming one true bool.
    private sealed class ActivityObservedReader(DbDataReader inner, ActivityRefusalObservation observation) : DbDataReader
    {
        private T Forward<T>(Func<T> action)
        { try { return action(); } catch { observation.ReadFailed(); throw; } }
        private T Scalar<T>(int ordinal, Func<T> action)
        { var value = Forward(action); observation.Scalar(ordinal, value); return value; }
        public override bool Read() { var value = Forward(inner.Read); observation.Read(value); return value; }
        public override async Task<bool> ReadAsync(CancellationToken token)
        {
            try { var value = await inner.ReadAsync(token); observation.Read(value); return value; }
            catch { observation.ReadFailed(); throw; }
        }
        public override bool GetBoolean(int ordinal) => Scalar(ordinal, () => inner.GetBoolean(ordinal));
        public override object GetValue(int ordinal) => Scalar(ordinal, () => inner.GetValue(ordinal));
        public override T GetFieldValue<T>(int ordinal) => Scalar(ordinal, () => inner.GetFieldValue<T>(ordinal));
        public override async Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken token)
        {
            try { var value = await inner.GetFieldValueAsync<T>(ordinal, token); observation.Scalar(ordinal, value); return value; }
            catch { observation.ReadFailed(); throw; }
        }
        public override int GetValues(object[] values)
        {
            var count = Forward(() => inner.GetValues(values));
            if (count == 1) observation.Scalar(0, values[0]); else observation.ReadFailed(); return count;
        }
        public override object this[int ordinal] => Scalar(ordinal, () => inner[ordinal]);
        public override object this[string name] => Scalar(Forward(() => inner.GetOrdinal(name)), () => inner[name]);
        public override bool NextResult() { var value = Forward(inner.NextResult); if (value) observation.ReadFailed(); return value; }
        public override async Task<bool> NextResultAsync(CancellationToken token)
        {
            try { var value = await inner.NextResultAsync(token); if (value) observation.ReadFailed(); return value; }
            catch { observation.ReadFailed(); throw; }
        }
        public override void Close()
        { try { inner.Close(); observation.Closed(); } catch { observation.ReadFailed(); throw; } }
        public override async Task CloseAsync()
        { try { await inner.CloseAsync(); observation.Closed(); } catch { observation.ReadFailed(); throw; } }
        protected override void Dispose(bool disposing)
        { if (disposing) { try { inner.Dispose(); observation.Closed(); } catch { observation.ReadFailed(); throw; } } }
        public override async ValueTask DisposeAsync()
        { try { await inner.DisposeAsync(); observation.Closed(); } catch { observation.ReadFailed(); throw; } }
        public override int Depth => Forward(() => inner.Depth);
        public override int FieldCount => Forward(() => inner.FieldCount);
        public override int VisibleFieldCount => Forward(() => inner.VisibleFieldCount);
        public override bool HasRows => Forward(() => inner.HasRows);
        public override bool IsClosed => Forward(() => inner.IsClosed);
        public override int RecordsAffected => Forward(() => inner.RecordsAffected);
        public override string GetName(int ordinal) => Forward(() => inner.GetName(ordinal));
        public override string GetDataTypeName(int ordinal) => Forward(() => inner.GetDataTypeName(ordinal));
        public override Type GetFieldType(int ordinal) => Forward(() => inner.GetFieldType(ordinal));
        public override int GetOrdinal(string name) => Forward(() => inner.GetOrdinal(name));
        public override bool IsDBNull(int ordinal) => Forward(() => inner.IsDBNull(ordinal));
        public override async Task<bool> IsDBNullAsync(int ordinal, CancellationToken token)
        { try { return await inner.IsDBNullAsync(ordinal, token); } catch { observation.ReadFailed(); throw; } }
        public override byte GetByte(int ordinal) => Scalar(ordinal, () => inner.GetByte(ordinal));
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            Forward(() => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length));
        public override char GetChar(int ordinal) => Scalar(ordinal, () => inner.GetChar(ordinal));
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            Forward(() => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length));
        public override Guid GetGuid(int ordinal) => Scalar(ordinal, () => inner.GetGuid(ordinal));
        public override short GetInt16(int ordinal) => Scalar(ordinal, () => inner.GetInt16(ordinal));
        public override int GetInt32(int ordinal) => Scalar(ordinal, () => inner.GetInt32(ordinal));
        public override long GetInt64(int ordinal) => Scalar(ordinal, () => inner.GetInt64(ordinal));
        public override float GetFloat(int ordinal) => Scalar(ordinal, () => inner.GetFloat(ordinal));
        public override double GetDouble(int ordinal) => Scalar(ordinal, () => inner.GetDouble(ordinal));
        public override decimal GetDecimal(int ordinal) => Scalar(ordinal, () => inner.GetDecimal(ordinal));
        public override string GetString(int ordinal) => Scalar(ordinal, () => inner.GetString(ordinal));
        public override DateTime GetDateTime(int ordinal) => Scalar(ordinal, () => inner.GetDateTime(ordinal));
        public override DataTable? GetSchemaTable() => Forward(inner.GetSchemaTable);
        public override System.Collections.IEnumerator GetEnumerator() => Forward(inner.GetEnumerator);
    }

    private sealed class ActivityReadProbe(ActivityRefusalObservation observation, GroupScope scope) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (!observation.Armed) return result;
            observation.Query(command.CommandText);
            if (command.CommandText.Contains("FROM [aioffice].[GroupOriginalInvalidations]", StringComparison.Ordinal))
            {
                if (result.HasResult || data.Context?.Database.CurrentTransaction is not { } tx
                    || command.Transaction is not { IsolationLevel: IsolationLevel.Serializable } transaction
                    || !ReferenceEquals(tx.GetDbTransaction(), transaction)
                    || command.Connection is not { State: ConnectionState.Open } connection
                    || !ReferenceEquals(connection, data.Context.Database.GetDbConnection())
                    || !new[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId, observation.BatchId }
                        .All(id => command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, id)))) throw Invalid();
                await using var lockCommand = connection.CreateCommand(); lockCommand.Transaction = transaction; lockCommand.CommandTimeout = 5;
                lockCommand.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
                var parameter = lockCommand.CreateParameter(); parameter.ParameterName = "@resource";
                parameter.Value = $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}";
                lockCommand.Parameters.Add(parameter);
                if (!Equals(await lockCommand.ExecuteScalarAsync(token), "Exclusive")) throw Invalid();
                observation.Dispatched(command, transaction);
            }
            return result;
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken token = default)
        {
            if (!observation.Armed || !command.CommandText.Contains("FROM [aioffice].[GroupOriginalInvalidations]", StringComparison.Ordinal))
                return ValueTask.FromResult(result);
            if (data.Context?.Database.CurrentTransaction is not { } tx || command.Transaction is not { } transaction
                || !ReferenceEquals(tx.GetDbTransaction(), transaction) || transaction.IsolationLevel != IsolationLevel.Serializable
                || command.Connection?.State != ConnectionState.Open
                || !ReferenceEquals(command.Connection, data.Context.Database.GetDbConnection())
                || !new[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId, observation.BatchId }
                    .All(id => command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, id))))
            { observation.ReadFailed(); throw Invalid(); }
            return ValueTask.FromResult(observation.Executed(command, transaction, result));
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData data, CancellationToken token = default)
        { observation.CommandFailed(); return Task.CompletedTask; }
        public override void CommandFailed(DbCommand command, CommandErrorEventData data) => observation.CommandFailed();
    }

    private sealed class CheckpointFlushProbe(CheckpointObservation observation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (observation.Armed)
            {
                var db = data.Context ?? throw Invalid();
                var changed = db.ChangeTracker.Entries().Where(x => x.State is not (EntityState.Unchanged or EntityState.Detached)).ToArray();
                if (result.HasResult || changed.Length != 1 || changed[0].State != EntityState.Added
                    || changed[0].Entity is not GroupOriginalInvalidationRecord row) throw Invalid();
                observation.Saving(row, changed.Length);
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (observation.Armed)
            {
                var row = data.Context!.ChangeTracker.Entries<GroupOriginalInvalidationRecord>().Single();
                if (row.State != EntityState.Unchanged) throw Invalid(); observation.Saved(row.Entity, result);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CheckpointTransactionProbe(CheckpointObservation observation, ActivityRefusalObservation refusals) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken token = default)
        {
            if (!observation.Armed) return result;
            if (result.IsSuppressed || data.Context is not PlatformDbContext db || db.ChangeTracker.HasChanges()
                || transaction.IsolationLevel != IsolationLevel.Serializable || transaction.Connection?.State != ConnectionState.Open
                || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), transaction)) throw Invalid();
            var row = db.ChangeTracker.Entries<GroupOriginalInvalidationRecord>().Single().Entity;
            await using var command = (transaction.Connection ?? throw Invalid()).CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 5;
            command.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{row.TenantId:N}/{row.CompanyId:N}/{row.BindingId:N}"; command.Parameters.Add(parameter);
            if (!Equals(await command.ExecuteScalarAsync(token), "Exclusive")) throw Invalid();
            observation.Committing(); return result;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            refusals.Committed(); if (observation.Armed) observation.Committed(); return Task.CompletedTask;
        }
    }
}
