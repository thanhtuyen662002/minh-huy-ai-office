using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal enum GroupOriginalModelChunkDisposition { Committed = 1, Waiting = 2 }
internal sealed record GroupOriginalModelChunkOutcome(Guid OperationId, GroupOriginalModelChunkDisposition Disposition,
    DateTimeOffset? RetryAtUtc, bool AcceptedResultRecovered, bool ExtractionFailed)
{
    public override string ToString() => "Group original model chunk progress (private metadata).";
}

// One restored MODEL chunk only, never a whole-batch completion/cursor or IT
// notification claim. The later batch coordinator owns host-only chunks and
// terminal/frontier proof. No RAM timer, provider client or caller DTO here.
internal sealed class GroupOriginalModelChunkCoordinator(IGroupOriginalModelChunkOperations operations, TimeProvider clock)
{
    internal async Task<GroupOriginalModelChunkOutcome> ProcessAsync(GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var original = GroupModelAttemptOriginalBinding.Create(plan, chunk, token);
        if (operations is null || clock is null) throw Unavailable();
        // This MUST remain the first effectful dependency. Persisted accepted
        // output survives model removal and sidecar/current-companion denial.
        var accepted = await operations.ReadAcceptedAsync(plan, chunk, token);
        token.ThrowIfCancellationRequested();
        if (accepted is not null) return await CommitAcceptedAsync(accepted, true);
        var budget = await operations.ReadBudgetAsync(plan, chunk, token);
        token.ThrowIfCancellationRequested();
        if (budget.Disposition != GroupModelAttemptDisposition.Available) return await ScheduleAsync(budget);
        var sidecar = await operations.ReadSidecarAsync(original, token);
        token.ThrowIfCancellationRequested();
        if (sidecar is null) throw Unavailable();
        var prepared = await operations.PrepareAsync(plan, chunk, sidecar, token);
        token.ThrowIfCancellationRequested();
        var started = await operations.StartAsync(prepared, token);
        token.ThrowIfCancellationRequested();
        if (started.Permit is not { } permit) return await ScheduleAsync(started.Budget);
        if (started.WasAlreadyStarted || started.Budget.Disposition != GroupModelAttemptDisposition.Wait || !ReferenceEquals(permit.Admission, prepared)
            || !ReferenceEquals(permit.Context, original.Context)) throw Unavailable();
        GroupModelAttemptResponse response;
        try { response = await operations.ExecuteAsync(permit, token); }
        catch (Exception error) when (error is IAiGatewayProviderFailure and not OperationCanceledException)
        {
            if (!GroupModelAttemptProviderFailure.TryClassify(error, out var failure, token)) throw Unavailable();
            return await ScheduleAsync(await operations.RecordFailureAsync(permit, failure, token));
        }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(response.Permit, permit)) throw Unavailable();
        if (response.Disposition == GroupModelAttemptResponseDisposition.Parsed)
        {
            var persisted = await operations.PersistAcceptedAsync(plan, chunk, response, token);
            return await CommitAcceptedAsync(persisted, false);
        }
        if (response.Disposition == GroupModelAttemptResponseDisposition.InvalidOutput)
            return await ScheduleAsync(await operations.RecordFailureAsync(permit, GroupModelAttemptFailure.InvalidOutput, token));
        if (response.Disposition == GroupModelAttemptResponseDisposition.Expired)
            return await ScheduleAsync(await operations.ReadBudgetAsync(plan, chunk, token));
        throw Unavailable();

        async Task<GroupOriginalModelChunkOutcome> CommitAcceptedAsync(GroupModelAttemptResult result, bool recovered)
        {
            token.ThrowIfCancellationRequested();
            // Preserve the exact context binding of the authenticated store
            // output before the final independently fenced effect writer.
            if (!ReferenceEquals(result.Proposal.Preparation, original.Context.Preparation)) throw Unavailable();
            await operations.CommitAcceptedAsync(result, original, token);
            token.ThrowIfCancellationRequested();
            return new(original.OperationId, GroupOriginalModelChunkDisposition.Committed, null, recovered, false);
        }

        async Task<GroupOriginalModelChunkOutcome> ScheduleAsync(GroupModelAttemptBudgetDecision decision)
        {
            token.ThrowIfCancellationRequested();
            if (decision.Disposition == GroupModelAttemptDisposition.Exhausted)
            {
                await operations.CommitExhaustedAsync(original, token);
                token.ThrowIfCancellationRequested();
                return new(original.OperationId, GroupOriginalModelChunkDisposition.Committed, null, false, true);
            }
            if (decision.Disposition is not (GroupModelAttemptDisposition.Available or GroupModelAttemptDisposition.Wait)) throw Unavailable();
            var retry = decision.Disposition == GroupModelAttemptDisposition.Wait
                ? decision.RetryAtUtc ?? throw Unavailable() : clock.GetUtcNow();
            if (retry.Offset != TimeSpan.Zero) throw Unavailable();
            return new(original.OperationId, GroupOriginalModelChunkDisposition.Waiting, retry, false, false);
        }
    }

    private static InvalidOperationException Unavailable() => new("Group original model chunk is not available.");
}

// Fixed host composition seam, never source/model-selected operations. Test
// fakes verify call ordering only; production implementation below uses the
// actual independently fenced stores. No operation here grants authority by
// itself or replaces the final SQL store's checks.
internal interface IGroupOriginalModelChunkOperations
{
    Task<GroupModelAttemptResult?> ReadAcceptedAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token);
    Task<GroupModelAttemptBudgetDecision> ReadBudgetAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token);
    Task<GroupOriginalAdmissionSidecar?> ReadSidecarAsync(GroupModelAttemptOriginalBinding original, CancellationToken token);
    Task<GroupModelAttemptAdmissionPreparer.PreparedAttempt> PrepareAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, GroupOriginalAdmissionSidecar sidecar, CancellationToken token);
    Task<GroupModelAttemptStartResult> StartAsync(GroupModelAttemptAdmissionPreparer.PreparedAttempt prepared, CancellationToken token);
    Task<GroupModelAttemptResponse> ExecuteAsync(GroupModelAttemptPermit permit, CancellationToken token);
    Task<GroupModelAttemptBudgetDecision> RecordFailureAsync(GroupModelAttemptPermit permit, GroupModelAttemptFailure failure, CancellationToken token);
    Task<GroupModelAttemptResult> PersistAcceptedAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, GroupModelAttemptResponse response, CancellationToken token);
    Task CommitAcceptedAsync(GroupModelAttemptResult accepted, GroupModelAttemptOriginalBinding original, CancellationToken token);
    Task CommitExhaustedAsync(GroupModelAttemptOriginalBinding original, CancellationToken token);
}
