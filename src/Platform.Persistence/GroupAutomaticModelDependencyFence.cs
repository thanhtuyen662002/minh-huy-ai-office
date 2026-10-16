using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

// An instant, metadata/cipher-only current check for the exact frozen model
// input. It grants no authority across a later provider wait or SQL effect.
// The caller must check again after the provider wait; the atomic effect store
// owns the final locked checks. No provider or key resolution occurs here.
public sealed class GroupAutomaticModelDependencyFence(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain)
{
    public async Task RequireCurrentAsync(GroupAutomaticModelContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        worker.Validate(); context.Scope.Validate();
        var handle = context.Preparation.Context.Handle;
        if (!ReferenceEquals(handle, context.Brain.Handle)
            || context.Scope.TenantId != worker.TenantId || context.Scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch)
            throw GroupServiceDirectory.Denied();
        if (sources is null || brain is null || !sources.UsesContext(database, worker, clock) || !brain.UsesContext(database, worker, clock)
            || !database.Database.IsSqlServer() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        try
        {
            var started = UtcNow();
            // Reuse the closed complete dependency encoding; this ephemeral
            // operation ID is not persisted or a work-effect identity.
            var expected = GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(
                context.Preparation.Context, context.Brain, Guid.NewGuid()));
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            var claims = new GroupBatchClaimStore(database, worker, clock);
            var permissions = new GroupWorkNotePermissionVerifier(database);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            var verdict = await sources.RequireManifestUnchangedLockedAsync(handle, expected, cancellationToken);
            if (verdict is GroupBatchClaimFenceVerdict.Current)
                verdict = await brain.RequireManifestUnchangedLockedAsync(handle, expected, cancellationToken);
            if (verdict is GroupBatchClaimFenceVerdict.Current)
            {
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                verdict = await claims.InspectCurrentLockedAsync(handle, cancellationToken);
            }
            if (verdict is GroupBatchClaimFenceVerdict.Expired expired)
                await claims.RetireExpiredLockedAsync(expired.Observation, cancellationToken);
            else if (verdict is not GroupBatchClaimFenceVerdict.Current) throw Unavailable();
            if (UtcNow() < started || database.ChangeTracker.HasChanges()) throw Unavailable();
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (verdict is GroupBatchClaimFenceVerdict.Expired) throw GroupServiceDirectory.Denied();
            if (UtcNow() < started) throw Unavailable();
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException) { throw Unavailable(); }
    }

    private DateTimeOffset UtcNow()
    { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }
    private static InvalidOperationException Unavailable() => new("Group model input dependencies are not available.");
}
