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
        var diagnostic = new ResultDiagnostic();
        try { await RunCoreAsync(mode, scope, operation, worker, options, diagnostic, token); }
        catch (Exception error)
        { throw diagnostic.FirstFailure ?? error as PhaseFailure ?? new PhaseFailure(diagnostic.Phase, Kind(error)); }
    }

    private static async Task RunCoreAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options,
        ResultDiagnostic diagnostic, CancellationToken token)
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
        diagnostic.Phase = ResultPhase.Claim;
        var claim = await new GroupBatchClaimStore(database, worker, clock).TryAcquireAsync(scope, allocation.Id,
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        var handle = claim.CurrentHandle ?? throw new InvalidOperationException();
        if (claim.WasAlreadyClaimed || claim.Receipt.Epoch != epoch) throw new InvalidOperationException();
        diagnostic.Phase = ResultPhase.Preparation;
        var input = await PrepareAsync(database, handle, token);
        var before = GroupAutomaticDependencyRuntimeProof.StateFingerprint(await StateAsync());
        await RequireEmptyEffectsAsync();
        if (mode == "attempt-result-resume")
        {
            diagnostic.Phase = ResultPhase.ResumeRead;
            var loaded = await input.Results.ReadOriginalAsync(input.Plan, input.Chunk, token) ?? throw new InvalidOperationException();
            diagnostic.Phase = ResultPhase.ResumeOutput;
            RequireOutput(loaded, input);
            diagnostic.Phase = ResultPhase.ResumeStart;
            var original = await Starts(database).SingleAsync(token);
            if (original.ClaimEpoch != 1 || original.ClaimOperationId != previous!.OperationId
                || loaded.Observed.ObservedAtUtc != original.StartedAtUtc) throw new InvalidOperationException();
            var bytes = loaded.Encode();
            try
            {
                diagnostic.Phase = ResultPhase.ConcurrentReads;
                var prepared = new Dictionary<PlatformDbContext, Prepared>();
                var readers = new OriginalResultReadCohort();
                var probes = new Dictionary<PlatformDbContext, OriginalResultReadProbe>();
                await RunPreparedConcurrentReadsAsync(_ =>
                {
                    var probe = new OriginalResultReadProbe(readers, scope);
                    var contender = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options).AddInterceptors(probe).Options);
                    probes.Add(contender, probe);
                    return contender;
                }, async (_, contender, currentToken) =>
                {
                    var replay = await ObserveAsync(ResultPhase.ConcurrentClaim, async () =>
                    {
                        var result = await new GroupBatchClaimStore(contender, worker, clock).TryAcquireAsync(scope, allocation.Id,
                            claim.Receipt.OwnerId, claim.Receipt.OperationId, TimeSpan.FromMinutes(2), currentToken) ?? throw new InvalidOperationException();
                        if (!result.WasAlreadyClaimed || result.CurrentHandle is null) throw new InvalidOperationException();
                        return result;
                    }, diagnostic);
                    var fresh = await ObserveAsync(ResultPhase.ConcurrentPreparation,
                        () => PrepareAsync(contender, replay.CurrentHandle!, currentToken), diagnostic);
                    prepared.Add(contender, fresh);
                }, async (_, contender, currentToken) =>
                {
                    var fresh = prepared[contender];
                    probes[contender].Arm(contender);
                    var accepted = await ObserveAsync(ResultPhase.ConcurrentRead, async () =>
                        await fresh.Results.ReadOriginalAsync(fresh.Plan, fresh.Chunk, currentToken) ?? throw new InvalidOperationException(), diagnostic);
                    await ObserveAsync(ResultPhase.ConcurrentChecks, () =>
                    {
                        RequireSame(bytes, accepted); RequireOutput(accepted, fresh);
                        if (fresh.Keys.Reads != 2 || fresh.Keys.Writes != 0) throw new InvalidOperationException();
                        RequireClean(contender); return Task.FromResult(true);
                    }, diagnostic);
                }, contender => contender.DisposeAsync(), diagnostic, token);
                await ObserveAsync(ResultPhase.ConcurrentChecks, () =>
                {
                    readers.RequireComplete(); return Task.FromResult(true);
                }, diagnostic);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            diagnostic.Phase = ResultPhase.RetrySuppression;
            await RefusedAsync(() => input.Journal.TryStartOriginalAsync(input.Plan, input.Chunk, Guid.NewGuid(), token));
            var start = new GroupModelAttemptStarted(scope, original.BatchId, original.InputSha256, original.Ordinal,
                original.RequestId, original.PredecessorRequestId, original.ClaimOperationId, original.ClaimEpoch,
                original.StartedAtUtc, original.DeadlineUtc);
            // This synthetic carrier cannot authorize a provider. The actual
            // journal must refuse any failure append over an accepted result.
            var permit = new GroupModelAttemptPermit(input.Chunk.Current.ModelContext!, start,
                GroupModelAttemptOriginalBinding.Create(input.Plan, input.Chunk, token));
            diagnostic.Phase = ResultPhase.FailureSuppression;
            await RefusedAsync(() => input.Journal.RecordFailureAsync(permit.Context, permit, GroupModelAttemptFailure.InvalidOutput, token));
            if (await Results(database).CountAsync(token) != 1 || input.Keys.Reads != 2 || input.Keys.Writes != 0)
                throw new InvalidOperationException();
        }
        else
        {
            diagnostic.Phase = ResultPhase.AttemptStart;
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
                diagnostic.Phase = ResultPhase.Commit;
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
                var fresh = await PrepareAsync(restarted, replay.CurrentHandle ?? throw new InvalidOperationException(), token);
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
                diagnostic.Phase = ResultPhase.Deadline;
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
        diagnostic.Phase = ResultPhase.Final;
        if (await Starts(database).CountAsync(token) != 1 || await database.GroupModelAttemptFailures.AnyAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token))
            throw new InvalidOperationException();
        var state = await StateAsync();
        if (mode == "attempt-result-deadline") state.ExpiryObservedAtUtc = null;
        if (GroupAutomaticDependencyRuntimeProof.StateFingerprint(state) != before) throw new InvalidOperationException();
        RequireClean(database); await RequireEmptyEffectsAsync();
        Console.WriteLine(mode switch { "attempt-result-commit" => CommitLine, "attempt-result-resume" => ResumeLine, _ => DeadlineLine });

        async Task<Prepared> PrepareAsync(PlatformDbContext db, GroupBatchClaimHandle current, CancellationToken currentToken)
        {
            var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
            var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
            var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
            var original = await new GroupBatchClaimStore(db, worker, clock).ReadCurrentAllocationAsync(current, currentToken);
            var ids = original.Revisions.Select(x => x.Metadata.MessageId).ToArray();
            var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(current, ids, currentToken));
            var history = await brain.ReadAsync(current, [], [], currentToken);
            if (ids.Length != 2 || preparation.Candidates.Count != 1 || preparation.Receipts.Count != 2
                || history.Items.Count != 0 || keys.Reads != 1 || keys.Writes != 0) throw new InvalidOperationException();
            var plan = GroupAutomaticInputPlan.Create(preparation, history, Profile, Model, 64000, [], currentToken);
            var store = new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain);
            var persisted = await store.PersistAsync(plan, preparation, history, currentToken);
            if (!persisted.Encode().AsSpan().SequenceEqual(plan.Encode())) throw new InvalidOperationException();
            var chunks = persisted.RestoreChunks(preparation, history, Profile, [], currentToken);
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

    // Every genuine original-result reader must hold its own actual Shared
    // SQL transaction before ANY reader proceeds. This fails if the shipped
    // path is still Exclusive, serializes the readers, or drops one reader.
    internal sealed class OriginalResultReadCohort
    {
        private readonly HashSet<DbTransaction> transactions = new(ReferenceEqualityComparer.Instance);
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task ArriveAsync(DbTransaction transaction, CancellationToken token)
        {
            lock (transactions)
            {
                if (!transactions.Add(transaction) || transactions.Count > 4) throw new InvalidOperationException();
                if (transactions.Count == 4) all.TrySetResult();
            }
            await all.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        internal void RequireComplete()
        { lock (transactions) { if (transactions.Count != 4 || !all.Task.IsCompletedSuccessfully) throw new InvalidOperationException(); } }
    }

    internal sealed class OriginalResultReadProbe(OriginalResultReadCohort cohort, GroupScope scope) : DbCommandInterceptor
    {
        private PlatformDbContext? expected;
        private bool arrived;
        internal void Arm(PlatformDbContext database)
        {
            scope.Validate();
            if (expected is not null) throw new InvalidOperationException();
            expected = database;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            // The lock command is manual ADO and does not emit EF scalar events.
            // Metadata is an actual EF read after Shared acquisition and every
            // pre-fence. Observe before opening its reader; key reads outside
            // the two owned units cannot accidentally join this rendezvous.
            if (expected is null || !command.CommandText.Contains("FROM [aioffice].[GroupModelAttemptResults]", StringComparison.Ordinal)) return result;
            if (result.HasResult || data.Context is not PlatformDbContext database || !ReferenceEquals(database, expected)
                || command.Transaction is null || command.Connection?.State != ConnectionState.Open
                || command.Transaction.IsolationLevel != IsolationLevel.Serializable
                || !ReferenceEquals(command.Connection, database.Database.GetDbConnection())
                || !ReferenceEquals(command.Transaction, database.Database.CurrentTransaction?.GetDbTransaction())
                || !ReferenceEquals(command.Transaction.Connection, command.Connection)) throw new InvalidOperationException();
            await using var mode = command.Connection!.CreateCommand();
            mode.Transaction = command.Transaction; mode.CommandTimeout = 10;
            mode.CommandText = "SELECT APPLOCK_MODE('public',@resource,'Transaction');";
            var resource = mode.CreateParameter(); resource.ParameterName = "@resource";
            resource.Value = $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}";
            mode.Parameters.Add(resource);
            if (await mode.ExecuteScalarAsync(token) is not string held || held != "Shared") throw new InvalidOperationException();
            if (!arrived)
            {
                arrived = true;
                await cohort.ArriveAsync(command.Transaction, token);
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (expected is not null && (command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException();
            return ValueTask.FromResult(result);
        }
    }

    internal enum ResultPhase
    {
        Setup, Claim, Preparation, ResumeRead, ResumeOutput, ResumeStart, ConcurrentReads, ConcurrentClaim,
        ConcurrentPreparation, ConcurrentRead, ConcurrentChecks, RetrySuppression, FailureSuppression,
        AttemptStart, Commit, Deadline, Final
    }
    internal enum FailureKind { Other, SourceLock, SourceLockMissingTransaction, SourceLockWaitTimeout, SourceLockCancelled, SourceLockDeadlock, SourceLockRejected, Result, Plan, Journal, Denied, Cancelled, SqlTimeout, SqlDeadlock }
    internal sealed class PhaseFailure(ResultPhase phase, FailureKind kind) : InvalidOperationException("Owned result proof failed.")
    {
        internal ResultPhase Phase { get; } = phase;
        internal FailureKind Kind { get; } = kind;
    }
    internal sealed class ResultDiagnostic
    {
        internal ResultPhase Phase = ResultPhase.Setup;
        internal PhaseFailure? FirstFailure;
    }
    internal static async Task<T> ObserveAsync<T>(ResultPhase phase, Func<Task<T>> call, ResultDiagnostic diagnostic)
    {
        try { return await call(); }
        catch (Exception error)
        {
            var failure = error as PhaseFailure ?? new PhaseFailure(phase, Kind(error));
            Interlocked.CompareExchange(ref diagnostic.FirstFailure, failure, null);
            throw failure;
        }
    }

    // Prepare four distinct contexts once, without making their many short
    // source-locked setup units contend with each other. The actual original
    // result reads still start together and retain all current SQL fences.
    // Setup failure releases all owned contexts and never launches any read.
    internal static async Task RunPreparedConcurrentReadsAsync<T>(Func<int, T> create,
        Func<int, T, CancellationToken, Task> prepare, Func<int, T, CancellationToken, Task> read,
        Func<T, ValueTask> dispose, ResultDiagnostic diagnostic, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var contexts = new List<T>(4);
        PhaseFailure? failure = null;
        try
        {
            for (var index = 0; index < 4; index++)
            {
                token.ThrowIfCancellationRequested();
                var context = create(index);
                contexts.Add(context);
                await ObserveAsync(ResultPhase.ConcurrentPreparation, async () =>
                {
                    await prepare(index, context, token);
                    return true;
                }, diagnostic);
            }
            await RunConcurrentReadsAsync((index, currentToken) => read(index, contexts[index], currentToken), diagnostic, token);
        }
        catch (Exception error)
        {
            failure = diagnostic.FirstFailure ?? error as PhaseFailure
                ?? new PhaseFailure(ResultPhase.ConcurrentPreparation, Kind(error));
            Interlocked.CompareExchange(ref diagnostic.FirstFailure, failure, null);
        }
        finally
        {
            foreach (var context in contexts)
            {
                try { await dispose(context); }
                catch (Exception error)
                {
                    var cleanup = new PhaseFailure(ResultPhase.ConcurrentChecks, Kind(error));
                    Interlocked.CompareExchange(ref diagnostic.FirstFailure, cleanup, null);
                    failure ??= diagnostic.FirstFailure;
                }
            }
        }
        if (failure is not null) throw failure;
    }

    // Four one-shot operations start together. A failed preparation cannot
    // strand a readiness barrier: cancel outstanding work, drain all tasks and
    // retain the first closed operation failure. Every success is still required.
    internal static async Task RunConcurrentReadsAsync(Func<int, CancellationToken, Task> read,
        ResultDiagnostic diagnostic, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = Enumerable.Range(0, 4).Select(async index =>
        {
            try
            {
                await release.Task.WaitAsync(stopped.Token);
                await read(index, stopped.Token);
            }
            catch (Exception error)
            {
                var failure = error as PhaseFailure ?? new PhaseFailure(ResultPhase.ConcurrentReads, Kind(error));
                Interlocked.CompareExchange(ref diagnostic.FirstFailure, failure, null);
                try { await stopped.CancelAsync(); }
                catch { /* A cancellation callback cannot replace the original operation failure. */ }
                throw failure;
            }
        }).ToArray();
        release.SetResult();
        try { await Task.WhenAll(reads); }
        catch { throw diagnostic.FirstFailure ?? new PhaseFailure(ResultPhase.ConcurrentReads, FailureKind.Other); }
    }
    private static FailureKind Kind(Exception error) => error switch
    {
        _ when GroupSourceTransactionLock.FailureOf(error) is { } actual => actual switch
        {
            GroupSourceLockFailure.MissingTransaction => FailureKind.SourceLockMissingTransaction,
            GroupSourceLockFailure.WaitTimeout => FailureKind.SourceLockWaitTimeout,
            GroupSourceLockFailure.Cancelled => FailureKind.SourceLockCancelled,
            GroupSourceLockFailure.Deadlock => FailureKind.SourceLockDeadlock,
            GroupSourceLockFailure.Rejected => FailureKind.SourceLockRejected,
            _ => FailureKind.SourceLock
        },
        OperationCanceledException => FailureKind.Cancelled,
        UnauthorizedAccessException => FailureKind.Denied,
        SqlException { Number: -2 } => FailureKind.SqlTimeout,
        SqlException { Number: 1205 } => FailureKind.SqlDeadlock,
        _ when error.GetType() == typeof(InvalidOperationException) => error.Message switch
        {
            "Group source transaction is not available." => FailureKind.SourceLock,
            "Group model attempt stored result is unavailable." => FailureKind.Result,
            "Group original input plan store is not available." => FailureKind.Plan,
            "Group model attempt journal is not available." => FailureKind.Journal,
            _ => FailureKind.Other
        },
        _ => FailureKind.Other
    };
    internal static string FailureDiagnostic(Exception error)
    {
        var phase = error is PhaseFailure f ? f.Phase switch
        {
            ResultPhase.Setup => "setup",
            ResultPhase.Claim => "claim",
            ResultPhase.Preparation => "preparation",
            ResultPhase.ResumeRead => "resume-read",
            ResultPhase.ResumeOutput => "resume-output",
            ResultPhase.ResumeStart => "resume-start",
            ResultPhase.ConcurrentReads => "concurrent-reads",
            ResultPhase.ConcurrentClaim => "concurrent-claim",
            ResultPhase.ConcurrentPreparation => "concurrent-preparation",
            ResultPhase.ConcurrentRead => "concurrent-read",
            ResultPhase.ConcurrentChecks => "concurrent-checks",
            ResultPhase.RetrySuppression => "retry-suppression",
            ResultPhase.FailureSuppression => "failure-suppression",
            ResultPhase.AttemptStart => "attempt-start",
            ResultPhase.Commit => "commit",
            ResultPhase.Deadline => "deadline",
            ResultPhase.Final => "final",
            _ => "unknown"
        } : "unknown";
        var kind = error is PhaseFailure k ? k.Kind switch
        {
            FailureKind.SourceLock => "source-lock",
            FailureKind.SourceLockMissingTransaction => "source-lock-missing-transaction",
            FailureKind.SourceLockWaitTimeout => "source-lock-wait-timeout",
            FailureKind.SourceLockCancelled => "source-lock-cancelled",
            FailureKind.SourceLockDeadlock => "source-lock-deadlock",
            FailureKind.SourceLockRejected => "source-lock-rejected",
            FailureKind.Result => "result",
            FailureKind.Plan => "plan",
            FailureKind.Journal => "journal",
            FailureKind.Denied => "denied",
            FailureKind.Cancelled => "cancelled",
            FailureKind.SqlTimeout => "sql-timeout",
            FailureKind.SqlDeadlock => "sql-deadlock",
            _ => "other"
        } : "other";
        return "FAIL owned attempt result runtime phase-" + phase + " kind-" + kind;
    }
}
