using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupIngressInboxReceipt(GroupIngressDispatchReference Reference, DateTimeOffset ReceivedAtUtc, bool WasAlreadyReceived);

// The broker supplies a hint, not authority or source text. Resolve the exact
// committed SQL graph under current host-bound Extract capability before ACK.
public sealed class GroupIngressInboxStore(PlatformDbContext database, GroupExtractionWorkerBinding worker, TimeProvider clock)
{
    public async Task<GroupIngressInboxReceipt> ReceiveAsync(GroupIngressDispatchReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        reference.Validate(); worker.Validate();
        var scope = reference.Source;
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        if (database.ChangeTracker.HasChanges() || (database.Database.IsRelational() && !database.Database.IsSqlServer())) throw Unavailable();
        var directory = new GroupExtractionDirectory(database, worker);
        var permissions = new GroupIngressPermissionVerifier(database);
        GroupIngressInboxRecord? staged = null;
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        try
        {
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            if (database.Database.IsSqlServer())
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    SELECT BindingId FROM aioffice.GroupSourceStates WITH (UPDLOCK,HOLDLOCK)
                    WHERE TenantId={scope.TenantId} AND CompanyId={scope.CompanyId} AND BindingId={scope.SourceBindingId};
                    """, cancellationToken);
            var authority = await directory.RequireAsync(scope, cancellationToken);
            var now = clock.GetUtcNow();
            if (now.Offset != TimeSpan.Zero) throw Unavailable();
            var outbox = await database.GroupIngressOutbox.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId &&
                x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.Id == reference.EventId, cancellationToken) ?? throw Unavailable();
            if (outbox.MessageId != reference.MessageId || outbox.Revision != reference.Revision || outbox.CommittedSequence != reference.CommittedSequence ||
                outbox.AvailableAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
            // AvailableAt is the publisher's retry reservation, not the SQL
            // commit time. A broker delivery can arrive before that reservation
            // expires or the producer records its confirmation. Authority and
            // commitment come from the original revision/receipt below.
            // Select metadata only. This boundary never materializes encrypted
            // source, resolves a key, invokes a model or advances batch cursors.
            var revision = await database.GroupMessageRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId &&
                x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.MessageId == reference.MessageId && x.Revision == reference.Revision)
                .Select(x => new { x.CommittedSequence, x.SourceVersion, x.DeletionGeneration, x.CommittedAtUtc }).SingleOrDefaultAsync(cancellationToken) ?? throw Unavailable();
            var state = await database.GroupSourceStates.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId &&
                x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, cancellationToken) ?? throw Unavailable();
            if (revision.CommittedSequence != reference.CommittedSequence || revision.SourceVersion != authority.Source.Version ||
                revision.DeletionGeneration != authority.Source.DeletionGeneration || revision.CommittedAtUtc.Offset != TimeSpan.Zero || revision.CommittedAtUtc > now ||
                outbox.AvailableAtUtc < revision.CommittedAtUtc ||
                state.CommittedSequence < reference.CommittedSequence || state.ScheduledThroughSequence < 0 || state.ScheduledThroughSequence > state.CommittedSequence)
                throw Unavailable();
            var receipts = await database.GroupIngressReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId &&
                x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.MessageId == reference.MessageId && x.Revision == reference.Revision)
                .Select(x => new { x.ServiceId, x.CredentialEpoch, x.ListenerEpoch, x.CommittedAtUtc }).Take(2).ToArrayAsync(cancellationToken);
            if (receipts.Length != 1 || receipts[0].ServiceId == Guid.Empty || receipts[0].CredentialEpoch <= 0 || receipts[0].ListenerEpoch <= 0 ||
                receipts[0].CommittedAtUtc != revision.CommittedAtUtc || receipts[0].CommittedAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
            var previous = await database.GroupIngressInbox.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId &&
                x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.EventId == reference.EventId, cancellationToken);
            if (previous is not null)
            {
                if (previous.MessageId != reference.MessageId || previous.Revision != reference.Revision || previous.CommittedSequence != reference.CommittedSequence ||
                    previous.SourceVersion != revision.SourceVersion || previous.DeletionGeneration != revision.DeletionGeneration || previous.ServiceId == Guid.Empty ||
                    previous.CredentialEpoch <= 0 || previous.GrantVersion <= 0 || previous.ReceivedAtUtc.Offset != TimeSpan.Zero ||
                    previous.ReceivedAtUtc < revision.CommittedAtUtc || previous.ReceivedAtUtc > now) throw Unavailable();
                await directory.RequireCurrentAsync(authority, cancellationToken);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new(reference, previous.ReceivedAtUtc, true);
            }
            staged = new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                EventId = reference.EventId,
                MessageId = reference.MessageId,
                Revision = reference.Revision,
                CommittedSequence = reference.CommittedSequence,
                SourceVersion = revision.SourceVersion,
                DeletionGeneration = revision.DeletionGeneration,
                ServiceId = worker.ServiceId,
                CredentialEpoch = worker.CredentialEpoch,
                GrantVersion = authority.Grant.Version,
                ReceivedAtUtc = now
            };
            database.GroupIngressInbox.Add(staged);
            await directory.RequireCurrentAsync(authority, cancellationToken);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await directory.RequireCurrentAsync(authority, cancellationToken);
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(reference, now, false);
        }
        finally
        {
            if (staged is not null) database.Entry(staged).State = EntityState.Detached;
        }
    }

    private static InvalidOperationException Unavailable() => new("Group inbox delivery is not available.");
}
