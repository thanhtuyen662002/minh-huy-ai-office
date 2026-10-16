using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Immutable original evidence only. A historical graph supplies no current
// authority, live lease, source/brain freshness, or completed-prefix proof.
// The future SQL reader must reconstruct all original rows under its source
// lock and current Extract fences before using this distinct sealed type.
internal sealed class GroupBatchHistoricalGraph
{
    private GroupBatchHistoricalGraph(GroupWholeBatchCoverage coverage, IReadOnlyList<GroupWorkEffectLedger> effects,
        GroupBatchOwnInputPlan ownInputs, DateTimeOffset observed)
    { Coverage = coverage; OriginalEffects = effects; OwnInputs = ownInputs; ObservedAtUtc = observed; }
    internal GroupWholeBatchCoverage Coverage { get; }
    internal IReadOnlyList<GroupWorkEffectLedger> OriginalEffects { get; }
    internal GroupBatchOwnInputPlan OwnInputs { get; }
    internal DateTimeOffset ObservedAtUtc { get; }
    public override string ToString() => "Group historical original graph (private metadata).";

    internal static GroupBatchHistoricalGraph Require(GroupWholeBatchCoverage coverage,
        IEnumerable<GroupWorkEffectLedger> originalEffects, IEnumerable<GroupBrainRequestOriginMetadata> origins,
        DateTimeOffset observed, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (coverage is null || originalEffects is null || origins is null || observed.Offset != TimeSpan.Zero
                || coverage.AllocatedAtUtc > observed) throw Unavailable();
            // Do not trust Count, indexer, CopyTo, or collection fast paths.
            // Bound actual enumeration before the existing expectation helper.
            var actual = new List<GroupWorkEffectLedger>(FrozenGroupBatch.MaximumMessages);
            foreach (var effect in originalEffects)
            {
                token.ThrowIfCancellationRequested();
                if (effect is null || actual.Count == FrozenGroupBatch.MaximumMessages) throw Unavailable();
                actual.Add(effect);
            }
            token.ThrowIfCancellationRequested();
            var effects = coverage.RequireOriginalEffects(actual.ToArray());
            foreach (var effect in effects)
            {
                token.ThrowIfCancellationRequested();
                var claim = effect.OriginalClaim;
                var manifest = coverage.Manifests.Single(x => x.OperationId == effect.OperationId);
                if (claim.Scope != coverage.Scope || claim.BatchId != coverage.BatchId
                    || claim.WorkOperationId != effect.OperationId || claim.CommittedAtUtc != effect.CommittedAtUtc
                    || claim.AuthoritySha256 != manifest.AuthoritySha256 || effect.CommittedAtUtc > observed) throw Unavailable();
            }
            var ownInputs = GroupBatchOwnInputPlan.RequireOriginal(coverage, effects, origins, token);
            token.ThrowIfCancellationRequested();
            return new(coverage, effects, ownInputs, observed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Group historical original graph is not available.");
}
