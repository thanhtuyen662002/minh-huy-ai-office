using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Observation only inside the caller's already owned source-lock unit.
// No original content, key, provider or claim-authority verdict is returned.
internal sealed class GroupOriginalActivityStore(PlatformDbContext database, GroupExtractionWorkerBinding worker)
{
    internal async Task<bool> IsLegacyActiveLockedAsync(GroupBatchClaimHandle handle, CancellationToken token)
    {
        ValidateEntry(handle, token);
        await new GroupOriginalInvalidationPermissionVerifier(database).RequireSafeRuntimeAsync(token);
        ValidateEntry(handle, token);
        return !await LegacyRows(handle).AnyAsync(token);
    }

    // Presence blocks generation0 even if the row's non-key fields are corrupt.
    // This query never interprets absence as a release or execution capability.
    private IQueryable<GroupOriginalInvalidationRecord> LegacyRows(GroupBatchClaimHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle); worker.Validate(); handle.Receipt.Scope.Validate();
        var scope = handle.Receipt.Scope;
        RequireWorker(handle, scope);
        return database.GroupOriginalInvalidations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId);
    }

    private void ValidateEntry(GroupBatchClaimHandle handle, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested(); worker.Validate();
        var scope = handle.Receipt.Scope; scope.Validate(); RequireWorker(handle, scope);
        if (!database.Database.IsSqlServer() || database.Database.CurrentTransaction is not { } transaction
            || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable
            || !ReferenceEquals(transaction.GetDbTransaction().Connection, database.Database.GetDbConnection())
            || database.Database.GetDbConnection().State != ConnectionState.Open
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
    }

    private void RequireWorker(GroupBatchClaimHandle handle, GroupScope scope)
    {
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch)
            throw GroupServiceDirectory.Denied();
        if (handle.Receipt.BatchId == Guid.Empty) throw Unavailable();
    }
    private static InvalidOperationException Unavailable() => new("Group original activity is not available.");
}
