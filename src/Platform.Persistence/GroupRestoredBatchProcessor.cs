using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal enum GroupRestoredBatchDisposition { AwaitingOriginal = 1, Waiting = 2, Completed = 3 }
internal sealed record GroupRestoredBatchOutcome(GroupScope Scope, Guid BatchId, GroupRestoredBatchDisposition Disposition,
    DateTimeOffset? RetryAtUtc, bool RecoveredTerminal)
{
    public override string ToString() => "Group restored batch progress (private metadata, not business resolution).";
}
internal sealed record GroupRestoredChunkProgress(Guid OperationId, bool Committed, DateTimeOffset? RetryAtUtc);

// One claimed batch, no first-original creation, scanner, timer or notification.
// Every actual effect remains owned by the independently fenced SQL stores.
internal sealed class GroupRestoredBatchProcessor(IGroupRestoredBatchOperations operations)
{
    internal async Task<GroupRestoredBatchOutcome> ProcessClaimedAsync(GroupBatchClaimHandle handle, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested();
        if (operations is null) throw Unavailable();
        var scope = handle.Receipt.Scope; scope.Validate(); var batchId = handle.Receipt.BatchId;
        if (batchId == Guid.Empty) throw Unavailable();
        // A committed historical terminal can be recovered without reopening
        // original/private/model context, under fresh current Extract authority.
        var prior = await operations.TryAdvanceCommittedAsync(handle, token);
        token.ThrowIfCancellationRequested();
        if (prior is not null) { RequireAdvance(prior); return Outcome(GroupRestoredBatchDisposition.Completed, null, true); }
        var batch = await operations.RestoreAsync(handle, token);
        token.ThrowIfCancellationRequested();
        if (batch is null) return Outcome(GroupRestoredBatchDisposition.AwaitingOriginal, null, false);
        if (!ReferenceEquals(batch.Preparation.Context.Handle, handle) || batch.Original.Dependencies.Scope != scope
            || batch.Original.Dependencies.BatchId != batchId || batch.Chunks.Count != batch.Original.Chunks.Count || batch.Chunks.Count is < 1 or > 100)
            throw Unavailable();
        batch.Original.RequireCompleteCurrentDependencies(batch.Preparation, batch.Chunks[0].Brain, token);
        for (var index = 0; index < batch.Chunks.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var chunk = batch.Chunks[index];
            if (!ReferenceEquals(chunk.Original, batch.Original.Chunks[index]) || chunk.Original.Ordinal != index + 1
                || !ReferenceEquals(chunk.Current.Preparation.Context.Handle, handle) || !ReferenceEquals(chunk.Brain.Handle, handle)) throw Unavailable();
            var result = await operations.ProcessChunkAsync(batch, chunk, token);
            token.ThrowIfCancellationRequested();
            if (result.OperationId != chunk.Original.OperationId || result.Committed && result.RetryAtUtc is not null) throw Unavailable();
            if (!result.Committed)
            {
                if (result.RetryAtUtc is not { Offset: var offset } retry || offset != TimeSpan.Zero) throw Unavailable();
                return Outcome(GroupRestoredBatchDisposition.Waiting, retry, false);
            }
        }
        var terminal = await operations.CommitTerminalAsync(batch, token);
        token.ThrowIfCancellationRequested();
        if (terminal.Scope != scope || terminal.BatchId != batchId || terminal.OperationId != batchId) throw Unavailable();
        var advanced = await operations.AdvanceAsync(scope, batchId, token);
        token.ThrowIfCancellationRequested(); RequireAdvance(advanced);
        return Outcome(GroupRestoredBatchDisposition.Completed, null, false);

        void RequireAdvance(GroupTerminalFrontierAdvanceResult result)
        { if (result.Scope != scope || result.BatchId != batchId || result.Version <= 0 || result.ThroughSequence <= 0) throw Unavailable(); }
        GroupRestoredBatchOutcome Outcome(GroupRestoredBatchDisposition disposition, DateTimeOffset? retry, bool recovered) =>
            new(scope, batchId, disposition, retry, recovered);
    }
    private static InvalidOperationException Unavailable() => new("Group restored batch is not available.");
}

// Internal trusted host composition seam. Recording ports test orchestration
// only; their progress/result objects cannot authorize terminal/frontier SQL.
internal interface IGroupRestoredBatchOperations
{
    Task<GroupTerminalFrontierAdvanceResult?> TryAdvanceCommittedAsync(GroupBatchClaimHandle handle, CancellationToken token);
    Task<GroupRestoredOriginalBatch?> RestoreAsync(GroupBatchClaimHandle handle, CancellationToken token);
    Task<GroupRestoredChunkProgress> ProcessChunkAsync(GroupRestoredOriginalBatch batch, GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token);
    Task<GroupBatchTerminalCommitResult> CommitTerminalAsync(GroupRestoredOriginalBatch batch, CancellationToken token);
    Task<GroupTerminalFrontierAdvanceResult> AdvanceAsync(GroupScope scope, Guid batch, CancellationToken token);
}
