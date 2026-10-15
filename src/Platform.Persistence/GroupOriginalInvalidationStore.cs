using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal enum GroupOriginalInvalidationOutcome
{
    MissingOriginal = 1, CurrentMetadata = 2, InvalidatedNeedsContinuation = 3,
    AlreadyCheckpointed = 4, RequiresTerminalRecovery = 5
}

// A metadata-only, zero-contributor quarantine owner. Not registered for runtime
// activation: old automatic writers must be drained before any checkpoint write.
// No result grants a model invocation, replacement original or completion.
internal sealed class GroupOriginalInvalidationStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain)
{
    private const string Savepoint = "GroupOriginalInvalidationWrites";

    internal async Task<GroupOriginalInvalidationOutcome> CheckpointAsync(GroupBatchClaimHandle handle,
        CancellationToken cancellationToken = default)
    {
        ValidateEntry(handle, cancellationToken);
        var callerToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var permissions = new GroupOriginalInvalidationPermissionVerifier(database);
        var originalStore = new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain);
        GroupOriginalInvalidationRecord? staged = null;
        GroupBatchClaimExpiryObservation? retainedExpiry = null;
        DateTimeOffset? minimumTime = null;
        var savepointCreated = false;
        var scope = handle.Receipt.Scope;
        var batch = handle.Receipt.BatchId;
        try
        {
            await using var unit = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            var sql = database.Database.CurrentTransaction ?? throw Unavailable();
            if (!sql.SupportsSavepoints) throw Unavailable();
            await FenceAsync(); // Acquires the transaction-owned source APPLOCK before savepoint.
            if (await HasTerminalAsync()) return await FinishAsync(GroupOriginalInvalidationOutcome.RequiresTerminalRecovery);
            var original = await originalStore.ReadForInvalidationLockedAsync(handle, await NowAsync(), cancellationToken);
            await FenceAsync();
            var prior = await Rows().SingleOrDefaultAsync(cancellationToken);
            if (original is null)
            {
                if (prior is not null) throw Unavailable();
                return await FinishAsync(GroupOriginalInvalidationOutcome.MissingOriginal);
            }
            if (prior is not null)
            {
                await RequireCheckpointAsync(prior, original);
                await RequireNoEffectsAsync();
                // Never resurrect a checkpoint even when metadata later matches again.
                return await FinishAsync(GroupOriginalInvalidationOutcome.AlreadyCheckpointed);
            }
            await RequireNoEffectsAsync();
            var observed = await InspectDependenciesAsync(original);
            if (observed.Matches) return await FinishAsync(GroupOriginalInvalidationOutcome.CurrentMetadata);
            var now = await NowAsync();
            var receipt = handle.Receipt;
            staged = new GroupOriginalInvalidationRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = batch,
                FormatVersion = 1,
                PlanSha256 = original.Plan.Sha256,
                OriginalClaimOperationId = original.OriginalClaimOperationId,
                OriginalClaimEpoch = original.OriginalClaimEpoch,
                ClaimOperationId = receipt.OperationId,
                ClaimEpoch = receipt.Epoch,
                ObservedDependenciesSha256 = observed.Sha256,
                CreatedAtUtc = now
            };
            staged.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(staged);
            await RequireCheckpointAsync(staged, original);
            await sql.CreateSavepointAsync(Savepoint, cancellationToken); savepointCreated = true;
            database.Add(staged);
            if (await database.SaveChangesAsync(cancellationToken) != 1) throw Unavailable();
            await FenceAsync();
            var rereadOriginal = await originalStore.ReadForInvalidationLockedAsync(handle, await NowAsync(), cancellationToken)
                ?? throw Unavailable();
            if (rereadOriginal != original && (rereadOriginal.OriginalClaimOperationId != original.OriginalClaimOperationId
                || rereadOriginal.OriginalClaimEpoch != original.OriginalClaimEpoch || rereadOriginal.CreatedAtUtc != original.CreatedAtUtc
                || !rereadOriginal.Plan.Encode().AsSpan().SequenceEqual(original.Plan.Encode()))) throw Unavailable();
            var finalObservation = await InspectDependenciesAsync(rereadOriginal);
            if (finalObservation.Matches || finalObservation.Sha256 != observed.Sha256) throw Unavailable();
            await RequireNoEffectsAsync();
            if (await HasTerminalAsync()) throw Unavailable();
            var written = await Rows().SingleOrDefaultAsync(cancellationToken) ?? throw Unavailable();
            await RequireCheckpointAsync(written, rereadOriginal);
            if (written.CheckpointSha256 != staged.CheckpointSha256) throw Unavailable();
            return await FinishAsync(GroupOriginalInvalidationOutcome.InvalidatedNeedsContinuation);

