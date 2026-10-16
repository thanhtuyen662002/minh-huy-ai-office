using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using System.Security.Cryptography;
using System.Text;

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
        selection_scope = SelectionScope,
        completeness = Completeness,
        selection_may_be_incomplete = SelectionMayBeIncomplete,
        // These are the frozen facts of this disclosure policy version.
        max_selected_revisions = 20,
        max_glossary_revisions = 4,
        max_request_code_hints = 100,
        request_metadata_probe_limit = 21,
        glossary_metadata_probe_limit = 5,
        selected_request_revisions = RequestCount,
        selected_glossary_revisions = GlossaryCount
    };
    // Closed original data only. Decoding does not populate selector issuance.
    internal const int MaximumOriginalBytes = 8 + 3 * (2 + 256) + 5 * 4 + 2 + 100 * 48;
    private const string SelectionScope = "same_authorized_group_current_generation_excluding_own_batch";
    private const string Completeness = "not_asserted";
    private static readonly UTF8Encoding OriginalUtf8 = new(false, true);
    private static readonly byte[] OriginalMagic = "AIOGHD01"u8.ToArray();

    internal byte[] EncodeOriginal()
    {
        using var stream = new MemoryStream(MaximumOriginalBytes);
        using var writer = new BinaryWriter(stream, OriginalUtf8, true);
        writer.Write(OriginalMagic);
        foreach (var text in new[] { PolicyVersion, SelectionScope, Completeness })
        {
            var bytes = OriginalUtf8.GetBytes(text);
            if (bytes.Length > 256) throw Unavailable();
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        foreach (var value in new[] { 20, 4, 100, 21, 5 }) writer.Write(value);
        writer.Write((byte)(SelectionMayBeIncomplete ? 1 : 0));
        if (entryHashes.Count is < 1 or > 100) throw Unavailable();
        writer.Write((byte)entryHashes.Count);
        foreach (var entry in entryHashes.OrderBy(x => x.Key))
        { writer.Write(entry.Key.ToByteArray()); writer.Write(Convert.FromHexString(entry.Value)); }
        writer.Flush();
        if (stream.Length > MaximumOriginalBytes) throw Unavailable();
        return stream.ToArray();
    }

    internal static void ValidateOriginal(byte[]? bytes, GroupWorkDependencyManifest manifest,
        IReadOnlyList<GroupAutomaticInputPlan.HistoryMember> history) => _ = ReadOriginal(bytes, manifest, history);

    internal static GroupHistorySelectionDisclosure RestoreOriginal(byte[] bytes,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext brain, GroupWorkDependencyManifest manifest,
        IReadOnlyList<GroupAutomaticInputPlan.HistoryMember> history, CancellationToken token)
    {
        var original = ReadOriginal(bytes, manifest, history);
        var result = new GroupHistorySelectionDisclosure(preparation.Context.Handle, manifest, original.Hashes,
            history.Select(x => new HistoryMember(x.Kind, x.RecordId)).ToArray(), original.Limited);
        result.RequireMatches(preparation, brain, false, token);
        if (!CryptographicOperations.FixedTimeEquals(bytes, result.EncodeOriginal())) throw Unavailable();
        return result;
    }

    private static (bool Limited, Dictionary<Guid, string> Hashes) ReadOriginal(byte[]? bytes,
        GroupWorkDependencyManifest manifest, IReadOnlyList<GroupAutomaticInputPlan.HistoryMember> history)
    {
        if (bytes is null || bytes.Length is < 64 or > MaximumOriginalBytes) throw Unavailable();
        try
        {
            if (history.Count > 20 || history.Count != manifest.Dependencies.Count
                || history.Count(x => x.Kind == GroupBrainContentKind.GlossaryRevision) > 4
                || history.Any(x => x.Kind is not (GroupBrainContentKind.RequestRevision or GroupBrainContentKind.GlossaryRevision))
                || !history.Select(x => (x.Kind, x.RecordId)).ToHashSet().SetEquals(manifest.Dependencies.Select(x => (x.Kind, x.RecordId)))) throw Unavailable();
            using var stream = new MemoryStream(bytes.ToArray(), false);
            using var reader = new BinaryReader(stream, OriginalUtf8, true);
            if (!Read(8).AsSpan().SequenceEqual(OriginalMagic)) throw Unavailable();
            foreach (var expected in new[] { PolicyVersion, SelectionScope, Completeness })
            {
                var length = reader.ReadUInt16();
                if (length > 256 || OriginalUtf8.GetString(Read(length)) != expected) throw Unavailable();
            }
            foreach (var expected in new[] { 20, 4, 100, 21, 5 }) if (reader.ReadInt32() != expected) throw Unavailable();
            var flag = reader.ReadByte(); if (flag > 1) throw Unavailable();
            var count = reader.ReadByte(); if (count != manifest.Sources.Count || count is < 1 or > 100) throw Unavailable();
            var hashes = new Dictionary<Guid, string>(); Guid? previous = null;
            for (var i = 0; i < count; i++)
            {
                var id = new Guid(Read(16)); var hash = Convert.ToHexString(Read(32));
                if (id == Guid.Empty || previous is not null && previous.Value.CompareTo(id) >= 0
                    || !manifest.Sources.Any(x => x.MessageId == id) || !hashes.TryAdd(id, hash)) throw Unavailable();
                previous = id;
            }
            if (stream.Position != stream.Length) throw Unavailable();
            return (flag == 1, hashes);

            byte[] Read(int count)
            {
                if (count > stream.Length - stream.Position) throw Unavailable();
                var value = reader.ReadBytes(count); if (value.Length != count) throw Unavailable(); return value;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or EndOfStreamException or OverflowException)
        { throw Unavailable(); }
    }

    private static GroupWorkDependencyManifest SourceManifest(GroupBatchSourcePreparation preparation) =>
        GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(preparation.Context,
            new(preparation.Context.Handle, [], []), preparation.BatchId));
    private static string EntryHash(GroupBatchSourceEntry entry) =>
        GroupWorkDependencyManifest.Fingerprint("aioffice-history-source-input-v1", entry);
    private static InvalidOperationException Unavailable() => new("Group history selection disclosure is not available.");
}
