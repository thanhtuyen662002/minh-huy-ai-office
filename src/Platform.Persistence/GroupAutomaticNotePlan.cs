using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class GroupAutomaticSourceReceipt
{
    internal GroupAutomaticSourceReceipt(Guid messageId, long revision, GroupWorkSourceOutcome outcome, bool attention)
    { MessageId = messageId; Revision = revision; Outcome = outcome; HasHostAttention = attention; }
    public Guid MessageId { get; }
    public long Revision { get; }
    public GroupWorkSourceOutcome Outcome { get; }
    public bool HasHostAttention { get; }
    public override string ToString() => "Group automatic source receipt (private plan).";
}

// Combines sealed text interpretations and host observations for one exact
// preparation. This is not an effect, raw-ledger completion or retry proof.
public sealed class GroupAutomaticNotePlan
{
    public const int MaximumNotes = 40; // At most20 AI +20 host, without truncation.
    private GroupAutomaticNotePlan(GroupBatchSourcePreparation preparation, GroupGroundedWorkProposal? proposal,
        GroupHostAttentionPlan host, GroupAutomaticSourceReceipt[] receipts, GroupAutomaticModelContext? extractionFailure = null)
    {
        Preparation = preparation; Proposal = proposal; Host = host;
        SourceDispositions = Array.AsReadOnly(receipts); ExtractionFailureContext = extractionFailure;
    }
    internal GroupBatchSourcePreparation Preparation { get; }
    internal GroupGroundedWorkProposal? Proposal { get; }
    internal GroupHostAttentionPlan Host { get; }
    internal GroupAutomaticModelContext? ExtractionFailureContext { get; }
    public GroupScope Scope => Preparation.Scope;
    public Guid BatchId => Preparation.BatchId;
    public bool HasCoverageGap => Host.HasCoverageGap;
    public IReadOnlyList<GroupGroundedWorkNote> AiNotes => Proposal?.Notes ?? Array.Empty<GroupGroundedWorkNote>();
    public IReadOnlyList<GroupHostAttentionNote> HostNotes => Host.Notes;
    public IReadOnlyList<GroupAutomaticSourceReceipt> SourceDispositions { get; }
    public int NoteCount => AiNotes.Count + HostNotes.Count;
    public override string ToString() => "Group automatic note plan (private content).";

    public static GroupAutomaticNotePlan Create(GroupBatchSourcePreparation preparation, GroupGroundedWorkProposal? proposal = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if ((proposal is not null && !ReferenceEquals(preparation, proposal.Preparation))
            || (preparation.Candidates.Count != 0 && proposal is null)) throw Unavailable();
        var host = GroupHostAttentionPlan.Create(preparation);
        if ((proposal?.Notes.Count ?? 0) + host.Notes.Count > MaximumNotes) throw Unavailable();
        var model = proposal?.SourceDispositions.ToDictionary(x => x.MessageId);
        var observed = host.Notes.SelectMany(x => x.SourceReferences).Select(x => (x.MessageId, x.Revision)).ToHashSet();
        var receipts = new List<GroupAutomaticSourceReceipt>();
        foreach (var source in preparation.Receipts.OrderBy(x => x.MessageId.ToString("D"), StringComparer.Ordinal))
        {
            var attention = observed.Contains((source.MessageId, source.Revision));
            var outcome = source.Disposition switch
            {
                GroupSourcePreparationDisposition.ModelText => ModelOutcome(),
                GroupSourcePreparationDisposition.Quarantined => GroupWorkSourceOutcome.Quarantined,
                GroupSourcePreparationDisposition.EmptyText => attention ? GroupWorkSourceOutcome.Attention : GroupWorkSourceOutcome.NoWork,
                GroupSourcePreparationDisposition.Recalled => GroupWorkSourceOutcome.Recalled,
                GroupSourcePreparationDisposition.ObsoleteGeneration => GroupWorkSourceOutcome.ObsoleteGeneration,
                GroupSourcePreparationDisposition.ChangedAfterCutoff => GroupWorkSourceOutcome.ChangedAfterCutoff,
                _ => throw Unavailable()
            };
            receipts.Add(new(source.MessageId, source.Revision, outcome, attention));
            GroupWorkSourceOutcome ModelOutcome()
            {
                if (model is null || !model.TryGetValue(source.MessageId, out var verdict) || verdict.Revision != source.Revision) throw Unavailable();
                return verdict.Disposition switch
                {
                    GroupModelSourceDisposition.Work => GroupWorkSourceOutcome.Work,
                    GroupModelSourceDisposition.NoWork => attention ? GroupWorkSourceOutcome.Attention : GroupWorkSourceOutcome.NoWork,
                    _ => throw Unavailable()
                };
            }
        }
        return new(preparation, proposal, host, receipts.ToArray());
    }

    // A structural plan only. Public callers cannot select the reason or
    // assert exhaustion; CommitExtractionFailureAsync rechecks the SQL journal.
    internal static GroupAutomaticNotePlan CreateExtractionFailure(GroupAutomaticModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var preparation = context.Preparation;
        var host = GroupHostAttentionPlan.CreateExtractionFailure(preparation);
        if (host.Notes.Count > MaximumNotes) throw Unavailable();
        var observed = host.Notes.SelectMany(x => x.SourceReferences).Select(x => (x.MessageId, x.Revision)).ToHashSet();
        var receipts = preparation.Receipts.OrderBy(x => x.MessageId.ToString("D"), StringComparer.Ordinal).Select(source =>
        {
            var attention = observed.Contains((source.MessageId, source.Revision));
            var outcome = source.Disposition switch
            {
                GroupSourcePreparationDisposition.ModelText when attention => GroupWorkSourceOutcome.Attention,
                GroupSourcePreparationDisposition.Quarantined => GroupWorkSourceOutcome.Quarantined,
                GroupSourcePreparationDisposition.EmptyText => attention ? GroupWorkSourceOutcome.Attention : GroupWorkSourceOutcome.NoWork,
                GroupSourcePreparationDisposition.Recalled => GroupWorkSourceOutcome.Recalled,
                GroupSourcePreparationDisposition.ObsoleteGeneration => GroupWorkSourceOutcome.ObsoleteGeneration,
                GroupSourcePreparationDisposition.ChangedAfterCutoff => GroupWorkSourceOutcome.ChangedAfterCutoff,
                _ => throw Unavailable()
            };
            return new GroupAutomaticSourceReceipt(source.MessageId, source.Revision, outcome, attention);
        }).ToArray();
        return new(preparation, null, host, receipts, context);
    }
    private static InvalidOperationException Unavailable() => new("Group automatic note plan is not available.");
}
