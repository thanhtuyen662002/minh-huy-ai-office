using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupModelAttemptResultRuntimeProof
{
    internal const string SharedExpiryLine = "PASS owned attempt result first expiry actual SQL original epoch replay NULL witness Shared claim observation disposed read session fresh Exclusive one witness original timestamp detached commit clock rewind denied no result key effects provider or model";

    private static async Task RunSharedExpiryAsync(GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options,
        ResultDiagnostic diagnostic, CancellationToken token)
    {
        // A separate fresh fixture already has exactly one legitimate accepted
        // epoch1 result. Replay it before expiry; never create a replacement or
        // reset a witness written by another qualifying action.
        await using var original = new PlatformDbContext(options);
        var allocation = await original.GroupBatchAllocations.AsNoTracking().SingleAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.OperationId == operation, token);
        var receipt = await original.GroupBatchClaimReceipts.AsNoTracking().SingleAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.BatchId == allocation.Id, token);
        var state = await StateAsync(original);
        if (receipt.Epoch != 1 || receipt.RequestedLifetimeTicks != TimeSpan.FromMinutes(2).Ticks
            || state.ExpiryObservedAtUtc is not null || state.Epoch != receipt.Epoch
            || state.OperationId != receipt.OperationId || state.OwnerId != receipt.OwnerId
            || state.IssuedAtUtc != receipt.IssuedAtUtc || state.ExpiresAtUtc != receipt.ExpiresAtUtc)
            throw new InvalidOperationException();
        var restore = receipt.IssuedAtUtc;
        var observed = receipt.ExpiresAtUtc;
        var planRow = await original.GroupAutomaticInputPlans.AsNoTracking().SingleAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.BatchId == allocation.Id, token);
        var start = await original.GroupModelAttemptStarts.AsNoTracking().SingleAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.BatchId == allocation.Id, token);
        var resultTimes = await original.Set<GroupModelAttemptResultRecord>().AsNoTracking().Where(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.BatchId == allocation.Id).Select(x => new
            {
                x.RequestId,
                x.PlanSha256,
                x.StartedAtUtc,
                x.ObservedAtUtc,
                x.PersistedAtUtc
            }).SingleAsync(token);
        if (restore.Offset != TimeSpan.Zero || observed <= restore || allocation.AllocatedAtUtc > restore
            || planRow.CreatedAtUtc > restore || start.StartedAtUtc > restore || resultTimes.StartedAtUtc > restore
            || resultTimes.ObservedAtUtc > restore || resultTimes.PersistedAtUtc > restore
            || start.ClaimEpoch != 1 || start.ClaimOperationId != receipt.OperationId
            || resultTimes.RequestId != start.RequestId || resultTimes.PlanSha256 != planRow.PlanSha256)
            throw new InvalidOperationException();
        var before = GroupAutomaticDependencyRuntimeProof.StateFingerprint(state);
        var clock = new GroupNoteRuntimeProof.OwnedClock(restore);
        var evidence = new SharedExpiryEvidence(scope, allocation.Id, clock, restore, observed);
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new SharedExpiryReadProbe(evidence), new SharedExpirySaveProbe(evidence),
                new SharedExpiryTransactionProbe(evidence), new SharedExpiryConnectionProbe(evidence)).Options);
        diagnostic.Phase = ResultPhase.SharedExpiryReplay;
        var replay = await new GroupBatchClaimStore(database, worker, clock).TryAcquireAsync(scope, allocation.Id,
            receipt.OwnerId, receipt.OperationId, TimeSpan.FromTicks(receipt.RequestedLifetimeTicks), token)
            ?? throw new InvalidOperationException();
        var handle = replay.CurrentHandle ?? throw new InvalidOperationException();
        if (!replay.WasAlreadyClaimed || replay.Receipt.Epoch != 1 || replay.Receipt.OperationId != receipt.OperationId
            || replay.Receipt.OwnerId != receipt.OwnerId || replay.Receipt.BatchId != allocation.Id
            || replay.Receipt.IssuedAtUtc != restore || replay.Receipt.ExpiresAtUtc != observed)
            throw new InvalidOperationException();
        diagnostic.Phase = ResultPhase.SharedExpiryPreparation;
        var input = await PrepareOriginalAsync();
        if (input.Plan.Sha256 != planRow.PlanSha256 || (await StateAsync(database)).ExpiryObservedAtUtc is not null
            || await database.GroupBatchClaimReceipts.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token) != 1)
            throw new InvalidOperationException();
        RequireClean(database);
        var keyReads = input.Keys.Reads; var keyWrites = input.Keys.Writes;
        evidence.Arm(database);
        clock.Current = observed;
        diagnostic.Phase = ResultPhase.SharedExpiryRead;
        try { await RequireShippingExpiryDenialAsync(() => input.Results.ReadOriginalAsync(input.Plan, input.Chunk, token)); }
        finally { evidence.Disarm(); }
        diagnostic.Phase = ResultPhase.SharedExpiryEvidence;
        evidence.RequireComplete(database);
        if (input.Keys.Reads != keyReads || input.Keys.Writes != keyWrites || keyReads != 1 || keyWrites != 0)
            throw new InvalidOperationException();
        diagnostic.Phase = ResultPhase.SharedExpiryReadback;
        await using var readback = new PlatformDbContext(options);
        var retired = await StateAsync(readback);
        if (retired.ExpiryObservedAtUtc != observed) throw new InvalidOperationException();
        // Detached comparison only. Never write/reset the physical witness.
        retired.ExpiryObservedAtUtc = null;
        if (GroupAutomaticDependencyRuntimeProof.StateFingerprint(retired) != before) throw new InvalidOperationException();
        diagnostic.Phase = ResultPhase.SharedExpiryRewind;
        clock.Current = restore;
        await RequireShippingExpiryDenialAsync(() => input.Results.ReadOriginalAsync(input.Plan, input.Chunk, token));
        if ((await StateAsync(readback)).ExpiryObservedAtUtc != observed || input.Keys.Reads != 1 || input.Keys.Writes != 0)
            throw new InvalidOperationException();
        RequireClean(database); RequireClean(readback);
        Console.WriteLine(SharedExpiryLine);

        Task<GroupBatchClaimStateRecord> StateAsync(PlatformDbContext db) => db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.BatchId == allocation.Id, token);
        async Task<Prepared> PrepareOriginalAsync()
        {
            var keys = new GroupNoteRuntimeProof.CountedKeys(database, scope);
            var sources = new GroupBatchSourceReader(database, worker, clock, keys, new());
            var brain = new GroupBrainCurrentReader(database, worker, clock, keys, new());
            var current = await new GroupBatchClaimStore(database, worker, clock).ReadCurrentAllocationAsync(handle, token);
            var ids = current.Revisions.Select(x => x.Metadata.MessageId).ToArray();
            var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, ids, token));
            var history = await brain.ReadAsync(handle, [], [], token);
            if (ids.Length != 2 || preparation.Candidates.Count != 1 || preparation.Receipts.Count != 2
                || history.Items.Count != 0 || keys.Reads != 1 || keys.Writes != 0) throw new InvalidOperationException();
            var plan = GroupAutomaticInputPlan.Create(preparation, history, Profile, Model, 64000, [], token);
            var store = new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain);
            var persisted = await store.PersistAsync(plan, preparation, history, token);
            if (!persisted.Encode().AsSpan().SequenceEqual(plan.Encode())) throw new InvalidOperationException();
            var chunks = persisted.RestoreChunks(preparation, history, Profile, [], token);
            if (chunks.Count != 1 || !chunks[0].Original.RequiresModel) throw new InvalidOperationException();
            return new(persisted, chunks[0], new(database, worker, clock, sources, brain),
                new(database, worker, clock, sources, brain, keys, new()), keys, sources, ids);
        }
    }

    internal static async Task RequireShippingExpiryDenialAsync(Func<Task> call)
    {
        try { await call(); }
        catch (UnauthorizedAccessException error) when (error.GetType() == typeof(UnauthorizedAccessException)
            && error.Message == "Group service access is not available.")
        { return; }
        throw new InvalidOperationException();
    }

    internal static void RequireFirstExpiryWitnessChange(PlatformDbContext db, GroupScope scope, Guid batch, DateTimeOffset observed)
    {
        db.ChangeTracker.DetectChanges();
        var changes = db.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (changes.Length != 1 || changes[0].State != EntityState.Modified || changes[0].Entity is not GroupBatchClaimStateRecord row
            || row.TenantId != scope.TenantId || row.CompanyId != scope.CompanyId || row.BindingId != scope.SourceBindingId
            || row.BatchId != batch || row.Epoch != 1 || row.ExpiryObservedAtUtc != observed
            || changes[0].Property(nameof(row.ExpiryObservedAtUtc)).OriginalValue is not null
            || !changes[0].Properties.Where(x => x.IsModified).Select(x => x.Metadata.Name).SequenceEqual([nameof(row.ExpiryObservedAtUtc)]))
            throw new InvalidOperationException();
    }

    internal sealed class SharedExpiryEvidence(GroupScope scope, Guid batch, GroupNoteRuntimeProof.OwnedClock clock,
        DateTimeOffset restore, DateTimeOffset observed)
    {
        private PlatformDbContext? expected;
        internal bool Armed { get; private set; }
        private DbTransaction? shared, exclusive;
        private int begins, sharedReads, exclusiveReads, sharedClosed, exclusiveClosed, saves, saved, committing, committed;
        private bool disposed, baselineRestored;
        internal void Arm(PlatformDbContext db)
        {
            scope.Validate();
            if (expected is not null || batch == Guid.Empty || restore.Offset != TimeSpan.Zero
                || observed.Offset != TimeSpan.Zero || observed <= restore) throw new InvalidOperationException();
            RequireClean(db); expected = db; Armed = true;
        }
        internal void Disarm() => Armed = false;
        internal PlatformDbContext Context(DbContext? context)
        {
            if (expected is null || context is not PlatformDbContext db || !ReferenceEquals(db, expected))
                throw new InvalidOperationException();
            return db;
        }
        internal async Task BeforeBeginAsync(DbConnection connection, TransactionStartingEventData data,
            InterceptionResult<DbTransaction> result, CancellationToken token)
        {
            if (!Armed) return;
            var db = Context(data.Context);
            if (result.HasResult || !ReferenceEquals(connection, db.Database.GetDbConnection())
                || connection.State != ConnectionState.Open || db.Database.CurrentTransaction is not null
                || db.ChangeTracker.HasChanges() || data.IsolationLevel != IsolationLevel.Serializable)
                throw new InvalidOperationException();
            if (begins == 0)
            {
                if (clock.Current != observed) throw new InvalidOperationException();
            }
            else if (begins == 1)
            {
                if (shared is null || shared.Connection is not null || sharedReads != 1 || sharedClosed != 1
                    || saves != 0 || committing != 0 || clock.Current != observed) throw new InvalidOperationException();
                await using var baseline = connection.CreateCommand(); baseline.CommandTimeout = 5;
                baseline.CommandText = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id=@@SPID;";
                if (Convert.ToInt32(await baseline.ExecuteScalarAsync(token)) != 2) throw new InvalidOperationException();
                disposed = baselineRestored = true;
                clock.Current = restore;
            }
            else throw new InvalidOperationException();
            begins++;
        }
        internal void Started(DbConnection connection, TransactionEndEventData data, DbTransaction transaction)
        {
            if (!Armed) return;
            var db = Context(data.Context);
            if (!ReferenceEquals(connection, db.Database.GetDbConnection()) || !ReferenceEquals(transaction.Connection, connection)
                || transaction.IsolationLevel != IsolationLevel.Serializable) throw new InvalidOperationException();
            if (begins == 1 && shared is null) shared = transaction;
            else if (begins == 2 && disposed && exclusive is null && !ReferenceEquals(shared, transaction)) exclusive = transaction;
            else throw new InvalidOperationException();
        }
        internal async Task ObserveClaimAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token)
        {
            if (!Armed) return;
            var db = Context(data.Context);
            var transaction = RequireTransaction(db, command.Transaction);
            if (!ReferenceEquals(command.Connection, db.Database.GetDbConnection()) || result.HasResult)
                throw new InvalidOperationException();
            if (!command.CommandText.Contains("FROM [aioffice].[GroupBatchClaimStates]", StringComparison.Ordinal))
            {
                if (ReferenceEquals(transaction, shared) && (command.CommandText.Contains("GroupModelAttemptResults", StringComparison.Ordinal)
                    || IsWrite(command.CommandText))) throw new InvalidOperationException();
                return;
            }
            var values = command.Parameters.Cast<DbParameter>().Select(x => x.Value).OfType<Guid>().ToArray();
            if (values.Length != 4 || !values.ToHashSet().SetEquals([scope.TenantId, scope.CompanyId, scope.SourceBindingId, batch]))
                throw new InvalidOperationException();
            if (ReferenceEquals(transaction, shared))
            {
                if (clock.Current != observed || ++sharedReads != 1) throw new InvalidOperationException();
                await RequireModeAsync(transaction, "Shared", token);
            }
            else if (ReferenceEquals(transaction, exclusive))
            {
                if (!disposed || clock.Current != restore || ++exclusiveReads != 1) throw new InvalidOperationException();
                await RequireModeAsync(transaction, "Exclusive", token);
            }
            else throw new InvalidOperationException();
        }
        internal void NonQuery(DbCommand command, CommandEventData data)
        {
            if (!Armed) return;
            var transaction = RequireTransaction(Context(data.Context), command.Transaction);
            if (ReferenceEquals(transaction, shared) && IsWrite(command.CommandText)) throw new InvalidOperationException();
        }
        private static bool IsWrite(string text) => text.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) || text.Contains("DELETE", StringComparison.OrdinalIgnoreCase);
        private DbTransaction RequireTransaction(PlatformDbContext db, DbTransaction? transaction)
        {
            if (transaction is null || transaction.IsolationLevel != IsolationLevel.Serializable
                || !ReferenceEquals(transaction, db.Database.CurrentTransaction?.GetDbTransaction())
                || !ReferenceEquals(transaction.Connection, db.Database.GetDbConnection())
                || transaction.Connection?.State != ConnectionState.Open) throw new InvalidOperationException();
            return transaction;
        }
        private async Task RequireModeAsync(DbTransaction transaction, string expectedMode, CancellationToken token)
        {
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction; command.CommandTimeout = 10;
            command.CommandText = "SELECT APPLOCK_MODE('public',@resource,'Transaction');";
            var resource = command.CreateParameter(); resource.ParameterName = "@resource";
            resource.Value = $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}";
            command.Parameters.Add(resource);
            if (await command.ExecuteScalarAsync(token) is not string mode || mode != expectedMode) throw new InvalidOperationException();
        }
        internal async Task SavingAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token)
        {
            if (!Armed) return;
            var db = Context(data.Context); var transaction = RequireTransaction(db, db.Database.CurrentTransaction?.GetDbTransaction());
            if (result.HasResult || !ReferenceEquals(transaction, exclusive) || !disposed || exclusiveReads != 1
                || clock.Current != restore || ++saves != 1) throw new InvalidOperationException();
            RequireFirstExpiryWitnessChange(db, scope, batch, observed);
            await RequireModeAsync(transaction, "Exclusive", token);
        }
        internal async Task SavedAsync(SaveChangesCompletedEventData data, int count, CancellationToken token)
        {
            if (!Armed) return;
            var db = Context(data.Context); var transaction = RequireTransaction(db, db.Database.CurrentTransaction?.GetDbTransaction());
            if (!ReferenceEquals(transaction, exclusive) || saves != 1 || ++saved != 1 || count != 1)
                throw new InvalidOperationException();
            await RequireModeAsync(transaction, "Exclusive", token);
        }
        internal async Task CommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken token)
        {
            if (!Armed) return;
            var db = Context(data.Context); _ = RequireTransaction(db, transaction);
            if (result.IsSuppressed || !ReferenceEquals(transaction, exclusive) || saves != 1 || saved != 1
                || ++committing != 1 || db.ChangeTracker.HasChanges()
                || db.ChangeTracker.Entries<GroupBatchClaimStateRecord>().Any()) throw new InvalidOperationException();
            await RequireModeAsync(transaction, "Exclusive", token);
        }
        internal void Committed(DbTransaction transaction, TransactionEndEventData data)
        {
            if (!Armed) return;
            var db = Context(data.Context);
            // Do not query APPLOCK_MODE after commit: the owned lock has ended.
            if (!ReferenceEquals(transaction, exclusive) || committing != 1 || ++committed != 1
                || db.ChangeTracker.HasChanges() || db.ChangeTracker.Entries<GroupBatchClaimStateRecord>().Any())
                throw new InvalidOperationException();
        }
        internal void Closed(DbConnection connection, ConnectionEndEventData data)
        {
            if (!Armed) return;
            var db = Context(data.Context);
            if (!ReferenceEquals(connection, db.Database.GetDbConnection()) || connection.State != ConnectionState.Closed
                || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            if (begins == 1 && shared is not null && shared.Connection is null && sharedReads == 1 && ++sharedClosed == 1) return;
            if (begins == 2 && exclusive is not null && exclusive.Connection is null && committed == 1 && ++exclusiveClosed == 1) return;
            throw new InvalidOperationException();
        }
        internal void RequireComplete(PlatformDbContext db)
        {
            _ = Context(db); RequireClean(db);
            if (Armed || begins != 2 || sharedReads != 1 || exclusiveReads != 1 || sharedClosed != 1 || exclusiveClosed != 1
                || !disposed || !baselineRestored || saves != 1 || saved != 1 || committing != 1 || committed != 1
                || shared is null || exclusive is null || ReferenceEquals(shared, exclusive)
                || shared.Connection is not null || exclusive.Connection is not null || clock.Current != restore)
                throw new InvalidOperationException();
        }
    }

    internal sealed class SharedExpiryReadProbe(SharedExpiryEvidence evidence) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        { await evidence.ObserveClaimAsync(command, data, result, token); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken token = default)
        { evidence.NonQuery(command, data); return ValueTask.FromResult(result); }
    }
    internal sealed class SharedExpirySaveProbe(SharedExpiryEvidence evidence) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        { await evidence.SavingAsync(data, result, token); return result; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        { await evidence.SavedAsync(data, result, token); return result; }
    }
    internal sealed class SharedExpiryTransactionProbe(SharedExpiryEvidence evidence) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData data, InterceptionResult<DbTransaction> result, CancellationToken token = default)
        { await evidence.BeforeBeginAsync(connection, data, result, token); return result; }
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
            TransactionEndEventData data, DbTransaction result, CancellationToken token = default)
        { evidence.Started(connection, data, result); return ValueTask.FromResult(result); }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken token = default)
        { await evidence.CommittingAsync(transaction, data, result, token); return result; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        { evidence.Committed(transaction, data); return Task.CompletedTask; }
    }
    internal sealed class SharedExpiryConnectionProbe(SharedExpiryEvidence evidence) : DbConnectionInterceptor
    {
        public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData data)
        { evidence.Closed(connection, data); return Task.CompletedTask; }
    }
}
