using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Internal projection for the shared fixed writer. Public callers can supply
// only sealed source/proposal plans; model output never selects provenance.
internal sealed class GroupNoteEffectPlan
{
    private GroupNoteEffectPlan(GroupBatchSourcePreparation preparation, Entry[] notes,
        GroupAutomaticSourceReceipt[] selected, bool automatic)
    {
        Preparation = preparation; Notes = Array.AsReadOnly(notes); Selected = Array.AsReadOnly(selected);
        MaximumNotes = automatic ? GroupAutomaticNotePlan.MaximumNotes : GroupGroundedWorkProposal.MaximumNotes;
        MaximumEvidenceRows = GroupGroundedWorkProposal.MaximumEvidence + (automatic ? 200 : 0);
        Outcome = notes.Any(x => x.Origin == GroupRequestRevisionOrigin.AiExtracted) ? GroupWorkCommitOutcome.Notes : GroupWorkCommitOutcome.Attention;
        if (notes.Length is < 1 || notes.Length > MaximumNotes || notes.Sum(x => x.Evidence.Count) > MaximumEvidenceRows) throw Unavailable();
    }
    internal GroupBatchSourcePreparation Preparation { get; }
    internal IReadOnlyList<Entry> Notes { get; }
    internal IReadOnlyList<GroupAutomaticSourceReceipt> Selected { get; }
    internal int MaximumNotes { get; }
    internal int MaximumEvidenceRows { get; }
    internal GroupWorkCommitOutcome Outcome { get; }
    public override string ToString() => "Group fixed note effect plan (private content).";

    internal static GroupNoteEffectPlan FromProposal(GroupGroundedWorkProposal proposal) => Project(() => new(proposal.Preparation,
        proposal.Notes.Select(Entry.FromAi).ToArray(), proposal.SourceDispositions.Select(x => new GroupAutomaticSourceReceipt(x.MessageId, x.Revision,
            x.Disposition switch
            {
                GroupModelSourceDisposition.Work => GroupWorkSourceOutcome.Work,
                GroupModelSourceDisposition.NoWork => GroupWorkSourceOutcome.NoWork,
                _ => throw Unavailable()
            }, false)).ToArray(), false));

    internal static GroupNoteEffectPlan FromAutomatic(GroupAutomaticNotePlan plan) => Project(() => new(plan.Preparation,
        plan.AiNotes.Select(Entry.FromAi).Concat(plan.HostNotes.Select(Entry.FromHost)).ToArray(), plan.SourceDispositions.ToArray(), true));

    private static GroupNoteEffectPlan Project(Func<GroupNoteEffectPlan> build)
    {
        try { return build(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { throw Unavailable(); }
    }

    internal sealed class Entry
    {
        private Entry(GroupNoteKind kind, GroupNoteBusinessStatus status, GroupRequestRevisionOrigin origin,
            GroupRequestVerificationLevel verification, GroupRequestEvidenceKind evidenceKind, string payload, GroupHostAttentionReference[] evidence)
        {
            Kind = kind; BusinessStatus = status; Origin = origin; VerificationLevel = verification; EvidenceKind = evidenceKind;
            Payload = payload; Evidence = Array.AsReadOnly(evidence);
        }
        internal GroupNoteKind Kind { get; }
        internal GroupNoteBusinessStatus BusinessStatus { get; }
        internal GroupRequestRevisionOrigin Origin { get; }
        internal GroupRequestVerificationLevel VerificationLevel { get; }
        internal GroupRequestEvidenceKind EvidenceKind { get; }
        internal string Payload { get; }
        internal IReadOnlyList<GroupHostAttentionReference> Evidence { get; }
        public override string ToString() => "Group fixed note entry (private content).";
        internal static Entry FromAi(GroupGroundedWorkNote note) => new(note.Kind switch
        {
            GroupWorkProposalKind.Incident => GroupNoteKind.Incident,
            GroupWorkProposalKind.ChangeRequest => GroupNoteKind.ChangeRequest,
            GroupWorkProposalKind.Request => GroupNoteKind.Question,
            GroupWorkProposalKind.NeedsClarification => GroupNoteKind.NeedsClarification,
            _ => throw Unavailable()
        }, note.Kind == GroupWorkProposalKind.NeedsClarification || note.MissingFields.Count != 0 || note.SuggestedRelationHint is not null
            ? GroupNoteBusinessStatus.NeedsClarification : GroupNoteBusinessStatus.New,
            GroupRequestRevisionOrigin.AiExtracted, GroupRequestVerificationLevel.SourceBackedAiInterpretation,
            GroupRequestEvidenceKind.LiteralSourceQuote, GroupBrainPayloadCodec.EncodeAiNote(note),
            note.Evidence.Select(x => new GroupHostAttentionReference(x.MessageId, x.Revision)).ToArray());
        internal static Entry FromHost(GroupHostAttentionNote note)
        {
            if (note.Reason is not (GroupHostAttentionReason.UnsupportedMedia or GroupHostAttentionReason.SecretQuarantine)) throw Unavailable();
            return new(GroupNoteKind.NeedsClarification, GroupNoteBusinessStatus.NeedsClarification, GroupRequestRevisionOrigin.HostAttention,
                GroupRequestVerificationLevel.HostObserved, GroupRequestEvidenceKind.HostMetadataAttention, note.EncodePayload(), note.SourceReferences.ToArray());
        }
    }
    private static InvalidOperationException Unavailable() => new("Group note commit is unavailable.");
}
