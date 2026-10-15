using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed partial class GroupTerminalFrontierStore
{
    // Historical terminal recovery precedes original/private/model work. A
    // scoped row's existence only selects the existing full authenticated
    // advance path; it cannot establish completion. Absence is metadata only,
    // never authority for a future original, attempt, note or terminal write.
    internal async Task<GroupTerminalFrontierAdvanceResult?> TryAdvanceCommittedAsync(GroupScope scope, Guid batch,
        CancellationToken caller = default)
    {
        ValidateEntry(scope, batch, caller);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); var token = deadline.Token;
        var observed = new MonotoneClock(clock); var exists = false;
        try
        {
            _ = observed.GetUtcNow();
            var directory = new GroupExtractionDirectory(database, worker);
            var permissions = new GroupTerminalAdvanceKeyPermissionVerifier(database);
            await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token))
            {
                await permissions.RequireSafeRuntimeAsync(token);
                var authority = await directory.RequireAsync(scope, token);
                await GroupSourceTransactionLock.RequireAsync(database, scope, token);
                await directory.RequireCurrentAsync(authority, token);
                var rows = await CommittedTerminalRows(scope, batch).ToArrayAsync(token);
                if (rows.Length > 1 || rows.Any(x => x != batch)) throw Unavailable();
                exists = rows.Length == 1;
                _ = observed.GetUtcNow();
                await permissions.RequireSafeRuntimeAsync(token);
                await directory.RequireCurrentAsync(authority, token);
                _ = observed.GetUtcNow();
                if (database.ChangeTracker.HasChanges()) throw Unavailable();
                await transaction.CommitAsync(token);
            }
            RequireOutsideSql(); _ = observed.GetUtcNow(); token.ThrowIfCancellationRequested();
            if (!exists) return null;
            // This owns separate fresh authority/history/key/effect units.
            // A malformed terminal, unavailable key or gap is never converted
            // into absence, a new original, success or a provider failure.
            // Carry the probe's first clock observation through every later
            // historical/key wait. The existing advance adds its own guard
            // around this same monotone clock, rather than forgetting it.
            return await new GroupTerminalFrontierStore(database, worker, observed, keys).AdvanceAsync(scope, batch, token);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException or OverflowException)
        { throw Unavailable(); }
    }

    // Metadata-only translation seam, consumed under the fixed unit above.
    internal IQueryable<Guid> CommittedTerminalRows(GroupScope scope, Guid batch)
    {
        ArgumentNullException.ThrowIfNull(scope); scope.Validate();
        if (batch == Guid.Empty) throw Unavailable();
        return database.GroupBatchTerminalReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.BatchId == batch).OrderBy(x => x.OperationId).Select(x => x.BatchId).Take(2);
    }
}
