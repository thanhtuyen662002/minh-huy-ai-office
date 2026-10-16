using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed partial class GroupAutomaticInputPlanStore
{
    // First original has no persisted plan yet. Authenticate the actual fenced
    // selector issuance and complete current source/brain/claim instead. This
    // metadata does not authorize original insertion or provider execution.
    internal async Task<IReadOnlyList<GroupBrainRequestOriginMetadata>> ReadSelectionOriginsAsync(GroupBatchSourcePreparation preparation,
        GroupBrainPrivateContext currentBrain, GroupAutomaticHistorySelection selection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparation); ArgumentNullException.ThrowIfNull(currentBrain);
        ArgumentNullException.ThrowIfNull(selection); token.ThrowIfCancellationRequested();
        var handle = preparation.Context.Handle; ValidateEntry(handle, token);
        if (!ReferenceEquals(handle, currentBrain.Handle)) throw Unavailable();
        _ = GroupAutomaticHistorySelector.RequireDisclosure(selection, preparation, currentBrain, false, token);
        var manifest = GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(preparation.Context, currentBrain, preparation.BatchId));
        RequireScope(handle, manifest); GroupBrainRequestOriginMetadata[]? origins = null;
        await InCurrentUnitAsync(handle, manifest, async (_, _, ct) =>
        {
            var ids = selection.RequestIds.ToArray();
            origins = ids.Length == 0 ? [] : await RestoredOriginRows(handle.Receipt.Scope, ids).ToArrayAsync(ct);
            if (origins.Length > GroupBrainCurrentReader.MaximumSelectedRevisions) throw Unavailable();
            return null;
        }, token);
        token.ThrowIfCancellationRequested(); return Array.AsReadOnly(origins ?? throw Unavailable());
    }
}