            IQueryable<GroupOriginalInvalidationRecord> Rows() => database.GroupOriginalInvalidations.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                    && x.BindingId == scope.SourceBindingId && x.BatchId == batch);

            async Task<GroupOriginalInvalidationOutcome> FinishAsync(GroupOriginalInvalidationOutcome outcome)
            {
                await FenceAsync();
                if (database.ChangeTracker.HasChanges()) throw Unavailable();
                await unit.CommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return outcome;
            }

            async Task FenceAsync()
            {
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                // Preserve authenticated expiry before any fresh claim inspection,
                // including after UTC has rewound before IssuedAtUtc.
                if (retainedExpiry is not null) await RetireAsync(retainedExpiry);
                await NowAsync();
                var verdict = await claims.InspectCurrentLockedAsync(handle, cancellationToken);
                if (verdict is GroupBatchClaimFenceVerdict.Expired expired) await RetireAsync(expired.Observation);
                if (verdict is not GroupBatchClaimFenceVerdict.Current) throw Unavailable();
                await NowAsync();
            }

            async Task<DateTimeOffset> NowAsync()
            {
                var nowAt = clock.GetUtcNow();
                if (nowAt.Offset != TimeSpan.Zero) throw Unavailable();
                if (nowAt >= handle.Receipt.ExpiresAtUtc) await RetireAsync(new(handle, nowAt));
                if (minimumTime is { } previous && nowAt < previous) throw Unavailable();
                minimumTime = nowAt; return nowAt;
            }

            async Task RetireAsync(GroupBatchClaimExpiryObservation observation)
            {
                if (!ReferenceEquals(observation.Handle, handle) || observation.ObservedAtUtc.Offset != TimeSpan.Zero
                    || observation.ObservedAtUtc < handle.Receipt.ExpiresAtUtc) throw Unavailable();
                retainedExpiry ??= observation;
                if (savepointCreated)
                {
                    await sql.RollbackToSavepointAsync(Savepoint, cancellationToken);
                    DetachStaged(); savepointCreated = false;
                }
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                await claims.RetireExpiredLockedAsync(retainedExpiry, cancellationToken);
                await permissions.RequireSafeRuntimeAsync(cancellationToken);
                if (database.ChangeTracker.HasChanges()) throw Unavailable();
                await unit.CommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                throw GroupServiceDirectory.Denied();
            }

            async Task<(bool Matches, string Sha256)> InspectDependenciesAsync(GroupAuthenticatedOriginalMetadata authenticated)
            {
                var manifest = authenticated.Plan.Dependencies;
                await FenceAsync();
                var source = await sources.InspectManifestLockedAsync(handle, manifest, cancellationToken);
                if (source.Verdict is GroupBatchClaimFenceVerdict.Expired sourceExpired) await RetireAsync(sourceExpired.Observation);
                var history = await brain.InspectManifestLockedAsync(handle, manifest, cancellationToken);
                if (history.Verdict is GroupBatchClaimFenceVerdict.Expired brainExpired) await RetireAsync(brainExpired.Observation);
                await FenceAsync();
                if (source.Matches is null || history.Matches is null || source.SnapshotSha256 is null || history.SnapshotSha256 is null)
                    throw Unavailable();
                return (source.Matches.Value && history.Matches.Value, GroupWorkDependencyManifest.Fingerprint(
                    "aioffice-group-zero-contributor-dependency-observation-v1", new
                    {
                        scope,
                        BatchId = batch,
                        PlanSha256 = authenticated.Plan.Sha256,
                        manifest.OperationId,
                        manifest.AllocatedThroughSequence,
                        manifest.AuthoritySha256,
                        SourceSha256 = source.SnapshotSha256,
                        BrainSha256 = history.SnapshotSha256,
                        CommittedContributors = 0,
                        LinkedEffects = 0
                    }));
            }

            async Task RequireCheckpointAsync(GroupOriginalInvalidationRecord row, GroupAuthenticatedOriginalMetadata authenticated)
            {
                var claim = await database.GroupBatchClaimReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch
                    && x.OperationId == row.ClaimOperationId, cancellationToken) ?? throw Unavailable();
                GroupOriginalInvalidationProof.Require(row, scope, batch, authenticated, claim, await NowAsync());
                var authority = handle.Authority;
                if (row.ClaimEpoch > handle.Receipt.Epoch || claim.ServiceId != authority.Principal.ServiceId
                    || claim.CredentialEpoch != authority.Principal.CredentialEpoch || claim.GrantVersion != authority.Grant.Version
                    || claim.SourceVersion != authority.Source.Version || claim.DeletionGeneration != authority.Source.DeletionGeneration
                    || claim.AccountVersion != authority.AccountVersion) throw Unavailable();
            }

            async Task<bool> HasTerminalAsync() => await database.GroupBatchTerminalReceipts.AsNoTracking().AnyAsync(x =>
                x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, cancellationToken)
                || await database.GroupTerminalAdvances.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, cancellationToken)
                || await database.GroupTerminalFrontierStates.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.LastTerminalBatchId == batch, cancellationToken);

            async Task RequireNoEffectsAsync()
            {
                // Starts, failures, admissions and accepted results are intentionally
                // retained. They are spent attempts, never committed contributors.
                if (await database.GroupWorkCommitReceipts.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, cancellationToken)
                    || await database.GroupWorkSourceDispositions.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                        && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, cancellationToken)
                    || await database.GroupWorkRawDispositions.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                        && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, cancellationToken)
                    || await database.GroupCustomerRequests.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                        && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OriginBatchId == batch, cancellationToken)
                    || await database.GroupRequestRevisions.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                        && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.SourceBatchId == batch, cancellationToken)
                    || await database.GroupNotesCommittedOutbox.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                        && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, cancellationToken)) throw Unavailable();
            }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        { throw new OperationCanceledException(callerToken); }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException or OverflowException)
        { throw Unavailable(); }
        finally { DetachStaged(); }

        void DetachStaged()
        {
            if (staged is not null) { database.Entry(staged).State = EntityState.Detached; staged = null; }
        }
    }

    private void ValidateEntry(GroupBatchClaimHandle handle, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested(); worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch)
            throw GroupServiceDirectory.Denied();
        if (sources is null || brain is null || !sources.UsesContext(database, worker, clock) || !brain.UsesContext(database, worker, clock)
            || !database.Database.IsSqlServer() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Group original invalidation checkpoint is unavailable.");
}
