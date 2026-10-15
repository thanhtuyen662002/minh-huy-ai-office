using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// First MODEL original only. Existing original recovery precedes every model
// factory/profile dependency. No provider execution or attempt/effect is here.
internal sealed class GroupOriginalModelCreator(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain,
    Func<IAiStructuredRequestAdmission> existingAdmission)
{
    internal async Task<GroupOriginalCreationResult> TryCreateAsync(GroupBatchClaimHandle handle, GroupBatchSourcePreparation preparation,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var store = new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain);
        var existing = await store.ReadAsync(handle, token); token.ThrowIfCancellationRequested();
        if (existing is not null) return new(GroupOriginalCreationDisposition.Available, existing, null);
        ArgumentNullException.ThrowIfNull(preparation);
        if (!ReferenceEquals(preparation.Context.Handle, handle) || preparation.Candidates.Count == 0 || existingAdmission is null) throw Unavailable();
        var allocation = await new GroupBatchClaimStore(database, worker, clock).ReadCurrentAllocationAsync(handle, token);
        token.ThrowIfCancellationRequested();
        var actualIds = allocation.Revisions.Select(x => x.Metadata.MessageId).Distinct().Order().ToArray();
        if (preparation.Context.AllocatedThroughSequence != allocation.AllocatedThroughSequence
            || !actualIds.SequenceEqual(preparation.Context.Items.Select(x => x.MessageId).Order())) throw Unavailable();
        var selection = await new GroupAutomaticHistorySelector(database, worker, clock, sources).SelectAsync(preparation, token);
        token.ThrowIfCancellationRequested();
        var history = await brain.ReadAsync(handle, selection.RequestIds, selection.GlossaryIds, token);
        token.ThrowIfCancellationRequested();
        var origins = await store.ReadSelectionOriginsAsync(preparation, history, selection, token); token.ThrowIfCancellationRequested();
        var admission = existingAdmission() ?? throw Unavailable();
        if (admission is not IAiStructuredRequestPlanner planner) throw Unavailable();
        var policy = await planner.DescribePlanningAsync(GroupGroundedWorkProposal.ResponseSchema, token); token.ThrowIfCancellationRequested();
        var schemaHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GroupGroundedWorkProposal.ResponseSchema)));
        if (policy.SchemaSha256 != schemaHash) throw Unavailable();
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("aioffice-group-model-original-v1\n" + policy.ProfileSetSha256
            + "\n" + GroupAutomaticModelContext.TokenAccountingVersion + "\n" + GroupHistorySelectionDisclosure.PolicyVersion + "\n" + GroupSecretQuarantine.PolicyVersion)));
        var budget = Math.Min(policy.MaximumInputTextTokens, GroupAutomaticModelContext.MaximumEstimatedTokenBudget);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, history, selection, profile, policy.ConfiguredPrimaryModel, budget, origins, token);
        var preparer = new GroupOriginalAdmissionPreparer(admission, clock);
        var issued = await preparer.PrepareAsync(original, preparation, history, origins, token); token.ThrowIfCancellationRequested();
        if (!issued.Sidecar.Entries.Any(x => x.Snapshot is not null)
            || issued.Sidecar.Entries.Any(x => x.Snapshot is { } snapshot && !policy.MatchesMeasurement(snapshot))) throw Unavailable();
        var stored = await new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain, preparer)
            .PersistV3WithAdmissionAsync(issued, preparation, history, token);
        token.ThrowIfCancellationRequested(); return new(GroupOriginalCreationDisposition.Available, stored, null);
    }
    private static InvalidOperationException Unavailable() => new("Group model original creation is not available.");
}
