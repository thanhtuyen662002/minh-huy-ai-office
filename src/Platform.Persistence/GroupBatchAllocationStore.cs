using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class GroupBatchAllocationCommitException() : InvalidOperationException("Group batch allocation commit is not available.");

// SQL owns eligibility and reservations; broker deliveries only wake a scanner.
// No source ciphertext, key, model, portal user or note is accessed here.
public sealed class GroupBatchAllocationStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    GroupBatchTiming timing, TimeProvider clock)
{
    public async Task<GroupBatchAllocationReceipt?> AllocateDueAsync(GroupScope scope, Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        worker.Validate(); ArgumentNullException.ThrowIfNull(scope); scope.Validate();
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        if (operationId == Guid.Empty || database.ChangeTracker.HasChanges()
            || (database.Database.IsRelational() && !database.Database.IsSqlServer())) throw Unavailable();
        var staged = new List<object>();
        var directory = new GroupExtractionDirectory(database, worker);
        var permissions = new GroupIngressPermissionVerifier(database);
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        try
        {
            await permissions.RequireSafeRuntimeAsync(cancellationToken);
            await GroupSourceTransactionLock.RequireAsync(database, scope, cancellationToken);
            var authority = await directory.RequireAsync(scope, cancellationToken);
            var now = clock.GetUtcNow();
            if (now.Offset != TimeSpan.Zero) throw Unavailable();
            // An original committed nonce wins before checking new backlog.
            var previous = await database.GroupBatchAllocations.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
                && x.OperationId == operationId, cancellationToken);
            if (previous is not null)
            {
                var original = await RestoreAsync(scope, previous, now, cancellationToken);
                await FinalAuthorityAsync();
                await transaction.CommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return original;
            }
            var state = await database.GroupSourceStates.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, cancellationToken);
            if (state is null)
            {
                if (await database.GroupMessageRevisions.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, cancellationToken)) throw Unavailable();
                await FinalAuthorityAsync(); await transaction.CommitAsync(cancellationToken); return null;
            }
            ValidateState(state);
            if (state.ScheduledThroughSequence == state.CommittedSequence)
            {
                if (state.FirstPendingAtUtc is not null) throw Unavailable();
                await FinalAuthorityAsync(); await transaction.CommitAsync(cancellationToken); return null;
            }
            if (state.FirstPendingAtUtc is null || state.LastPendingAtUtc is null || state.LastPendingAtUtc > now) throw Unavailable();
            var due = timing.ComputeDue(state.FirstPendingAtUtc.Value, state.LastPendingAtUtc.Value);
            if (now < due) { await FinalAuthorityAsync(); await transaction.CommitAsync(cancellationToken); return null; }

            var candidates = await Candidates(scope).Where(x => x.CommittedSequence > state.ScheduledThroughSequence
                && x.CommittedSequence <= state.CommittedSequence).OrderBy(x => x.CommittedSequence)
                .Take(GroupBatchAllocationPrefix.MaximumCandidateRows).ToArrayAsync(cancellationToken);
            foreach (var candidate in candidates) ValidateOriginal(candidate, now);
            var prefix = GroupBatchAllocationPrefix.Select(scope, state.ScheduledThroughSequence, state.CommittedSequence,
                candidates.Select(x => Metadata(scope, x)).ToArray());
            var selected = candidates.Take(prefix.Rows.Count).ToArray();
            var batch = new GroupBatchAllocationRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                Id = Guid.NewGuid(),
                OperationId = operationId,
                AfterSequence = prefix.AfterSequence,
                AllocatedThroughSequence = prefix.AllocatedThrough,
                ObservedCommittedThroughSequence = prefix.ObservedCommittedThrough,
                RawRevisionCount = selected.Length,
                IsHistoricalBackfill = prefix.IsHistoricalBackfill,
                SourceVersion = authority.Source.Version,
                DeletionGeneration = authority.Source.DeletionGeneration,
                AccountVersion = authority.AccountVersion,
                ServiceId = worker.ServiceId,
                CredentialEpoch = worker.CredentialEpoch,
                GrantVersion = authority.Grant.Version,
                AllocatedAtUtc = now
            };
            Add(batch);
            foreach (var row in selected) Add(new GroupBatchAllocatedRevisionRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = batch.Id,
                CommittedSequence = row.CommittedSequence,
                MessageId = row.MessageId,
                Revision = row.Revision,
                ContentSha256 = row.ContentSha256,
                Kind = row.Kind,
                CommittedAtUtc = row.CommittedAtUtc,
                IsHistoricalBackfill = row.IsHistoricalBackfill,
                SourceVersion = row.SourceVersion,
                DeletionGeneration = row.DeletionGeneration
            });
            // Discard tracked old cursor snapshots. Fresh locked state is the only reservation input.
            foreach (var tracked in database.ChangeTracker.Entries<GroupSourceStateRecord>().Where(x => x.Entity.TenantId == scope.TenantId
                && x.Entity.CompanyId == scope.CompanyId && x.Entity.BindingId == scope.SourceBindingId).ToArray()) tracked.State = EntityState.Detached;
            database.Attach(state); staged.Add(state);
            state.ScheduledThroughSequence = prefix.AllocatedThrough;
            if (prefix.HasUnallocatedSuffix)
            {
                var first = prefix.FirstUnallocated ?? throw Unavailable();
                if (first.CommittedAtUtc > state.LastPendingAtUtc) throw Unavailable();
                state.FirstPendingAtUtc = first.CommittedAtUtc;
            }
            else { state.FirstPendingAtUtc = null; state.LastPendingAtUtc = null; }
            await FinalAuthorityAsync();
            await database.SaveChangesAsync(cancellationToken);
            await FinalAuthorityAsync();
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Receipt(scope, batch, selected, false);

            async Task FinalAuthorityAsync()
            {
                await directory.RequireCurrentAsync(authority, cancellationToken);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
            }
        }
        catch (DbUpdateException) { throw new GroupBatchAllocationCommitException(); }
        catch (Exception error) when (error is SqlException or OverflowException)
        { throw Unavailable(); }
        finally { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; }
        void Add(object entity) { database.Add(entity); staged.Add(entity); }
    }

    private async Task<GroupBatchAllocationReceipt> RestoreAsync(GroupScope scope, GroupBatchAllocationRecord batch,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (batch.Id == Guid.Empty || batch.OperationId == Guid.Empty || batch.AfterSequence < 0
            || batch.AllocatedThroughSequence <= batch.AfterSequence || batch.ObservedCommittedThroughSequence < batch.AllocatedThroughSequence
            || batch.RawRevisionCount is < 1 or > GroupBatchAllocationPrefix.MaximumRawRevisions
            || batch.AllocatedThroughSequence - batch.AfterSequence != batch.RawRevisionCount || batch.SourceVersion <= 0
            || batch.DeletionGeneration < 0 || batch.AccountVersion <= 0 || batch.ServiceId == Guid.Empty
            || batch.CredentialEpoch <= 0 || batch.GrantVersion <= 0 || batch.AllocatedAtUtc.Offset != TimeSpan.Zero
            || batch.AllocatedAtUtc > now) throw Unavailable();
        var rows = await database.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch.Id)
            .OrderBy(x => x.CommittedSequence).Take(GroupBatchAllocationPrefix.MaximumCandidateRows).ToArrayAsync(cancellationToken);
        var originals = await Candidates(scope).Where(x => x.CommittedSequence > batch.AfterSequence
            && x.CommittedSequence <= batch.AllocatedThroughSequence).OrderBy(x => x.CommittedSequence)
            .Take(GroupBatchAllocationPrefix.MaximumCandidateRows).ToArrayAsync(cancellationToken);
        if (rows.Length != batch.RawRevisionCount || originals.Length != rows.Length) throw Unavailable();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index]; var original = originals[index]; ValidateOriginal(original, now);
            if (row.CommittedSequence != original.CommittedSequence || row.MessageId != original.MessageId || row.Revision != original.Revision
                || row.ContentSha256 != original.ContentSha256 || row.Kind != original.Kind || row.CommittedAtUtc != original.CommittedAtUtc
                || row.IsHistoricalBackfill != original.IsHistoricalBackfill || row.SourceVersion != original.SourceVersion
                || row.DeletionGeneration != original.DeletionGeneration || original.CommittedAtUtc > batch.AllocatedAtUtc) throw Unavailable();
        }
        var prefix = GroupBatchAllocationPrefix.Select(scope, batch.AfterSequence, batch.AllocatedThroughSequence,
            originals.Select(x => Metadata(scope, x)).ToArray());
        if (prefix.AllocatedThrough != batch.AllocatedThroughSequence || prefix.IsHistoricalBackfill != batch.IsHistoricalBackfill) throw Unavailable();
        var state = await database.GroupSourceStates.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, cancellationToken) ?? throw Unavailable();
        ValidateState(state);
        if (state.ScheduledThroughSequence < batch.AllocatedThroughSequence || state.CommittedSequence < batch.ObservedCommittedThroughSequence) throw Unavailable();
        return Receipt(scope, batch, originals, true);
    }

    private IQueryable<Candidate> Candidates(GroupScope scope) => database.GroupMessageRevisions.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId)
        .Select(x => new Candidate
        {
            MessageId = x.MessageId,
            Revision = x.Revision,
            CommittedSequence = x.CommittedSequence,
            ContentSha256 = x.ContentSha256,
            Kind = x.Kind,
            CommittedAtUtc = x.CommittedAtUtc,
            IsHistoricalBackfill = x.IsHistoricalBackfill,
            SourceVersion = x.SourceVersion,
            DeletionGeneration = x.DeletionGeneration,
            ReceiptCount = database.GroupIngressReceipts.Count(r => r.TenantId == x.TenantId && r.CompanyId == x.CompanyId
                && r.BindingId == x.BindingId && r.MessageId == x.MessageId && r.Revision == x.Revision),
            ValidReceiptCount = database.GroupIngressReceipts.Count(r => r.TenantId == x.TenantId && r.CompanyId == x.CompanyId
                && r.BindingId == x.BindingId && r.MessageId == x.MessageId && r.Revision == x.Revision && r.CommittedAtUtc == x.CommittedAtUtc
                && r.ServiceId != Guid.Empty && r.CredentialEpoch > 0 && r.ListenerEpoch > 0)
        });

    private static void ValidateState(GroupSourceStateRecord state)
    {
        if (state.ScheduledThroughSequence < 0 || state.CommittedSequence < state.ScheduledThroughSequence
            || (state.FirstPendingAtUtc is null) != (state.LastPendingAtUtc is null)
            || (state.ScheduledThroughSequence == state.CommittedSequence) != (state.FirstPendingAtUtc is null)
            || (state.FirstPendingAtUtc is not null && (state.FirstPendingAtUtc.Value.Offset != TimeSpan.Zero
                || state.LastPendingAtUtc!.Value.Offset != TimeSpan.Zero || state.FirstPendingAtUtc > state.LastPendingAtUtc))) throw Unavailable();
    }

    private static void ValidateOriginal(Candidate row, DateTimeOffset now)
    {
        if (row.SourceVersion <= 0 || row.DeletionGeneration < 0 || row.ReceiptCount != 1 || row.ValidReceiptCount != 1
            || row.CommittedAtUtc.Offset != TimeSpan.Zero || row.CommittedAtUtc > now) throw Unavailable();
    }
    private static GroupPendingRevisionMetadata Metadata(GroupScope scope, Candidate row) => new(scope,
        row.MessageId, row.Revision, row.CommittedSequence, row.ContentSha256, row.Kind, row.CommittedAtUtc, row.IsHistoricalBackfill);
    private static GroupBatchAllocationReceipt Receipt(GroupScope scope, GroupBatchAllocationRecord batch, Candidate[] rows, bool previous) =>
        new(scope, batch.Id, batch.OperationId, batch.AfterSequence, batch.AllocatedThroughSequence, batch.ObservedCommittedThroughSequence,
            batch.IsHistoricalBackfill, batch.SourceVersion, batch.DeletionGeneration, batch.AccountVersion, batch.ServiceId,
            batch.CredentialEpoch, batch.GrantVersion, batch.AllocatedAtUtc,
            Array.AsReadOnly(rows.Select(x => new GroupAllocatedRevision(Metadata(scope, x), x.SourceVersion, x.DeletionGeneration)).ToArray()), previous);
    private static InvalidOperationException Unavailable() => new("Group batch allocation is not available.");
    private sealed class Candidate
    {
        public Guid MessageId { get; init; }
        public long Revision { get; init; }
        public long CommittedSequence { get; init; }
        public string ContentSha256 { get; init; } = "";
        public GroupSourceEventKind Kind { get; init; }
        public DateTimeOffset CommittedAtUtc { get; init; }
        public bool IsHistoricalBackfill { get; init; }
        public long SourceVersion { get; init; }
        public long DeletionGeneration { get; init; }
        public int ReceiptCount { get; init; }
        public int ValidReceiptCount { get; init; }
    }
}
