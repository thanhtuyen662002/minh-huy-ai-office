using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Called only by the whole-dependency reader inside its current authority,
// Serializable/source-lock fences. One original contributor at a time keeps
// bounded encrypted graph memory; there are no keys, writes or completion here.
internal sealed class GroupWholeBatchEffectReader(PlatformDbContext database)
{
    internal async Task<GroupWorkEffectLedger> RequireLockedAsync(GroupWorkCommitReceiptRecord receipt,
        GroupBatchClaimReceiptRecord original, IReadOnlyList<GroupWorkSourceDispositionRecord> selected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(original); token.ThrowIfCancellationRequested();
        if (!GroupWorkEffectDigest.HasExpectation(receipt)) throw Unavailable();
        ValidateTransaction();
        var requests = await RequestRows(receipt).ToArrayAsync(token);
        if (requests.Length != receipt.NoteCount || requests.Length > GroupAutomaticNotePlan.MaximumNotes
            || requests.Select(x => x.Id).Distinct().Count() != requests.Length) throw Unavailable();
        var ids = requests.Select(x => x.Id).ToArray();
        // Inspect only lengths first; refuse the aggregate before selecting
        // encrypted payloads. Each original contributor keeps the existing256k cap.
        var metadata = await MetadataRows(receipt, ids).ToArrayAsync(token);
        RequireEnvelopeMetadata(ids, metadata);
        var revisions = await RevisionRows(receipt, ids).ToArrayAsync(token);
        if (revisions.Length != metadata.Length || revisions.Any(x => !metadata.Any(m => m.RequestId == x.RequestId
            && m.Length == x.ProtectedContent.Length && m.KeyId == x.ContentKeyId && m.Sha256 == x.EnvelopeSha256))) throw Unavailable();
        var evidence = await EvidenceRows(receipt, ids).ToArrayAsync(token);
        var outboxes = await OutboxRows(receipt).ToArrayAsync(token);
        var items = await ItemRows(receipt, outboxes.Select(x => x.Id).ToArray()).ToArrayAsync(token);
        var graph = GroupWorkEffectLedger.Require(receipt, original, outboxes.Length == 1 && outboxes[0].IsHistoricalBackfill,
            selected, requests, revisions, evidence, outboxes, items);
        GroupWorkEffectDigest.RequireUnchanged(receipt, graph);
        ValidateTransaction(); token.ThrowIfCancellationRequested();
        return graph;
    }

    internal sealed record EnvelopeMetadata(Guid RequestId, int? Length, string KeyId, string Sha256);
    internal static void RequireEnvelopeMetadata(Guid[] ids, EnvelopeMetadata[] metadata)
    {
        ArgumentNullException.ThrowIfNull(ids); ArgumentNullException.ThrowIfNull(metadata);
        if (ids.Length > GroupAutomaticNotePlan.MaximumNotes || metadata.Length != ids.Length
            || ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Length || metadata.Any(x => x is null)
            || metadata.Select(x => x.RequestId).Distinct().Count() != metadata.Length
            || metadata.Any(x => !ids.Contains(x.RequestId) || x.Length is not (>= 30 and <= GroupBrainContentProtector.MaximumEnvelopeLength))
            || metadata.Sum(x => (long)x.Length!.Value) > GroupBrainCurrentReader.MaximumSelectedEnvelopeBytes) throw Unavailable();
    }

    private void ValidateTransaction()
    {
        if (!database.Database.IsSqlServer() || System.Transactions.Transaction.Current is not null
            || database.Database.CurrentTransaction is not { SupportsSavepoints: true } transaction
            || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable || database.ChangeTracker.HasChanges()
            || !ReferenceEquals(transaction.GetDbTransaction().Connection, database.Database.GetDbConnection())) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
    }

    private IQueryable<GroupCustomerRequestRecord> RequestRows(GroupWorkCommitReceiptRecord r) => database.GroupCustomerRequests.AsNoTracking()
        .Where(x => x.TenantId == r.TenantId && x.CompanyId == r.CompanyId && x.BindingId == r.BindingId
            && x.OriginBatchId == r.BatchId && x.OriginOperationId == r.OperationId).OrderBy(x => x.OriginCandidateOrdinal)
        .Take(GroupAutomaticNotePlan.MaximumNotes + 1);
    private IQueryable<GroupRequestRevisionRecord> RevisionRows(GroupWorkCommitReceiptRecord r, Guid[] ids) => database.GroupRequestRevisions.AsNoTracking()
        .Where(x => x.TenantId == r.TenantId && x.CompanyId == r.CompanyId && x.BindingId == r.BindingId
            && ids.Contains(x.RequestId) && x.Revision == 1).OrderBy(x => x.RequestId).Take(GroupAutomaticNotePlan.MaximumNotes + 1);
    private IQueryable<EnvelopeMetadata> MetadataRows(GroupWorkCommitReceiptRecord r, Guid[] ids) => RevisionRows(r, ids)
        .Select(x => new EnvelopeMetadata(x.RequestId, EF.Functions.DataLength(x.ProtectedContent), x.ContentKeyId, x.EnvelopeSha256));
    private IQueryable<GroupRequestEvidenceRecord> EvidenceRows(GroupWorkCommitReceiptRecord r, Guid[] ids) => database.GroupRequestEvidence.AsNoTracking()
        .Where(x => x.TenantId == r.TenantId && x.CompanyId == r.CompanyId && x.BindingId == r.BindingId
            && ids.Contains(x.RequestId) && x.RequestRevision == 1).OrderBy(x => x.RequestId).ThenBy(x => x.Ordinal)
        .Take(301);
    private IQueryable<GroupNotesCommittedOutboxRecord> OutboxRows(GroupWorkCommitReceiptRecord r) => database.GroupNotesCommittedOutbox.AsNoTracking()
        .Where(x => x.TenantId == r.TenantId && x.CompanyId == r.CompanyId && x.BindingId == r.BindingId && x.BatchId == r.BatchId
            && x.OperationId == r.OperationId).Take(2);
    private IQueryable<GroupNotesCommittedItemRecord> ItemRows(GroupWorkCommitReceiptRecord r, Guid[] ids) => database.GroupNotesCommittedItems.AsNoTracking()
        .Where(x => x.TenantId == r.TenantId && x.CompanyId == r.CompanyId && x.BindingId == r.BindingId && ids.Contains(x.OutboxId))
        .OrderBy(x => x.Ordinal).Take(GroupAutomaticNotePlan.MaximumNotes + 1);
    private static InvalidOperationException Unavailable() => new("Whole group batch original effects are not available.");
}
