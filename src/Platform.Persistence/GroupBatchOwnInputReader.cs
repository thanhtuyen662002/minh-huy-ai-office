using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Metadata-only query inside the whole reader's current authority and source
// lock. No decrypt, new brain selection, standalone authorization or write.
internal sealed class GroupBatchOwnInputReader(PlatformDbContext database)
{
    internal async Task<GroupBatchOwnInputPlan> RequireLockedAsync(GroupWholeBatchDependencyVerdict.Current current,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(current); token.ThrowIfCancellationRequested();
        ValidateTransaction();
        var ids = current.Coverage.Manifests.SelectMany(x => x.Dependencies)
            .Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId).Distinct().Order().ToArray();
        if (ids.Length > GroupBatchOwnInputPlan.MaximumOriginRows) throw Unavailable();
        try
        {
            var rows = ids.Length == 0 ? [] : await OriginRows(current.Coverage.Scope, ids).ToArrayAsync(token);
            var plan = GroupBatchOwnInputPlan.Require(current, rows, token);
            ValidateTransaction(); token.ThrowIfCancellationRequested(); return plan;
        }
        catch (Exception error) when (error is SqlException or ArgumentException) { throw Unavailable(); }
    }

    internal IQueryable<GroupBrainRequestOriginMetadata> OriginRows(GroupScope scope, Guid[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids); scope.Validate();
        if (ids.Length is < 1 or > GroupBatchOwnInputPlan.MaximumOriginRows
            || ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Length) throw Unavailable();
        // Aggregate origin metadata only: each original manifest still limits
        // selected brain revisions to20 and encrypted envelopes to256000 bytes.
        return database.GroupCustomerRequests.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && ids.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new GroupBrainRequestOriginMetadata(new(x.TenantId, x.CompanyId, x.BindingId),
                x.Id, x.OriginBatchId, x.OriginOperationId, x.OriginCandidateOrdinal, x.CreatedAtUtc))
            .Take(GroupBatchOwnInputPlan.MaximumOriginRows + 1);
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
    private static InvalidOperationException Unavailable() => new("Group own-input dependencies are not available.");
}
