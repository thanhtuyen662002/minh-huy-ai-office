using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Fixed host observations only. A plan is not persisted work, a complete raw
// allocation disposition or authority to call a provider or produce effects.
public sealed class GroupHostAttentionNote
{
    internal GroupHostAttentionNote(GroupHostAttentionReason reason, bool gap, GroupHostAttentionReference[] references)
    { Reason = reason; HasCoverageGap = gap; SourceReferences = Array.AsReadOnly(references); }
    public GroupHostAttentionReason Reason { get; }
    public bool HasCoverageGap { get; }
    public IReadOnlyList<GroupHostAttentionReference> SourceReferences { get; }
    public string EncodePayload() => GroupBrainPayloadCodec.EncodeHostAttention(Reason, HasCoverageGap, SourceReferences);
    public override string ToString() => "Group host attention note (private plan).";
}

public sealed class GroupHostAttentionPlan
{
    private GroupHostAttentionPlan(GroupBatchSourcePreparation preparation, GroupHostAttentionNote[] notes)
    { Preparation = preparation; Notes = Array.AsReadOnly(notes); }
    internal GroupBatchSourcePreparation Preparation { get; }
    public GroupScope Scope => Preparation.Scope;
    public Guid BatchId => Preparation.BatchId;
    public bool HasCoverageGap => Preparation.Context.HasCoverageGap;
    public IReadOnlyList<GroupHostAttentionNote> Notes { get; }
    public override string ToString() => "Group host attention plan (private metadata).";

    // No caller-selected reason, source IDs, gap flag or model output. Readable
    // media captions remain separate AI candidates; quarantine stays excluded.
    // ExtractionFailed requires a later durable bounded-retry exhaustion proof.
    public static GroupHostAttentionPlan Create(GroupBatchSourcePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var eligible = preparation.Receipts.Where(x => x.Disposition is GroupSourcePreparationDisposition.ModelText
            or GroupSourcePreparationDisposition.EmptyText or GroupSourcePreparationDisposition.Quarantined).ToArray();
        var notes = new List<GroupHostAttentionNote>();
        Append(GroupHostAttentionReason.UnsupportedMedia, eligible.Where(x => x.HasUnsupportedMedia));
        Append(GroupHostAttentionReason.SecretQuarantine, eligible.Where(x => x.Disposition == GroupSourcePreparationDisposition.Quarantined));
        return new(preparation, notes.ToArray());

        void Append(GroupHostAttentionReason reason, IEnumerable<GroupSourcePreparationReceipt> observed)
        {
            var references = observed.OrderBy(x => x.MessageId.ToString("D"), StringComparer.Ordinal)
                .Select(x => new GroupHostAttentionReference(x.MessageId, x.Revision));
            foreach (var chunk in references.Chunk(10)) notes.Add(new(reason, preparation.Context.HasCoverageGap, chunk));
        }
    }
}
