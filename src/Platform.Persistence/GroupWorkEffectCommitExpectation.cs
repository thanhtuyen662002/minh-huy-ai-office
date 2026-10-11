using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Used only inside the existing effect writers' owned unit, after their
// current authority/dependency fences. This helper neither commits nor
// authorizes a caller. New expectations must precede the receipt INSERT;
// legacy replay retains its existing full validation without backfilling.
internal static class GroupWorkEffectCommitExpectation
{
    internal static async Task StageLockedAsync(PlatformDbContext database, GroupWorkCommitReceiptRecord receipt,
        bool historical, List<object> staged, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(staged); token.ThrowIfCancellationRequested();
        if (staged.Count > 1022 || staged.OfType<GroupWorkCommitReceiptRecord>().Count() != 1
            || !ReferenceEquals(staged.OfType<GroupWorkCommitReceiptRecord>().Single(), receipt)
            || staged.Any(x => x is not (GroupWorkCommitReceiptRecord or GroupWorkSourceDispositionRecord or GroupCustomerRequestRecord
                or GroupRequestRevisionRecord or GroupRequestEvidenceRecord or GroupNotesCommittedOutboxRecord
                or GroupNotesCommittedItemRecord or GroupWorkRawDispositionRecord))) throw Unavailable();
        var original = await OriginalClaimLockedAsync(database, receipt, token);
        var graph = GroupWorkEffectLedger.Require(receipt, original, historical,
            staged.OfType<GroupWorkSourceDispositionRecord>().ToArray(), staged.OfType<GroupCustomerRequestRecord>().ToArray(),
            staged.OfType<GroupRequestRevisionRecord>().ToArray(), staged.OfType<GroupRequestEvidenceRecord>().ToArray(),
            staged.OfType<GroupNotesCommittedOutboxRecord>().ToArray(), staged.OfType<GroupNotesCommittedItemRecord>().ToArray());
        token.ThrowIfCancellationRequested();
        GroupWorkEffectDigest.Stage(receipt, graph);
    }

    internal static async Task RequireReplayLockedAsync(PlatformDbContext database, GroupWorkCommitReceiptRecord receipt,
        bool historical, IReadOnlyList<GroupWorkSourceDispositionRecord> selected, IReadOnlyList<GroupCustomerRequestRecord> requests,
        IReadOnlyList<GroupRequestRevisionRecord> revisions, IReadOnlyList<GroupRequestEvidenceRecord> evidence,
        IReadOnlyList<GroupNotesCommittedOutboxRecord> outboxes, IReadOnlyList<GroupNotesCommittedItemRecord> items, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!GroupWorkEffectDigest.HasExpectation(receipt)) return;
        var original = await OriginalClaimLockedAsync(database, receipt, token);
        var graph = GroupWorkEffectLedger.Require(receipt, original, historical, selected, requests, revisions, evidence, outboxes, items);
        token.ThrowIfCancellationRequested();
        GroupWorkEffectDigest.RequireUnchanged(receipt, graph);
    }

    private static async Task<GroupBatchClaimReceiptRecord> OriginalClaimLockedAsync(PlatformDbContext database,
        GroupWorkCommitReceiptRecord receipt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(receipt); token.ThrowIfCancellationRequested();
        if (!database.Database.IsSqlServer() || System.Transactions.Transaction.Current is not null
            || database.Database.CurrentTransaction is not { SupportsSavepoints: true } transaction
            || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable || database.ChangeTracker.HasChanges()
            || !ReferenceEquals(transaction.GetDbTransaction().Connection, database.Database.GetDbConnection())) throw Unavailable();
        try
        {
            if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
        var rows = await OriginalClaimRows(database, receipt).ToArrayAsync(token);
        if (rows.Length != 1) throw Unavailable();
        return rows[0];
    }

    private static IQueryable<GroupBatchClaimReceiptRecord> OriginalClaimRows(PlatformDbContext database, GroupWorkCommitReceiptRecord receipt) =>
        database.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == receipt.TenantId && x.CompanyId == receipt.CompanyId
            && x.BindingId == receipt.BindingId && x.BatchId == receipt.BatchId && x.Epoch == receipt.ClaimEpoch).Take(2);
    private static InvalidOperationException Unavailable() => new("Group original effect expectation is not available.");
}
