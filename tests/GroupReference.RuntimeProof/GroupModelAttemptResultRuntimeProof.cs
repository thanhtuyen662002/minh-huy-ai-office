using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Prepared disposable SQL proof. Synthetic output exercises storage only;
// it never invokes or qualifies a provider, model, note effect or coordinator.
internal static class GroupModelAttemptResultRuntimeProof
{
    internal const string PrepareLine = "PASS owned attempt result source allocation no claims plans starts results effects provider or model";
    internal const string CommitLine = "PASS owned attempt result actual original SQL start encrypted accepted output committed reply interrupted fresh context byte exact replay one result zero failures no notes provider or model";
    internal const string ResumeLine = "PASS owned attempt result separate process current replacement claim original plan start output retained four fresh concurrent reads retry failure suppressed no overwrite notes provider or model";
    internal const string DeadlineLine = "PASS owned attempt result actual SQL flushed insert at original deadline transaction rollback detached zero results fresh expired source read durable witness clock rewind refused no notes provider or model";
    private const string Profile = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Model = "owned-synthetic-result";

    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("attempt-result-prepare" or "attempt-result-commit" or "attempt-result-resume" or "attempt-result-deadline"))
            throw new InvalidOperationException();
        if (mode == "attempt-result-prepare")
        {
            await GroupAutomaticNoteRuntimeProof.RunAsync("automatic-note-prepare", scope, operation, worker, options, token);
            await using var clean = new PlatformDbContext(options);
            if (await Results(clean).AnyAsync(token) || await Starts(clean).AnyAsync(token)
                || await Plans(clean).AnyAsync(token)) throw new InvalidOperationException();
            Console.WriteLine(PrepareLine); return;
        }
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var evidence = new Evidence(scope, clock);
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new ResultReadProbe(evidence), new ResultFlushProbe(evidence), new ResultTransactionProbe(evidence)).Options);
        var allocation = await database.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var previous = await database.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id)
            .OrderByDescending(x => x.Epoch).FirstOrDefaultAsync(token);
        var epoch = mode == "attempt-result-resume" ? 2 : 1;
        if ((previous?.Epoch ?? 0) != epoch - 1) throw new InvalidOperationException();
        clock.Current = previous is null ? allocation.AllocatedAtUtc : previous.ExpiresAtUtc.AddTicks(1);
        var claim = await new GroupBatchClaimStore(database, worker, clock).TryAcquireAsync(scope, allocation.Id,
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        var handle = claim.CurrentHandle ?? throw new InvalidOperationException();
        if (claim.WasAlreadyClaimed || claim.Receipt.Epoch != epoch) throw new InvalidOperationException();
        var input = await PrepareAsync(database, handle);
        var before = GroupAutomaticDependencyRuntimeProof.StateFingerprint(await StateAsync());
        await RequireEmptyEffectsAsync();
        if (mode == "attempt-result-resume")
        {
            var loaded = await input.Results.ReadOriginalAsync(input.Plan, input.Chunk, token) ?? throw new InvalidOperationException();
            RequireOutput(loaded, input);
            var original = await Starts(database).SingleAsync(token);
            if (original.ClaimEpoch != 1 || original.ClaimOperationId != previous!.OperationId
                || loaded.Observed.ObservedAtUtc != original.StartedAtUtc) throw new InvalidOperationException();
            var bytes = loaded.Encode();
            try
            {
                using var ready = new SemaphoreSlim(0, 4);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reads = Enumerable.Range(0, 4).Select(async _ =>
                {
                    await using var contender = new PlatformDbContext(options);
                    var replay = await new GroupBatchClaimStore(contender, worker, clock).TryAcquireAsync(scope, allocation.Id,
                        claim.Receipt.OwnerId, claim.Receipt.OperationId, TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
                    if (!replay.WasAlreadyClaimed) throw new InvalidOperationException();
                    var fresh = await PrepareAsync(contender, replay.CurrentHandle ?? throw new InvalidOperationException());
                    ready.Release(); await release.Task.WaitAsync(token);
                    var accepted = await fresh.Results.ReadOriginalAsync(fresh.Plan, fresh.Chunk, token) ?? throw new InvalidOperationException();
                    RequireSame(bytes, accepted); RequireOutput(accepted, fresh);
                    if (fresh.Keys.Reads != 2 || fresh.Keys.Writes != 0) throw new InvalidOperationException();
                    RequireClean(contender);
                }).ToArray();
                for (var i = 0; i < 4; i++) await ready.WaitAsync(token);
                release.SetResult(); await Task.WhenAll(reads);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            await RefusedAsync(() => input.Journal.TryStartOriginalAsync(input.Plan, input.Chunk, Guid.NewGuid(), token));
            var start = new GroupModelAttemptStarted(scope, original.BatchId, original.InputSha256, original.Ordinal,
                original.RequestId, original.PredecessorRequestId, original.ClaimOperationId, original.ClaimEpoch,
                original.StartedAtUtc, original.DeadlineUtc);
            // This synthetic carrier cannot authorize a provider. The actual
            // journal must refuse any failure append over an accepted result.
            var permit = new GroupModelAttemptPermit(input.Chunk.Current.ModelContext!, start,
                GroupModelAttemptOriginalBinding.Create(input.Plan, input.Chunk, token));
            await RefusedAsync(() => input.Journal.RecordFailureAsync(permit.Context, permit, GroupModelAttemptFailure.InvalidOutput, token));
            if (await Results(database).CountAsync(token) != 1 || input.Keys.Reads != 2 || input.Keys.Writes != 0)
                throw new InvalidOperationException();
        }
        else
        {
            if (await Results(database).AnyAsync(token) || await Starts(database).AnyAsync(token)
                || await input.Results.ReadOriginalAsync(input.Plan, input.Chunk, token) is not null) throw new InvalidOperationException();
            var started = await input.Journal.TryStartOriginalAsync(input.Plan, input.Chunk, Guid.NewGuid(), token);
            var permit = started.Permit ?? throw new InvalidOperationException();
            if (started.WasAlreadyStarted || permit.Original is null) throw new InvalidOperationException();
            var request = permit.Context.CreateGatewayRequest(permit.Start.RequestId);
            var wire = new AiGatewayResponse(request.RequestId, request.Capability, Output(input), Model, 123, 7)
            { ProviderId = "owned-synthetic-provider" };
            var response = GroupModelAttemptResponse.Parse(permit, wire, clock, token);
            if (response.Disposition != GroupModelAttemptResponseDisposition.Parsed) throw new InvalidOperationException();
            evidence.Armed = true; evidence.Deadline = permit.Start.DeadlineUtc;
            if (mode == "attempt-result-commit")
            {
                evidence.LoseReply = true;
                var lost = false;
                try { _ = await input.Results.PersistOriginalAsync(input.Plan, input.Chunk, response, token); }
                catch (OwnedResultReplyLostException) { lost = true; }
                finally { evidence.Armed = false; }
                if (!lost || !evidence.ReplyLost || !evidence.Flushed || !evidence.LockObserved
                    || await Results(database).CountAsync(token) != 1) throw new InvalidOperationException();
                await using var restarted = new PlatformDbContext(options);
                var replay = await new GroupBatchClaimStore(restarted, worker, clock).TryAcquireAsync(scope, allocation.Id,
                    claim.Receipt.OwnerId, claim.Receipt.OperationId, TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
                if (!replay.WasAlreadyClaimed) throw new InvalidOperationException();
                var fresh = await PrepareAsync(restarted, replay.CurrentHandle ?? throw new InvalidOperationException());
                var loaded = await fresh.Results.ReadOriginalAsync(fresh.Plan, fresh.Chunk, token) ?? throw new InvalidOperationException();
                var expected = GroupModelAttemptResult.Create(response, input.Chunk, token).Encode();
                try { RequireSame(expected, loaded); }
                finally { CryptographicOperations.ZeroMemory(expected); }
                RequireOutput(loaded, fresh);
                // Exact replay uses the original carrier on its original current
                // context; neither this path nor recovery obtains a new permit.
                var replayed = await input.Results.PersistOriginalAsync(input.Plan, input.Chunk, response, token);
                if (!replayed.Proposal.EncodeValidatedOutput().AsSpan().SequenceEqual(response.Proposal!.EncodeValidatedOutput())
                    || input.Keys.Writes != 1 || input.Keys.Reads != 2 || fresh.Keys.Reads != 2 || fresh.Keys.Writes != 0)
                    throw new InvalidOperationException();
                RequireClean(restarted);
            }
            else
            {
                evidence.ExpireAfterFlush = true;
                try { await RefusedAsync(() => input.Results.PersistOriginalAsync(input.Plan, input.Chunk, response, token)); }
                finally { evidence.Armed = false; }
                RequireClean(database);
                await using (var rolledBack = new PlatformDbContext(options))
                {
                    if (!evidence.Flushed || !evidence.LockObserved || await Results(rolledBack).AnyAsync(token))
                        throw new InvalidOperationException();
                }
                // Result deadline refusal precedes the source expiry inspection.
                // This separate actual source read supplies durable retirement;
                // do not attribute its witness to the result rollback itself.
                await RefusedAsync(() => input.Sources.ReadAsync(handle, input.MessageIds, token));
                if ((await StateAsync()).ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc) throw new InvalidOperationException();
                clock.Current = claim.Receipt.IssuedAtUtc;
                await RefusedAsync(() => input.Results.ReadOriginalAsync(input.Plan, input.Chunk, token));
                await RefusedAsync(() => input.Results.PersistOriginalAsync(input.Plan, input.Chunk, response, token));
                if (await Results(database).AnyAsync(token)) throw new InvalidOperationException();
            }
        }
        if (await Starts(database).CountAsync(token) != 1 || await database.GroupModelAttemptFailures.AnyAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token))
            throw new InvalidOperationException();
        var state = await StateAsync();
        if (mode == "attempt-result-deadline") state.ExpiryObservedAtUtc = null;
        if (GroupAutomaticDependencyRuntimeProof.StateFingerprint(state) != before) throw new InvalidOperationException();
        RequireClean(database); await RequireEmptyEffectsAsync();
        Console.WriteLine(mode switch { "attempt-result-commit" => CommitLine, "attempt-result-resume" => ResumeLine, _ => DeadlineLine });

        async Task<Prepared> PrepareAsync(PlatformDbContext db, GroupBatchClaimHandle current)
        {
            var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
            var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
            var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
            var original = await new GroupBatchClaimStore(db, worker, clock).ReadCurrentAllocationAsync(current, token);
            var ids = original.Revisions.Select(x => x.Metadata.MessageId).ToArray();
            var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(current, ids, token));
            var history = await brain.ReadAsync(current, [], [], token);
            if (ids.Length != 2 || preparation.Candidates.Count != 1 || preparation.Receipts.Count != 2
                || history.Items.Count != 0 || keys.Reads != 1 || keys.Writes != 0) throw new InvalidOperationException();
            var plan = GroupAutomaticInputPlan.Create(preparation, history, Profile, Model, 64000, [], token);
            var store = new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain);
            var persisted = await store.PersistAsync(plan, preparation, history, token);
            if (!persisted.Encode().AsSpan().SequenceEqual(plan.Encode())) throw new InvalidOperationException();
            var chunks = persisted.RestoreChunks(preparation, history, Profile, [], token);
            if (chunks.Count != 1 || !chunks[0].Original.RequiresModel) throw new InvalidOperationException();
            return new(persisted, chunks[0], new(db, worker, clock, sources, brain),
                new(db, worker, clock, sources, brain, keys, new()), keys, sources, ids);
        }
        Task<GroupBatchClaimStateRecord> StateAsync() => database.GroupBatchClaimStates.AsNoTracking().SingleAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        async Task RequireEmptyEffectsAsync()
        {
            if (await GroupNoteRuntimeProof.TargetRowsAsync(database, scope, Guid.Empty, token) != 0
                || await database.GroupWorkRawDispositions.AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
        }
        IQueryable<GroupModelAttemptResultRecord> Results(PlatformDbContext db) => db.Set<GroupModelAttemptResultRecord>().AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId);
        IQueryable<GroupModelAttemptStartRecord> Starts(PlatformDbContext db) => db.GroupModelAttemptStarts.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId);
        IQueryable<GroupAutomaticInputPlanRecord> Plans(PlatformDbContext db) => db.GroupAutomaticInputPlans.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId);
    }

    private sealed record Prepared(GroupAutomaticInputPlan Plan, GroupAutomaticInputPlan.RestoredChunk Chunk,
        GroupModelAttemptJournalStore Journal, GroupModelAttemptResultStore Results, GroupNoteRuntimeProof.CountedKeys Keys,
        GroupBatchSourceReader Sources, Guid[] MessageIds);
    private static string Output(Prepared input)
    {
        var source = input.Chunk.Current.Preparation.Candidates.Single();
        return JsonSerializer.Serialize(new
        {
            version = GroupGroundedWorkProposal.FormatVersion,
            notes = new[] { new { type = "needs_clarification", title = "owned synthetic inventory question",
                problem = "owned synthetic warehouse missing", outcome = "owned synthetic inventory lookup",
                source_refs = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, quote = source.Text } },
                missing_fields = new[] { "owned warehouse detail" }, requested_deadline_text = (string?)null, suggested_relation = (string?)null } },
            source_dispositions = new[] { new { message_id = source.MessageId.ToString("D"), revision = source.Revision, disposition = "work" } }
        });
    }
    private static void RequireOutput(GroupModelAttemptResult result, Prepared input)
    {
        if (result.Observed.ProviderId != "owned-synthetic-provider" || result.Observed.Model != Model
            || result.Observed.InputTokens != 123 || result.Observed.OutputTokens != 7
            || !ReferenceEquals(result.Proposal.Preparation, input.Chunk.Current.Preparation)
            || !result.Proposal.EncodeValidatedOutput().AsSpan().SequenceEqual(
                GroupGroundedWorkProposal.Parse(input.Chunk.Current.Preparation, Output(input)).EncodeValidatedOutput()))
            throw new InvalidOperationException();
    }
    private static void RequireSame(byte[] expected, GroupModelAttemptResult result)
    {
        var actual = result.Encode();
        try { if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidOperationException(); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }
    private static void RequireClean(PlatformDbContext db)
    {
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null
            || db.Database.GetDbConnection().State != ConnectionState.Closed
            || db.ChangeTracker.Entries<GroupModelAttemptResultRecord>().Any()) throw new InvalidOperationException();
    }
    private static async Task RefusedAsync(Func<Task> call)
    {
        var refused = false;
        try { await call(); }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException) { refused = true; }
        if (!refused) throw new InvalidOperationException();
    }
    private sealed class OwnedResultReplyLostException : IOException;
    private sealed class Evidence(GroupScope scope, GroupNoteRuntimeProof.OwnedClock clock)
    {
        internal bool Armed, LoseReply, ReplyLost, ExpireAfterFlush, Flushed, LockObserved;
        internal DateTimeOffset Deadline;
        internal async Task RequireLockAsync(DbTransaction transaction, CancellationToken token)
        {
            if (transaction.IsolationLevel != IsolationLevel.Serializable || transaction.Connection is null) throw new InvalidOperationException();
            await using var command = transaction.Connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 5;
            command.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}";
            command.Parameters.Add(parameter);
            if (!string.Equals((string?)await command.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
            LockObserved = true;
        }
        internal void Expire() => clock.Current = Deadline;
    }
    private sealed class ResultReadProbe(Evidence evidence) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (evidence.Armed && command.CommandText.Contains("FROM [aioffice].[GroupModelAttemptResults]", StringComparison.Ordinal))
                await evidence.RequireLockAsync(command.Transaction ?? throw new InvalidOperationException(), token);
            return result;
        }
    }
    private sealed class ResultFlushProbe(Evidence evidence) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (!evidence.Armed || data.Context is not PlatformDbContext db || !db.ChangeTracker.Entries<GroupModelAttemptResultRecord>().Any()) return result;
            if (result != 1) throw new InvalidOperationException();
            await evidence.RequireLockAsync(db.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException(), token);
            var row = db.ChangeTracker.Entries<GroupModelAttemptResultRecord>().Single().Entity;
            if (await db.Set<GroupModelAttemptResultRecord>().AsNoTracking().CountAsync(x => x.TenantId == row.TenantId
                && x.CompanyId == row.CompanyId && x.BindingId == row.BindingId && x.RequestId == row.RequestId, token) != 1)
                throw new InvalidOperationException();
            evidence.Flushed = true;
            if (evidence.ExpireAfterFlush) evidence.Expire();
            return result;
        }
    }
    private sealed class ResultTransactionProbe(Evidence evidence) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            if (evidence.Armed && evidence.LoseReply && data.Context is PlatformDbContext db
                && db.ChangeTracker.Entries<GroupModelAttemptResultRecord>().Any())
            { evidence.LoseReply = false; evidence.ReplyLost = true; throw new OwnedResultReplyLostException(); }
            return Task.CompletedTask;
        }
    }
}
