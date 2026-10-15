using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Trusted fixed composition; the existing admission factory remains lazy.
internal sealed class GroupClaimedBatchOperations(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain, IGroupSourceKeyProvider keys,
    Func<IAiStructuredRequestAdmission> existingAdmission) : IGroupClaimedBatchOperations
{
    private readonly GroupRestoredBatchProcessor restored = new(new GroupRestoredBatchOperations(database, worker, clock, sources, brain, keys, existingAdmission));
    public Task<GroupRestoredBatchOutcome> ProcessRestoredAsync(GroupBatchClaimHandle handle, CancellationToken token) => restored.ProcessClaimedAsync(handle, token);
    public Task<GroupOriginalCreationResult> CreateWithoutModelAsync(GroupBatchClaimHandle handle, CancellationToken token) =>
        new GroupOriginalWithoutModelCreator(database, worker, clock, sources, brain).TryCreateAsync(handle, token);
    public Task<GroupOriginalCreationResult> CreateModelAsync(GroupBatchClaimHandle handle, GroupBatchSourcePreparation preparation, CancellationToken token) =>
        new GroupOriginalModelCreator(database, worker, clock, sources, brain, existingAdmission).TryCreateAsync(handle, preparation, token);
}
