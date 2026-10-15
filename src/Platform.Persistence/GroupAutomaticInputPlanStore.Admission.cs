using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed partial class GroupAutomaticInputPlanStore
{
    // Used only by the new admitted journal inside its existing owned unit.
    // Reuse the locked entry/permission/receipt validation; never open a
    // nested transaction or turn a structurally valid sidecar into authority.
    internal async Task RequireOriginalAdmissionUnchangedLockedAsync(GroupBatchClaimHandle handle,
        GroupAutomaticInputPlan expected, GroupOriginalAdmissionSidecar expectedAdmission, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(expectedAdmission); token.ThrowIfCancellationRequested();
        expectedAdmission.RequirePlan(expected);
        await RequireOriginalUnchangedLockedAsync(handle, expected, token);
        await new GroupOriginalAdmissionPermissionVerifier(database).RequireSafeRuntimeAsync(token);
        var row = await ReadLockedAsync(handle, token) ?? throw Unavailable();
        var now = UtcNow();
        var original = await DecodeAsync(handle, row, now, token);
        if (!CryptographicOperations.FixedTimeEquals(original.Encode(), expected.Encode())) throw Unavailable();
        var metadata = await ReadAdmissionLockedAsync(handle, token) ?? throw Unavailable();
        var actual = DecodeAdmission(handle, original, row, metadata, now);
        if (!CryptographicOperations.FixedTimeEquals(actual.Encode(), expectedAdmission.Encode())) throw Unavailable();
        await new GroupOriginalAdmissionPermissionVerifier(database).RequireSafeRuntimeAsync(token);
    }

    // One original+sidecar commit, never PersistV3 followed by a second unit.
    // The fixed preparer instance must authenticate its non-serialized carrier.
    internal async Task<GroupAutomaticInputPlan> PersistV3WithAdmissionAsync(GroupOriginalAdmissionPreparer.PreparedOriginal issued,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext currentBrain, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparation); ArgumentNullException.ThrowIfNull(currentBrain);
        if (admissions is null) throw Unavailable();
        admissions.RequireIssued(issued, preparation, currentBrain, token);
        var handle = preparation.Context.Handle; ValidateEntry(handle, token);
        var frozen = GroupAutomaticInputPlan.Read(issued.Original.Encode());
        var sidecar = GroupOriginalAdmissionSidecar.Read(issued.Sidecar.Encode()); sidecar.RequirePlan(frozen);
        RequireScope(handle, frozen.Dependencies);
        var result = await InCurrentUnitAsync(handle, frozen.Dependencies, async (now, staged, ct) =>
        {
            var scope = handle.Receipt.Scope;
            var ids = frozen.Dependencies.Dependencies.Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId).ToArray();
            var origins = await database.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && ids.Contains(x.Id))
                .OrderBy(x => x.Id).Take(GroupBrainCurrentReader.MaximumSelectedRevisions + 1)
                .Select(x => new GroupBrainRequestOriginMetadata(scope, x.Id, x.OriginBatchId, x.OriginOperationId,
                    x.OriginCandidateOrdinal, x.CreatedAtUtc)).ToArrayAsync(ct);
            issued.Original.RequireOriginalIssuance(preparation, currentBrain, origins, ct);
            frozen.RequireRestored(preparation, currentBrain, frozen.ProfileSha256, origins, ct);
            var old = await ReadLockedAsync(handle, ct); var oldSidecar = await ReadAdmissionLockedAsync(handle, ct);
            if (old is not null)
            {
                var original = await DecodeAsync(handle, old, now, ct);
                if (!CryptographicOperations.FixedTimeEquals(original.Encode(), frozen.Encode()) || oldSidecar is null) throw Unavailable();
                var existing = DecodeAdmission(handle, original, old, oldSidecar, now);
                if (!CryptographicOperations.FixedTimeEquals(existing.Encode(), sidecar.Encode())) throw Unavailable();
                return original;
            }
            if (oldSidecar is not null) throw Unavailable();
            var receipt = handle.Receipt;
            if (sidecar.OriginalClaimOperationId != receipt.OperationId || sidecar.OriginalClaimEpoch != receipt.Epoch) throw Unavailable();
            var planRow = new GroupAutomaticInputPlanRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = receipt.BatchId,
                FormatVersion = 3,
                PlanSha256 = frozen.Sha256,
                Plan = frozen.Encode(),
                OriginalClaimOperationId = receipt.OperationId,
                OriginalClaimEpoch = receipt.Epoch,
                CreatedAtUtc = now
            };
            var admissionRow = new GroupOriginalAdmissionSidecarRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = receipt.BatchId,
                FormatVersion = GroupOriginalAdmissionSidecar.Version,
                PlanSha256 = frozen.Sha256,
                SidecarSha256 = sidecar.Sha256,
                Sidecar = sidecar.Encode(),
                OriginalClaimOperationId = receipt.OperationId,
                OriginalClaimEpoch = receipt.Epoch,
                CreatedAtUtc = now
            };
            database.Add(planRow); staged.Add(planRow); database.Add(admissionRow); staged.Add(admissionRow);
            await database.SaveChangesAsync(ct);
            // The shared final claim/fence inspection owns expiry retirement.
            return frozen;
        }, token, async ct =>
        {
            await new GroupOriginalAdmissionPermissionVerifier(database).RequireSafeRuntimeAsync(ct);
            return admissions.InspectIssuedLocked(issued, preparation, currentBrain, ct);
        });
        return result ?? throw Unavailable();
    }

    // New-attempt branch only, AFTER existing accepted-result recovery. Old
    // ReadAsync/RequireOriginalUnchanged/result reads do not call this method
    // or gain any sidecar/table/current-model permission dependency.
    internal async Task<GroupOriginalAdmissionSidecar?> ReadOriginalAdmissionAsync(GroupBatchClaimHandle handle,
        GroupAutomaticInputPlan expected, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ValidateEntry(handle, token);
        GroupOriginalAdmissionSidecar? answer = null;
        await InCurrentUnitAsync(handle, expected.Dependencies, async (now, _, ct) =>
        {
            var row = await ReadLockedAsync(handle, ct) ?? throw Unavailable();
            var original = await DecodeAsync(handle, row, now, ct);
            if (!CryptographicOperations.FixedTimeEquals(original.Encode(), expected.Encode())) throw Unavailable();
            var metadata = await ReadAdmissionLockedAsync(handle, ct);
            answer = metadata is null ? null : DecodeAdmission(handle, original, row, metadata, now);
            return original;
        }, token, async ct =>
        {
            await new GroupOriginalAdmissionPermissionVerifier(database).RequireSafeRuntimeAsync(ct);
            return null;
        });
        return answer;
    }

    private Task<StoredAdmission?> ReadAdmissionLockedAsync(GroupBatchClaimHandle handle, CancellationToken token)
    {
        var scope = handle.Receipt.Scope;
        return database.Database.SqlQuery<StoredAdmission>($"""
            SELECT TenantId,CompanyId,BindingId,BatchId,FormatVersion,PlanSha256,SidecarSha256,
              OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc,
              CASE WHEN DATALENGTH([Sidecar]) BETWEEN 211 AND 1010000 THEN [Sidecar] END AS [Sidecar]
            FROM aioffice.GroupOriginalAdmissionSidecars
            """).SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId, token);
    }

    private static GroupOriginalAdmissionSidecar DecodeAdmission(GroupBatchClaimHandle handle, GroupAutomaticInputPlan original,
        StoredPlan plan, StoredAdmission row, DateTimeOffset now)
    {
        if (row.FormatVersion != GroupOriginalAdmissionSidecar.Version || row.Sidecar is null
            || row.TenantId != handle.Receipt.Scope.TenantId || row.CompanyId != handle.Receipt.Scope.CompanyId
            || row.BindingId != handle.Receipt.Scope.SourceBindingId || row.BatchId != handle.Receipt.BatchId
            || row.OriginalClaimOperationId != plan.OriginalClaimOperationId || row.OriginalClaimEpoch != plan.OriginalClaimEpoch
            || row.CreatedAtUtc.Offset != TimeSpan.Zero || row.CreatedAtUtc != plan.CreatedAtUtc || row.CreatedAtUtc > now
            || row.PlanSha256 != plan.PlanSha256) throw Unavailable();
        var sidecar = GroupOriginalAdmissionSidecar.Read(row.Sidecar); sidecar.RequirePlan(original);
        if (sidecar.Scope != handle.Receipt.Scope || sidecar.BatchId != handle.Receipt.BatchId
            || sidecar.PlanSha256 != row.PlanSha256 || sidecar.Sha256 != row.SidecarSha256
            || sidecar.OriginalClaimOperationId != row.OriginalClaimOperationId || sidecar.OriginalClaimEpoch != row.OriginalClaimEpoch) throw Unavailable();
        return sidecar;
    }

    private sealed record StoredAdmission(Guid TenantId, Guid CompanyId, Guid BindingId, Guid BatchId, int FormatVersion,
        string PlanSha256, string SidecarSha256, Guid OriginalClaimOperationId, long OriginalClaimEpoch,
        DateTimeOffset CreatedAtUtc, byte[]? Sidecar);
}
