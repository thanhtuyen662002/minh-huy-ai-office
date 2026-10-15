using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupOriginalAdmissionRuntimeProof
{
    internal const string JournalLine = "PASS owned original admission actual current gate prepared journal and companion both flushed rollback no rows committed exact scoped provenance current snapshot deadline capped replay no second permit accepted result FIRST zero provider";
    private static bool RollbackAdmitted;
    private static bool SawAdmittedFlush;

    private static async Task<GroupModelAttemptPermit> RequireAdmittedJournalAsync(PlatformDbContext db,
        GroupExtractionWorkerBinding worker, GroupNoteRuntimeProof.OwnedClock clock, GroupBatchSourceReader sources,
        GroupBrainCurrentReader brain, GroupModelAttemptAdmissionPreparer issuer, GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk chunk, GroupOriginalAdmissionSidecar sidecar, CancellationToken token)
    {
        Phase = "admitted-journal-prepare";
        var prepared = await issuer.PrepareAsync(GroupAutomaticInputPlan.Read(plan.Encode()), chunk,
            GroupOriginalAdmissionSidecar.Read(sidecar.Encode()), token);
        var journal = new GroupModelAttemptJournalStore(db, worker, clock, sources, brain, issuer);
        var scope = chunk.Current.ModelContext!.Scope;
        if (await StartCount() != 0 || await CompanionCount() != 0) throw new InvalidOperationException();
        Phase = "admitted-journal-rollback"; RollbackAdmitted = true; SawAdmittedFlush = false;
        var rolledBack = false;
        try { await journal.TryStartAdmittedOriginalAsync(prepared, token); }
        catch (AdmittedRollback) { rolledBack = true; }
        finally { RollbackAdmitted = false; }
        if (!rolledBack || !SawAdmittedFlush || await StartCount() != 0 || await CompanionCount() != 0) throw new InvalidOperationException();
        RequireClean(db);
        Phase = "admitted-journal-commit";
        var start = await journal.TryStartAdmittedOriginalAsync(prepared, token);
        var permit = start.Permit ?? throw new InvalidOperationException();
        if (start.WasAlreadyStarted || !ReferenceEquals(permit.Admission, prepared)
            || !ReferenceEquals(permit.Original, prepared.Original) || permit.Start.RequestId != prepared.RequestId
            || permit.Start.DeadlineUtc > prepared.ExpiresAtUtc || await StartCount() != 1 || await CompanionCount() != 1)
            throw new InvalidOperationException();
        RequireClean(db);
        var row = await db.GroupModelAttemptAdmissions.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.RequestId == prepared.RequestId, token);
        if (row.BatchId != chunk.Current.ModelContext.BatchId || row.InputSha256 != chunk.Original.InputSha256
            || row.PlanSha256 != plan.Sha256 || row.SidecarSha256 != sidecar.Sha256 || row.ChunkOrdinal != chunk.Original.Ordinal
            || row.ChunkOperationId != chunk.Original.OperationId || row.FormatVersion != 1
            || row.PreparedAtUtc != prepared.StartedAtUtc || row.ExpiresAtUtc != prepared.ExpiresAtUtc
            || row.SnapshotSha256 != prepared.Snapshot.SnapshotSha256
            || !CryptographicOperations.FixedTimeEquals(row.Snapshot, GroupStructuredAdmissionSnapshotCodec.Write(prepared.Snapshot)))
            throw new InvalidOperationException();
        Phase = "admitted-journal-replay";
        var replay = await journal.TryStartAdmittedOriginalAsync(prepared, token);
        if (!replay.WasAlreadyStarted || replay.Permit is not null || replay.Budget != start.Budget
            || await StartCount() != 1 || await CompanionCount() != 1) throw new InvalidOperationException();
        RequireClean(db); return permit;

        Task<int> StartCount() => db.GroupModelAttemptStarts.CountAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
        Task<int> CompanionCount() => db.GroupModelAttemptAdmissions.CountAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token);
    }

    private sealed class AdmittedRollback : IOException;
    private sealed class AdmittedFlushProbe(GroupScope scope) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (!RollbackAdmitted || data.Context is not PlatformDbContext db) return result;
            if (result != 2 || db.Database.CurrentTransaction is not { } tx
                || tx.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable || !tx.SupportsSavepoints
                || db.ChangeTracker.Entries<GroupModelAttemptStartRecord>().Count() != 1
                || db.ChangeTracker.Entries<GroupModelAttemptAdmissionRecord>().Count() != 1
                || await db.GroupModelAttemptStarts.CountAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token) != 1
                || await db.GroupModelAttemptAdmissions.CountAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token) != 1)
                throw new InvalidOperationException();
            SawAdmittedFlush = true; throw new AdmittedRollback();
        }
    }
}
