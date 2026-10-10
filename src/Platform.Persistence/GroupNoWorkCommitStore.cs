using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupNoWorkCommitResult(GroupScope Scope, Guid BatchId, Guid OperationId,
    string SourceSetSha256, int SelectedMessageCount, DateTimeOffset CommittedAtUtc, bool WasAlreadyCommitted);

// First fixed effect consumer: selected model-eligible NoWork receipts only.
// It cannot manufacture notes, host attention, raw completion or an outbox.
public sealed class GroupNoWorkCommitStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain)
{
    private const string EffectSavepoint = "aioffice_group_no_work_effect";

    public async Task<GroupNoWorkCommitResult> CommitAsync(GroupGroundedWorkProposal proposal,
        GroupBrainPrivateContext dependencies, Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal); ArgumentNullException.ThrowIfNull(dependencies);
        var context = proposal.Preparation.Context; var handle = context.Handle; var scope = context.Scope;
        ValidateInput(proposal, dependencies, operationId, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        var selected = context.Items.ToDictionary(x => x.MessageId, x => x.Revision);
        var sourceHash = SourceHash(selected);
        var staged = new List<object>(); var savepointCreated = false; DateTimeOffset? effectTime = null;
        try
        {
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            var sql = database.Database.CurrentTransaction;
            if (sql is null || !sql.SupportsSavepoints) throw Unavailable();
            var claims = new GroupBatchClaimStore(database, worker, clock);
            var permissions = new GroupWorkNotePermissionVerifier(database);
            // This initial inspection takes the transaction-owned source lock
            // before the effect savepoint. Its locks survive effect rollback.
            await FenceAsync();
            var prior = await database.GroupWorkCommitReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId
                && x.OperationId == operationId, cancellationToken);
            if (prior is not null)
            {
                await ValidateReplayAsync(prior);
                await FenceAsync();
                await transaction.CommitAsync(cancellationToken);
                return Result(prior, true);
            }
            var ids = selected.Keys.ToArray();
            if (await database.GroupWorkSourceDispositions.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == context.BatchId
                && ids.Contains(x.MessageId), cancellationToken)) throw Unavailable();
            await sql.CreateSavepointAsync(EffectSavepoint, cancellationToken); savepointCreated = true;
            var now = UtcNow();
            if (now < handle.Receipt.IssuedAtUtc) throw Unavailable();
            effectTime = now;
            var receipt = new GroupWorkCommitReceiptRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = context.BatchId,
                OperationId = operationId,
                SourceSetSha256 = sourceHash,
                SelectedMessageCount = selected.Count,
                NoteCount = 0,
                Outcome = GroupWorkCommitOutcome.NoWork,
                ServiceId = handle.Receipt.ServiceId,
                ClaimEpoch = handle.Receipt.Epoch,
                CredentialEpoch = handle.Receipt.CredentialEpoch,
                GrantVersion = handle.Receipt.GrantVersion,
                SourceVersion = handle.Receipt.SourceVersion,
                DeletionGeneration = handle.Receipt.DeletionGeneration,
                AccountVersion = handle.Receipt.AccountVersion,
                CommittedAtUtc = now
            };
            staged.Add(receipt);
            staged.AddRange(selected.Select(x => new GroupWorkSourceDispositionRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = context.BatchId,
                OperationId = operationId,
                MessageId = x.Key,
                MessageRevision = x.Value,
                Outcome = GroupWorkSourceOutcome.NoWork
            }));
            database.AddRange(staged);
            await database.SaveChangesAsync(cancellationToken);
            // These fixed effects have already reached SQL. An expired final
            // verdict must roll them back before writing only its witness.
            await FenceAsync();
            if (database.ChangeTracker.HasChanges()) throw Unavailable();
            await transaction.CommitAsync(cancellationToken);
            return Result(receipt, false);

            async Task FenceAsync()
            {
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                await ObserveAsync(await claims.InspectCurrentLockedAsync(handle, cancellationToken));
                await ObserveAsync(await sources.RequireUnchangedLockedAsync(context, cancellationToken));
                await ObserveAsync(await brain.RequireUnchangedLockedAsync(dependencies, cancellationToken));
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                if (effectTime is { } minimum && UtcNow() < minimum) throw GroupServiceDirectory.Denied();
                await ObserveAsync(await claims.InspectCurrentLockedAsync(handle, cancellationToken));
            }
            async Task ObserveAsync(GroupBatchClaimFenceVerdict verdict)
            {
                if (verdict is not GroupBatchClaimFenceVerdict.Expired expired) return;
                if (savepointCreated)
                {
                    await sql.RollbackToSavepointAsync(EffectSavepoint, cancellationToken);
                    DetachStaged();
                }
                if (database.ChangeTracker.HasChanges()) throw Unavailable();
                await claims.RetireExpiredLockedAsync(expired.Observation, cancellationToken);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw GroupServiceDirectory.Denied();
            }
            async Task ValidateReplayAsync(GroupWorkCommitReceiptRecord original)
            {
                if (original.BatchId != context.BatchId || original.SourceSetSha256 != sourceHash
                    || original.SelectedMessageCount != selected.Count || original.NoteCount != 0 || original.Outcome != GroupWorkCommitOutcome.NoWork
                    || original.ServiceId != handle.Receipt.ServiceId || original.ClaimEpoch <= 0 || original.ClaimEpoch > handle.Receipt.Epoch
                    || original.CredentialEpoch != handle.Receipt.CredentialEpoch || original.GrantVersion != handle.Receipt.GrantVersion
                    || original.SourceVersion != handle.Receipt.SourceVersion || original.DeletionGeneration != handle.Receipt.DeletionGeneration
                    || original.AccountVersion != handle.Receipt.AccountVersion || original.CommittedAtUtc.Offset != TimeSpan.Zero
                    || original.CommittedAtUtc > UtcNow()) throw Unavailable();
                var rows = await database.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == context.BatchId
                    && x.OperationId == operationId).Take(101).ToArrayAsync(cancellationToken);
                if (rows.Length != selected.Count || rows.Select(x => x.MessageId).Distinct().Count() != rows.Length
                    || rows.Any(x => x.Outcome != GroupWorkSourceOutcome.NoWork || !selected.TryGetValue(x.MessageId, out var revision)
                        || revision != x.MessageRevision)) throw Unavailable();
                if (await database.GroupCustomerRequests.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OriginBatchId == context.BatchId
                    && x.OriginOperationId == operationId, cancellationToken)
                    || await database.GroupNotesCommittedOutbox.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                        && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == context.BatchId
                        && x.OperationId == operationId, cancellationToken)) throw Unavailable();
            }
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or InvalidOperationException or ArgumentException)
        { throw Unavailable(); }
        finally { DetachStaged(); }

        void DetachStaged()
        { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; }
        GroupNoWorkCommitResult Result(GroupWorkCommitReceiptRecord value, bool previous) =>
            new(scope, value.BatchId, value.OperationId, value.SourceSetSha256, value.SelectedMessageCount, value.CommittedAtUtc, previous);
    }

    private void ValidateInput(GroupGroundedWorkProposal proposal, GroupBrainPrivateContext dependencies, Guid operationId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); worker.Validate(); proposal.Scope.Validate();
        if (proposal.Scope.TenantId != worker.TenantId || proposal.Scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        var preparation = proposal.Preparation; var context = preparation.Context;
        if (operationId == Guid.Empty || dependencies.Handle.Receipt != context.Handle.Receipt || proposal.Notes.Count != 0
            || context.HasCoverageGap || context.Items.Count is < 1 or > 100 || preparation.Candidates.Count != context.Items.Count
            || preparation.Receipts.Any(x => x.Disposition != GroupSourcePreparationDisposition.ModelText || x.HasUnsupportedMedia)
            || proposal.SourceDispositions.Count != context.Items.Count
            || proposal.SourceDispositions.Any(x => x.Disposition != GroupModelSourceDisposition.NoWork)) throw Unavailable();
        if (!database.Database.IsSqlServer() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (ArgumentException) { throw Unavailable(); }
    }
    private DateTimeOffset UtcNow()
    { var now = clock.GetUtcNow(); return now.Offset == TimeSpan.Zero ? now : throw Unavailable(); }
    private static string SourceHash(IReadOnlyDictionary<Guid, long> selected)
    {
        var metadata = string.Join("\n", selected.OrderBy(x => x.Key).Select(x => x.Key.ToString("D") + "/" + x.Value.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(metadata)));
    }
    private static InvalidOperationException Unavailable() => new("Group work commit is unavailable.");
}
