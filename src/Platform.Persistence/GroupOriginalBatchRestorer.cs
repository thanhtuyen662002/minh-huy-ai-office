namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed class GroupRestoredOriginalBatch(GroupAutomaticInputPlan original, GroupBatchSourcePreparation preparation,
    IReadOnlyList<GroupAutomaticInputPlan.RestoredChunk> chunks)
{
    internal GroupAutomaticInputPlan Original { get; } = original;
    internal GroupBatchSourcePreparation Preparation { get; } = preparation;
    internal IReadOnlyList<GroupAutomaticInputPlan.RestoredChunk> Chunks { get; } = Array.AsReadOnly(chunks.ToArray());
    public override string ToString() => "Group restored original batch (private context, no effect authority).";
}

// Fixed host recovery only. Missing originals are returned to the later
// first-creation path; they never trigger history reselection or model options
// here. Current/source/brain/original/claim fences precede context release.
internal sealed class GroupOriginalBatchRestorer(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain)
{
    internal async Task<GroupRestoredOriginalBatch?> ReadAsync(GroupBatchClaimHandle handle, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var store = new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain);
        var original = await store.ReadAsync(handle, token);
        token.ThrowIfCancellationRequested();
        if (original is null) return null;
        var messageIds = original.Dependencies.Sources.Select(x => x.MessageId).ToArray();
        var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, messageIds, token));
        token.ThrowIfCancellationRequested();
        var requestIds = original.HistoryOrder.Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId).ToArray();
        var glossaryIds = original.HistoryOrder.Where(x => x.Kind == GroupBrainContentKind.GlossaryRevision).Select(x => x.RecordId).ToArray();
        var currentBrain = await brain.ReadAsync(handle, requestIds, glossaryIds, token);
        token.ThrowIfCancellationRequested();
        var origins = await store.ReadRestoredOriginsAsync(original, preparation, currentBrain, token);
        token.ThrowIfCancellationRequested();
        var chunks = original.RestoreChunks(preparation, currentBrain, original.ProfileSha256, origins, token);
        token.ThrowIfCancellationRequested();
        return new(original, preparation, chunks);
    }
}
