using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Separate owned phase after all four original fixture oracles have finished.
// This qualifies one-contributor, two-selected, zero-selected-brain receipts.
// It does not advance a frontier, wire automation or evaluate a model.
internal static class GroupTerminalReceiptRuntimeProof
{
    internal const string Success = "PASS owned terminal receipt actual SQL insert readback immutable24 columns expiry postflush rollback witness only clock rollback original and later lease replay nonce collision original effects unchanged no cursor or model";
    private static readonly string[] Columns = ["TenantId", "CompanyId", "BindingId", "BatchId", "OperationId", "ManifestVersion", "Manifest", "ManifestSha256",
        "AfterSequence", "ThroughSequence", "RawRevisionCount", "SelectedMessageCount", "ContributorCount", "NoteCount", "ClaimOperationId", "ClaimOwnerId",
        "ClaimEpoch", "ServiceId", "CredentialEpoch", "GrantVersion", "SourceVersion", "DeletionGeneration", "AccountVersion", "CommittedAtUtc"];

    internal static async Task RunAsync(GroupScope scope, Guid allocationOperation, GroupExtractionWorkerBinding worker,
        DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var evidence = new Evidence(scope, clock);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new FlushProbe(evidence), new RollbackProbe(evidence)).Options);
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == allocationOperation, token);
        var work = await db.GroupWorkCommitReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        if (work.SelectedMessageCount != 2 || work.NoteCount is not (0 or 1 or 4) || work.DependencyManifestVersion != 1
            || work.DependencyManifest is not { Length: 274 } || work.DependencyManifest[161] != 0
            || allocation.AfterSequence != 0 || allocation.RawRevisionCount is not (2 or 500)) throw new InvalidOperationException();
        var originalGraph = await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, work.OperationId, token);
        var originalRaw = await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, work.OperationId, allocation.RawRevisionCount, token);
        var beforeClaims = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id).OrderBy(x => x.Epoch).ToArrayAsync(token);
        if (beforeClaims.Length is not (2 or 3) || await RowsAsync() != 0 || await FrontierRowsAsync() != 0) throw new InvalidOperationException();
        var claim = await AcquireAsync(beforeClaims[^1].ExpiresAtUtc);
        var keys = new GroupNoteRuntimeProof.CountedKeys(db, scope);
        var sources = new GroupBatchSourceReader(db, worker, clock, keys, new());
        var brain = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        var store = new GroupBatchTerminalStore(db, worker, clock, sources, brain);
        var terminalOperation = Guid.NewGuid();
        evidence.Handle = claim; evidence.Operation = terminalOperation; evidence.Armed = true;
        await DeniedAsync(() => store.CommitAsync(claim, terminalOperation, token));
        evidence.Armed = false;
        if (!evidence.Staged || !evidence.Flushed || !evidence.RolledBack || !evidence.WitnessSaved
            || evidence.LockChecks < 2 || await RowsAsync() != 0) throw new InvalidOperationException();
        var retired = await StateAsync();
        if (retired.Epoch != claim.Receipt.Epoch || retired.ExpiryObservedAtUtc != claim.Receipt.ExpiresAtUtc) throw new InvalidOperationException();
        clock.Current = claim.Receipt.IssuedAtUtc;
        await DeniedAsync(() => store.CommitAsync(claim, terminalOperation, token));
        await OriginalAsync();
        var committedClaim = await AcquireAsync(claim.Receipt.ExpiresAtUtc);
        var committed = await store.CommitAsync(committedClaim, terminalOperation, token);
        var written = await db.GroupBatchTerminalReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        var originalAcquisition = await db.GroupBatchClaimReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id
            && x.OperationId == committedClaim.Receipt.OperationId, token);
        var fingerprint = RequireValue(written, allocation, work, originalAcquisition, terminalOperation);
        if (committed.WasAlreadyCommitted || committed.ManifestSha256 != Convert.ToHexString(written.ManifestSha256)
            || committed.CommittedAtUtc != clock.Current || written.CommittedAtUtc != clock.Current || await RowsAsync() != 1) throw new InvalidOperationException();
        var replay = await store.CommitAsync(committedClaim, terminalOperation, token);
        if (!replay.WasAlreadyCommitted || committed != replay with { WasAlreadyCommitted = false }) throw new InvalidOperationException();
        var later = await AcquireAsync(committedClaim.Receipt.ExpiresAtUtc);
        var laterReplay = await store.CommitAsync(later, terminalOperation, token);
        if (!laterReplay.WasAlreadyCommitted || committed != laterReplay with { WasAlreadyCommitted = false }) throw new InvalidOperationException();
        var collision = false;
        try { await store.CommitAsync(later, Guid.NewGuid(), token); }
        catch (InvalidOperationException error) when (error.Message == "Group terminal commit is not available." && error.InnerException is null) { collision = true; }
        if (!collision) throw new InvalidOperationException();
        foreach (var column in Columns)
        {
            var refused = false;
            try
            {
                // Identifiers come only from the private fixed 24-column list.
                var updateSql = "UPDATE aioffice.GroupBatchTerminalReceipts SET [" + column + "]=[" + column + "] "
                    + "WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND BatchId=@batch;";
                await db.Database.ExecuteSqlRawAsync(updateSql,
                    new object[] { new SqlParameter("@tenant", scope.TenantId), new SqlParameter("@company", scope.CompanyId),
                        new SqlParameter("@binding", scope.SourceBindingId), new SqlParameter("@batch", allocation.Id) }, token);
            }
            catch (SqlException error) when (error.Number == 229) { refused = true; }
            if (!refused) throw new InvalidOperationException();
        }
        var finalRow = await db.GroupBatchTerminalReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        if (RequireValue(finalRow, allocation, work, originalAcquisition, terminalOperation) != fingerprint
            || await RowsAsync() != 1 || keys.Reads != 0 || keys.Writes != 0) throw new InvalidOperationException();
        await OriginalAsync();
        var afterClaims = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id).OrderBy(x => x.Epoch).ToArrayAsync(token);
        if (afterClaims.Length != beforeClaims.Length + 3
            || !JsonSerializer.SerializeToUtf8Bytes(beforeClaims).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(afterClaims[..beforeClaims.Length])))
            throw new InvalidOperationException();
        Console.WriteLine(Success);

        async Task<GroupBatchClaimHandle> AcquireAsync(DateTimeOffset previousExpiry)
        {
            clock.Current = previousExpiry.AddTicks(1);
            var result = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, allocation.Id, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token)
                ?? throw new InvalidOperationException();
            return !result.WasAlreadyClaimed && result.CurrentHandle is { } handle ? handle : throw new InvalidOperationException();
        }
        Task<int> RowsAsync() => TerminalRowsAsync(db, scope, token);
        Task<int> FrontierRowsAsync() => db.GroupTerminalFrontierStates.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        Task<GroupBatchClaimStateRecord> StateAsync() => db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        async Task OriginalAsync()
        {
            if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null || db.ChangeTracker.Entries<GroupBatchTerminalReceiptRecord>().Any()
                || await FrontierRowsAsync() != 0 || await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, work.OperationId, token) != originalGraph
                || await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, work.OperationId, allocation.RawRevisionCount, token) != originalRaw)
                throw new InvalidOperationException();
        }
    }

    internal static string RequireValue(GroupBatchTerminalReceiptRecord row, GroupBatchAllocationRecord allocation,
        GroupWorkCommitReceiptRecord work, GroupBatchClaimReceiptRecord claim, Guid operation)
    {
        var bytes = row.Manifest;
        if (operation == Guid.Empty || row.TenantId != allocation.TenantId || row.CompanyId != allocation.CompanyId || row.BindingId != allocation.BindingId
            || row.BatchId != allocation.Id || row.OperationId != operation || row.ManifestVersion != 1 || bytes is not { Length: 257 }
            || row.ManifestSha256 is not { Length: 32 } || !bytes.AsSpan(0, 8).SequenceEqual("AIOGTRM1"u8)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), row.ManifestSha256)
            || row.AfterSequence != allocation.AfterSequence || row.ThroughSequence != allocation.AllocatedThroughSequence
            || work.TenantId != row.TenantId || work.CompanyId != row.CompanyId || work.BindingId != row.BindingId || work.BatchId != row.BatchId || work.OperationId == Guid.Empty
            || row.RawRevisionCount != allocation.RawRevisionCount || row.SelectedMessageCount != work.SelectedMessageCount
            || row.ContributorCount != 1 || row.NoteCount != work.NoteCount || row.CommittedAtUtc.Offset != TimeSpan.Zero
            || claim.TenantId != row.TenantId || claim.CompanyId != row.CompanyId || claim.BindingId != row.BindingId || claim.BatchId != row.BatchId
            || row.ClaimOperationId != claim.OperationId || row.ClaimOwnerId != claim.OwnerId || row.ClaimEpoch != claim.Epoch
            || row.ServiceId != claim.ServiceId || row.CredentialEpoch != claim.CredentialEpoch || row.GrantVersion != claim.GrantVersion
            || row.SourceVersion != claim.SourceVersion || row.DeletionGeneration != claim.DeletionGeneration || row.AccountVersion != claim.AccountVersion
            || row.CommittedAtUtc < claim.IssuedAtUtc || row.CommittedAtUtc >= claim.ExpiresAtUtc || row.CommittedAtUtc < work.CommittedAtUtc
            || row.CommittedAtUtc < allocation.AllocatedAtUtc)
            throw new InvalidOperationException();
        var ids = new[] { row.TenantId, row.CompanyId, row.BindingId, row.BatchId, allocation.OperationId, operation };
        for (var index = 0; index < ids.Length; index++)
            if (ids[index] == Guid.Empty || new Guid(bytes.AsSpan(8 + index * 16, 16)) != ids[index]) throw new InvalidOperationException();
        if (BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(104, 8)) != row.AfterSequence
            || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(112, 8)) != row.ThroughSequence
            || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(120, 8)) != allocation.ObservedCommittedThroughSequence
            || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(128, 8)) != allocation.AllocatedAtUtc.UtcTicks
            || bytes[136] != (allocation.IsHistoricalBackfill ? 1 : 0) || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(137, 2)) != row.RawRevisionCount
            || bytes[139] != row.SelectedMessageCount || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(140, 4)) != row.NoteCount
            || bytes.AsSpan(144, 32).IndexOfAnyExcept((byte)0) < 0 || bytes[176] != 1 || new Guid(bytes.AsSpan(177, 16)) != work.OperationId
            || bytes.AsSpan(193, 32).IndexOfAnyExcept((byte)0) < 0 || bytes.AsSpan(225, 32).IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidOperationException();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row)));
    }

    private static Task<int> TerminalRowsAsync(PlatformDbContext db, GroupScope scope, CancellationToken token) => db.GroupBatchTerminalReceipts.AsNoTracking()
        .CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
    private static async Task DeniedAsync(Func<Task<GroupBatchTerminalCommitResult>> action)
    { var denied = false; try { await action(); } catch (UnauthorizedAccessException) { denied = true; } if (!denied) throw new InvalidOperationException(); }

    private sealed class Evidence(GroupScope scope, GroupNoteRuntimeProof.OwnedClock clock)
    {
        internal GroupScope Scope { get; } = scope;
        internal GroupBatchClaimHandle Handle = null!;
        internal Guid Operation;
        internal bool Armed, Staged, Flushed, RolledBack, WitnessSaved;
        internal int LockChecks;
        internal void Expire() => clock.Current = Handle.Receipt.ExpiresAtUtc;
        internal async Task RequireLockAsync(DbTransaction transaction, CancellationToken token)
        {
            if (transaction.IsolationLevel != IsolationLevel.Serializable || transaction.Connection is null) throw new InvalidOperationException();
            await using var command = transaction.Connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 5;
            command.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{Scope.TenantId:N}/{Scope.CompanyId:N}/{Scope.SourceBindingId:N}"; command.Parameters.Add(parameter);
            if (!string.Equals((string?)await command.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
            LockChecks++;
        }
    }
    private sealed class FlushProbe(Evidence evidence) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (!evidence.Armed || data.Context is not PlatformDbContext db) return ValueTask.FromResult(result);
            var changed = db.ChangeTracker.Entries().Where(x => x.State is not (EntityState.Unchanged or EntityState.Detached)).ToArray();
            if (!evidence.Flushed)
            {
                if (changed.Length != 1 || changed[0].State != EntityState.Added || changed[0].Entity is not GroupBatchTerminalReceiptRecord receipt
                    || receipt.OperationId != evidence.Operation || receipt.BatchId != evidence.Handle.Receipt.BatchId) throw new InvalidOperationException();
                evidence.Staged = true;
            }
            else
            {
                if (!evidence.RolledBack || db.ChangeTracker.Entries<GroupBatchTerminalReceiptRecord>().Any() || changed.Length != 1
                    || changed[0].State != EntityState.Modified || changed[0].Entity is not GroupBatchClaimStateRecord state
                    || state.BatchId != evidence.Handle.Receipt.BatchId || state.Epoch != evidence.Handle.Receipt.Epoch
                    || state.ExpiryObservedAtUtc != evidence.Handle.Receipt.ExpiresAtUtc
                    || changed[0].Properties.Count(x => x.IsModified) != 1
                    || !changed[0].Property(nameof(GroupBatchClaimStateRecord.ExpiryObservedAtUtc)).IsModified) throw new InvalidOperationException();
            }
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (!evidence.Armed || data.Context is not PlatformDbContext db) return result;
            if (!evidence.Flushed)
            {
                if (!evidence.Staged || await TerminalRowsAsync(db, evidence.Scope, token) != 1) throw new InvalidOperationException();
                evidence.Flushed = true; evidence.Expire();
            }
            else
            {
                if (!evidence.RolledBack || await TerminalRowsAsync(db, evidence.Scope, token) != 0) throw new InvalidOperationException();
                evidence.WitnessSaved = true;
            }
            return result;
        }
    }
    private sealed class RollbackProbe(Evidence evidence) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken token = default)
        { if (evidence.Armed) await evidence.RequireLockAsync(transaction, token); return result; }
        public override async Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData data, CancellationToken token = default)
        {
            if (!evidence.Armed || !evidence.Flushed || data.Context is not PlatformDbContext db) return;
            await evidence.RequireLockAsync(transaction, token);
            if (await TerminalRowsAsync(db, evidence.Scope, token) != 0) throw new InvalidOperationException();
            evidence.RolledBack = true;
        }
    }
}
