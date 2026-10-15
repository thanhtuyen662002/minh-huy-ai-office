namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed partial class GroupModelAttemptJournalStore
{
    // Non-authorizing retry metadata, AFTER accepted-result recovery. Neither
    // a current model, sidecar SELECT nor a new companion is needed merely to
    // wait or to prove exhaustion. The final failure writer rechecks SQL.
    internal Task<GroupModelAttemptBudgetDecision> ReadOriginalBudgetAsync(GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk restored, CancellationToken token = default)
    {
        var original = GroupModelAttemptOriginalBinding.Create(plan, restored, token);
        ValidateEntry(original.Context);
        return InCurrentUnitAsync(original.Context, original, async (now, _, ct) =>
        {
            var identity = GroupModelAttemptBudget.InputIdentity(original.Context);
            var (starts, failures) = await ReadJournalAsync(original.Context, identity, ct);
            return GroupModelAttemptBudget.Read(original.Context.Scope, original.Context.BatchId,
                identity, starts, failures, now, ct);
        }, token);
    }
}
