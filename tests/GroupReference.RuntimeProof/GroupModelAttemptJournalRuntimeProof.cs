using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Owned synthetic SQL proof only. The shipping journal, locked readers and
// claim store run unchanged. No provider output, tokenizer or worker DI proof.
internal static class GroupModelAttemptJournalRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("model-journal-prepare" or "model-journal-lost-reply" or "model-journal-resume"
            or "model-journal-cap" or "model-journal-expiry")) throw new InvalidOperationException();
        if (mode == "model-journal-prepare")
        {
            await GroupAutomaticNoteRuntimeProof.RunAsync("automatic-note-prepare", scope, operation, worker, options, token);
            return;
        }
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var evidence = new JournalEvidence(scope, clock);
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new JournalFlushProbe(evidence), new JournalTransactionProbe(evidence)).Options);
        var allocation = await database.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var previous = await database.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id)
            .OrderByDescending(x => x.Epoch).FirstOrDefaultAsync(token);
        var expectedEpoch = mode switch { "model-journal-lost-reply" => 1, "model-journal-resume" => 2, _ => 3 };
        if ((previous?.Epoch ?? 0) != (mode == "model-journal-expiry" ? 3 : expectedEpoch - 1)) throw new InvalidOperationException();
        clock.Current = previous is null ? allocation.AllocatedAtUtc
            : mode == "model-journal-expiry" ? previous.IssuedAtUtc
            : previous.ExpiresAtUtc.AddSeconds(expectedEpoch == 2 ? 15 : 60);
        var claim = await new GroupBatchClaimStore(database, worker, clock).TryAcquireAsync(scope, allocation.Id,
            mode == "model-journal-expiry" ? previous!.OwnerId : Guid.NewGuid(),
            mode == "model-journal-expiry" ? previous!.OperationId : Guid.NewGuid(), TimeSpan.FromMinutes(2), token)
            ?? throw new InvalidOperationException();
        if (claim.CurrentHandle is null || claim.Receipt.Epoch != expectedEpoch
            || claim.WasAlreadyClaimed != (mode == "model-journal-expiry")) throw new InvalidOperationException();
        var prepared = await PrepareAsync(database, claim.CurrentHandle, "owned-synthetic-journal");
        var context = prepared.Context;
        var identity = GroupModelAttemptBudget.InputIdentity(context);
        var store = prepared.Store;
        evidence.Claim = claim.Receipt;
        await RequireEmptyEffectsAsync();
        var stateBefore = await StateAsync();
        var originalState = GroupAutomaticDependencyRuntimeProof.StateFingerprint(stateBefore);
        var keysBefore = prepared.Keys.Reads;

        if (mode == "model-journal-lost-reply")
        {
            if (await StartsAsync() != 0 || await FailuresAsync() != 0) throw new InvalidOperationException();
            var exhaustionWriter = new GroupNoteCommitStore(database, worker, clock,
                new(database, worker, clock, prepared.Keys, new()), new(database, worker, clock, prepared.Keys, new()), prepared.Keys, new());
            await RefusedAsync(() => exhaustionWriter.CommitExtractionFailureAsync(context, Guid.NewGuid(), token));
            if (prepared.Keys.Writes != 0) throw new InvalidOperationException();
            evidence.LoseReply = true; evidence.RequestId = operation;
            var lost = false;
            try { _ = await store.TryStartAsync(context, operation, token); }
            catch (OwnedJournalReplyLostException) { lost = true; }
            if (!lost || !evidence.CommittedReplyLost || await StartsAsync() != 1 || await FailuresAsync() != 0)
                throw new InvalidOperationException();
            await RefusedAsync(() => exhaustionWriter.CommitExtractionFailureAsync(context, Guid.NewGuid(), token));
            if (prepared.Keys.Writes != 0) throw new InvalidOperationException();
            // A fresh context supplies a replay after the real commit's reply
            // was interrupted. The original permit was never returned.
            await using var restarted = new PlatformDbContext(options);
            var restartedClaim = await new GroupBatchClaimStore(restarted, worker, clock).TryAcquireAsync(scope, allocation.Id,
                claim.Receipt.OwnerId, claim.Receipt.OperationId, TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
            var restart = await PrepareAsync(restarted, restartedClaim.CurrentHandle ?? throw new InvalidOperationException(), "owned-synthetic-journal");
            var replay = await restart.Store.TryStartAsync(restart.Context, operation, token);
            if (!replay.WasAlreadyStarted || replay.Permit is not null || replay.Budget.ConsumedAttempts != 1
                || replay.Budget.Disposition != GroupModelAttemptDisposition.Wait || restart.Keys.Reads != 1 || restart.Keys.Writes != 0
                || GroupModelAttemptBudget.InputIdentity(restart.Context) != identity || restarted.ChangeTracker.HasChanges())
                throw new InvalidOperationException();
        }
        else if (mode == "model-journal-resume")
        {
            var replay = await store.TryStartAsync(context, operation, token);
            if (!replay.WasAlreadyStarted || replay.Permit is not null || replay.Budget.ConsumedAttempts != 1
                || replay.Budget.Disposition != GroupModelAttemptDisposition.Available) throw new InvalidOperationException();
            var secondId = await database.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.CommittedSequence == 2).Select(x => x.Id).SingleAsync(token);
            var start = await store.TryStartAsync(context, secondId, token);
            if (start.WasAlreadyStarted || start.Permit?.Start.Ordinal != 2 || start.Permit.Start.PredecessorRequestId != operation
                || start.Permit.Start.ClaimEpoch != 2 || start.Budget.ConsumedAttempts != 2) throw new InvalidOperationException();
            var failure = await store.RecordFailureAsync(context, start.Permit, GroupModelAttemptFailure.InvalidOutput, token);
            if (failure.Disposition != GroupModelAttemptDisposition.Wait || failure.RetryAtUtc != clock.Current.AddSeconds(60))
                throw new InvalidOperationException();
            var digest = await JournalDigestAsync();
            if (await store.RecordFailureAsync(context, start.Permit, GroupModelAttemptFailure.InvalidOutput, token) != failure)
                throw new InvalidOperationException();
            await RefusedAsync(() => store.RecordFailureAsync(context, start.Permit, GroupModelAttemptFailure.TransientProvider, token));
            if (await JournalDigestAsync() != digest || await StartsAsync() != 2 || await FailuresAsync() != 1) throw new InvalidOperationException();
            var now = clock.Current; clock.Current = now.AddTicks(-1);
            await RefusedAsync(() => store.TryStartAsync(context, Guid.NewGuid(), token)); clock.Current = now;
            if (await JournalDigestAsync() != digest) throw new InvalidOperationException();
        }
        else if (mode == "model-journal-cap")
        {
            // Four actual contexts/claims/readers start together. The original
            // source lock must let exactly one consume the last ordinal.
            using var ready = new SemaphoreSlim(0, 4);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var contender = new PlatformDbContext(options);
                var replayClaim = await new GroupBatchClaimStore(contender, worker, clock).TryAcquireAsync(scope, allocation.Id,
                    claim.Receipt.OwnerId, claim.Receipt.OperationId, TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
                var input = await PrepareAsync(contender, replayClaim.CurrentHandle ?? throw new InvalidOperationException(), "owned-synthetic-journal");
                if (GroupModelAttemptBudget.InputIdentity(input.Context) != identity) throw new InvalidOperationException();
                ready.Release(); await release.Task.WaitAsync(token);
                var result = await input.Store.TryStartAsync(input.Context, Guid.NewGuid(), token);
                if (input.Keys.Reads != 1 || input.Keys.Writes != 0 || contender.ChangeTracker.HasChanges()) throw new InvalidOperationException();
                if (result.Permit is not null)
                {
                    var failed = await input.Store.RecordFailureAsync(input.Context, result.Permit, GroupModelAttemptFailure.Interrupted, token);
                    if (failed.Disposition != GroupModelAttemptDisposition.Exhausted || failed.ConsumedAttempts != 3) throw new InvalidOperationException();
                }
                return result;
            }).ToArray();
            for (var i = 0; i < 4; i++) await ready.WaitAsync(token);
            release.SetResult();
            var results = await Task.WhenAll(tasks);
            var winning = results.Single(x => x.Permit is not null);
            if (winning.Permit!.Start.Ordinal != 3 || winning.Permit.Start.ClaimEpoch != 3
                || results.Any(x => x.WasAlreadyStarted || x.Budget.ConsumedAttempts != 3)
                || await StartsAsync() != 3 || await FailuresAsync() != 2) throw new InvalidOperationException();
            var digest = await JournalDigestAsync();
            var fourth = await store.TryStartAsync(context, Guid.NewGuid(), token);
            if (fourth.Permit is not null || fourth.WasAlreadyStarted || fourth.Budget.Disposition != GroupModelAttemptDisposition.Exhausted
                || fourth.Budget.ConsumedAttempts != 3 || await JournalDigestAsync() != digest) throw new InvalidOperationException();
            await RequirePhysicalRefusalsAsync(database, scope, token);
            if (await JournalDigestAsync() != digest) throw new InvalidOperationException();
        }
        else
        {
            var digest = await JournalDigestAsync();
            var expiry = await PrepareAsync(database, claim.CurrentHandle, "owned-synthetic-journal-expiry");
            evidence.InputIdentity = GroupModelAttemptBudget.InputIdentity(expiry.Context);
            evidence.RequestId = Guid.NewGuid(); evidence.ExpireAfterFlush = true;
            await RefusedAsync(() => expiry.Store.TryStartAsync(expiry.Context, evidence.RequestId, token));
            var state = await StateAsync();
            if (!evidence.Flushed || !evidence.RolledBack || !evidence.WitnessOnly || state.ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc
                || GroupAutomaticDependencyRuntimeProof.StateFingerprint(withExpiryCleared()) != originalState
                || await JournalDigestAsync() != digest || await StartsAsync() != 3 || await FailuresAsync() != 2
                || expiry.Keys.Reads != 1 || expiry.Keys.Writes != 0) throw new InvalidOperationException();
            clock.Current = claim.Receipt.IssuedAtUtc;
            await RefusedAsync(() => expiry.Store.TryStartAsync(expiry.Context, Guid.NewGuid(), token));
            if (await JournalDigestAsync() != digest || (await StateAsync()).ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc)
                throw new InvalidOperationException();
            GroupBatchClaimStateRecord withExpiryCleared() { state.ExpiryObservedAtUtc = null; return state; }
        }
        if (prepared.Keys.Reads != keysBefore || prepared.Keys.Writes != 0 || database.ChangeTracker.HasChanges()
            || database.Database.CurrentTransaction is not null || database.Database.GetDbConnection().State != ConnectionState.Closed)
            throw new InvalidOperationException();
        if (mode != "model-journal-expiry" && GroupAutomaticDependencyRuntimeProof.StateFingerprint(await StateAsync()) != originalState)
            throw new InvalidOperationException();
        await RequireEmptyEffectsAsync();
        Console.WriteLine(mode switch
        {
            "model-journal-lost-reply" => "PASS owned model journal actual start committed reply interrupted fresh context replay no second permit one start zero failures no effects provider or model",
            "model-journal-resume" => "PASS owned model journal separate process replacement exact input second ordinal original deadline bounded backoff immutable failure replay clock rewind denied no effects provider or model",
            "model-journal-cap" => "PASS owned model journal four actual concurrent contexts one third ordinal two failures fourth unavailable original claim predecessor timing cap and mutation SQL refusals no effects provider or model",
            _ => "PASS owned model journal flushed start source lock savepoint rollback clean detach witness only durable clock rewind denied original three starts two failures no effects provider or model"
        });

        async Task<(GroupAutomaticModelContext Context, GroupModelAttemptJournalStore Store, GroupNoteRuntimeProof.CountedKeys Keys)> PrepareAsync(
            PlatformDbContext db, GroupBatchClaimHandle handle, string model)
        {
            var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
            var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
            var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
            var selected = await db.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId)
                .OrderBy(x => x.CommittedSequence).Select(x => x.MessageId).ToArrayAsync(token);
            if (selected.Length != 2 || selected.Distinct().Count() != 2) throw new InvalidOperationException();
            var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, selected, token));
            var history = await brain.ReadAsync(handle, [], [], token);
            if (preparation.Receipts.Count != 2 || preparation.Candidates.Count != 1 || history.Items.Count != 0
                || keys.Reads != 1 || keys.Writes != 0) throw new InvalidOperationException();
            var input = GroupAutomaticModelContext.Create(preparation, history, model, 64000);
            return (input, new(db, worker, clock, sources, brain), keys);
        }
        Task<int> StartsAsync() => database.GroupModelAttemptStarts.CountAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        Task<int> FailuresAsync() => database.GroupModelAttemptFailures.CountAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        Task<GroupBatchClaimStateRecord> StateAsync() => database.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        async Task<string> JournalDigestAsync()
        {
            var starts = await database.GroupModelAttemptStarts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.InputSha256).ThenBy(x => x.Ordinal).ToArrayAsync(token);
            var failures = await database.GroupModelAttemptFailures.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.RequestId).ToArrayAsync(token);
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object[] { starts, failures })));
        }
        async Task RequireEmptyEffectsAsync()
        {
            if (await GroupNoteRuntimeProof.TargetRowsAsync(database, scope, Guid.Empty, token) != 0
                || await database.GroupWorkCommitReceipts.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupCustomerRequests.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupRequestRevisions.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupRequestEvidence.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupWorkSourceDispositions.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupNotesCommittedOutbox.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupNotesCommittedItems.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
                || await database.GroupWorkRawDispositions.AnyAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token))
                throw new InvalidOperationException();
        }
    }

    private static async Task RefusedAsync(Func<Task> call)
    {
        var refused = false;
        try { await call(); }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException();
    }

    private sealed class OwnedJournalReplyLostException : IOException;
    private sealed class JournalEvidence(GroupScope scope, GroupNoteRuntimeProof.OwnedClock clock)
    {
        internal GroupScope Scope = scope;
        internal GroupNoteRuntimeProof.OwnedClock Clock = clock;
        internal GroupBatchClaimReceipt? Claim;
        internal Guid RequestId;
        internal string? InputIdentity;
        internal bool LoseReply, CommittedReplyLost, ExpireAfterFlush, Flushed, RolledBack, WitnessOnly;
        internal async Task RequireLockAsync(DbTransaction transaction, CancellationToken token)
        {
            if (transaction.IsolationLevel != IsolationLevel.Serializable || transaction.Connection is null) throw new InvalidOperationException();
            await using var check = transaction.Connection.CreateCommand(); check.Transaction = transaction; check.CommandTimeout = 5;
            check.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var value = check.CreateParameter(); value.ParameterName = "@resource";
            value.Value = $"aioffice:group-ingest:{Scope.TenantId:N}/{Scope.CompanyId:N}/{Scope.SourceBindingId:N}";
            check.Parameters.Add(value);
            if (!string.Equals((string?)await check.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
        }
    }
    private sealed class JournalFlushProbe(JournalEvidence evidence) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (!evidence.ExpireAfterFlush || evidence.Flushed || data.Context is not PlatformDbContext db
                || !db.ChangeTracker.Entries<GroupModelAttemptStartRecord>().Any(x => x.Entity.RequestId == evidence.RequestId)) return result;
            var transaction = db.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException();
            await evidence.RequireLockAsync(transaction, token);
            if (await db.GroupModelAttemptStarts.AsNoTracking().CountAsync(x => x.TenantId == evidence.Scope.TenantId
                && x.CompanyId == evidence.Scope.CompanyId && x.BindingId == evidence.Scope.SourceBindingId
                && x.RequestId == evidence.RequestId && x.InputSha256 == evidence.InputIdentity, token) != 1) throw new InvalidOperationException();
            evidence.Flushed = true; evidence.Clock.Current = evidence.Claim!.ExpiresAtUtc;
            return result;
        }
    }
    private sealed class JournalTransactionProbe(JournalEvidence evidence) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            if (evidence.LoseReply && data.Context is PlatformDbContext db
                && db.ChangeTracker.Entries<GroupModelAttemptStartRecord>().Any(x => x.Entity.RequestId == evidence.RequestId))
            {
                evidence.LoseReply = false; evidence.CommittedReplyLost = true;
                throw new OwnedJournalReplyLostException();
            }
            if (evidence.ExpireAfterFlush && evidence.RolledBack && data.Context is PlatformDbContext witness)
            {
                var rows = witness.ChangeTracker.Entries().Where(x => x.State is not (EntityState.Detached or EntityState.Unchanged)).ToArray();
                // SaveChanges has accepted the witness; only the claim record
                // remains tracked and the canceled append is already detached.
                if (rows.Length != 0 || witness.ChangeTracker.Entries<GroupModelAttemptStartRecord>().Any()
                    || !witness.ChangeTracker.Entries<GroupBatchClaimStateRecord>().Any(x =>
                        x.Entity.BatchId == evidence.Claim!.BatchId && x.Entity.Epoch == evidence.Claim.Epoch
                        && x.Entity.ExpiryObservedAtUtc == evidence.Claim.ExpiresAtUtc)) throw new InvalidOperationException();
                evidence.WitnessOnly = true;
            }
            return Task.CompletedTask;
        }
        public override async Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData data, CancellationToken token = default)
        {
            if (!evidence.ExpireAfterFlush || !evidence.Flushed || data.Context is not PlatformDbContext db) return;
            await evidence.RequireLockAsync(transaction, token);
            if (await db.GroupModelAttemptStarts.AsNoTracking().AnyAsync(x => x.TenantId == evidence.Scope.TenantId
                && x.CompanyId == evidence.Scope.CompanyId && x.BindingId == evidence.Scope.SourceBindingId
                && x.RequestId == evidence.RequestId, token)) throw new InvalidOperationException();
            evidence.RolledBack = true;
        }
    }

    private static async Task RequirePhysicalRefusalsAsync(PlatformDbContext database, GroupScope scope, CancellationToken token)
    {
        var rows = await database.GroupModelAttemptStarts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).OrderBy(x => x.Ordinal).ToArrayAsync(token);
        if (rows.Length != 3 || !rows.Select(x => x.Ordinal).SequenceEqual(new[] { 1, 2, 3 })
            || rows.Any(x => x.InputSha256 == new string('B', 64))) throw new InvalidOperationException();
        const string insert = "INSERT INTO aioffice.GroupModelAttemptStarts(TenantId,CompanyId,BindingId,RequestId,BatchId,InputSha256,Ordinal,PredecessorOrdinal,PredecessorRequestId,ClaimOperationId,ClaimEpoch,StartedAtUtc,DeadlineUtc) SELECT TenantId,CompanyId,BindingId,@newId,BatchId,{0},{1},{2},{3},ClaimOperationId,{4},StartedAtUtc,DeadlineUtc FROM aioffice.GroupModelAttemptStarts WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND RequestId=@original;";
        var probes = new (string Sql, int[] Errors, string Boundary, Guid Original)[]
        {
            (string.Format(insert, "InputSha256", "4", "3", "RequestId", "ClaimEpoch"), [547], "CK_GroupModelAttemptStarts_Chain", rows[2].RequestId),
            (string.Format(insert, "@hash", "1", "NULL", "NULL", "ClaimEpoch+1000"), [547], "FK_GroupModelAttemptStarts_OriginalClaim", rows[0].RequestId),
            (string.Format(insert, "@hash", "2", "1", "RequestId", "ClaimEpoch"), [547], "FK_GroupModelAttemptStarts_Predecessor", rows[0].RequestId),
            (string.Format(insert, "InputSha256", "1", "NULL", "NULL", "ClaimEpoch"), [2601, 2627], "IX_GroupModelAttemptStarts_TenantId_CompanyId_BindingId_BatchId_InputSha256_Ordinal", rows[0].RequestId),
            ("INSERT INTO aioffice.GroupModelAttemptFailures(TenantId,CompanyId,BindingId,RequestId,Failure,StartedAtUtc,DeadlineUtc,ObservedAtUtc) SELECT TenantId,CompanyId,BindingId,RequestId,1,DATEADD(nanosecond,100,StartedAtUtc),DeadlineUtc,DATEADD(nanosecond,100,StartedAtUtc) FROM aioffice.GroupModelAttemptStarts WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND RequestId=@original;", [547], "FK_GroupModelAttemptFailures_OriginalStart", rows[0].RequestId),
            ("UPDATE aioffice.GroupModelAttemptStarts SET Ordinal=Ordinal WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [229], "GroupModelAttemptStarts", rows[0].RequestId),
            ("DELETE FROM aioffice.GroupModelAttemptStarts WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [229], "GroupModelAttemptStarts", rows[0].RequestId),
            ("UPDATE aioffice.GroupModelAttemptFailures SET Failure=Failure WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [229], "GroupModelAttemptFailures", rows[0].RequestId),
            ("DELETE FROM aioffice.GroupModelAttemptFailures WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;", [229], "GroupModelAttemptFailures", rows[0].RequestId)
        };
        foreach (var probe in probes)
        {
            await using var connection = new SqlConnection(database.Database.GetConnectionString());
            await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await using var command = new SqlCommand(probe.Sql, connection, transaction) { CommandTimeout = 10 };
            command.Parameters.AddWithValue("@tenant", scope.TenantId); command.Parameters.AddWithValue("@company", scope.CompanyId);
            command.Parameters.AddWithValue("@binding", scope.SourceBindingId); command.Parameters.AddWithValue("@original", probe.Original);
            command.Parameters.AddWithValue("@newId", Guid.NewGuid()); command.Parameters.AddWithValue("@hash", new string('B', 64));
            var refused = false;
            try { await command.ExecuteNonQueryAsync(token); }
            // Inspect the exact named boundary privately; no engine message,
            // SQL, metadata, credentials or exception body is ever printed.
            catch (SqlException error) when (probe.Errors.Contains(error.Number)
                && error.Message.Contains(probe.Boundary, StringComparison.Ordinal))
            { refused = true; }
            await transaction.RollbackAsync(token);
            if (!refused) throw new InvalidOperationException();
        }
    }
}
