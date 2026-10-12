using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Metadata read inside the caller's current, source-locked SQL transaction.
// Rows alone never grant retry, provider or work-note authority.
internal static class GroupModelAttemptJournalReader
{
    internal static Task<(GroupModelAttemptStarted[] Starts, GroupModelAttemptFailed[] Failures)> ReadAsync(
        PlatformDbContext database, GroupAutomaticModelContext context, string identity, CancellationToken token) =>
        ReadCoreAsync(database, context, identity, false, token);

    // Only the result store authenticates an accepted outcome; ordinary retry
    // and exhaustion callers must refuse whenever success already exists.
    internal static Task<(GroupModelAttemptStarted[] Starts, GroupModelAttemptFailed[] Failures)> ReadIncludingAcceptedAsync(
        PlatformDbContext database, GroupAutomaticModelContext context, string identity, CancellationToken token) =>
        ReadCoreAsync(database, context, identity, true, token);

    private static async Task<(GroupModelAttemptStarted[] Starts, GroupModelAttemptFailed[] Failures)> ReadCoreAsync(
        PlatformDbContext database, GroupAutomaticModelContext context, string identity, bool includeAccepted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!database.Database.IsSqlServer() || database.Database.CurrentTransaction is null) throw Unavailable();
        var scope = context.Scope;
        await new BindingStorePermissionVerifier(database)
            .RequireProofAsync(GroupModelAttemptResultPermissionVerifier.VerificationSql, token);
        if (!includeAccepted && await database.Set<GroupModelAttemptResultRecord>().AsNoTracking().AnyAsync(x =>
            x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
            && x.BatchId == context.BatchId && x.InputSha256 == identity, token)) throw Unavailable();
        var rows = await database.GroupModelAttemptStarts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == context.BatchId
            && x.InputSha256 == identity).OrderBy(x => x.Ordinal).Take(GroupModelAttemptBudget.MaximumAttempts + 1).ToArrayAsync(token);
        var ids = rows.Select(x => x.RequestId).ToArray();
        var outcomes = await database.GroupModelAttemptFailures.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && ids.Contains(x.RequestId))
            .Take(GroupModelAttemptBudget.MaximumAttempts + 1).ToArrayAsync(token);
        if (rows.Any(x => x.PredecessorOrdinal != (x.Ordinal == 1 ? null : x.Ordinal - 1))
            || outcomes.Any(x => !rows.Any(start => start.RequestId == x.RequestId
                && start.StartedAtUtc == x.StartedAtUtc && start.DeadlineUtc == x.DeadlineUtc))) throw Unavailable();
        return (rows.Select(x => new GroupModelAttemptStarted(scope, x.BatchId, x.InputSha256, x.Ordinal,
            x.RequestId, x.PredecessorRequestId, x.ClaimOperationId, x.ClaimEpoch, x.StartedAtUtc, x.DeadlineUtc)).ToArray(),
            outcomes.Select(x => new GroupModelAttemptFailed(scope, x.RequestId,
                (GroupModelAttemptFailure)x.Failure, x.ObservedAtUtc)).ToArray());
    }

    private static InvalidOperationException Unavailable() => new("Group model attempt journal is not available.");
}
