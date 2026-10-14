using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Original bounded host selection metadata, not complete history, live SQL
// authority, provider permission, a token profile or durable plan storage.
internal sealed class GroupHistorySelectionDisclosure
{
    internal const string PolicyVersion = "group-history-selection-v1";
    private readonly GroupBatchClaimHandle handle;
    private readonly GroupWorkDependencyManifest sourceManifest;
    private readonly Dictionary<Guid, string> entryHashes;
    private readonly HistoryMember[] selected;

    private GroupHistorySelectionDisclosure(GroupBatchClaimHandle handle, GroupWorkDependencyManifest manifest,
        Dictionary<Guid, string> entryHashes, HistoryMember[] selected, bool limited)
    {
        this.handle = handle; sourceManifest = manifest;
        this.entryHashes = new(entryHashes); this.selected = selected.ToArray();
        SelectionMayBeIncomplete = limited;
        RequestCount = selected.Count(x => x.Kind == GroupBrainContentKind.RequestRevision);
        GlossaryCount = selected.Count(x => x.Kind == GroupBrainContentKind.GlossaryRevision);
    }
    internal GroupScope Scope => sourceManifest.Scope;
    internal Guid BatchId => sourceManifest.BatchId;
    internal bool SelectionMayBeIncomplete { get; }
    internal int RequestCount { get; }
    internal int GlossaryCount { get; }
    internal IReadOnlyList<HistoryMember> SelectedOrder => Array.AsReadOnly(selected.ToArray());
    internal sealed record HistoryMember(GroupBrainContentKind Kind, Guid RecordId);
    public override string ToString() => "Group history selection disclosure (private metadata).";

    // This captures data only. V2 consumers MUST obtain the resulting object
    // through the selector's private issuance table, which only SelectAsync
    // populates after its original final current-source fence.
    internal static GroupHistorySelectionDisclosure Capture(GroupBatchSourcePreparation preparation,
        GroupAutomaticHistorySelection selection)
    {
        if (selection.Scope != preparation.Scope) throw Unavailable();
        var manifest = SourceManifest(preparation);
        var selected = selection.RequestIds.Select(x => new HistoryMember(GroupBrainContentKind.RequestRevision, x))
            .Concat(selection.GlossaryIds.Select(x => new HistoryMember(GroupBrainContentKind.GlossaryRevision, x))).ToArray();
        if (selected.Length > GroupBrainCurrentReader.MaximumSelectedRevisions || selection.GlossaryIds.Count > 4
            || selected.Any(x => x.RecordId == Guid.Empty) || selected.Distinct().Count() != selected.Length) throw Unavailable();
        return new(preparation.Context.Handle, manifest, preparation.Context.Items.ToDictionary(x => x.MessageId, EntryHash),
            selected, selection.HasAdditionalHistory);
    }

    internal void RequireMatches(GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain,
        bool allowSourceSubset, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(preparation); ArgumentNullException.ThrowIfNull(brain); token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(preparation.Context.Handle, handle) || !ReferenceEquals(brain.Handle, handle)
            || preparation.Scope != Scope || brain.Scope != Scope || preparation.BatchId != BatchId) throw Unavailable();
        var current = SourceManifest(preparation);
        if (current.AllocatedThroughSequence != sourceManifest.AllocatedThroughSequence
            || current.AuthoritySha256 != sourceManifest.AuthoritySha256 || current.CoverageSha256 != sourceManifest.CoverageSha256
            || !allowSourceSubset && !current.Sources.SequenceEqual(sourceManifest.Sources)
            || current.Sources.Any(x => !sourceManifest.Sources.Contains(x))) throw Unavailable();
        foreach (var entry in preparation.Context.Items)
        {
            token.ThrowIfCancellationRequested();
            if (!entryHashes.TryGetValue(entry.MessageId, out var expected) || EntryHash(entry) != expected) throw Unavailable();
        }
        var order = brain.Items.Select(x => new HistoryMember(x.Kind, x.RecordId)).ToArray();
        if (!order.SequenceEqual(selected) || brain.Snapshots.Count != selected.Length
            || brain.Snapshots.Where((snapshot, index) => snapshot.Kind != selected[index].Kind
                || snapshot.RecordId != selected[index].RecordId || snapshot.Revision.Revision != brain.Items[index].Revision).Any()) throw Unavailable();
        // Validate the entire contributing structural set. Fresh SQL authority
        // still belongs to the existing later release/effect fences.
        _ = GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(preparation.Context, brain, BatchId));
        token.ThrowIfCancellationRequested();
    }

    internal object PromptMetadata() => new
    {
        policy_version = PolicyVersion,
        selection_scope = "same_authorized_group_current_generation_excluding_own_batch",
        completeness = "not_asserted",
        selection_may_be_incomplete = SelectionMayBeIncomplete,
        max_selected_revisions = GroupBrainCurrentReader.MaximumSelectedRevisions,
        max_glossary_revisions = 4,
        max_request_code_hints = GroupAutomaticHistoryHints.MaximumRequestCodes,
        request_metadata_probe_limit = GroupBrainCurrentReader.MaximumSelectedRevisions + 1,
        glossary_metadata_probe_limit = 5,
        selected_request_revisions = RequestCount,
        selected_glossary_revisions = GlossaryCount
    };
    private static GroupWorkDependencyManifest SourceManifest(GroupBatchSourcePreparation preparation) =>
        GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(preparation.Context,
            new(preparation.Context.Handle, [], []), preparation.BatchId));
    private static string EntryHash(GroupBatchSourceEntry entry) =>
        GroupWorkDependencyManifest.Fingerprint("aioffice-history-source-input-v1", entry);
    private static InvalidOperationException Unavailable() => new("Group history selection disclosure is not available.");
}
