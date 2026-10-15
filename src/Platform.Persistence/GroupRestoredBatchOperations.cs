using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed class GroupRestoredBatchOperations(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain, IGroupSourceKeyProvider keys,
    Func<IAiStructuredRequestAdmission> existingAdmission) : IGroupRestoredBatchOperations
{
    // Construction stores the existing factory; no provider/options/profile
    // is initialized for terminal recovery, restoration or host-only chunks.
    private readonly GroupOriginalModelChunkCoordinator model = new(
        new GroupOriginalModelChunkOperations(database, worker, clock, sources, brain, keys, existingAdmission), clock);
    public Task<GroupTerminalFrontierAdvanceResult?> TryAdvanceCommittedAsync(GroupBatchClaimHandle handle, CancellationToken token) =>
        new GroupTerminalFrontierStore(database, worker, clock, keys).TryAdvanceCommittedAsync(handle.Receipt.Scope, handle.Receipt.BatchId, token);
    public Task<GroupRestoredOriginalBatch?> RestoreAsync(GroupBatchClaimHandle handle, CancellationToken token) =>
        new GroupOriginalBatchRestorer(database, worker, clock, sources, brain).ReadAsync(handle, token);
    public async Task<GroupRestoredChunkProgress> ProcessChunkAsync(GroupRestoredOriginalBatch batch,
        GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!batch.Chunks.Any(x => ReferenceEquals(x, chunk)) || chunk.Original.Ordinal < 1 || chunk.Original.Ordinal > batch.Original.Chunks.Count
            || !ReferenceEquals(batch.Original.Chunks[chunk.Original.Ordinal - 1], chunk.Original)) throw Unavailable();
        if (chunk.Original.RequiresModel)
        {
            var result = await model.ProcessAsync(batch.Original, chunk, token);
            if (!Enum.IsDefined(result.Disposition)) throw Unavailable();
            return new(result.OperationId, result.Disposition == GroupOriginalModelChunkDisposition.Committed, result.RetryAtUtc);
        }
        if (chunk.Current.ModelContext is not null) throw Unavailable();
        var plan = GroupAutomaticNotePlan.Create(chunk.Current.Preparation);
        if (plan.NoteCount == 0)
            await new GroupNoWorkCommitStore(database, worker, clock, sources, brain).CommitAutomaticAsync(plan, chunk.Brain, chunk.Original.OperationId, token);
        else
            await new GroupNoteCommitStore(database, worker, clock, sources, brain, keys, new()).CommitAutomaticAsync(plan, chunk.Brain, chunk.Original.OperationId, token);
        token.ThrowIfCancellationRequested();
        return new(chunk.Original.OperationId, true, null);
    }
    public Task<GroupBatchTerminalCommitResult> CommitTerminalAsync(GroupRestoredOriginalBatch batch, CancellationToken token) =>
        new GroupBatchTerminalStore(database, worker, clock, sources, brain).CommitAsync(batch.Preparation.Context.Handle,
            batch.Preparation.BatchId, token);
    public Task<GroupTerminalFrontierAdvanceResult> AdvanceAsync(GroupScope scope, Guid batch, CancellationToken token) =>
        new GroupTerminalFrontierStore(database, worker, clock, keys).AdvanceAsync(scope, batch, token);
    private static InvalidOperationException Unavailable() => new("Group restored batch operations are not available.");
}
