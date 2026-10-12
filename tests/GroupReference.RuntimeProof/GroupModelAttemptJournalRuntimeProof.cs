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
        var diagnostic = new JournalDiagnostic();
        try { await RunCoreAsync(mode, scope, operation, worker, options, token, diagnostic); }
        catch (PhaseFailure error) { throw diagnostic.FirstFailure ?? error; }
        catch (Exception error)
        {
            // Only closed codes survive. A contender's original refusal is
            // retained if the unchanged readiness wait later times out.
            throw diagnostic.FirstFailure ?? new PhaseFailure(diagnostic.Phase, Kind(error));
        }
    }

    private static async Task RunCoreAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token,
        JournalDiagnostic diagnostic)
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
        diagnostic.Phase = JournalPhase.Claim;
        var claim = await new GroupBatchClaimStore(database, worker, clock).TryAcquireAsync(scope, allocation.Id,
            mode == "model-journal-expiry" ? previous!.OwnerId : Guid.NewGuid(),
            mode == "model-journal-expiry" ? previous!.OperationId : Guid.NewGuid(), TimeSpan.FromMinutes(2), token)
            ?? throw new InvalidOperationException();
        if (claim.CurrentHandle is null || claim.Receipt.Epoch != expectedEpoch
            || claim.WasAlreadyClaimed != (mode == "model-journal-expiry")) throw new InvalidOperationException();
        diagnostic.Phase = JournalPhase.Preparation;
        var prepared = await PrepareAsync(database, claim.CurrentHandle, "owned-synthetic-journal");
        var context = prepared.Context;
        var identity = GroupModelAttemptBudget.InputIdentity(context);
        var store = prepared.Store;
        evidence.Claim = claim.Receipt;
        await RequireEmptyEffectsAsync();
        var stateBefore = await StateAsync();
        var originalState = GroupAutomaticDependencyRuntimeProof.StateFingerprint(stateBefore);
        evidence.OriginalState = originalState;
        var keysBefore = prepared.Keys.Reads;

        if (mode == "model-journal-lost-reply")
        {
            diagnostic.Phase = JournalPhase.LostReply;
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
            diagnostic.Phase = JournalPhase.Resume;
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
            diagnostic.Phase = JournalPhase.CapReady;
            // Four actual contexts/claims/readers start together. The original
            // source lock must let exactly one consume the last ordinal.
            using var ready = new SemaphoreSlim(0, 4);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var contender = new PlatformDbContext(options);
                var replayClaim = await ObserveAsync(JournalPhase.CapClaim, async () =>
                    await new GroupBatchClaimStore(contender, worker, clock).TryAcquireAsync(scope, allocation.Id,
                        claim.Receipt.OwnerId, claim.Receipt.OperationId, TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException(), diagnostic);
                var input = await ObserveAsync(JournalPhase.CapPreparation, () =>
                    PrepareAsync(contender, replayClaim.CurrentHandle ?? throw new InvalidOperationException(), "owned-synthetic-journal"), diagnostic);
                if (GroupModelAttemptBudget.InputIdentity(input.Context) != identity) throw new InvalidOperationException();
                ready.Release(); await release.Task.WaitAsync(token);
                var result = await ObserveAsync(JournalPhase.CapStart, () =>
                    input.Store.TryStartAsync(input.Context, Guid.NewGuid(), token), diagnostic);
                if (input.Keys.Reads != 1 || input.Keys.Writes != 0 || contender.ChangeTracker.HasChanges()) throw new InvalidOperationException();
                if (result.Permit is not null)
                {
                    var failed = await ObserveAsync(JournalPhase.CapFailure, () =>
                        input.Store.RecordFailureAsync(input.Context, result.Permit, GroupModelAttemptFailure.Interrupted, token), diagnostic);
                    if (failed.Disposition != GroupModelAttemptDisposition.Exhausted || failed.ConsumedAttempts != 3) throw new InvalidOperationException();
                }
                return result;
            }).ToArray();
            for (var i = 0; i < 4; i++) await ready.WaitAsync(token);
            release.SetResult();
            diagnostic.Phase = JournalPhase.CapAggregate;
            var results = await Task.WhenAll(tasks);
            diagnostic.Phase = JournalPhase.CapCounts;
            var winning = results.Single(x => x.Permit is not null);
            if (winning.Permit!.Start.Ordinal != 3 || winning.Permit.Start.ClaimEpoch != 3
                || results.Any(x => x.WasAlreadyStarted || x.Budget.ConsumedAttempts != 3)
                || await StartsAsync() != 3 || await FailuresAsync() != 2) throw new InvalidOperationException();
            var digest = await JournalDigestAsync();
            diagnostic.Phase = JournalPhase.CapFourth;
            var fourth = await store.TryStartAsync(context, Guid.NewGuid(), token);
            if (fourth.Permit is not null || fourth.WasAlreadyStarted || fourth.Budget.Disposition != GroupModelAttemptDisposition.Exhausted
                || fourth.Budget.ConsumedAttempts != 3 || await JournalDigestAsync() != digest) throw new InvalidOperationException();
            diagnostic.Phase = JournalPhase.PhysicalRows;
            await RequirePhysicalRefusalsAsync(database, scope, token, diagnostic);
            if (await JournalDigestAsync() != digest) throw new InvalidOperationException();
        }
        else
        {
            diagnostic.Phase = JournalPhase.Expiry;
            var digest = await JournalDigestAsync();
            var expiry = await PrepareAsync(database, claim.CurrentHandle, "owned-synthetic-journal-expiry");
            evidence.InputIdentity = GroupModelAttemptBudget.InputIdentity(expiry.Context);
            evidence.RequestId = Guid.NewGuid(); evidence.ExpireAfterFlush = true;
            await RefusedAsync(() => expiry.Store.TryStartAsync(expiry.Context, evidence.RequestId, token));
            var state = await StateAsync();
            if (!evidence.Flushed || !evidence.RolledBack || !evidence.WitnessOnly || evidence.WitnessCommitChecks != 1
                || evidence.WitnessCommits != 1 || state.ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc
                || GroupAutomaticDependencyRuntimeProof.StateFingerprint(withExpiryCleared()) != originalState
                || await JournalDigestAsync() != digest || await StartsAsync() != 3 || await FailuresAsync() != 2
                || expiry.Keys.Reads != 1 || expiry.Keys.Writes != 0) throw new InvalidOperationException();
            clock.Current = claim.Receipt.IssuedAtUtc;
            await RefusedAsync(() => expiry.Store.TryStartAsync(expiry.Context, Guid.NewGuid(), token));
            if (await JournalDigestAsync() != digest || (await StateAsync()).ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc
                || evidence.WitnessCommitChecks != 2 || evidence.WitnessCommits != 2)
                throw new InvalidOperationException();
            GroupBatchClaimStateRecord withExpiryCleared() { state.ExpiryObservedAtUtc = null; return state; }
        }
        diagnostic.Phase = JournalPhase.Final;
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
    internal sealed class JournalEvidence(GroupScope scope, GroupNoteRuntimeProof.OwnedClock clock)
    {
        internal GroupScope Scope = scope;
        internal GroupNoteRuntimeProof.OwnedClock Clock = clock;
        internal GroupBatchClaimReceipt? Claim;
        internal Guid RequestId;
        internal string? InputIdentity;
        internal bool LoseReply, CommittedReplyLost, ExpireAfterFlush, Flushed, RolledBack, WitnessOnly;
        internal string? OriginalState;
        internal bool WitnessSaveIntent, WitnessSaved;
        internal int WitnessCommitChecks, WitnessCommits;

        internal void RequireWitnessSaveIntent(PlatformDbContext database)
        {
            var claim = Claim ?? throw new InvalidOperationException();
            var pending = database.ChangeTracker.Entries().Where(x => x.State is not (EntityState.Detached or EntityState.Unchanged)).ToArray();
            if (!ExpireAfterFlush || !Flushed || !RolledBack || WitnessSaved || WitnessSaveIntent || pending.Length != 1
                || pending[0].Entity is not GroupBatchClaimStateRecord state || pending[0].State != EntityState.Modified
                || !pending[0].Properties.Where(x => x.IsModified).Select(x => x.Metadata.Name)
                    .SequenceEqual(new[] { nameof(GroupBatchClaimStateRecord.ExpiryObservedAtUtc) })
                || pending[0].OriginalValues.GetValue<DateTimeOffset?>(nameof(GroupBatchClaimStateRecord.ExpiryObservedAtUtc)) is not null
                || state.ExpiryObservedAtUtc != claim.ExpiresAtUtc || !MatchesOriginal(state)
                || GroupAutomaticDependencyRuntimeProof.StateFingerprint((GroupBatchClaimStateRecord)pending[0].OriginalValues.ToObject()) != OriginalState)
                throw new InvalidOperationException();
            WitnessSaveIntent = true;
        }

        internal bool MatchesOriginal(GroupBatchClaimStateRecord state)
        {
            var claim = Claim ?? throw new InvalidOperationException();
            if (OriginalState is null || state.TenantId != Scope.TenantId || state.CompanyId != Scope.CompanyId
                || state.BindingId != Scope.SourceBindingId || state.BatchId != claim.BatchId || state.Epoch != claim.Epoch
                || state.OwnerId != claim.OwnerId || state.OperationId != claim.OperationId || state.IssuedAtUtc != claim.IssuedAtUtc
                || state.ExpiresAtUtc != claim.ExpiresAtUtc || state.ExpiryObservedAtUtc != claim.ExpiresAtUtc) return false;
            var original = JsonSerializer.Deserialize<GroupBatchClaimStateRecord>(JsonSerializer.SerializeToUtf8Bytes(state))
                ?? throw new InvalidOperationException();
            original.ExpiryObservedAtUtc = null;
            return GroupAutomaticDependencyRuntimeProof.StateFingerprint(original) == OriginalState;
        }

        internal void RequireDetachedWitness(PlatformDbContext database)
        {
            if (!ExpireAfterFlush || !Flushed || !RolledBack || !WitnessSaved || WitnessSaveIntent
                || database.ChangeTracker.HasChanges() || database.ChangeTracker.Entries<GroupBatchClaimStateRecord>().Any()
                || database.ChangeTracker.Entries<GroupModelAttemptStartRecord>().Any()
                || database.ChangeTracker.Entries<GroupModelAttemptFailureRecord>().Any()) throw new InvalidOperationException();
        }

        internal async Task RequirePersistedWitnessAsync(PlatformDbContext database, DbTransaction transaction, CancellationToken token)
        {
            await RequireLockAsync(transaction, token);
            var claim = Claim ?? throw new InvalidOperationException();
            var state = await database.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == Scope.TenantId
                && x.CompanyId == Scope.CompanyId && x.BindingId == Scope.SourceBindingId && x.BatchId == claim.BatchId, token);
            if (!MatchesOriginal(state) || await database.GroupModelAttemptStarts.AsNoTracking().AnyAsync(x =>
                x.TenantId == Scope.TenantId && x.CompanyId == Scope.CompanyId && x.BindingId == Scope.SourceBindingId
                && x.RequestId == RequestId, token)) throw new InvalidOperationException();
        }
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
    internal sealed class JournalFlushProbe(JournalEvidence evidence) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result,
            CancellationToken token = default)
        {
            if (evidence.ExpireAfterFlush && evidence.RolledBack)
                evidence.RequireWitnessSaveIntent(data.Context as PlatformDbContext ?? throw new InvalidOperationException());
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (evidence.WitnessSaveIntent)
            {
                var witness = data.Context as PlatformDbContext ?? throw new InvalidOperationException();
                var unit = witness.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException();
                if (result != 1) throw new InvalidOperationException();
                await evidence.RequirePersistedWitnessAsync(witness, unit, token);
                evidence.WitnessSaveIntent = false; evidence.WitnessSaved = true;
                return result;
            }
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
    internal sealed class JournalTransactionProbe(JournalEvidence evidence) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken token = default)
        {
            if (evidence.ExpireAfterFlush && evidence.RolledBack)
            {
                var database = data.Context as PlatformDbContext ?? throw new InvalidOperationException();
                evidence.RequireDetachedWitness(database);
                if (evidence.WitnessCommitChecks != evidence.WitnessCommits) throw new InvalidOperationException();
                await evidence.RequirePersistedWitnessAsync(database, transaction, token);
                evidence.WitnessCommitChecks++;
            }
            return result;
        }
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
                // The production store detaches the saved witness in finally.
                // Carry the actual locked precommit observation across commit;
                // a second already-retired rewind must recheck it under SQL.
                evidence.RequireDetachedWitness(witness);
                if (evidence.WitnessCommitChecks != evidence.WitnessCommits + 1) throw new InvalidOperationException();
                evidence.WitnessCommits++; evidence.WitnessOnly = true;
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

    private static async Task RequirePhysicalRefusalsAsync(PlatformDbContext database, GroupScope scope, CancellationToken token, JournalDiagnostic diagnostic)
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
        var probeIndex = 0;
        foreach (var probe in probes)
        {
            diagnostic.Phase = (JournalPhase)((int)JournalPhase.PhysicalChain + probeIndex++);
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

    internal enum JournalPhase
    {
        Setup, Claim, Preparation, LostReply, Resume, CapReady, CapClaim, CapPreparation, CapStart, CapFailure,
        CapAggregate, CapCounts, CapFourth, PhysicalRows, PhysicalChain, PhysicalClaim, PhysicalPredecessor,
        PhysicalUnique, PhysicalFailureStart, PhysicalStartUpdate, PhysicalStartDelete, PhysicalFailureUpdate,
        PhysicalFailureDelete, Expiry, Final
    }
    internal enum FailureKind { Other, SourceLock, Journal, Denied, Cancelled, SqlTimeout, SqlDeadlock }
    internal sealed class PhaseFailure(JournalPhase phase, FailureKind kind) : InvalidOperationException("Owned journal proof failed.")
    {
        internal JournalPhase Phase { get; } = phase;
        internal FailureKind Kind { get; } = kind;
    }
    internal sealed class JournalDiagnostic
    {
        internal JournalPhase Phase = JournalPhase.Setup;
        internal PhaseFailure? FirstFailure;
    }
    internal static Task<T> ObserveAsync<T>(JournalPhase phase, Func<Task<T>> call, JournalDiagnostic diagnostic) =>
        ObserveCoreAsync(phase, call, failure => Interlocked.CompareExchange(ref diagnostic.FirstFailure, failure, null));

    // Inert execution seam: runs exactly the supplied operation once and never
    // retries, opens resources, swallows failures or prints exception data.
    internal static async Task<T> ObserveCoreAsync<T>(JournalPhase phase, Func<Task<T>> call, Action<PhaseFailure> record)
    {
        try { return await call(); }
        catch (Exception error)
        {
            var failure = error as PhaseFailure ?? new PhaseFailure(phase, Kind(error));
            record(failure); throw failure;
        }
    }
    private static FailureKind Kind(Exception error) => error switch
    {
        OperationCanceledException => FailureKind.Cancelled,
        UnauthorizedAccessException => FailureKind.Denied,
        SqlException { Number: -2 } => FailureKind.SqlTimeout,
        SqlException { Number: 1205 } => FailureKind.SqlDeadlock,
        InvalidOperationException { Message: "Group source transaction is not available." } => FailureKind.SourceLock,
        InvalidOperationException { Message: "Group model attempt journal is not available." } => FailureKind.Journal,
        _ => FailureKind.Other
    };
    internal static string FailureDiagnostic(Exception error)
    {
        var phase = error is PhaseFailure f ? f.Phase switch
        {
            JournalPhase.Setup => "setup",
            JournalPhase.Claim => "claim",
            JournalPhase.Preparation => "preparation",
            JournalPhase.LostReply => "lost-reply",
            JournalPhase.Resume => "resume",
            JournalPhase.CapReady => "cap-ready",
            JournalPhase.CapClaim => "cap-claim",
            JournalPhase.CapPreparation => "cap-preparation",
            JournalPhase.CapStart => "cap-start",
            JournalPhase.CapFailure => "cap-failure",
            JournalPhase.CapAggregate => "cap-aggregate",
            JournalPhase.CapCounts => "cap-counts",
            JournalPhase.CapFourth => "cap-fourth",
            JournalPhase.PhysicalRows => "physical-rows",
            JournalPhase.PhysicalChain => "physical-chain",
            JournalPhase.PhysicalClaim => "physical-claim",
            JournalPhase.PhysicalPredecessor => "physical-predecessor",
            JournalPhase.PhysicalUnique => "physical-unique",
            JournalPhase.PhysicalFailureStart => "physical-failure-start",
            JournalPhase.PhysicalStartUpdate => "physical-start-update",
            JournalPhase.PhysicalStartDelete => "physical-start-delete",
            JournalPhase.PhysicalFailureUpdate => "physical-failure-update",
            JournalPhase.PhysicalFailureDelete => "physical-failure-delete",
            JournalPhase.Expiry => "expiry",
            JournalPhase.Final => "final",
            _ => "unknown"
        } : "unknown";
        var kind = error is PhaseFailure k ? k.Kind switch
        {
            FailureKind.SourceLock => "source-lock",
            FailureKind.Journal => "journal",
            FailureKind.Denied => "denied",
            FailureKind.Cancelled => "cancelled",
            FailureKind.SqlTimeout => "sql-timeout",
            FailureKind.SqlDeadlock => "sql-deadlock",
            _ => "other"
        } : "other";
        return "FAIL owned model journal runtime phase-" + phase + " kind-" + kind;
    }
}
