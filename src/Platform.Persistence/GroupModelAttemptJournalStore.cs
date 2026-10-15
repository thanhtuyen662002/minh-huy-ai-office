using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// A start permit is returned only after the append is committed. It is not a
// work-note capability; callers must fence again after a provider wait and the
// effect store must validate all dependencies inside its own final transaction.
internal sealed class GroupModelAttemptPermit(GroupAutomaticModelContext context, GroupModelAttemptStarted start,
    GroupModelAttemptOriginalBinding? original = null, GroupModelAttemptAdmissionPreparer.PreparedAttempt? admission = null)
{
    internal GroupAutomaticModelContext Context { get; } = context;
    internal GroupModelAttemptStarted Start { get; } = start;
    internal GroupModelAttemptOriginalBinding? Original { get; } = original;
    internal GroupModelAttemptAdmissionPreparer.PreparedAttempt? Admission { get; } = admission;
    public override string ToString() => "Committed group model attempt (private metadata).";
}
internal sealed record GroupModelAttemptStartResult(GroupModelAttemptBudgetDecision Budget,
    GroupModelAttemptPermit? Permit, bool WasAlreadyStarted);

// SQL-only journal. Every call holds the original source lock, rechecks exact
// source/brain manifests and current authority, and never invokes a provider.
// Structural exhaustion is returned only as metadata; no attention capability
// or worker registration is provided by this preparation.
internal sealed partial class GroupModelAttemptJournalStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain,
    GroupModelAttemptAdmissionPreparer? admissions = null)
{
    private const string Savepoint = "GroupModelAttemptJournalWrites";

    // Structural/context validation shared with the fixed accepted-result
    // read unit. This grants no current SQL authority or mutation capability.
    internal void RequireOriginalReadEntry(GroupModelAttemptOriginalBinding original, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(original); token.ThrowIfCancellationRequested();
        ValidateEntry(original.Context);
    }

    internal Task<GroupModelAttemptStartResult> TryStartAsync(GroupAutomaticModelContext context, Guid requestId,
        CancellationToken token = default) => TryStartCoreAsync(context, requestId, null, token);

    internal Task<GroupModelAttemptStartResult> TryStartOriginalAsync(GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk restored, Guid requestId, CancellationToken token = default)
    {
        var original = GroupModelAttemptOriginalBinding.Create(plan, restored, token);
        return TryStartCoreAsync(original.Context, requestId, original, token);
    }

    // The result store reuses this same owned transaction, lock, full original
    // plan authentication, savepoint, expiry witness and staged-row cleanup.
    // Its additional fence runs before and after both manifest inspections.
    internal Task<T> InOriginalCurrentUnitAsync<T>(GroupModelAttemptOriginalBinding original,
        Func<DateTimeOffset, List<object>, CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task> additionalFence, CancellationToken token, GroupModelAttemptPermit? admittedPermit = null)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(additionalFence); token.ThrowIfCancellationRequested();
        ValidateEntry(original.Context);
        if (admittedPermit?.Admission is { } admitted)
        {
            if (!ReferenceEquals(admittedPermit.Context, original.Context) || admittedPermit.Original is not { } plan
                || plan.Plan.Sha256 != original.Plan.Sha256) throw Unavailable();
            var issuedFence = CreateIssuedFence(admitted, original.Context, token);
            return InCurrentUnitAsync(original.Context, original, operation, token, async ct =>
            {
                await additionalFence(ct);
                await RequireAdmittedPermitLockedAsync(admittedPermit, ct);
            }, issuedFence);
        }
        return InCurrentUnitAsync(original.Context, original, operation, token, additionalFence);
    }

    private Task<GroupModelAttemptStartResult> TryStartCoreAsync(GroupAutomaticModelContext context, Guid requestId,
        GroupModelAttemptOriginalBinding? originalPlan, CancellationToken token,
        GroupModelAttemptAdmissionPreparer.PreparedAttempt? admitted = null,
        Func<GroupBatchClaimExpiryObservation?, CancellationToken, GroupBatchClaimExpiryObservation?>? issuedFence = null)
    {
        ArgumentNullException.ThrowIfNull(context); token.ThrowIfCancellationRequested();
        if (requestId == Guid.Empty) throw Unavailable();
        ValidateEntry(context);
        var identity = GroupModelAttemptBudget.InputIdentity(context);
        return InCurrentUnitAsync<GroupModelAttemptStartResult>(context, originalPlan, async (now, staged, cancellationToken) =>
        {
            var (starts, failures) = await ReadJournalAsync(context, identity, cancellationToken);
            var decision = GroupModelAttemptBudget.Read(context.Scope, context.BatchId, identity, starts, failures, now, cancellationToken);
            var scope = context.Scope;
            var original = await database.GroupModelAttemptStarts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.RequestId == requestId, cancellationToken);
            if (original is not null)
            {
                if (original.BatchId != context.BatchId || original.InputSha256 != identity
                    || !starts.Any(x => x.RequestId == requestId)) throw Unavailable();
                if (admitted is not null)
                    await RequireAdmissionRowLockedAsync(admitted, Start(scope, original), cancellationToken);
                // A lost reply/replayed nonce cannot grant a second provider
                // invocation, including after replacement by another claim.
                return new(decision, null, true);
            }
            if (decision.Disposition != GroupModelAttemptDisposition.Available) return new(decision, null, false);
            var receipt = context.Preparation.Context.Handle.Receipt;
            var deadline = now.Add(GroupModelAttemptBudget.MaximumAttemptLifetime);
            if (deadline > receipt.ExpiresAtUtc) deadline = receipt.ExpiresAtUtc;
            if (admitted is not null)
            {
                if (now < admitted.StartedAtUtc || now >= admitted.ExpiresAtUtc) throw Unavailable();
                if (deadline > admitted.ExpiresAtUtc) deadline = admitted.ExpiresAtUtc;
            }
            if (deadline <= now) throw Unavailable();
            var ordinal = decision.ConsumedAttempts + 1;
            var previous = starts.LastOrDefault();
            var record = new GroupModelAttemptStartRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                RequestId = requestId,
                BatchId = context.BatchId,
                InputSha256 = identity,
                Ordinal = ordinal,
                PredecessorOrdinal = previous?.Ordinal,
                PredecessorRequestId = previous?.RequestId,
                ClaimOperationId = receipt.OperationId,
                ClaimEpoch = receipt.Epoch,
                StartedAtUtc = now,
                DeadlineUtc = deadline
            };
            var start = Start(scope, record);
            var after = GroupModelAttemptBudget.Read(scope, context.BatchId, identity, starts.Append(start), failures, now, cancellationToken);
            if (after.Disposition != GroupModelAttemptDisposition.Wait) throw Unavailable();
            database.Add(record); staged.Add(record);
            if (admitted is not null)
            {
                var companion = AdmissionRecord(admitted);
                database.Add(companion); staged.Add(companion);
            }
            var saved = await database.SaveChangesAsync(cancellationToken);
            if (admitted is not null && saved != 2) throw Unavailable();
            return new(after, new(context, start, originalPlan, admitted), false);
        }, token, admitted is null ? null : ct => RequireAdmittedOriginalLockedAsync(admitted, ct), issuedFence);
    }

    internal Task<GroupModelAttemptBudgetDecision> RecordFailureAsync(GroupAutomaticModelContext context,
        GroupModelAttemptPermit permit, GroupModelAttemptFailure failure, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(permit); token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(permit.Context, context) || !Enum.IsDefined(failure)) throw Unavailable();
        ValidateEntry(context);
        var identity = GroupModelAttemptBudget.InputIdentity(context);
        if (permit.Start.Scope != context.Scope || permit.Start.BatchId != context.BatchId || permit.Start.InputSha256 != identity) throw Unavailable();
        if (permit.Original is { } original && !ReferenceEquals(original.Context, context)) throw Unavailable();
        var issuedFence = permit.Admission is { } admitted ? CreateIssuedFence(admitted, context, token) : null;
        return InCurrentUnitAsync(context, permit.Original, async (now, staged, cancellationToken) =>
        {
            var scope = context.Scope;
            var (starts, failures) = await ReadJournalAsync(context, identity, cancellationToken);
            var start = starts.SingleOrDefault(x => x.RequestId == permit.Start.RequestId) ?? throw Unavailable();
            if (start != permit.Start || now < start.StartedAtUtc || now > start.DeadlineUtc) throw Unavailable();
            var decision = GroupModelAttemptBudget.Read(scope, context.BatchId, identity, starts, failures, now, cancellationToken);
            var original = failures.SingleOrDefault(x => x.RequestId == start.RequestId);
            if (original is not null)
            {
                if (original.Failure != failure) throw Unavailable();
                return decision;
            }
            // Only the current final attempt can receive its first failure.
            // An earlier observation cannot retroactively change retry policy.
            if (starts[^1].RequestId != start.RequestId) throw Unavailable();
            var record = new GroupModelAttemptFailureRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                RequestId = start.RequestId,
                Failure = (int)failure,
                StartedAtUtc = start.StartedAtUtc,
                DeadlineUtc = start.DeadlineUtc,
                ObservedAtUtc = now
            };
            var after = GroupModelAttemptBudget.Read(scope, context.BatchId, identity, starts,
                failures.Append(new(scope, start.RequestId, failure, now)), now, cancellationToken);
            database.Add(record); staged.Add(record);
            await database.SaveChangesAsync(cancellationToken);
            return after;
        }, token, issuedFence is null ? null : ct => RequireAdmittedPermitLockedAsync(permit, ct), issuedFence);
    }

    private async Task<T> InCurrentUnitAsync<T>(GroupAutomaticModelContext context, GroupModelAttemptOriginalBinding? original,
        Func<DateTimeOffset, List<object>, CancellationToken, Task<T>> operation, CancellationToken token,
        Func<CancellationToken, Task>? additionalFence = null,
        Func<GroupBatchClaimExpiryObservation?, CancellationToken, GroupBatchClaimExpiryObservation?>? issuedFence = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); token = deadline.Token;
        var handle = context.Preparation.Context.Handle;
        var expected = original?.Plan.Dependencies ?? GroupWorkDependencyManifest.Read(
            GroupWorkDependencyManifest.Create(context.Preparation.Context, context.Brain, Guid.NewGuid()));
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var permissions = new GroupModelAttemptPermissionVerifier(database);
        var staged = new List<object>();
        try
        {
            var began = UtcNow();
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token);
            var sql = database.Database.CurrentTransaction ?? throw Unavailable();
            if (!sql.SupportsSavepoints) throw Unavailable();
            var verdict = await InspectAsync();
            await RequireCurrentOrRetireAsync(verdict, transaction, sql, false);
            await sql.CreateSavepointAsync(Savepoint, token);
            var now = UtcNow(); if (now < began) throw Unavailable();
            if (issuedFence is not null && now >= handle.Receipt.ExpiresAtUtc)
                await RequireCurrentOrRetireAsync(ApplyIssuedFence(new GroupBatchClaimFenceVerdict.Expired(new(handle, now))), transaction, sql, false);
            var result = await operation(now, staged, token);
            verdict = await InspectAsync();
            await RequireCurrentOrRetireAsync(verdict, transaction, sql, true);
            var finalAt = UtcNow();
            if (issuedFence is not null && finalAt >= handle.Receipt.ExpiresAtUtc)
                await RequireCurrentOrRetireAsync(ApplyIssuedFence(new GroupBatchClaimFenceVerdict.Expired(new(handle, finalAt))), transaction, sql, true);
            if (finalAt < now || database.ChangeTracker.HasChanges()) throw Unavailable();
            await transaction.CommitAsync(token);
            token.ThrowIfCancellationRequested();
            var completed = UtcNow();
            if (completed < now || (result is GroupModelAttemptStartResult { Permit: { } permit }
                && completed >= permit.Start.DeadlineUtc)) throw Unavailable();
            return result;

            async Task<GroupBatchClaimFenceVerdict> InspectAsync()
            {
                await permissions.RequireSafeRuntimeAsync(token);
                // A retained authentic expiry must reach retirement even if
                // UTC has rewound before the original claim was issued.
                // Retirement independently reauthenticates actual current SQL
                // state/authority and refuses a foreign/replacement claim.
                if (issuedFence is not null)
                {
                    var retained = InspectIssuedExpiry(null);
                    if (retained is not null) return retained;
                    var first = ApplyIssuedFence(await claims.InspectCurrentLockedAsync(handle, token));
                    if (first is not GroupBatchClaimFenceVerdict.Current) return first;
                }
                if (additionalFence is not null) await additionalFence(token);
                var current = ApplyIssuedFence(await sources.RequireManifestUnchangedLockedAsync(handle, expected, token));
                if (current is GroupBatchClaimFenceVerdict.Current)
                    current = ApplyIssuedFence(await brain.RequireManifestUnchangedLockedAsync(handle, expected, token));
                if (current is GroupBatchClaimFenceVerdict.Current)
                {
                    if (original is not null)
                        await new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain)
                            .RequireOriginalUnchangedLockedAsync(handle, original.Plan, token);
                    await permissions.RequireSafeRuntimeAsync(token);
                    if (additionalFence is not null) await additionalFence(token);
                    current = ApplyIssuedFence(await claims.InspectCurrentLockedAsync(handle, token));
                }
                return current;
            }

            GroupBatchClaimFenceVerdict ApplyIssuedFence(GroupBatchClaimFenceVerdict current)
            {
                if (issuedFence is null || current is not (GroupBatchClaimFenceVerdict.Current or GroupBatchClaimFenceVerdict.Expired)) return current;
                return InspectIssuedExpiry((current as GroupBatchClaimFenceVerdict.Expired)?.Observation) ?? current;
            }

            GroupBatchClaimFenceVerdict.Expired? InspectIssuedExpiry(GroupBatchClaimExpiryObservation? sqlObserved)
            {
                var observed = issuedFence?.Invoke(sqlObserved, token);
                if (observed is null) return null;
                if (!ReferenceEquals(observed.Handle, handle) || observed.ObservedAtUtc.Offset != TimeSpan.Zero
                    || observed.ObservedAtUtc < handle.Receipt.ExpiresAtUtc) throw Unavailable();
                return new GroupBatchClaimFenceVerdict.Expired(observed);
            }

            async Task RequireCurrentOrRetireAsync(GroupBatchClaimFenceVerdict current,
                DataSourceRegistrationTransaction unit, IDbContextTransaction currentSql, bool rollbackWrites)
            {
                if (current is GroupBatchClaimFenceVerdict.Current) return;
                if (current is not GroupBatchClaimFenceVerdict.Expired expired) throw Unavailable();
                if (rollbackWrites) { await currentSql.RollbackToSavepointAsync(Savepoint, token); DetachStaged(); }
                await claims.RetireExpiredLockedAsync(expired.Observation, token);
                await unit.CommitAsync(token);
                token.ThrowIfCancellationRequested();
                throw GroupServiceDirectory.Denied();
            }
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException or OverflowException)
        { throw Unavailable(); }
        finally { DetachStaged(); }

        void DetachStaged()
        { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; staged.Clear(); }
    }

    private Task<(GroupModelAttemptStarted[] Starts, GroupModelAttemptFailed[] Failures)> ReadJournalAsync(
        GroupAutomaticModelContext context, string identity, CancellationToken token) =>
        GroupModelAttemptJournalReader.ReadAsync(database, context, identity, token);

    private void ValidateEntry(GroupAutomaticModelContext context)
    {
        worker.Validate(); context.Scope.Validate(); var handle = context.Preparation.Context.Handle;
        if (!ReferenceEquals(handle, context.Brain.Handle) || context.Scope != handle.Receipt.Scope
            || context.BatchId != handle.Receipt.BatchId || context.Scope.TenantId != worker.TenantId
            || context.Scope.CompanyId != worker.CompanyId || handle.Receipt.ServiceId != worker.ServiceId
            || handle.Receipt.CredentialEpoch != worker.CredentialEpoch) throw GroupServiceDirectory.Denied();
        if (sources is null || brain is null || !sources.UsesContext(database, worker, clock) || !brain.UsesContext(database, worker, clock)
            || !database.Database.IsSqlServer() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
    }

    private DateTimeOffset UtcNow()
    { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }
    private static GroupModelAttemptStarted Start(GroupScope scope, GroupModelAttemptStartRecord record) =>
        new(scope, record.BatchId, record.InputSha256, record.Ordinal, record.RequestId, record.PredecessorRequestId,
            record.ClaimOperationId, record.ClaimEpoch, record.StartedAtUtc, record.DeadlineUtc);
    private static InvalidOperationException Unavailable() => new("Group model attempt journal is not available.");
}
