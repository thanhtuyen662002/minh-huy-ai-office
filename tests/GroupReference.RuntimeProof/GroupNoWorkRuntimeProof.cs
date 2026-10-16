using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static class GroupNoWorkRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        var progress = new ProofProgress();
        try { await RunOwnedAsync(mode, scope, operation, worker, options, progress, token); }
        catch
        {
            // Fixed test-only tokens: never exception data, SQL, source, IDs or keys.
            Console.WriteLine(progress.FailureLine());
            throw;
        }
    }

    private static async Task RunOwnedAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, ProofProgress progress, CancellationToken token)
    {
        var clock = new OwnedClock(TimeProvider.System.GetUtcNow());
        var evidence = new EffectEvidence(scope, clock, progress);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new FlushProbe(evidence), new RollbackProbe(evidence)).Options);
        var batch = await db.GroupBatchAllocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation)
            .Select(x => x.Id).SingleAsync(token);
        var original = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .OrderByDescending(x => x.Epoch).FirstAsync(token);
        var claims = new GroupBatchClaimStore(db, worker, clock);
        progress.Phase = ProofPhase.Claim;
        GroupBatchClaimResult claim;
        if (mode is "no-work-expiry" or "no-work-commit")
        {
            var expected = mode == "no-work-expiry" ? 4 : 5;
            var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, token);
            if (original.Epoch != expected || state.Epoch != expected || state.ExpiryObservedAtUtc != original.ExpiresAtUtc)
                throw new InvalidOperationException();
            if (clock.Current <= original.ExpiresAtUtc) clock.Current = original.ExpiresAtUtc.AddTicks(1);
            claim = await claims.TryAcquireAsync(scope, batch, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token)
                ?? throw new InvalidOperationException();
            if (claim.WasAlreadyClaimed || claim.Receipt.Epoch != expected + 1 || claim.CurrentHandle is null) throw new InvalidOperationException();
        }
        else
        {
            if (mode != "no-work-mars" || original.Epoch != 6) throw new InvalidOperationException();
            if (clock.Current < original.IssuedAtUtc) clock.Current = original.IssuedAtUtc;
            claim = await claims.TryAcquireAsync(scope, batch, original.OwnerId, original.OperationId,
                TimeSpan.FromTicks(original.RequestedLifetimeTicks), token) ?? throw new InvalidOperationException();
            if (!claim.WasAlreadyClaimed || claim.CurrentHandle is null) throw new InvalidOperationException();
        }
        var handle = claim.CurrentHandle!; var keys = new CountedKeys(db, scope);
        var sourceReader = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brainReader = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var sourceId = await db.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .Select(x => x.MessageId).Distinct().OrderBy(x => x).FirstAsync(token);
        progress.Phase = ProofPhase.Source;
        var source = await sourceReader.ReadAsync(handle, [sourceId], token);
        progress.Phase = ProofPhase.Preparation;
        var preparation = GroupBatchSourcePreparation.Create(source);
        if (preparation.Candidates.Count != 1 || preparation.Receipts.Any(x => x.HasUnsupportedMedia) || source.HasCoverageGap)
            throw new InvalidOperationException();
        // Fixed synthetic classifier output, not an actual model evaluation.
        var proposal = GroupGroundedWorkProposal.Parse(preparation, JsonSerializer.Serialize(new
        {
            version = GroupGroundedWorkProposal.FormatVersion,
            notes = Array.Empty<object>(),
            source_dispositions = preparation.Candidates.Select(x => new { message_id = x.MessageId.ToString("D"), revision = x.Revision, disposition = "no_work" })
        }));
        var requestId = await db.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.Id).SingleAsync(token);
        var glossaryId = await db.GroupGlossaryEntries.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.Id).SingleAsync(token);
        progress.Phase = ProofPhase.Brain;
        var dependencies = await brainReader.ReadAsync(handle, [requestId], [glossaryId], token);
        progress.Phase = ProofPhase.Dependencies;
        if (keys.Reads != 2 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        var effectOperation = Guid.NewGuid();
        if (mode == "no-work-mars")
        {
            var noOpen = new ForbiddenOpen();
            var connection = new SqlConnectionStringBuilder(db.Database.GetConnectionString()) { MultipleActiveResultSets = true };
            await using var mars = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlServer(connection.ConnectionString).AddInterceptors(noOpen).Options);
            var store = new GroupNoWorkCommitStore(mars, worker, clock,
                new(mars, worker, clock, keys, new()), new(mars, worker, clock, keys, new()));
            await RefuseAsync(() => store.CommitAsync(proposal, dependencies, effectOperation, token));
            if (noOpen.Attempts != 0 || keys.Reads != 2 || mars.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned NoWork runtime MARS refuses before connection keys or effects"); return;
        }
        var consumer = new GroupNoWorkCommitStore(db, worker, clock, sourceReader, brainReader);
        progress.Phase = ProofPhase.Commit;
        if (mode == "no-work-expiry")
        {
            evidence.Operation = effectOperation; evidence.Expires = handle.Receipt.ExpiresAtUtc; evidence.Armed = true;
            var denied = false;
            try { await consumer.CommitAsync(proposal, dependencies, effectOperation, token); }
            catch (UnauthorizedAccessException) { denied = true; }
            progress.Phase = ProofPhase.ExpiryChecks;
            if (!denied || !evidence.Flushed || !evidence.RolledBack || evidence.SavepointChecks < 1
                || keys.Reads != 2 || db.ChangeTracker.Entries<GroupWorkCommitReceiptRecord>().Any()
                || db.ChangeTracker.Entries<GroupWorkSourceDispositionRecord>().Any() || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, token);
            if (state.Epoch != 5 || state.ExpiryObservedAtUtc != handle.Receipt.ExpiresAtUtc || await TargetRowsAsync(db, effectOperation, token) != 0)
                throw new InvalidOperationException();
            var before = clock.Current; clock.Current = handle.Receipt.IssuedAtUtc;
            progress.Phase = ProofPhase.ClockRollback;
            denied = false;
            try { await consumer.CommitAsync(proposal, dependencies, effectOperation, token); }
            catch (UnauthorizedAccessException) { denied = true; }
            clock.Current = before;
            if (!denied || keys.Reads != 2 || db.ChangeTracker.HasChanges() || await TargetRowsAsync(db, effectOperation, token) != 0)
                throw new InvalidOperationException();
            Console.WriteLine("PASS owned NoWork runtime flushed two SQL effects rollback with source lock retained clean detach witness only and clock rollback denial"); return;
        }
        var committed = await consumer.CommitAsync(proposal, dependencies, effectOperation, token);
        progress.Phase = ProofPhase.Replay;
        var replay = await consumer.CommitAsync(proposal, dependencies, effectOperation, token);
        if (committed.WasAlreadyCommitted || !replay.WasAlreadyCommitted || committed != replay with { WasAlreadyCommitted = false }
            || committed.SelectedMessageCount != 1 || committed.Scope != scope || committed.BatchId != batch
            || await TargetRowsAsync(db, effectOperation, token) != 2) throw new InvalidOperationException();
        await RefuseAsync(() => consumer.CommitAsync(proposal, dependencies, Guid.NewGuid(), token));
        progress.Phase = ProofPhase.DuplicateChecks;
        if (keys.Reads != 2 || db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null
            || await TargetRowsAsync(db, effectOperation, token) != 2) throw new InvalidOperationException();
        Console.WriteLine("PASS owned NoWork runtime actual atomic SQL receipt disposition exact original replay and new nonce duplicate refusal");
    }

    private static async Task RefuseAsync(Func<Task<GroupNoWorkCommitResult>> action)
    {
        var refused = false;
        try { await action(); }
        catch (InvalidOperationException error) when (error.Message == "Group work commit is unavailable." && error.InnerException is null) { refused = true; }
        if (!refused) throw new InvalidOperationException();
    }
    private static async Task<int> TargetRowsAsync(PlatformDbContext db, Guid operation, CancellationToken token) =>
        await db.GroupWorkCommitReceipts.AsNoTracking().CountAsync(x => x.OperationId == operation, token)
        + await db.GroupWorkSourceDispositions.AsNoTracking().CountAsync(x => x.OperationId == operation, token);
    private sealed class OwnedClock(DateTimeOffset now) : TimeProvider
    { internal DateTimeOffset Current = now; public override DateTimeOffset GetUtcNow() => Current; }
    internal enum ProofPhase { Setup, Claim, Source, Preparation, Brain, Dependencies, Commit, Savepoint, Flush, Rollback, ExpiryChecks, ClockRollback, Replay, DuplicateChecks }
    internal sealed class ProofProgress
    {
        internal ProofPhase Phase;
        internal string FailureLine() => Enum.IsDefined(Phase)
            ? "FAIL owned NoWork runtime phase-" + Phase.ToString().ToLowerInvariant()
            : "FAIL owned NoWork runtime phase-unknown";
    }
    private sealed class EffectEvidence(GroupScope scope, OwnedClock clock, ProofProgress progress)
    {
        internal bool Armed, Flushed, RolledBack;
        internal int SavepointChecks;
        internal Guid Operation;
        internal DateTimeOffset Expires;
        internal void Observe(ProofPhase phase) => progress.Phase = phase;
        internal async Task RequireSourceLockAsync(DbTransaction transaction, CancellationToken token)
        {
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 5;
            command.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}"; command.Parameters.Add(parameter);
            if (!string.Equals((string?)await command.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
        }
        internal void Advance() => clock.Current = Expires;
    }
    private sealed class FlushProbe(EffectEvidence evidence) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken token = default)
        {
            if (!evidence.Armed || evidence.Flushed || eventData.Context is not PlatformDbContext db) return result;
            if (!db.ChangeTracker.Entries<GroupWorkCommitReceiptRecord>().Any(x => x.Entity.OperationId == evidence.Operation)) return result;
            evidence.Observe(ProofPhase.Flush);
            if (await TargetRowsAsync(db, evidence.Operation, token) != 2) throw new InvalidOperationException();
            evidence.Flushed = true; evidence.Advance(); return result;
        }
    }
    private sealed class RollbackProbe(EffectEvidence evidence) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken token = default)
        {
            if (evidence.Armed) { evidence.Observe(ProofPhase.Savepoint); await evidence.RequireSourceLockAsync(transaction, token); evidence.SavepointChecks++; }
            return result;
        }
        public override async Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken token = default)
        {
            if (!evidence.Armed || !evidence.Flushed || eventData.Context is not PlatformDbContext db) return;
            evidence.Observe(ProofPhase.Rollback);
            await evidence.RequireSourceLockAsync(transaction, token);
            if (await TargetRowsAsync(db, evidence.Operation, token) != 0) throw new InvalidOperationException();
            evidence.RolledBack = true;
        }
    }
    private sealed class ForbiddenOpen : DbConnectionInterceptor
    {
        internal int Attempts;
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken token = default)
        { Attempts++; throw new InvalidOperationException(); }
    }
    private sealed class CountedKeys : IGroupSourceKeyProvider
    {
        private readonly PlatformDbContext db; private readonly GroupScope scope; private readonly ConfiguredGroupSourceKeyProvider configured;
        internal int Reads;
        internal CountedKeys(PlatformDbContext db, GroupScope scope)
        {
            this.db = db; this.scope = scope;
            configured = new(new CompositeSecretResolver([new EnvironmentVariableSecretResolver()]),
                [new(scope, "owned-native-source-v1", SecretReference.Parse("secretref://env/AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY"), true)]);
        }
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken token = default) => throw new InvalidOperationException();
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string id, CancellationToken token = default)
        {
            if (source != scope || id != "owned-native-source-v1" || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
                throw new InvalidOperationException();
            Reads++; return await configured.ResolveReadAsync(source, id, token);
        }
    }
}
