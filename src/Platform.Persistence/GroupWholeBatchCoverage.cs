using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// A bounded structural prerequisite for a future terminal transaction. This
// does not validate current authority/dependencies or release a frontier.
internal sealed class GroupWholeBatchCoverage
{
    private GroupWholeBatchCoverage(GroupWorkDependencyManifest[] manifests, int rawCount, int notes)
    { Manifests = Array.AsReadOnly(manifests); RawCount = rawCount; NoteCount = notes; }
    internal IReadOnlyList<GroupWorkDependencyManifest> Manifests { get; }
    internal int RawCount { get; }
    internal int NoteCount { get; }
    public override string ToString() => "Group batch coverage (private metadata).";

    internal static GroupWholeBatchCoverage Require(GroupBatchAllocationReceipt allocation,
        IReadOnlyList<GroupWorkCommitReceiptRecord> receipts, IReadOnlyList<GroupWorkSourceDispositionRecord> selected,
        IReadOnlyList<GroupWorkRawDispositionRecord> raw)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(receipts); ArgumentNullException.ThrowIfNull(selected); ArgumentNullException.ThrowIfNull(raw);
        var scope = allocation.Scope;
        if (scope.TenantId == Guid.Empty || scope.CompanyId == Guid.Empty || scope.SourceBindingId == Guid.Empty
            || allocation.BatchId == Guid.Empty || allocation.OperationId == Guid.Empty || allocation.AfterSequence < 0
            || allocation.Revisions.Count is < 1 or > GroupBatchAllocationPrefix.MaximumRawRevisions
            || allocation.AllocatedThroughSequence <= allocation.AfterSequence
            || allocation.AllocatedThroughSequence - allocation.AfterSequence != allocation.Revisions.Count
            || allocation.ObservedCommittedThroughSequence < allocation.AllocatedThroughSequence
            || allocation.AllocatedAtUtc.Offset != TimeSpan.Zero
            || receipts.Count is < 1 or > FrozenGroupBatch.MaximumMessages || selected.Count is < 1 or > FrozenGroupBatch.MaximumMessages
            || raw.Count != allocation.Revisions.Count) throw Unavailable();
        var revisions = allocation.Revisions.ToArray();
        var heads = new Dictionary<Guid, GroupPendingRevisionMetadata>();
        var identities = new HashSet<(Guid, long)>();
        for (var index = 0; index < revisions.Length; index++)
        {
            if (revisions[index] is null || revisions[index].Metadata is null
                || revisions[index].SourceVersion <= 0 || revisions[index].DeletionGeneration < 0) throw Unavailable();
            var row = revisions[index].Metadata;
            if (row.Scope != scope || row.CommittedSequence != allocation.AfterSequence + index + 1 || row.MessageId == Guid.Empty
                || row.Revision <= 0 || row.CommittedAtUtc.Offset != TimeSpan.Zero || row.CommittedAtUtc > allocation.AllocatedAtUtc
                || row.IsHistoricalBackfill != allocation.IsHistoricalBackfill || row.ContentSha256 is null || row.ContentSha256.Length != 64
                || row.ContentSha256.Any(x => x is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
                || !Enum.IsDefined(row.Kind) || !identities.Add((row.MessageId, row.Revision))
                || heads.TryGetValue(row.MessageId, out var previous) && row.Revision <= previous.Revision) throw Unavailable();
            heads[row.MessageId] = row;
        }
        if (heads.Count != selected.Count || heads.Count > FrozenGroupBatch.MaximumMessages) throw Unavailable();
        var dispositions = new Dictionary<Guid, GroupWorkSourceDispositionRecord>();
        foreach (var row in selected)
        {
            if (row is null || !Scoped(row.TenantId, row.CompanyId, row.BindingId, row.BatchId) || row.OperationId == Guid.Empty
                || !heads.TryGetValue(row.MessageId, out var head) || row.MessageRevision != head.Revision
                || !Enum.IsDefined(row.Outcome) || !dispositions.TryAdd(row.MessageId, row)) throw Unavailable();
        }
        var contributors = new Dictionary<Guid, GroupWorkDependencyManifest>(); var noteCount = 0;
        foreach (var receipt in receipts)
        {
            if (receipt is null || !Scoped(receipt.TenantId, receipt.CompanyId, receipt.BindingId, receipt.BatchId) || receipt.OperationId == Guid.Empty
                || receipt.ServiceId == Guid.Empty || receipt.ClaimEpoch <= 0 || receipt.CredentialEpoch <= 0 || receipt.GrantVersion <= 0
                || receipt.SourceVersion <= 0 || receipt.DeletionGeneration < 0 || receipt.AccountVersion <= 0
                || receipt.CommittedAtUtc.Offset != TimeSpan.Zero || receipt.CommittedAtUtc < allocation.AllocatedAtUtc
                || receipt.SelectedMessageCount is < 1 or > FrozenGroupBatch.MaximumMessages
                || receipt.NoteCount is < 0 or > GroupAutomaticNotePlan.MaximumNotes || !Enum.IsDefined(receipt.Outcome)
                || (receipt.NoteCount == 0) != (receipt.Outcome == GroupWorkCommitOutcome.NoWork)
                || receipt.DependencyManifestVersion != GroupWorkDependencyManifest.Version) throw Unavailable();
            var manifest = GroupWorkDependencyManifest.Read(receipt.DependencyManifest?.ToArray());
            var own = selected.Where(x => x.OperationId == receipt.OperationId).OrderBy(x => x.MessageId).ToArray();
            if (manifest.Scope != scope || manifest.BatchId != allocation.BatchId || manifest.OperationId != receipt.OperationId
                || manifest.AllocatedThroughSequence != allocation.AllocatedThroughSequence
                || own.Length != receipt.SelectedMessageCount || manifest.Sources.Count != own.Length
                || manifest.Sources.Where((source, index) => source.MessageId != own[index].MessageId || source.Revision != own[index].MessageRevision).Any()
                || !contributors.TryAdd(receipt.OperationId, manifest)) throw Unavailable();
            noteCount += receipt.NoteCount;
        }
        if (dispositions.Values.Any(x => !contributors.ContainsKey(x.OperationId))) throw Unavailable();
        if (raw.Any(x => x is null)) throw Unavailable();
        var rows = raw.OrderBy(x => x.CommittedSequence).ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index]; var allocated = revisions[index].Metadata;
            var head = dispositions[allocated.MessageId];
            if (!Scoped(row.TenantId, row.CompanyId, row.BindingId, row.BatchId) || row.CommittedSequence != allocated.CommittedSequence
                || row.MessageId != allocated.MessageId || row.RawRevision != allocated.Revision || row.SelectedMessageRevision != head.MessageRevision
                || row.OperationId != head.OperationId || row.Outcome != head.Outcome
                || row.Relation != (row.RawRevision == row.SelectedMessageRevision
                    ? GroupWorkRawRelation.SelectedHead : GroupWorkRawRelation.SupersededBySelectedHead)) throw Unavailable();
        }
        return new(contributors.OrderBy(x => x.Key).Select(x => x.Value).ToArray(), rows.Length, noteCount);

        bool Scoped(Guid tenant, Guid company, Guid binding, Guid batch) => tenant == scope.TenantId && company == scope.CompanyId
            && binding == scope.SourceBindingId && batch == allocation.BatchId;
    }
    private static InvalidOperationException Unavailable() => new("Whole group batch coverage is not available.");
}
