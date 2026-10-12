using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Disposable SQL proof for an empty-history original plan. The current store,
// allocation, readers and recovery codec execute unchanged. This does not
// qualify history selection, provider release, tokenizer or automatic DI.
internal static class GroupAutomaticInputPlanRuntimeProof
{
    internal const string PrepareLine = "PASS owned original plan actual two source inbox allocation no claims plans effects or model";
    internal const string CommitLine = "PASS owned original plan actual SQL committed reply interrupted fresh context exact original allocation plan chunks replay one immutable row source locks no extra keys effects provider or model";
    internal const string ResumeLine = "PASS owned original plan separate process replacement original claim epoch one current epoch two exact persisted chunks concurrent replay changed profile refused SQL constraints immutable rights no effects provider or model";
    internal const string ExpiryLine = "PASS owned original plan SQL flushed insert source lock savepoint rollback clean detach witness only durable clock rewind refused zero plans effects provider or model";
    private const string Profile = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("original-plan-prepare" or "original-plan-commit" or "original-plan-resume" or "original-plan-expiry"))
            throw new InvalidOperationException();
        if (mode == "original-plan-prepare")
        {
            await GroupAutomaticNoteRuntimeProof.RunAsync("automatic-note-prepare", scope, operation, worker, options, token);
            await using var clean = new PlatformDbContext(options);
            if (await clean.GroupAutomaticInputPlans.AnyAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
            Console.WriteLine(PrepareLine); return;
        }
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var evidence = new Evidence(scope, clock);
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new PlanReadProbe(evidence), new PlanFlushProbe(evidence), new PlanTransactionProbe(evidence)).Options);
        var allocation = await database.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var previous = await database.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id)
            .OrderByDescending(x => x.Epoch).FirstOrDefaultAsync(token);
        var epoch = mode == "original-plan-resume" ? 2 : 1;
        if ((previous?.Epoch ?? 0) != epoch - 1) throw new InvalidOperationException();
        clock.Current = previous is null ? allocation.AllocatedAtUtc : previous.ExpiresAtUtc.AddTicks(1);
        var claim = await new GroupBatchClaimStore(database, worker, clock).TryAcquireAsync(scope, allocation.Id,
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        if (claim.WasAlreadyClaimed || claim.CurrentHandle is null || claim.Receipt.Epoch != epoch) throw new InvalidOperationException();
        evidence.Claim = claim.Receipt;
        var input = await PrepareAsync(database, claim.CurrentHandle);
        var stateBefore = GroupAutomaticDependencyRuntimeProof.StateFingerprint(await StateAsync());
        await RequireEmptyEffectsAsync();

        if (mode == "original-plan-commit")
        {
            if (await CountPlansAsync() != 0 || await input.Store.ReadAsync(claim.CurrentHandle, token) is not null)
                throw new InvalidOperationException();
            evidence.Armed = true; evidence.LoseReply = true;
            var lost = false;
            try { _ = await input.Store.PersistAsync(input.Plan, input.Preparation, input.Brain, token); }
            catch (OwnedPlanReplyLostException) { lost = true; }
            finally { evidence.Armed = false; }
            if (!lost || !evidence.ReplyLost || !evidence.ReadLocked || await CountPlansAsync() != 1)
                throw new InvalidOperationException();
            // Fresh DbContext and fresh protected reads after a real SQL
            // commit whose reply was interrupted; no plaintext cache is used.
            await using var restarted = new PlatformDbContext(options);
            var replay = await new GroupBatchClaimStore(restarted, worker, clock).TryAcquireAsync(scope, allocation.Id,
                claim.Receipt.OwnerId, claim.Receipt.OperationId, TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
            if (!replay.WasAlreadyClaimed || replay.CurrentHandle is null) throw new InvalidOperationException();
            var fresh = await PrepareAsync(restarted, replay.CurrentHandle);
            var loaded = await fresh.Store.ReadAsync(replay.CurrentHandle, token) ?? throw new InvalidOperationException();
            RequireSame(input.Plan, loaded);
            var restored = loaded.RestoreChunks(fresh.Preparation, fresh.Brain, Profile, [], token);
            RequireRestored(loaded, restored);
            RequireSame(loaded, await fresh.Store.PersistAsync(loaded, fresh.Preparation, fresh.Brain, token));
            if (fresh.Keys.Reads != 1 || fresh.Keys.Writes != 0 || restarted.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        }
        else if (mode == "original-plan-resume")
        {
            evidence.Armed = true;
            GroupAutomaticInputPlan loaded;
            try { loaded = await input.Store.ReadAsync(claim.CurrentHandle, token) ?? throw new InvalidOperationException(); }
            finally { evidence.Armed = false; }
            RequireSame(input.Plan, loaded);
            RequireRestored(loaded, loaded.RestoreChunks(input.Preparation, input.Brain, Profile, [], token));
            var row = await database.GroupAutomaticInputPlans.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
            if (!evidence.ReadLocked || row.OriginalClaimEpoch != 1 || row.OriginalClaimOperationId != previous!.OperationId
                || row.CreatedAtUtc != previous.IssuedAtUtc) throw new InvalidOperationException();
            using var ready = new SemaphoreSlim(0, 2);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var contender = new PlatformDbContext(options);
                var replay = await new GroupBatchClaimStore(contender, worker, clock).TryAcquireAsync(scope, allocation.Id,
                    claim.Receipt.OwnerId, claim.Receipt.OperationId, TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
                var fresh = await PrepareAsync(contender, replay.CurrentHandle ?? throw new InvalidOperationException());
                ready.Release(); await release.Task.WaitAsync(token);
                var stored = await fresh.Store.PersistAsync(fresh.Plan, fresh.Preparation, fresh.Brain, token);
                RequireSame(loaded, stored);
                if (fresh.Keys.Reads != 1 || fresh.Keys.Writes != 0 || contender.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            }).ToArray();
            for (var i = 0; i < 2; i++) await ready.WaitAsync(token);
            release.SetResult(); await Task.WhenAll(tasks);
            var changed = GroupAutomaticInputPlan.Create(input.Preparation, input.Brain, new string('B', 64),
                "owned-synthetic-original", 64000, [], token);
            await RefusedAsync(() => input.Store.PersistAsync(changed, input.Preparation, input.Brain, token));
            await RequirePhysicalRefusalsAsync(database, scope, token);
            RequireSame(loaded, await input.Store.ReadAsync(claim.CurrentHandle, token) ?? throw new InvalidOperationException());
            if (await CountPlansAsync() != 1) throw new InvalidOperationException();
        }
        else
        {
            if (await CountPlansAsync() != 0) throw new InvalidOperationException();
            evidence.Armed = true; evidence.ExpireAfterFlush = true;
            try { await RefusedAsync(() => input.Store.PersistAsync(input.Plan, input.Preparation, input.Brain, token)); }
            finally { evidence.Armed = false; }
            var state = await StateAsync();
            if (!evidence.Flushed || !evidence.RolledBack || !evidence.WitnessOnly || !evidence.ReadLocked
                || state.ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc || await CountPlansAsync() != 0)
                throw new InvalidOperationException();
            state.ExpiryObservedAtUtc = null;
            if (GroupAutomaticDependencyRuntimeProof.StateFingerprint(state) != stateBefore) throw new InvalidOperationException();
            clock.Current = claim.Receipt.IssuedAtUtc;
            await RefusedAsync(() => input.Store.ReadAsync(claim.CurrentHandle, token));
            await RefusedAsync(() => input.Store.PersistAsync(input.Plan, input.Preparation, input.Brain, token));
            if (await CountPlansAsync() != 0 || (await StateAsync()).ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc)
                throw new InvalidOperationException();
        }
        if (mode != "original-plan-expiry" && GroupAutomaticDependencyRuntimeProof.StateFingerprint(await StateAsync()) != stateBefore)
            throw new InvalidOperationException();
        if (input.Keys.Reads != 1 || input.Keys.Writes != 0 || database.ChangeTracker.HasChanges()
            || database.Database.CurrentTransaction is not null || database.Database.GetDbConnection().State != ConnectionState.Closed
            || database.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any()) throw new InvalidOperationException();
        await RequireEmptyEffectsAsync();
        Console.WriteLine(mode switch { "original-plan-commit" => CommitLine, "original-plan-resume" => ResumeLine, _ => ExpiryLine });

        async Task<(GroupAutomaticInputPlan Plan, GroupAutomaticInputPlanStore Store, GroupBatchSourcePreparation Preparation,
            GroupBrainPrivateContext Brain, GroupNoteRuntimeProof.CountedKeys Keys)> PrepareAsync(PlatformDbContext db, GroupBatchClaimHandle handle)
        {
            var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
            var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
            var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
            var original = await new GroupBatchClaimStore(db, worker, clock).ReadCurrentAllocationAsync(handle, token);
            if (original.BatchId != allocation.Id || original.Revisions.Count != 2 || original.AfterSequence != 0
                || original.AllocatedThroughSequence != 2) throw new InvalidOperationException();
            var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle,
                original.Revisions.Select(x => x.Metadata.MessageId).ToArray(), token));
            var history = await brain.ReadAsync(handle, [], [], token);
            if (preparation.Receipts.Count != 2 || preparation.Candidates.Count != 1 || history.Items.Count != 0
                || keys.Reads != 1 || keys.Writes != 0) throw new InvalidOperationException();
            var plan = GroupAutomaticInputPlan.Create(preparation, history, Profile, "owned-synthetic-original", 64000, [], token);
            return (plan, new(db, worker, clock, sources, brain), preparation, history, keys);
        }
        Task<int> CountPlansAsync() => database.GroupAutomaticInputPlans.CountAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        Task<GroupBatchClaimStateRecord> StateAsync() => database.GroupBatchClaimStates.AsNoTracking().SingleAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        async Task RequireEmptyEffectsAsync()
        {
            if (await GroupNoteRuntimeProof.TargetRowsAsync(database, scope, Guid.Empty, token) != 0
                || await database.GroupWorkRawDispositions.AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupModelAttemptStarts.AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupModelAttemptFailures.AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
        }
    }

    private static void RequireSame(GroupAutomaticInputPlan original, GroupAutomaticInputPlan loaded)
    { if (!CryptographicOperations.FixedTimeEquals(original.Encode(), loaded.Encode())) throw new InvalidOperationException(); }
    private static void RequireRestored(GroupAutomaticInputPlan plan, IReadOnlyList<GroupAutomaticInputPlan.RestoredChunk> chunks)
    {
        if (chunks.Count != plan.Chunks.Count || chunks.Count != 1 || chunks.Sum(x => x.Original.Members.Count) != 2)
            throw new InvalidOperationException();
        foreach (var chunk in chunks)
        {
            if (chunk.Original.OperationId != plan.Chunks[chunk.Current.Ordinal - 1].OperationId
                || chunk.Original.RequiresModel != (chunk.Current.ModelContext is not null)) throw new InvalidOperationException();
            if (chunk.Current.ModelContext is not null && GroupModelAttemptBudget.InputIdentity(chunk.Current.ModelContext) != chunk.Original.InputSha256)
                throw new InvalidOperationException();
        }
    }
    private static async Task RefusedAsync(Func<Task> call)
    {
        var denied = false;
        try { await call(); }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException) { denied = true; }
        if (!denied) throw new InvalidOperationException();
    }
    private sealed class OwnedPlanReplyLostException : IOException;
    private sealed class Evidence(GroupScope scope, GroupNoteRuntimeProof.OwnedClock clock)
    {
        internal GroupScope Scope = scope;
        internal GroupNoteRuntimeProof.OwnedClock Clock = clock;
        internal GroupBatchClaimReceipt? Claim;
        internal bool Armed, LoseReply, ReplyLost, ExpireAfterFlush, Flushed, RolledBack, WitnessFlushed, WitnessOnly, ReadLocked;
        internal async Task RequireLockAsync(DbTransaction transaction, CancellationToken token)
        {
            if (transaction.IsolationLevel != IsolationLevel.Serializable || transaction.Connection is null) throw new InvalidOperationException();
            await using var check = transaction.Connection.CreateCommand(); check.Transaction = transaction; check.CommandTimeout = 5;
            check.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = check.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{Scope.TenantId:N}/{Scope.CompanyId:N}/{Scope.SourceBindingId:N}";
            check.Parameters.Add(parameter);
            if (!string.Equals((string?)await check.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
        }
    }
    private sealed class PlanReadProbe(Evidence evidence) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (!evidence.Armed || !command.CommandText.Contains("FROM aioffice.GroupAutomaticInputPlans", StringComparison.Ordinal)) return result;
            await evidence.RequireLockAsync(command.Transaction ?? throw new InvalidOperationException(), token);
            if (!command.CommandText.Contains("DATALENGTH([Plan]) BETWEEN 256 AND 32000", StringComparison.Ordinal)) throw new InvalidOperationException();
            evidence.ReadLocked = true; return result;
        }
    }
    private sealed class PlanFlushProbe(Evidence evidence) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (evidence.Armed && evidence.ExpireAfterFlush && evidence.RolledBack && data.Context is PlatformDbContext witness)
            {
                await evidence.RequireLockAsync(witness.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException(), token);
                if (result != 1 || witness.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any()
                    || !witness.ChangeTracker.Entries<GroupBatchClaimStateRecord>().Any(x => x.Entity.BatchId == evidence.Claim!.BatchId
                        && x.Entity.Epoch == evidence.Claim.Epoch && x.Entity.ExpiryObservedAtUtc == evidence.Claim.ExpiresAtUtc))
                    throw new InvalidOperationException();
                evidence.WitnessFlushed = true;
            }
            if (!evidence.Armed || !evidence.ExpireAfterFlush || evidence.Flushed || data.Context is not PlatformDbContext db
                || !db.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any()) return result;
            await evidence.RequireLockAsync(db.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException(), token);
            if (await db.GroupAutomaticInputPlans.AsNoTracking().CountAsync(x => x.TenantId == evidence.Scope.TenantId
                && x.CompanyId == evidence.Scope.CompanyId && x.BindingId == evidence.Scope.SourceBindingId, token) != 1) throw new InvalidOperationException();
            evidence.Flushed = true; evidence.Clock.Current = evidence.Claim!.ExpiresAtUtc; return result;
        }
    }
    private sealed class PlanTransactionProbe(Evidence evidence) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            if (evidence.LoseReply && data.Context is PlatformDbContext db && db.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any())
            { evidence.LoseReply = false; evidence.ReplyLost = true; throw new OwnedPlanReplyLostException(); }
            if (evidence.ExpireAfterFlush && evidence.RolledBack && data.Context is PlatformDbContext witness)
            {
                if (!evidence.WitnessFlushed || witness.ChangeTracker.HasChanges()
                    || witness.ChangeTracker.Entries<GroupAutomaticInputPlanRecord>().Any()) throw new InvalidOperationException();
                evidence.WitnessOnly = true;
            }
            return Task.CompletedTask;
        }
        public override async Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData data, CancellationToken token = default)
        {
            if (!evidence.ExpireAfterFlush || !evidence.Flushed || data.Context is not PlatformDbContext db) return;
            await evidence.RequireLockAsync(transaction, token);
            if (await db.GroupAutomaticInputPlans.AsNoTracking().AnyAsync(x => x.TenantId == evidence.Scope.TenantId
                && x.CompanyId == evidence.Scope.CompanyId && x.BindingId == evidence.Scope.SourceBindingId, token)) throw new InvalidOperationException();
            evidence.RolledBack = true;
        }
    }
    private static async Task RequirePhysicalRefusalsAsync(PlatformDbContext database, GroupScope scope, CancellationToken token)
    {
        // Create a distinct allocation inside the probe transaction to avoid
        // accepting a primary-key error as evidence of a different boundary.
        const string newAllocation = "INSERT INTO aioffice.GroupBatchAllocations(TenantId,CompanyId,BindingId,Id,OperationId,AfterSequence,AllocatedThroughSequence,ObservedCommittedThroughSequence,RawRevisionCount,IsHistoricalBackfill,SourceVersion,DeletionGeneration,AccountVersion,ServiceId,CredentialEpoch,GrantVersion,AllocatedAtUtc) SELECT TenantId,CompanyId,BindingId,@batch,@operation,AllocatedThroughSequence,AllocatedThroughSequence+RawRevisionCount,AllocatedThroughSequence+RawRevisionCount,RawRevisionCount,IsHistoricalBackfill,SourceVersion,DeletionGeneration,AccountVersion,ServiceId,CredentialEpoch,GrantVersion,AllocatedAtUtc FROM aioffice.GroupBatchAllocations WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;";
        const string newClaim = "INSERT INTO aioffice.GroupBatchClaimReceipts(TenantId,CompanyId,BindingId,OperationId,BatchId,OwnerId,Epoch,RequestedLifetimeTicks,IssuedAtUtc,ExpiresAtUtc,ServiceId,CredentialEpoch,GrantVersion,SourceVersion,DeletionGeneration,AccountVersion,AuthoritySha256) SELECT TenantId,CompanyId,BindingId,@operation,@batch,OwnerId,Epoch,RequestedLifetimeTicks,IssuedAtUtc,ExpiresAtUtc,ServiceId,CredentialEpoch,GrantVersion,SourceVersion,DeletionGeneration,AccountVersion,AuthoritySha256 FROM aioffice.GroupBatchClaimReceipts WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND Epoch=1;";
        var probes = new (string Sql, int[] Errors, string Boundary)[]
        {
            ("UPDATE aioffice.GroupAutomaticInputPlans SET PlanSha256=PlanSha256 WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [229], "GroupAutomaticInputPlans"),
            ("DELETE FROM aioffice.GroupAutomaticInputPlans WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [229], "GroupAutomaticInputPlans"),
            ("INSERT INTO aioffice.GroupAutomaticInputPlans SELECT TenantId,CompanyId,BindingId,@batch,FormatVersion,PlanSha256,[Plan],OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc FROM aioffice.GroupAutomaticInputPlans WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [547], "FK_GroupAutomaticInputPlans_"),
            (newAllocation + "INSERT INTO aioffice.GroupAutomaticInputPlans SELECT TenantId,CompanyId,BindingId,@batch,FormatVersion,PlanSha256,[Plan],OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc FROM aioffice.GroupAutomaticInputPlans WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [547], "FK_GroupAutomaticInputPlans_OriginalClaim"),
            (newAllocation + newClaim + "INSERT INTO aioffice.GroupAutomaticInputPlans SELECT TenantId,CompanyId,BindingId,@batch,FormatVersion,PlanSha256,CONVERT(varbinary(max),0x00),@operation,OriginalClaimEpoch,CreatedAtUtc FROM aioffice.GroupAutomaticInputPlans WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [547], "CK_GroupAutomaticInputPlans_Format"),
            ("INSERT INTO aioffice.GroupAutomaticInputPlans SELECT * FROM aioffice.GroupAutomaticInputPlans WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [2601, 2627], "PK_GroupAutomaticInputPlans")
        };
        foreach (var probe in probes)
        {
            await using var connection = new SqlConnection(database.Database.GetConnectionString()); await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await using var command = new SqlCommand(probe.Sql, connection, transaction) { CommandTimeout = 10 };
            command.Parameters.AddWithValue("@tenant", scope.TenantId); command.Parameters.AddWithValue("@company", scope.CompanyId);
            command.Parameters.AddWithValue("@binding", scope.SourceBindingId); command.Parameters.AddWithValue("@batch", Guid.NewGuid());
            command.Parameters.AddWithValue("@operation", Guid.NewGuid());
            var denied = false;
            try { await command.ExecuteNonQueryAsync(token); }
            catch (SqlException error) when (probe.Errors.Contains(error.Number) && error.Message.Contains(probe.Boundary, StringComparison.Ordinal))
            { denied = true; }
            await transaction.RollbackAsync(token);
            if (!denied) throw new InvalidOperationException();
        }
    }
}
