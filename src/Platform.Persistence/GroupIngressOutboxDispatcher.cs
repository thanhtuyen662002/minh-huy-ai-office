using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Publisher confirmation is not worker acceptance. Retry the same immutable
// reference until SQL contains the authorized worker's durable inbox receipt.
public sealed class GroupIngressOutboxDispatcher(PlatformDbContext database,
    GroupExtractionWorkerBinding worker, IGroupIngressReferencePublisher publisher, TimeProvider clock)
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PublishDeadline = TimeSpan.FromSeconds(10);

    public async Task<bool> PublishNextAsync(GroupScope source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        source.Validate(); worker.Validate();
        if (source.TenantId != worker.TenantId || source.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        RequireCleanContext();
        var reservation = await ReserveAsync(source, cancellationToken);
        if (reservation is null) return false;
        // The durable retry reservation precedes the external operation. Lost
        // confirms and process death preserve the original EventId for replay.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PublishDeadline);
        try
        {
            var directory = new GroupExtractionDirectory(database, worker);
            await directory.RequireCurrentAsync(reservation.Authority, deadline.Token);
            var publish = publisher.PublishAsync(reservation.Reference, deadline.Token);
            _ = publish.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await publish.WaitAsync(deadline.Token);
            await MarkConfirmedAsync(reservation, deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch { throw Unavailable(); }
    }

    private async Task<Reservation?> ReserveAsync(GroupScope scope, CancellationToken cancellationToken)
    {
        GroupIngressOutboxRecord? tracked = null;
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        try
        {
            var permissions = new GroupIngressPermissionVerifier(database);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await LockSourceAsync(scope, cancellationToken);
            var directory = new GroupExtractionDirectory(database, worker);
            var authority = await directory.RequireAsync(scope, cancellationToken);
            var now = UtcNow();
            tracked = await database.GroupIngressOutbox.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId &&
                x.BindingId == scope.SourceBindingId && x.AvailableAtUtc <= now &&
                !database.GroupIngressInbox.Any(receipt => receipt.TenantId == x.TenantId && receipt.CompanyId == x.CompanyId &&
                    receipt.BindingId == x.BindingId && receipt.EventId == x.Id))
                .OrderBy(x => x.CommittedSequence).FirstOrDefaultAsync(cancellationToken);
            if (tracked is null)
            {
                await directory.RequireCurrentAsync(authority, cancellationToken);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            var reference = new GroupIngressDispatchReference(1, scope, tracked.Id, tracked.MessageId, tracked.Revision, tracked.CommittedSequence);
            reference.Validate();
            var committed = await RequireCommittedGraphAsync(reference, authority, cancellationToken);
            if (tracked.AvailableAtUtc.Offset != TimeSpan.Zero || tracked.AvailableAtUtc < committed ||
                tracked.PublishAttempts < 0 || tracked.PublishAttempts == int.MaxValue ||
                (tracked.PublishedAtUtc is { } published && (published.Offset != TimeSpan.Zero || published < committed || published > now)))
                throw Unavailable();
            TrackFreshOutbox(tracked);
            tracked.PublishAttempts++;
            tracked.AvailableAtUtc = now + RetryDelay;
            await directory.RequireCurrentAsync(authority, cancellationToken);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await directory.RequireCurrentAsync(authority, cancellationToken);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(reference, authority, tracked.PublishAttempts, tracked.AvailableAtUtc);
        }
        finally { if (tracked is not null) database.Entry(tracked).State = EntityState.Detached; }
    }

    private async Task MarkConfirmedAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        RequireCleanContext();
        GroupIngressOutboxRecord? tracked = null;
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        try
        {
            var reference = reservation.Reference;
            var scope = reference.Source;
            var permissions = new GroupIngressPermissionVerifier(database);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await LockSourceAsync(scope, cancellationToken);
            var directory = new GroupExtractionDirectory(database, worker);
            await directory.RequireCurrentAsync(reservation.Authority, cancellationToken);
            tracked = await database.GroupIngressOutbox.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId &&
                x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.Id == reference.EventId, cancellationToken) ?? throw Unavailable();
            if (tracked.MessageId != reference.MessageId || tracked.Revision != reference.Revision || tracked.CommittedSequence != reference.CommittedSequence)
                throw Unavailable();
            await RequireCommittedGraphAsync(reference, reservation.Authority, cancellationToken);
            // A later reservation owns these delivery fields. A delayed confirm
            // cannot overwrite that attempt or shorten its retry checkpoint.
            if (tracked.PublishAttempts == reservation.Attempt && tracked.AvailableAtUtc == reservation.AvailableAtUtc)
            {
                TrackFreshOutbox(tracked);
                tracked.PublishedAtUtc = UtcNow();
                await database.SaveChangesAsync(cancellationToken);
            }
            await directory.RequireCurrentAsync(reservation.Authority, cancellationToken);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { if (tracked is not null) database.Entry(tracked).State = EntityState.Detached; }
    }

    private async Task<DateTimeOffset> RequireCommittedGraphAsync(GroupIngressDispatchReference reference,
        GroupExtractionAuthority authority, CancellationToken cancellationToken)
    {
        var scope = reference.Source;
        var revision = await database.GroupMessageRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId &&
            x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.MessageId == reference.MessageId && x.Revision == reference.Revision)
            .Select(x => new { x.CommittedSequence, x.SourceVersion, x.DeletionGeneration, x.CommittedAtUtc }).SingleOrDefaultAsync(cancellationToken) ?? throw Unavailable();
        if (revision.CommittedSequence != reference.CommittedSequence || revision.SourceVersion != authority.Source.Version ||
            revision.DeletionGeneration != authority.Source.DeletionGeneration || revision.CommittedAtUtc.Offset != TimeSpan.Zero || revision.CommittedAtUtc > UtcNow())
            throw Unavailable();
        var state = await database.GroupSourceStates.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId &&
            x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, cancellationToken) ?? throw Unavailable();
        if (state.CommittedSequence < reference.CommittedSequence || state.ScheduledThroughSequence < 0 || state.ScheduledThroughSequence > state.CommittedSequence)
            throw Unavailable();
        var receipts = await database.GroupIngressReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId &&
            x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.MessageId == reference.MessageId && x.Revision == reference.Revision)
            .Select(x => new { x.ServiceId, x.CredentialEpoch, x.ListenerEpoch, x.CommittedAtUtc }).Take(2).ToArrayAsync(cancellationToken);
        if (receipts.Length != 1 || receipts[0].ServiceId == Guid.Empty || receipts[0].CredentialEpoch <= 0 || receipts[0].ListenerEpoch <= 0 ||
            receipts[0].CommittedAtUtc != revision.CommittedAtUtc || receipts[0].CommittedAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
        return revision.CommittedAtUtc;
    }

    private Task LockSourceAsync(GroupScope scope, CancellationToken cancellationToken) => database.Database.IsSqlServer()
        ? database.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT BindingId FROM aioffice.GroupSourceStates WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId={scope.TenantId} AND CompanyId={scope.CompanyId} AND BindingId={scope.SourceBindingId};
            """, cancellationToken) : Task.CompletedTask;

    private void RequireCleanContext()
    {
        if (database.ChangeTracker.HasChanges() || (database.Database.IsRelational() && !database.Database.IsSqlServer())) throw Unavailable();
    }

    private void TrackFreshOutbox(GroupIngressOutboxRecord current)
    {
        // An unchanged entity may still be stale after another context commits.
        // Source locks protect SQL, not the EF identity map. Attach only the
        // freshly read row and preserve all unrelated caller-tracked entities.
        var previous = database.ChangeTracker.Entries<GroupIngressOutboxRecord>().SingleOrDefault(entry =>
            entry.Entity.TenantId == current.TenantId && entry.Entity.CompanyId == current.CompanyId &&
            entry.Entity.BindingId == current.BindingId && entry.Entity.Id == current.Id);
        if (previous is not null)
        {
            if (previous.State != EntityState.Unchanged) throw Unavailable();
            previous.State = EntityState.Detached;
        }
        database.Attach(current);
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.GetUtcNow();
        return now.Offset == TimeSpan.Zero ? now : throw Unavailable();
    }

    private sealed record Reservation(GroupIngressDispatchReference Reference, GroupExtractionAuthority Authority, int Attempt, DateTimeOffset AvailableAtUtc);
    private static InvalidOperationException Unavailable() => new("Group reference publication is not available.");
}
