namespace MinhHuy.AIOffice.Platform.Persistence;

// A single already claimed batch. No scanner, claim acquisition, timer or
// effect authority. Actual stores independently reauthenticate every stage.
internal sealed class GroupClaimedBatchProcessor(IGroupClaimedBatchOperations operations)
{
    internal async Task<GroupRestoredBatchOutcome> ProcessAsync(GroupBatchClaimHandle handle, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested();
        if (operations is null) throw Unavailable();
        var scope = handle.Receipt.Scope; scope.Validate(); var batch = handle.Receipt.BatchId;
        if (batch == Guid.Empty) throw Unavailable();
        // Terminal recovery/accepted output/wait/exhaustion all precede any
        // original creation or current model configuration dependency.
        var prior = await operations.ProcessRestoredAsync(handle, token); token.ThrowIfCancellationRequested(); Require(prior);
        if (prior.Disposition != GroupRestoredBatchDisposition.AwaitingOriginal) return prior;
        var created = await operations.CreateWithoutModelAsync(handle, token); token.ThrowIfCancellationRequested();
        if (created is null) throw Unavailable();
        if (created.Disposition == GroupOriginalCreationDisposition.RequiresModel)
        {
            if (created.Original is not null || created.Preparation is not { } preparation
                || !ReferenceEquals(preparation.Context.Handle, handle) || preparation.Candidates.Count == 0) throw Unavailable();
            created = await operations.CreateModelAsync(handle, preparation, token); token.ThrowIfCancellationRequested();
        }
        // Availability is not completion. Always re-enter the actual original
        // recovery/effect/terminal/frontier owners after creation or its replay.
        if (created is not { Disposition: GroupOriginalCreationDisposition.Available, Original: { } original, Preparation: null }
            || original.Dependencies.Scope != scope || original.Dependencies.BatchId != batch) throw Unavailable();
        var result = await operations.ProcessRestoredAsync(handle, token); token.ThrowIfCancellationRequested(); Require(result);
        if (result.Disposition == GroupRestoredBatchDisposition.AwaitingOriginal) throw Unavailable();
        return result;

        void Require(GroupRestoredBatchOutcome value)
        {
            if (value is null || value.Scope != scope || value.BatchId != batch || !Enum.IsDefined(value.Disposition)
                || value.RecoveredTerminal && value.Disposition != GroupRestoredBatchDisposition.Completed) throw Unavailable();
            if (value.Disposition == GroupRestoredBatchDisposition.Waiting)
            {
                if (value.RetryAtUtc is not { } retry || retry.Offset != TimeSpan.Zero) throw Unavailable();
            }
            else if (value.RetryAtUtc is not null) throw Unavailable();
        }
    }
    private static InvalidOperationException Unavailable() => new("Group claimed batch is not available.");
}

internal interface IGroupClaimedBatchOperations
{
    Task<GroupRestoredBatchOutcome> ProcessRestoredAsync(GroupBatchClaimHandle handle, CancellationToken token);
    Task<GroupOriginalCreationResult> CreateWithoutModelAsync(GroupBatchClaimHandle handle, CancellationToken token);
    Task<GroupOriginalCreationResult> CreateModelAsync(GroupBatchClaimHandle handle, GroupBatchSourcePreparation preparation, CancellationToken token);
}
