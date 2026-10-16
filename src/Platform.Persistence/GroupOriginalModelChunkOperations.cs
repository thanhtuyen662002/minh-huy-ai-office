using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Constructed only by trusted host composition. The existing admission
// factory is lazy so accepted-result recovery, waiting and exhaustion do not
// instantiate/check a current Gateway/provider/options/release profile.
internal sealed class GroupOriginalModelChunkOperations : IGroupOriginalModelChunkOperations
{
    private readonly PlatformDbContext database;
    private readonly GroupExtractionWorkerBinding worker;
    private readonly TimeProvider clock;
    private readonly GroupBatchSourceReader sources;
    private readonly GroupBrainCurrentReader brain;
    private readonly IGroupSourceKeyProvider keys;
    private readonly Lazy<GroupModelAttemptAdmissionPreparer> issuer;

    internal GroupOriginalModelChunkOperations(PlatformDbContext database, GroupExtractionWorkerBinding worker,
        TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain, IGroupSourceKeyProvider keys,
        Func<IAiStructuredRequestAdmission> existingAdmission)
    {
        ArgumentNullException.ThrowIfNull(existingAdmission);
        this.database = database; this.worker = worker; this.clock = clock; this.sources = sources; this.brain = brain; this.keys = keys;
        issuer = new(() => new(database, existingAdmission() ?? throw Unavailable(), clock));
    }

    private GroupModelAttemptJournalStore Journal(bool admitted) => new(database, worker, clock, sources, brain, admitted ? issuer.Value : null);
    private GroupModelAttemptResultStore Results(bool admitted) => new(database, worker, clock, sources, brain, keys, new(), admitted ? issuer.Value : null);
    public Task<GroupModelAttemptResult?> ReadAcceptedAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token) =>
        Results(false).ReadOriginalAsync(plan, chunk, token);
    public Task<GroupModelAttemptBudgetDecision> ReadBudgetAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token) =>
        Journal(false).ReadOriginalBudgetAsync(plan, chunk, token);
    public Task<GroupOriginalAdmissionSidecar?> ReadSidecarAsync(GroupModelAttemptOriginalBinding original, CancellationToken token) =>
        new GroupAutomaticInputPlanStore(database, worker, clock, sources, brain).ReadOriginalAdmissionAsync(
            original.Context.Preparation.Context.Handle, original.Plan, token);
    public Task<GroupModelAttemptAdmissionPreparer.PreparedAttempt> PrepareAsync(GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk chunk, GroupOriginalAdmissionSidecar sidecar, CancellationToken token) =>
        issuer.Value.PrepareAsync(plan, chunk, sidecar, token);
    public Task<GroupModelAttemptStartResult> StartAsync(GroupModelAttemptAdmissionPreparer.PreparedAttempt prepared, CancellationToken token) =>
        Journal(true).TryStartAdmittedOriginalAsync(prepared, token);
    public Task<GroupModelAttemptResponse> ExecuteAsync(GroupModelAttemptPermit permit, CancellationToken token) =>
        Journal(true).ExecuteAdmittedOriginalAsync(permit, token);
    public Task<GroupModelAttemptBudgetDecision> RecordFailureAsync(GroupModelAttemptPermit permit, GroupModelAttemptFailure failure, CancellationToken token) =>
        Journal(true).RecordFailureAsync(permit.Context, permit, failure, token);
    public Task<GroupModelAttemptResult> PersistAcceptedAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk,
        GroupModelAttemptResponse response, CancellationToken token) => Results(true).PersistOriginalAsync(plan, chunk, response, token);
    public async Task CommitAcceptedAsync(GroupModelAttemptResult accepted, GroupModelAttemptOriginalBinding original, CancellationToken token)
    {
        var plan = GroupAutomaticNotePlan.Create(original.Context.Preparation, accepted.Proposal);
        if (plan.NoteCount == 0)
            await new GroupNoWorkCommitStore(database, worker, clock, sources, brain).CommitAutomaticAsync(plan, original.Context.Brain, original.OperationId, token);
        else
            await new GroupNoteCommitStore(database, worker, clock, sources, brain, keys, new()).CommitAutomaticAsync(plan, original.Context.Brain, original.OperationId, token);
    }
    public async Task CommitExhaustedAsync(GroupModelAttemptOriginalBinding original, CancellationToken token) =>
        _ = await new GroupNoteCommitStore(database, worker, clock, sources, brain, keys, new())
            .CommitExtractionFailureAsync(original.Context, original.OperationId, token);
    private static InvalidOperationException Unavailable() => new("Group original model operations are not available.");
}
