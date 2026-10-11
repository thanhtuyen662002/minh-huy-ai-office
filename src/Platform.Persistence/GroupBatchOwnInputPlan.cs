using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Immutable request-origin metadata, read separately from the versioned brain
// fingerprint. Current IT revisions do not replace the original create identity.
internal sealed record GroupBrainRequestOriginMetadata(GroupScope Scope, Guid RequestId, Guid OriginBatchId,
    Guid OriginOperationId, int OriginCandidateOrdinal, DateTimeOffset CreatedAtUtc);

// A structural prerequisite only. Its caller must read origins inside the same
// current-authority/source-lock SQL unit that authenticated every original effect.
internal sealed class GroupBatchOwnInputPlan
{
    internal const int MaximumOriginRows = FrozenGroupBatch.MaximumMessages * GroupBrainCurrentReader.MaximumSelectedRevisions;
    private GroupBatchOwnInputPlan(Guid[] order, int edges)
    { ContributorOrder = Array.AsReadOnly(order); OwnDependencyCount = edges; }
    internal IReadOnlyList<Guid> ContributorOrder { get; }
    internal int OwnDependencyCount { get; }
    public override string ToString() => "Group own-input plan (private metadata).";

    internal static GroupBatchOwnInputPlan Require(GroupWholeBatchDependencyVerdict.Current current,
        IEnumerable<GroupBrainRequestOriginMetadata> originRows, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return RequireOriginal(current.Coverage, current.OriginalEffects, originRows, token);
    }

    internal static GroupBatchOwnInputPlan RequireOriginal(GroupWholeBatchCoverage coverage,
        IEnumerable<GroupWorkEffectLedger> originalEffects, IEnumerable<GroupBrainRequestOriginMetadata> originRows,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(originRows); token.ThrowIfCancellationRequested();
        try
        {
            if (coverage is null) throw Unavailable();
            var effects = coverage.RequireOriginalEffects(originalEffects);
            var byOperation = effects.ToDictionary(x => x.OperationId);
            var created = new Dictionary<Guid, (GroupWorkEffectLedger Effect, int Ordinal)>();
            foreach (var effect in effects)
                for (var index = 0; index < effect.OriginalRequestIds.Count; index++)
                    if (!created.TryAdd(effect.OriginalRequestIds[index], (effect, index + 1))) throw Unavailable();
            var requested = coverage.Manifests.SelectMany(x => x.Dependencies)
                .Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId).ToHashSet();
            if (requested.Count > MaximumOriginRows) throw Unavailable();
            var origins = new Dictionary<Guid, GroupBrainRequestOriginMetadata>(); var observed = 0;
            foreach (var row in originRows)
            {
                token.ThrowIfCancellationRequested();
                if (++observed > MaximumOriginRows || row is null || row.Scope != coverage.Scope
                    || !requested.Contains(row.RequestId) || row.OriginBatchId == Guid.Empty || row.OriginOperationId == Guid.Empty
                    || row.OriginCandidateOrdinal is < 1 or > GroupAutomaticNotePlan.MaximumNotes || row.CreatedAtUtc.Offset != TimeSpan.Zero
                    || row.RequestId != RequestIdentity(row)
                    || !origins.TryAdd(row.RequestId, row)) throw Unavailable();
            }
            if (origins.Count != requested.Count) throw Unavailable();
            var parents = byOperation.Keys.ToDictionary(x => x, _ => new HashSet<Guid>());
            var children = byOperation.Keys.ToDictionary(x => x, _ => new HashSet<Guid>()); var edges = 0;
            foreach (var manifest in coverage.Manifests)
            {
                token.ThrowIfCancellationRequested();
                var child = byOperation[manifest.OperationId];
                foreach (var dependency in manifest.Dependencies.Where(x => x.Kind == GroupBrainContentKind.RequestRevision))
                {
                    var origin = origins[dependency.RecordId];
                    if (origin.CreatedAtUtc > child.CommittedAtUtc) throw Unavailable();
                    var known = created.TryGetValue(dependency.RecordId, out var original);
                    if (origin.OriginBatchId != coverage.BatchId)
                    { if (known) throw Unavailable(); continue; }
                    if (!known || origin.OriginOperationId != original.Effect.OperationId || origin.OriginCandidateOrdinal != original.Ordinal
                        || origin.CreatedAtUtc != original.Effect.CommittedAtUtc || origin.OriginOperationId == child.OperationId) throw Unavailable();
                    if (parents[child.OperationId].Add(original.Effect.OperationId))
                    { children[original.Effect.OperationId].Add(child.OperationId); edges++; }
                }
            }
            // Equal UTC timestamps are legitimate. Deterministic DAG ordering,
            // rather than a strict timestamp comparison, rejects cycles.
            var ready = new SortedSet<Guid>(parents.Where(x => x.Value.Count == 0).Select(x => x.Key));
            var order = new List<Guid>(effects.Count);
            while (ready.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var operation = ready.Min; ready.Remove(operation); order.Add(operation);
                foreach (var child in children[operation])
                { parents[child].Remove(operation); if (parents[child].Count == 0) ready.Add(child); }
            }
            if (order.Count != effects.Count) throw Unavailable();
            token.ThrowIfCancellationRequested(); return new(order.ToArray(), edges);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Unavailable(); }
    }
    // Bind external origins too: changing an own request's origin to another
    // batch must not turn it into an accepted external dependency.
    private static Guid RequestIdentity(GroupBrainRequestOriginMetadata row) => new(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
        $"aioffice-group-note-id-v1/{row.Scope.TenantId:D}/{row.Scope.CompanyId:D}/{row.Scope.SourceBindingId:D}/{row.OriginBatchId:D}/{row.OriginOperationId:D}/request/{row.OriginCandidateOrdinal}"))).AsSpan(0, 16));
    private static InvalidOperationException Unavailable() => new("Group own-input dependencies are not available.");
}
