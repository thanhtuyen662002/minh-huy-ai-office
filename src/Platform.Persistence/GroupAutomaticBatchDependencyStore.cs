using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

// An instant automatic-batch dependency check under trusted Extract authority.
// Success is not a completion receipt or permission for a future write/send.
// The terminal writer must repeat the internal check in its own effect unit.
public sealed class GroupAutomaticBatchDependencyStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain)
{
    public async Task RequireCurrentAsync(GroupBatchClaimHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle); cancellationToken.ThrowIfCancellationRequested(); worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch) throw GroupServiceDirectory.Denied();
        if (sources is null || brain is null || !sources.UsesContext(database, worker, clock) || !brain.UsesContext(database, worker, clock)
            || !database.Database.IsSqlServer() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        try
        {
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            var verdict = await new GroupWholeBatchDependencyReader(database, worker, clock, sources, brain).ReadLockedAsync(handle, cancellationToken);
            if (verdict is GroupWholeBatchDependencyVerdict.Expired expired)
                await new GroupBatchClaimStore(database, worker, clock).RetireExpiredLockedAsync(expired.Observation, cancellationToken);
            else if (verdict is not GroupWholeBatchDependencyVerdict.Current) throw Unavailable();
            if (database.ChangeTracker.HasChanges()) throw Unavailable();
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (verdict is GroupWholeBatchDependencyVerdict.Expired) throw GroupServiceDirectory.Denied();
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException) { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Automatic group batch dependencies are not available.");
}
