using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Reads original metadata/cipher graphs under fresh current Extract authority.
// No expired handle/current verdict or today's mutable source/brain digest is
// manufactured. This reader does not own/commit a transaction, resolve keys,
// decrypt, write effects, retire claims, sign an advance or update a cursor.
internal sealed class GroupBatchHistoricalSqlReader(PlatformDbContext database,
    GroupExtractionWorkerBinding worker, TimeProvider clock)
{
    internal async Task<GroupBatchHistoricalTerminalEvidence> ReadLockedAsync(GroupExtractionAuthority authority,
        Guid batch, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(authority); token.ThrowIfCancellationRequested();
        worker.Validate(); var scope = authority.Source.Scope; scope.Validate();
        if (batch == Guid.Empty) throw Unavailable();
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId
            || authority.Principal.ServiceId != worker.ServiceId || authority.Principal.CredentialEpoch != worker.CredentialEpoch)
            throw GroupServiceDirectory.Denied();
        ValidateTransaction();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); token = deadline.Token;
        try
        {
            var directory = new GroupExtractionDirectory(database, worker);
            var permissions = new GroupBatchTerminalPermissionVerifier(database);
            await permissions.RequireSafeRuntimeAsync(token);
            await directory.RequireCurrentAsync(authority, token);
            await GroupSourceTransactionLock.RequireAsync(database, scope, token);
            await directory.RequireCurrentAsync(authority, token);
            var initial = UtcNow();
            var terminalMetadata = await TerminalRows(scope, batch).Select(x => new BlobMetadata(x.OperationId,
                EF.Functions.DataLength(x.Manifest), EF.Functions.DataLength(x.ManifestSha256))).ToArrayAsync(token);
            RequireBlobMetadata(terminalMetadata, 1, GroupBatchTerminalManifest.MinimumBytes, GroupBatchTerminalManifest.MaximumBytes);
            var terminalRows = await TerminalRows(scope, batch).ToArrayAsync(token);
            if (terminalRows.Length != 1 || terminalRows[0].OperationId != terminalMetadata[0].OperationId
                || terminalRows[0].Manifest.Length != terminalMetadata[0].Length
                || terminalRows[0].ManifestSha256.Length != terminalMetadata[0].DigestLength) throw Unavailable();
            var terminal = terminalRows[0];
            var allocation = await new GroupBatchAllocationStore(database, worker, GroupBatchTiming.InitialTuning, clock)
                .ReadAllocatedLockedAsync(scope, batch, initial, token);
            // Deletion generations retire earlier source evidence. Version,
            // credential/grant/account rotation alone does not erase a commit.
            if (allocation.DeletionGeneration != authority.Source.DeletionGeneration) throw GroupServiceDirectory.Denied();
            var workMetadata = await WorkRows(scope, batch).Select(x => new BlobMetadata(x.OperationId,
                EF.Functions.DataLength(x.DependencyManifest), EF.Functions.DataLength(x.ExpectedEffectSha256))).ToArrayAsync(token);
            RequireBlobMetadata(workMetadata, FrozenGroupBatch.MaximumMessages, GroupWorkDependencyManifest.MinimumBytes,
                GroupWorkDependencyManifest.MaximumBytes);
            var work = await WorkRows(scope, batch).ToArrayAsync(token);
            if (work.Length != workMetadata.Length || work.Where((row, index) => row.OperationId != workMetadata[index].OperationId
                || row.DependencyManifest is null || row.DependencyManifest.Length != workMetadata[index].Length
                || row.ExpectedEffectSha256?.Length != workMetadata[index].DigestLength).Any()) throw Unavailable();
            var selected = await SelectedRows(scope, batch).ToArrayAsync(token);
            var raw = await RawRows(scope, batch).ToArrayAsync(token);
            var heads = new List<GroupPendingRevisionMetadata>();
            foreach (var id in allocation.Revisions.Select(x => x.Metadata.MessageId).Distinct().Order())
            {
                var rows = await CutoffRows(scope, id, allocation.AllocatedThroughSequence).ToArrayAsync(token);
                if (rows.Length != 1) throw Unavailable(); heads.Add(rows[0]);
            }
            var coverage = GroupWholeBatchCoverage.Require(allocation, heads, work, selected, raw);
            var epochs = work.Select(x => x.ClaimEpoch).Append(terminal.ClaimEpoch).Distinct().Order().ToArray();
            if (epochs.Length is < 1 or > FrozenGroupBatch.MaximumMessages + 1 || epochs.Any(x => x <= 0)) throw Unavailable();
            var originals = await OriginalRows(scope, batch, epochs).ToArrayAsync(token);
            if (originals.Length != epochs.Length || originals.Select(x => x.Epoch).Distinct().Count() != epochs.Length) throw Unavailable();
            var effects = new List<GroupWorkEffectLedger>(work.Length);
            var effectReader = new GroupWholeBatchEffectReader(database);
            foreach (var row in work)
            {
                if (row.DeletionGeneration != authority.Source.DeletionGeneration) throw GroupServiceDirectory.Denied();
                var original = originals.Single(x => x.Epoch == row.ClaimEpoch);
                effects.Add(await effectReader.RequireLockedAsync(row, original,
                    selected.Where(x => x.OperationId == row.OperationId).ToArray(), token));
            }
            var ids = coverage.Manifests.SelectMany(x => x.Dependencies).Where(x => x.Kind == GroupBrainContentKind.RequestRevision)
                .Select(x => x.RecordId).Distinct().Order().ToArray();
            if (ids.Length > GroupBatchOwnInputPlan.MaximumOriginRows) throw Unavailable();
            var origins = ids.Length == 0 ? [] : await new GroupBatchOwnInputReader(database).OriginRows(scope, ids).ToArrayAsync(token);
            await permissions.RequireSafeRuntimeAsync(token);
            await directory.RequireCurrentAsync(authority, token);
            ValidateTransaction(); var observed = UtcNow(); if (observed < initial) throw Unavailable();
            var graph = GroupBatchHistoricalGraph.Require(coverage, effects, origins, observed, token);
            var acquisition = originals.Single(x => x.Epoch == terminal.ClaimEpoch);
            var result = GroupBatchHistoricalTerminalEvidence.Require(graph, acquisition, terminal, observed);
            token.ThrowIfCancellationRequested(); return result;
        }
        catch (Exception error) when (error is SqlException or ArgumentException or OverflowException) { throw Unavailable(); }
    }

    internal sealed record BlobMetadata(Guid OperationId, int? Length, int? DigestLength);
    internal static void RequireBlobMetadata(BlobMetadata[] rows, int maximum, int minimumLength, int maximumLength)
    {
        if (rows is null || maximum is < 1 or > FrozenGroupBatch.MaximumMessages || rows.Length is < 1 || rows.Length > maximum
            || rows.Any(x => x is null || x.OperationId == Guid.Empty || x.Length < minimumLength || x.Length > maximumLength
                || x.Length is null || x.DigestLength != 32) || rows.Select(x => x.OperationId).Distinct().Count() != rows.Length) throw Unavailable();
    }
    private DateTimeOffset UtcNow()
    { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }
    private void ValidateTransaction()
    {
        if (!database.Database.IsSqlServer() || System.Transactions.Transaction.Current is not null
            || database.Database.CurrentTransaction is not { SupportsSavepoints: true } transaction
            || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable || database.ChangeTracker.HasChanges()
            || !ReferenceEquals(transaction.GetDbTransaction().Connection, database.Database.GetDbConnection())) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
    }
    internal IQueryable<GroupBatchTerminalReceiptRecord> TerminalRows(GroupScope scope, Guid batch) => database.GroupBatchTerminalReceipts.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch).Take(2);
    internal IQueryable<GroupWorkCommitReceiptRecord> WorkRows(GroupScope scope, Guid batch) => database.GroupWorkCommitReceipts.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
        .OrderBy(x => x.OperationId).Take(FrozenGroupBatch.MaximumMessages + 1);
    internal IQueryable<GroupWorkSourceDispositionRecord> SelectedRows(GroupScope scope, Guid batch) => database.GroupWorkSourceDispositions.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
        .OrderBy(x => x.MessageId).Take(FrozenGroupBatch.MaximumMessages + 1);
    internal IQueryable<GroupWorkRawDispositionRecord> RawRows(GroupScope scope, Guid batch) => database.GroupWorkRawDispositions.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
        .OrderBy(x => x.CommittedSequence).Take(GroupBatchAllocationPrefix.MaximumCandidateRows);
    internal IQueryable<GroupBatchClaimReceiptRecord> OriginalRows(GroupScope scope, Guid batch, long[] epochs) => database.GroupBatchClaimReceipts.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.BatchId == batch && epochs.Contains(x.Epoch)).OrderBy(x => x.Epoch).Take(FrozenGroupBatch.MaximumMessages + 2);
    internal IQueryable<GroupPendingRevisionMetadata> CutoffRows(GroupScope scope, Guid message, long cutoff) => database.GroupMessageRevisions.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.MessageId == message && x.CommittedSequence <= cutoff)
        .OrderByDescending(x => x.Kind == GroupSourceEventKind.Recall ? 3 : x.Kind == GroupSourceEventKind.Edit ? 2 : 1)
        .ThenByDescending(x => x.Revision)
        .Select(x => new GroupPendingRevisionMetadata(new(x.TenantId, x.CompanyId, x.BindingId), x.MessageId, x.Revision,
            x.CommittedSequence, x.ContentSha256, x.Kind, x.CommittedAtUtc, x.IsHistoricalBackfill)).Take(1);
    private static InvalidOperationException Unavailable() => new("Group historical SQL evidence is not available.");
}
