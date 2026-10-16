using System.Security.Cryptography;
using System.Text;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal enum GroupOriginalCreationDisposition { Available = 1, RequiresModel = 2 }
internal sealed record GroupOriginalCreationResult(GroupOriginalCreationDisposition Disposition,
    GroupAutomaticInputPlan? Original, GroupBatchSourcePreparation? Preparation)
{
    public override string ToString() => "Group original creation progress (private context, no effect authority).";
}

// Initial original+sidecar only, not notes/terminal/frontier completion. This
// component cannot create a model original or initialize any AI configuration.
internal sealed class GroupOriginalWithoutModelCreator(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain)
{
    private static readonly string Profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        "aioffice-group-original-without-model-v1\n" + GroupHistorySelectionDisclosure.PolicyVersion + "\n"
        + GroupSecretQuarantine.PolicyVersion + "\n" + GroupAutomaticModelContext.TokenAccountingVersion)));

    internal async Task<GroupOriginalCreationResult> TryCreateAsync(GroupBatchClaimHandle handle, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var existing = await new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain).ReadAsync(handle, token);
        token.ThrowIfCancellationRequested();
        if (existing is not null) return new(GroupOriginalCreationDisposition.Available, existing, null);
        var allocation = await new GroupBatchClaimStore(database, worker, clock).ReadCurrentAllocationAsync(handle, token);
        token.ThrowIfCancellationRequested();
        var ids = allocation.Revisions.Select(x => x.Metadata.MessageId).Distinct().Order().ToArray();
        var preparation = GroupBatchSourcePreparation.Create(await sources.ReadAsync(handle, ids, token));
        token.ThrowIfCancellationRequested();
        if (preparation.Candidates.Count != 0) return new(GroupOriginalCreationDisposition.RequiresModel, null, preparation);
        var selection = await new GroupAutomaticHistorySelector(database, worker, clock, sources).SelectWithoutModelAsync(preparation, token);
        token.ThrowIfCancellationRequested();
        var history = await brain.ReadAsync(handle, [], [], token);
        token.ThrowIfCancellationRequested();
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, history, selection, Profile, null, 0, [], token);
        var preparer = new GroupOriginalAdmissionPreparer(null, clock);
        var issued = await preparer.PrepareAsync(plan, preparation, history, [], token);
        token.ThrowIfCancellationRequested();
        var stored = await new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain, preparer)
            .PersistV3WithAdmissionAsync(issued, preparation, history, token);
        token.ThrowIfCancellationRequested();
        return new(GroupOriginalCreationDisposition.Available, stored, null);
    }
}
