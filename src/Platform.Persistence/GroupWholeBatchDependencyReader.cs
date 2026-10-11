using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal abstract record GroupWholeBatchDependencyVerdict
{
    internal sealed record Current(GroupWholeBatchCoverage Coverage) : GroupWholeBatchDependencyVerdict;
    internal sealed record Expired(GroupBatchClaimExpiryObservation Observation) : GroupWholeBatchDependencyVerdict;
}

// Metadata/cipher-only prerequisite inside the future caller's SQL unit. It
// does not own a transaction, commit, expiry witness, effects or frontier.
// Every original contributor must still match; no latest-chunk-only shortcut.
internal sealed class GroupWholeBatchDependencyReader(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain)
{
    internal async Task<GroupWholeBatchDependencyVerdict> ReadLockedAsync(GroupBatchClaimHandle handle,
        CancellationToken token = default)
    {
        ValidateEntry(handle, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); token = deadline.Token;
        try
        {
            var claims = new GroupBatchClaimStore(database, worker, clock);
            var permissions = new GroupWorkNotePermissionVerifier(database);
            await permissions.RequireSafeRuntimeAsync(token);
            var initial = await claims.InspectCurrentLockedAsync(handle, token);
            if (initial is GroupBatchClaimFenceVerdict.Expired initialExpiry) return new GroupWholeBatchDependencyVerdict.Expired(initialExpiry.Observation);
            var allocation = ((GroupBatchClaimFenceVerdict.Current)initial).Allocation;
            var now = UtcNow(); var scope = handle.Receipt.Scope;
            var receipts = await ReceiptRows(scope, handle.Receipt.BatchId).ToArrayAsync(token);
            var selected = await SelectedRows(scope, handle.Receipt.BatchId).ToArrayAsync(token);
            var raw = await RawRows(scope, handle.Receipt.BatchId).ToArrayAsync(token);
            // The source SQL lock is already held by InspectCurrentLockedAsync.
            // Cutoff winners include all original history, even before After.
            var ids = allocation.Revisions.Select(x => x.Metadata.MessageId).Distinct().Order().ToArray();
            if (ids.Length is < 1 or > FrozenGroupBatch.MaximumMessages) throw Unavailable();
            var heads = new List<GroupPendingRevisionMetadata>(ids.Length);
            foreach (var id in ids)
            {
                var rows = await CutoffRows(scope, id, allocation.AllocatedThroughSequence).ToArrayAsync(token);
                if (rows.Length != 1) throw Unavailable();
                heads.Add(rows[0]);
            }
            var coverage = GroupWholeBatchCoverage.Require(allocation, heads, receipts, selected, raw);
            foreach (var receipt in receipts)
            {
                if (receipt.ServiceId != handle.Receipt.ServiceId || receipt.CredentialEpoch != handle.Receipt.CredentialEpoch
                    || receipt.GrantVersion != handle.Receipt.GrantVersion || receipt.SourceVersion != handle.Receipt.SourceVersion
                    || receipt.DeletionGeneration != handle.Receipt.DeletionGeneration || receipt.AccountVersion != handle.Receipt.AccountVersion
                    || receipt.ClaimEpoch > handle.Receipt.Epoch || receipt.CommittedAtUtc > now) throw Unavailable();
            }
            foreach (var manifest in coverage.Manifests)
            {
                var receipt = receipts.Single(x => x.OperationId == manifest.OperationId);
                var originalSourceHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\n",
                    manifest.Sources.Select(x => x.MessageId.ToString("D") + "/" + x.Revision.ToString(CultureInfo.InvariantCulture))))));
                if (receipt.SourceSetSha256 != originalSourceHash) throw Unavailable();
                var sourceVerdict = await sources.RequireManifestUnchangedLockedAsync(handle, manifest, token);
                if (sourceVerdict is GroupBatchClaimFenceVerdict.Expired sourceExpiry) return new GroupWholeBatchDependencyVerdict.Expired(sourceExpiry.Observation);
                var brainVerdict = await brain.RequireManifestUnchangedLockedAsync(handle, manifest, token);
                if (brainVerdict is GroupBatchClaimFenceVerdict.Expired brainExpiry) return new GroupWholeBatchDependencyVerdict.Expired(brainExpiry.Observation);
            }
            await permissions.RequireSafeRuntimeAsync(token);
            var final = await claims.InspectCurrentLockedAsync(handle, token);
            if (final is GroupBatchClaimFenceVerdict.Expired finalExpiry) return new GroupWholeBatchDependencyVerdict.Expired(finalExpiry.Observation);
            if (UtcNow() < now || database.ChangeTracker.HasChanges()) throw Unavailable();
            token.ThrowIfCancellationRequested();
            return new GroupWholeBatchDependencyVerdict.Current(coverage);
        }
        catch (Exception error) when (error is SqlException or ArgumentException) { throw Unavailable(); }
    }

    private void ValidateEntry(GroupBatchClaimHandle handle, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested(); worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch) throw GroupServiceDirectory.Denied();
        if (sources is null || brain is null || !sources.UsesContext(database, worker, clock) || !brain.UsesContext(database, worker, clock)) throw Unavailable();
        if (!database.Database.IsSqlServer() || System.Transactions.Transaction.Current is not null
            || database.Database.CurrentTransaction is not { } transaction || !transaction.SupportsSavepoints
            || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable || database.ChangeTracker.HasChanges()) throw Unavailable();
    }
    private DateTimeOffset UtcNow()
    { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }

    private IQueryable<GroupWorkCommitReceiptRecord> ReceiptRows(GroupScope scope, Guid batch) => database.GroupWorkCommitReceipts.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
        .OrderBy(x => x.OperationId).Take(FrozenGroupBatch.MaximumMessages + 1);
    private IQueryable<GroupWorkSourceDispositionRecord> SelectedRows(GroupScope scope, Guid batch) => database.GroupWorkSourceDispositions.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
        .OrderBy(x => x.MessageId).Take(FrozenGroupBatch.MaximumMessages + 1);
    private IQueryable<GroupWorkRawDispositionRecord> RawRows(GroupScope scope, Guid batch) => database.GroupWorkRawDispositions.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
        .OrderBy(x => x.CommittedSequence).Take(GroupBatchAllocationPrefix.MaximumCandidateRows);
    private IQueryable<GroupPendingRevisionMetadata> CutoffRows(GroupScope scope, Guid message, long cutoff) => database.GroupMessageRevisions.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.MessageId == message && x.CommittedSequence <= cutoff)
        .OrderByDescending(x => x.Kind == GroupSourceEventKind.Recall ? 3 : x.Kind == GroupSourceEventKind.Edit ? 2 : 1)
        .ThenByDescending(x => x.Revision)
        .Select(x => new GroupPendingRevisionMetadata(new(x.TenantId, x.CompanyId, x.BindingId), x.MessageId, x.Revision, x.CommittedSequence,
            x.ContentSha256, x.Kind, x.CommittedAtUtc, x.IsHistoricalBackfill)).Take(1);
    private static InvalidOperationException Unavailable() => new("Whole group batch dependencies are not available.");
}
