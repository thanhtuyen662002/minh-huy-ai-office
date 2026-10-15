using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed partial class GroupModelAttemptJournalStore
{
    // New starts only. The fixed issuer's exact request is prepared before
    // entering SQL; no model/tokenizer/key/provider work occurs in this unit.
    internal Task<GroupModelAttemptStartResult> TryStartAdmittedOriginalAsync(
        GroupModelAttemptAdmissionPreparer.PreparedAttempt issued, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (issued is null) throw Unavailable();
        var context = issued.Original.Context;
        var issuedFence = CreateIssuedFence(issued, context, token);
        return TryStartCoreAsync(context, issued.RequestId, issued.Original, token, issued, issuedFence);
    }

    private Func<GroupBatchClaimExpiryObservation?, CancellationToken, GroupBatchClaimExpiryObservation?> CreateIssuedFence(
        GroupModelAttemptAdmissionPreparer.PreparedAttempt issued, GroupAutomaticModelContext context, CancellationToken token)
    {
        if (admissions is null || !admissions.UsesContext(database, clock)
            || !ReferenceEquals(issued.Original.Context, context)) throw Unavailable();
        // Retain the FIRST authentic observation, even if SQL or a later
        // issuer clock read immediately sees UTC rewind. Retirement still
        // authenticates the current SQL claim and cannot touch a replacement.
        var observed = admissions.InspectIssuedLocked(issued, context, token);
        return InspectIssued;

        GroupBatchClaimExpiryObservation? InspectIssued(GroupBatchClaimExpiryObservation? sqlObserved, CancellationToken ct)
        {
            observed ??= sqlObserved;
            return observed ??= admissions.InspectIssuedLocked(issued, context, ct);
        }
    }

    private async Task RequireAdmittedPermitLockedAsync(GroupModelAttemptPermit permit, CancellationToken token)
    {
        var issued = permit.Admission ?? throw Unavailable();
        if (!ReferenceEquals(issued.Original, permit.Original) || !ReferenceEquals(issued.Original.Context, permit.Context)
            || issued.RequestId != permit.Start.RequestId) throw Unavailable();
        await RequireAdmittedOriginalLockedAsync(issued, token);
        await RequireAdmissionRowLockedAsync(issued, permit.Start, token);
    }

    private async Task RequireAdmittedOriginalLockedAsync(GroupModelAttemptAdmissionPreparer.PreparedAttempt issued,
        CancellationToken token)
    {
        await new GroupModelAttemptAdmissionPermissionVerifier(database).RequireSafeRuntimeAsync(token);
        var context = issued.Original.Context;
        await new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain)
            .RequireOriginalAdmissionUnchangedLockedAsync(context.Preparation.Context.Handle,
                issued.Original.Plan, issued.Sidecar, token);
        await new GroupModelAttemptAdmissionPermissionVerifier(database).RequireSafeRuntimeAsync(token);
    }

    private static GroupModelAttemptAdmissionRecord AdmissionRecord(GroupModelAttemptAdmissionPreparer.PreparedAttempt issued)
    {
        var context = issued.Original.Context; var scope = context.Scope;
        return new()
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            RequestId = issued.RequestId,
            BatchId = context.BatchId,
            InputSha256 = GroupModelAttemptBudget.InputIdentity(context),
            PlanSha256 = issued.Original.Plan.Sha256,
            SidecarSha256 = issued.Sidecar.Sha256,
            ChunkOrdinal = issued.Ordinal,
            ChunkOperationId = issued.Original.OperationId,
            FormatVersion = 1,
            SnapshotSha256 = issued.Snapshot.SnapshotSha256,
            Snapshot = GroupStructuredAdmissionSnapshotCodec.Write(issued.Snapshot),
            PreparedAtUtc = issued.StartedAtUtc,
            ExpiresAtUtc = issued.ExpiresAtUtc
        };
    }

    // Replay authenticates the companion but never issues another permit or
    // retrofits a missing row for a legacy start. The payload is bounded on
    // SQL before EF materialization, just like the original sidecar reader.
    private async Task RequireAdmissionRowLockedAsync(GroupModelAttemptAdmissionPreparer.PreparedAttempt issued,
        GroupModelAttemptStarted start, CancellationToken token)
    {
        var context = issued.Original.Context; var scope = context.Scope;
        if (start.Scope != scope || start.RequestId != issued.RequestId || start.BatchId != context.BatchId
            || start.InputSha256 != GroupModelAttemptBudget.InputIdentity(context)
            || start.StartedAtUtc < issued.StartedAtUtc || start.StartedAtUtc >= issued.ExpiresAtUtc
            || start.DeadlineUtc > issued.ExpiresAtUtc) throw Unavailable();
        var row = await database.Database.SqlQuery<StoredAdmission>($"""
            SELECT TenantId,CompanyId,BindingId,RequestId,BatchId,InputSha256,PlanSha256,SidecarSha256,
              ChunkOrdinal,ChunkOperationId,FormatVersion,SnapshotSha256,PreparedAtUtc,ExpiresAtUtc,
              CASE WHEN DATALENGTH([Snapshot]) BETWEEN 256 AND 10000 THEN [Snapshot] END AS [Snapshot]
            FROM aioffice.GroupModelAttemptAdmissions
            """).SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.RequestId == issued.RequestId, token) ?? throw Unavailable();
        var expected = AdmissionRecord(issued);
        if (row.TenantId != expected.TenantId || row.CompanyId != expected.CompanyId || row.BindingId != expected.BindingId
            || row.RequestId != expected.RequestId || row.BatchId != expected.BatchId || row.InputSha256 != expected.InputSha256
            || row.PlanSha256 != expected.PlanSha256 || row.SidecarSha256 != expected.SidecarSha256
            || row.ChunkOrdinal != expected.ChunkOrdinal || row.ChunkOperationId != expected.ChunkOperationId
            || row.FormatVersion != 1 || row.SnapshotSha256 != expected.SnapshotSha256 || row.Snapshot is null
            || row.PreparedAtUtc.Offset != TimeSpan.Zero || row.ExpiresAtUtc.Offset != TimeSpan.Zero
            || row.PreparedAtUtc != expected.PreparedAtUtc || row.ExpiresAtUtc != expected.ExpiresAtUtc)
            throw Unavailable();
        var snapshot = GroupStructuredAdmissionSnapshotCodec.Read(row.Snapshot);
        if (snapshot.SnapshotSha256 != row.SnapshotSha256
            || !CryptographicOperations.FixedTimeEquals(row.Snapshot, expected.Snapshot)) throw Unavailable();
    }

    private sealed record StoredAdmission(Guid TenantId, Guid CompanyId, Guid BindingId, Guid RequestId, Guid BatchId,
        string InputSha256, string PlanSha256, string SidecarSha256, int ChunkOrdinal, Guid ChunkOperationId,
        int FormatVersion, string SnapshotSha256, DateTimeOffset PreparedAtUtc, DateTimeOffset ExpiresAtUtc, byte[]? Snapshot);
}
