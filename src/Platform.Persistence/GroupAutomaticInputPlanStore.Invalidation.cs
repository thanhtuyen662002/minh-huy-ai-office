using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed record GroupAuthenticatedOriginalMetadata(GroupAutomaticInputPlan Plan, Guid OriginalClaimOperationId,
    long OriginalClaimEpoch, DateTimeOffset CreatedAtUtc)
{
    public override string ToString() => "Authenticated original metadata (private, no authority).";
}

internal sealed partial class GroupAutomaticInputPlanStore
{
    // Metadata only, called by the checkpoint owner in its already current,
    // source-locked unit. Unlike ReadAsync this cannot release model context.
    internal async Task<GroupAuthenticatedOriginalMetadata?> ReadForInvalidationLockedAsync(GroupBatchClaimHandle handle,
        DateTimeOffset now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested(); worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch) throw GroupServiceDirectory.Denied();
        if (sources is null || brain is null || !sources.UsesContext(database, worker, clock) || !brain.UsesContext(database, worker, clock)
            || !database.Database.IsSqlServer() || database.Database.CurrentTransaction is not { SupportsSavepoints: true } sql
            || sql.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable || System.Transactions.Transaction.Current is not null
            || !ReferenceEquals(sql.GetDbTransaction().Connection, database.Database.GetDbConnection())
            || database.Database.GetDbConnection().State != ConnectionState.Open
            || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
        await new GroupAutomaticInputPlanPermissionVerifier(database).RequireSafeRuntimeAsync(token);
        var row = await ReadLockedAsync(handle, token);
        if (row is null) return null;
        var original = await DecodeAsync(handle, row, now, token);
        return new(original, row.OriginalClaimOperationId, row.OriginalClaimEpoch, row.CreatedAtUtc);
    }
}
