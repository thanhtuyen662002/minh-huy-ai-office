using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Structural partition only. No operation ID, provider call, retry proof,
// persisted disposition or current authority is created by this plan.
public sealed class GroupAutomaticContextChunk
{
    internal GroupAutomaticContextChunk(int ordinal, GroupBatchSourcePreparation preparation, GroupAutomaticModelContext? context)
    { Ordinal = ordinal; Preparation = preparation; ModelContext = context; }
    public int Ordinal { get; }
    public GroupBatchSourcePreparation Preparation { get; }
    public GroupAutomaticModelContext? ModelContext { get; }
    public override string ToString() => "Group automatic context chunk (private content).";
}

public static class GroupAutomaticContextChunks
{
    public static IReadOnlyList<GroupAutomaticContextChunk> Create(GroupBatchSourcePreparation preparation,
        GroupBrainPrivateContext brain, string? modelId, int estimatedTokenBudget, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparation); ArgumentNullException.ThrowIfNull(brain); token.ThrowIfCancellationRequested();
        try
        {
            var original = preparation.Context;
            if (!ReferenceEquals(original.Handle, brain.Handle) || original.Scope != brain.Scope) throw Unavailable();
            // Validate the entire original structural dependency set, including
            // non-model receipts. This neither checks SQL nor grants authority.
            _ = GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(original, brain, original.BatchId));
            var ordered = original.Items.OrderBy(x => x.CommittedSequence).ThenBy(x => x.MessageId).ToArray();
            var snapshots = original.Snapshots.ToDictionary(x => x.Head.MessageId);
            var singletonCosts = new Dictionary<Guid, int>();
            int? overhead = null;
            // Every candidate must fit alone before returning any partial plan.
            // Oversized or unsafe sources/history refuse; nothing is truncated.
            foreach (var candidate in preparation.Candidates)
            {
                token.ThrowIfCancellationRequested();
                var item = ordered.Single(x => x.MessageId == candidate.MessageId);
                var single = GroupAutomaticModelContext.Create(Subset([item]), brain, modelId!, estimatedTokenBudget);
                var source = single.Manifest.Entries.Single(x => x.Kind == AiContextSourceKind.Conversation);
                var fixedCost = checked(single.Manifest.UsedTokens - source.EstimatedTokens);
                if (overhead is not null && overhead != fixedCost) throw Unavailable();
                overhead = fixedCost; singletonCosts.Add(item.MessageId, source.EstimatedTokens);
            }
            var chunks = new List<GroupAutomaticContextChunk>();
            var selected = new List<GroupBatchSourceEntry>(); var modelCount = 0; var sourceCost = 0;
            foreach (var item in ordered)
            {
                token.ThrowIfCancellationRequested();
                if (singletonCosts.TryGetValue(item.MessageId, out var cost))
                {
                    var nextCount = checked(modelCount + 1);
                    var nextCost = checked(sourceCost + cost);
                    // JSON arrays add exactly one ASCII comma per additional
                    // source; all policy/history/framing/schema are required.
                    if (modelCount != 0 && checked(overhead!.Value + nextCost + nextCount - 1) > estimatedTokenBudget)
                    { Append(); nextCount = 1; nextCost = cost; }
                    modelCount = nextCount; sourceCost = nextCost;
                }
                selected.Add(item);
            }
            Append();
            if (chunks.Count is < 1 or > 100 || chunks.Sum(x => x.Preparation.Receipts.Count) != preparation.Receipts.Count
                || !chunks.SelectMany(x => x.Preparation.Receipts).ToHashSet().SetEquals(preparation.Receipts)) throw Unavailable();
            token.ThrowIfCancellationRequested(); return Array.AsReadOnly(chunks.ToArray());

            GroupBatchSourcePreparation Subset(GroupBatchSourceEntry[] items) => GroupBatchSourcePreparation.Create(new(
                original.Handle, original.AllocatedThroughSequence, original.Coverage,
                items.Select(x => snapshots[x.MessageId]).ToArray(), items));

            void Append()
            {
                if (selected.Count == 0) throw Unavailable();
                var part = Subset(selected.ToArray());
                var context = modelCount == 0 ? null : GroupAutomaticModelContext.Create(part, brain, modelId!, estimatedTokenBudget);
                if (context is not null && context.Manifest.UsedTokens != checked(overhead!.Value + sourceCost + modelCount - 1)) throw Unavailable();
                chunks.Add(new(chunks.Count + 1, part, context)); selected.Clear(); modelCount = 0; sourceCost = 0;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Group automatic context chunks are not available.");
}
