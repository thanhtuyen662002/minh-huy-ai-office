using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed partial class GroupAutomaticInputPlanStore
{
    // Current scoped origin metadata for reconstructing an already stored
    // original. No reselection, key/provider wait, sidecar/current admission
    // dependency or first-insert issuance. Later restoration/effects still
    // perform their complete exact dependency checks.
    internal async Task<IReadOnlyList<GroupBrainRequestOriginMetadata>> ReadRestoredOriginsAsync(GroupAutomaticInputPlan plan,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext currentBrain, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(currentBrain); token.ThrowIfCancellationRequested();
        var handle = preparation.Context.Handle;
        ValidateEntry(handle, token); RequireScope(handle, plan.Dependencies);
        if (!ReferenceEquals(handle, currentBrain.Handle)) throw Unavailable();
        plan.RequireCompleteCurrentDependencies(preparation, currentBrain, token);
        GroupBrainRequestOriginMetadata[]? origins = null;
        await InCurrentUnitAsync(handle, plan.Dependencies, async (now, _, ct) =>
        {
            var stored = await ReadLockedAsync(handle, ct) ?? throw Unavailable();
            var original = await DecodeAsync(handle, stored, now, ct);
            if (!CryptographicOperations.FixedTimeEquals(original.Encode(), plan.Encode())) throw Unavailable();
            var ids = original.HistoryOrder.Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId).ToArray();
            origins = ids.Length == 0 ? [] : await RestoredOriginRows(handle.Receipt.Scope, ids).ToArrayAsync(ct);
            if (origins.Length > GroupBrainCurrentReader.MaximumSelectedRevisions) throw Unavailable();
            return original;
        }, token);
        token.ThrowIfCancellationRequested();
        return Array.AsReadOnly(origins ?? throw Unavailable());
    }

    // Translation seam only. Normal reads occur exclusively in the fixed
    // current unit above; a query object is not authority to release context.
    internal IQueryable<GroupBrainRequestOriginMetadata> RestoredOriginRows(GroupScope scope, Guid[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids); scope.Validate();
        if (ids.Length is < 1 or > GroupBrainCurrentReader.MaximumSelectedRevisions || ids.Any(x => x == Guid.Empty)
            || ids.Distinct().Count() != ids.Length) throw Unavailable();
        return database.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && ids.Contains(x.Id)).OrderBy(x => x.Id)
            .Select(x => new GroupBrainRequestOriginMetadata(scope, x.Id, x.OriginBatchId, x.OriginOperationId, x.OriginCandidateOrdinal, x.CreatedAtUtc))
            .Take(GroupBrainCurrentReader.MaximumSelectedRevisions + 1);
    }
}
