using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupOriginalAdmissionRuntimeProof
{
    internal const string JournalExpiryLine = "PASS owned admitted journal authentic expiry observed UTC before claim issuance actual entry exact witness old interval refused replacement current no second attempt accepted graphs retained zero provider";

    // Called only after the old epoch-three recovery/corruption/permission/read
    // proofs finish. This adds no accepted result/start/effect or model call.
    private static async Task RequireJournalRetainedExpiryAsync(PlatformDbContext db, GroupScope scope,
        GroupBatchClaimHandle handle, GroupExtractionWorkerBinding worker, GroupNoteRuntimeProof.OwnedClock clock,
        GroupBatchSourceReader sources, GroupBrainCurrentReader brain, GroupBatchSourcePreparation preparation,
        GroupBrainPrivateContext dependencies, GroupAutomaticInputPlan plan, CancellationToken token)
    {
        var original = plan.RestoreChunks(preparation, dependencies, Profile, [], token).Single();
        var sidecar = await new GroupAutomaticInputPlanStore(db, worker, clock, sources, brain).ReadOriginalAdmissionAsync(handle, plan, token)
            ?? throw new InvalidOperationException();
        var probe = new AdmittedExecutionSqlProbe();
        // Reuse the existing actual Gateway/catalog composition, with the
        // same shared clock wrapper for issuer and SQL owner.
        var rewind = new RetainedExpiryClock(clock);
        var (issuer, coldAdapter) = ExecutionIssuer(db, rewind, probe, original);
        var prepared = await issuer.PrepareAsync(plan, original, sidecar, token);
        // Readers must use the exact same owning clock; no context mismatch
        // may masquerade as the regression's successful refusal.
        var fixedSources = new GroupBatchSourceReader(db, worker, rewind,
            new GroupNoteRuntimeProof.CountedKeys(db, scope), new());
        var fixedBrain = new GroupBrainCurrentReader(db, worker, rewind,
            new GroupNoteRuntimeProof.CountedKeys(db, scope), new());
        var journal = new GroupModelAttemptJournalStore(db, worker, rewind, fixedSources, fixedBrain, issuer);
        var counts = await Counts(); Phase = "admitted-journal-retained-before-issuance";
        rewind.Arm(handle.Receipt.ExpiresAtUtc, handle.Receipt.IssuedAtUtc.AddTicks(-1));
        await RefusedAsync(() => journal.TryStartAdmittedOriginalAsync(prepared, token));
        if (!rewind.Observed || clock.Current != handle.Receipt.IssuedAtUtc.AddTicks(-1)) throw new InvalidOperationException();
        RequireClean(db);
        var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId, token);
        if (state.Epoch != handle.Receipt.Epoch || state.ExpiryObservedAtUtc != handle.Receipt.ExpiresAtUtc
            || await Counts() != counts || coldAdapter.Executions != 0) throw new InvalidOperationException();
        Phase = "admitted-journal-retained-old-interval"; clock.Current = handle.Receipt.IssuedAtUtc;
        await RefusedAsync(() => new GroupBatchClaimStore(db, worker, rewind).ReadCurrentAllocationAsync(handle, token));
        clock.Current = handle.Receipt.ExpiresAtUtc.AddTicks(1);
        var replacement = await new GroupBatchClaimStore(db, worker, rewind).TryAcquireAsync(scope, handle.Receipt.BatchId,
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token) ?? throw new InvalidOperationException();
        if (replacement.Receipt.Epoch != handle.Receipt.Epoch + 1 || replacement.CurrentHandle is null) throw new InvalidOperationException();
        var replacementState = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId, token);
        Phase = "admitted-journal-retained-replacement";
        rewind.Arm(handle.Receipt.ExpiresAtUtc, handle.Receipt.IssuedAtUtc.AddTicks(-1));
        await RefusedAsync(() => journal.TryStartAdmittedOriginalAsync(prepared, token)); RequireClean(db);
        var after = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId, token);
        if (after.Epoch != replacementState.Epoch || after.OperationId != replacementState.OperationId || after.ExpiryObservedAtUtc is not null
            || await Counts() != counts || coldAdapter.Executions != 0) throw new InvalidOperationException();
        clock.Current = replacement.Receipt.IssuedAtUtc;
        _ = await new GroupBatchClaimStore(db, worker, rewind).ReadCurrentAllocationAsync(replacement.CurrentHandle, token);
        RequireClean(db);

        async Task<(int Starts, int Companions, int Results, int Failures)> Counts() =>
            (await db.GroupModelAttemptStarts.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
             await db.GroupModelAttemptAdmissions.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
             await db.GroupModelAttemptResults.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
             await db.GroupModelAttemptFailures.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token));
    }

    private sealed class RetainedExpiryClock(GroupNoteRuntimeProof.OwnedClock shared) : TimeProvider
    {
        private bool armed;
        private DateTimeOffset observedAt, rewindAt;
        internal bool Observed { get; private set; }
        internal void Arm(DateTimeOffset observation, DateTimeOffset rewind) { observedAt = observation; rewindAt = rewind; armed = true; Observed = false; }
        public override DateTimeOffset GetUtcNow()
        {
            if (!armed) return shared.GetUtcNow();
            armed = false; Observed = true; shared.Current = rewindAt; return observedAt;
        }
    }
}
