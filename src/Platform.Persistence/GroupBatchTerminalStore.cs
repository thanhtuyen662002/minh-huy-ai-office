using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupBatchTerminalCommitResult(GroupScope Scope, Guid BatchId, Guid OperationId,
    string ManifestSha256, long AfterSequence, long ThroughSequence, int RawRevisionCount,
    int SelectedMessageCount, int ContributorCount, int NoteCount, DateTimeOffset CommittedAtUtc, bool WasAlreadyCommitted);

// Owns the append-only terminal receipt, not a cursor or a future write/send
// capability. Every call reconstructs every original contributor under the
// current Extract lease in its own Serializable/source-lock SQL unit.
public sealed class GroupBatchTerminalStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain)
{
    private const string EffectSavepoint = "aioffice_group_terminal_effect";

    public async Task<GroupBatchTerminalCommitResult> CommitAsync(GroupBatchClaimHandle handle, Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateEntry(handle, operationId, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        var staged = new List<object>(); var savepointCreated = false; DateTimeOffset? minimumTime = null;
        try
        {
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            var sql = database.Database.CurrentTransaction;
            if (sql is null || !sql.SupportsSavepoints) throw Unavailable();
            var claims = new GroupBatchClaimStore(database, worker, clock);
            var permissions = new GroupBatchTerminalPermissionVerifier(database);
            var reader = new GroupWholeBatchDependencyReader(database, worker, clock, sources, brain);
            // Initial whole read acquires the transaction-owned source APPLOCK
            // before the savepoint, so effect rollback retains that lock.
            var current = await FenceAsync(null);
            var scope = handle.Receipt.Scope;
            var priorRows = await ReceiptRows(scope, handle.Receipt.BatchId, operationId).ToArrayAsync(cancellationToken);
            if (priorRows.Length > 1) throw Unavailable();
            if (priorRows.Length == 1)
            {
                var prior = priorRows[0];
                if (prior.BatchId != handle.Receipt.BatchId || prior.OperationId != operationId) throw Unavailable();
                var original = await OriginalClaimAsync(prior.ClaimOperationId);
                GroupBatchTerminalCommitProof.Require(current, handle.Receipt, AuthorityHash(), original, prior, operationId, await ObserveNowAsync());
                current = await FenceAsync(prior.Manifest);
                GroupBatchTerminalCommitProof.Require(current, handle.Receipt, AuthorityHash(), original, prior, operationId, await ObserveNowAsync());
                if (database.ChangeTracker.HasChanges()) throw Unavailable();
                await transaction.CommitAsync(cancellationToken);
                return Result(prior, true);
            }
            var acquisition = await OriginalClaimAsync(handle.Receipt.OperationId);
            current = await FenceAsync(null);
            var now = await ObserveNowAsync();
            if (now < handle.Receipt.IssuedAtUtc) throw Unavailable();
            var receipt = GroupBatchTerminalCommitProof.Stage(current, handle.Receipt, AuthorityHash(), acquisition, operationId, now);
            var expectedManifest = receipt.Manifest.ToArray();
            await sql.CreateSavepointAsync(EffectSavepoint, cancellationToken); savepointCreated = true;
            staged.Add(receipt); database.Add(receipt);
            await database.SaveChangesAsync(cancellationToken);
            current = await FenceAsync(expectedManifest);
            // Verify the actual row that reached SQL, independently of the
            // tracked object. A valid carrier is insufficient for write-back.
            var written = await ReceiptRows(scope, handle.Receipt.BatchId, operationId).ToArrayAsync(cancellationToken);
            if (written.Length != 1) throw Unavailable();
            GroupBatchTerminalCommitProof.RequireWritten(current, handle.Receipt, AuthorityHash(), acquisition, written[0],
                operationId, expectedManifest, now, await ObserveNowAsync());
            if (database.ChangeTracker.HasChanges()) throw Unavailable();
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            return Result(written[0], false);

            async Task<GroupWholeBatchDependencyVerdict.Current> FenceAsync(byte[]? expected)
            {
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                var verdict = await reader.ReadLockedAsync(handle, cancellationToken);
                if (verdict is GroupWholeBatchDependencyVerdict.Expired expired) await ObserveExpiryAsync(expired.Observation);
                if (verdict is not GroupWholeBatchDependencyVerdict.Current fresh) throw Unavailable();
                if (expected is not null) GroupBatchTerminalManifest.RequireUnchanged(fresh, operationId, expected);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                var final = await claims.InspectCurrentLockedAsync(handle, cancellationToken);
                if (final is GroupBatchClaimFenceVerdict.Expired finalExpired) await ObserveExpiryAsync(finalExpired.Observation);
                _ = await ObserveNowAsync();
                cancellationToken.ThrowIfCancellationRequested(); return fresh;
            }

            async Task<DateTimeOffset> ObserveNowAsync()
            {
                var observed = UtcNow();
                // Retire every observed expiry, including a subsequent clock
                // rollback. Every proof-time read uses this same owned path.
                if (observed >= handle.Receipt.ExpiresAtUtc) await ObserveExpiryAsync(new GroupBatchClaimExpiryObservation(handle, observed));
                if (minimumTime is { } minimum && observed < minimum) throw Unavailable();
                minimumTime = observed; return observed;
            }

            async Task ObserveExpiryAsync(GroupBatchClaimExpiryObservation observation)
            {
                if (savepointCreated)
                {
                    await sql.RollbackToSavepointAsync(EffectSavepoint, cancellationToken);
                    DetachStaged();
                }
                if (database.ChangeTracker.HasChanges()) throw Unavailable();
                await claims.RetireExpiredLockedAsync(observation, cancellationToken);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw GroupServiceDirectory.Denied();
            }

            async Task<GroupBatchClaimReceiptRecord> OriginalClaimAsync(Guid claimOperation)
            {
                if (claimOperation == Guid.Empty) throw Unavailable();
                var rows = await database.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId
                    && x.OperationId == claimOperation).Take(2).ToArrayAsync(cancellationToken);
                return rows.Length == 1 ? rows[0] : throw Unavailable();
            }
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or InvalidOperationException or ArgumentException or OverflowException)
        { throw Unavailable(); }
        finally { DetachStaged(); }

        string AuthorityHash() => GroupBatchClaimStore.AuthorityFingerprint(handle.Authority);
        void DetachStaged() { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; }
        GroupBatchTerminalCommitResult Result(GroupBatchTerminalReceiptRecord record, bool previous) => new(handle.Receipt.Scope,
            record.BatchId, record.OperationId, Convert.ToHexString(record.ManifestSha256), record.AfterSequence, record.ThroughSequence,
            record.RawRevisionCount, record.SelectedMessageCount, record.ContributorCount, record.NoteCount, record.CommittedAtUtc, previous);
    }

    internal IQueryable<GroupBatchTerminalReceiptRecord> ReceiptRows(GroupScope scope, Guid batch, Guid operation)
    {
        scope.Validate(); if (batch == Guid.Empty || operation == Guid.Empty) throw Unavailable();
        return database.GroupBatchTerminalReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && (x.BatchId == batch || x.OperationId == operation))
            .OrderBy(x => x.BatchId).Take(3);
    }

    private void ValidateEntry(GroupBatchClaimHandle handle, Guid operation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested(); worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch) throw GroupServiceDirectory.Denied();
        if (operation == Guid.Empty || sources is null || brain is null || !sources.UsesContext(database, worker, clock)
            || !brain.UsesContext(database, worker, clock) || !database.Database.IsSqlServer()
            || database.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null
            || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
    }
    private DateTimeOffset UtcNow() { var now = clock.GetUtcNow(); return now.Offset == TimeSpan.Zero ? now : throw Unavailable(); }
    private static InvalidOperationException Unavailable() => new("Group terminal commit is not available.");
}
