using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// A bounded structural prerequisite for a future terminal transaction. This
// does not validate current authority/dependencies or release a frontier.
internal sealed class GroupWholeBatchCoverage
{
    private readonly GroupWorkCommitReceiptRecord[] originalReceipts;
    private GroupWholeBatchCoverage(GroupBatchAllocationReceipt allocation, GroupWorkDependencyManifest[] manifests,
        GroupWorkCommitReceiptRecord[] receipts, int rawCount, int notes, string fingerprint)
    {
        Scope = allocation.Scope; BatchId = allocation.BatchId; AllocationOperationId = allocation.OperationId;
        AfterSequence = allocation.AfterSequence; ThroughSequence = allocation.AllocatedThroughSequence;
        ObservedCommittedThrough = allocation.ObservedCommittedThroughSequence; AllocatedAtUtc = allocation.AllocatedAtUtc;
        IsHistoricalBackfill = allocation.IsHistoricalBackfill; Manifests = Array.AsReadOnly(manifests);
        originalReceipts = receipts; RawCount = rawCount; NoteCount = notes; Fingerprint = fingerprint;
    }
    internal GroupScope Scope { get; }
    internal Guid BatchId { get; }
    internal Guid AllocationOperationId { get; }
    internal long AfterSequence { get; }
    internal long ThroughSequence { get; }
    internal long ObservedCommittedThrough { get; }
    internal DateTimeOffset AllocatedAtUtc { get; }
    internal bool IsHistoricalBackfill { get; }
    internal string Fingerprint { get; }
    internal IReadOnlyList<GroupWorkDependencyManifest> Manifests { get; }
    internal int RawCount { get; }
    internal int NoteCount { get; }
    public override string ToString() => "Group batch coverage (private metadata).";

    internal static GroupWholeBatchCoverage Require(GroupBatchAllocationReceipt allocation,
        IReadOnlyList<GroupPendingRevisionMetadata> cutoffHeads,
        IReadOnlyList<GroupWorkCommitReceiptRecord> receipts, IReadOnlyList<GroupWorkSourceDispositionRecord> selected,
        IReadOnlyList<GroupWorkRawDispositionRecord> raw)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(cutoffHeads);
        ArgumentNullException.ThrowIfNull(receipts); ArgumentNullException.ThrowIfNull(selected); ArgumentNullException.ThrowIfNull(raw);
        // Count/indexer/CopyTo on a supplied collection is not evidence of the
        // enumerated input. Bound every actual enumeration before using it.
        var revisions = Freeze(allocation.Revisions, GroupBatchAllocationPrefix.MaximumRawRevisions, x => x);
        var actualHeads = Freeze(cutoffHeads, FrozenGroupBatch.MaximumMessages, x => x);
        var actualReceipts = Freeze(receipts, FrozenGroupBatch.MaximumMessages, CopyReceipt);
        var actualSelected = Freeze(selected, FrozenGroupBatch.MaximumMessages, x => new GroupWorkSourceDispositionRecord
        {
            TenantId = x.TenantId,
            CompanyId = x.CompanyId,
            BindingId = x.BindingId,
            BatchId = x.BatchId,
            MessageId = x.MessageId,
            MessageRevision = x.MessageRevision,
            OperationId = x.OperationId,
            Outcome = x.Outcome
        });
        var actualRaw = Freeze(raw, GroupBatchAllocationPrefix.MaximumRawRevisions, x => new GroupWorkRawDispositionRecord
        {
            TenantId = x.TenantId,
            CompanyId = x.CompanyId,
            BindingId = x.BindingId,
            BatchId = x.BatchId,
            CommittedSequence = x.CommittedSequence,
            MessageId = x.MessageId,
            RawRevision = x.RawRevision,
            SelectedMessageRevision = x.SelectedMessageRevision,
            OperationId = x.OperationId,
            Outcome = x.Outcome,
            Relation = x.Relation
        });
        var scope = allocation.Scope;
        if (scope.TenantId == Guid.Empty || scope.CompanyId == Guid.Empty || scope.SourceBindingId == Guid.Empty
            || allocation.BatchId == Guid.Empty || allocation.OperationId == Guid.Empty || allocation.AfterSequence < 0
            || revisions.Length < 1
            || allocation.AllocatedThroughSequence <= allocation.AfterSequence
            || allocation.AllocatedThroughSequence - allocation.AfterSequence != revisions.Length
            || allocation.ObservedCommittedThroughSequence < allocation.AllocatedThroughSequence
            || allocation.AllocatedAtUtc.Offset != TimeSpan.Zero
            || actualHeads.Length < 1 || actualReceipts.Length < 1 || actualSelected.Length < 1
            || actualRaw.Length != revisions.Length) throw Unavailable();
        var previousRevisions = new Dictionary<Guid, long>();
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
                || previousRevisions.TryGetValue(row.MessageId, out var previous) && row.Revision <= previous) throw Unavailable();
            previousRevisions[row.MessageId] = row.Revision;
        }
        // The source reader chooses Recall > Edit > other kinds over ALL
        // original revisions through the cutoff. The winner can precede this
        // allocation; allocation rows alone cannot reconstruct its identity.
        // The owned SQL unit must independently reconstruct these cutoff heads.
        var heads = new Dictionary<Guid, GroupPendingRevisionMetadata>();
        foreach (var row in actualHeads)
        {
            if (row is null || row.Scope != scope || row.MessageId == Guid.Empty || row.Revision <= 0
                || row.CommittedSequence <= 0 || row.CommittedSequence > allocation.AllocatedThroughSequence
                || row.CommittedAtUtc.Offset != TimeSpan.Zero || row.CommittedAtUtc > allocation.AllocatedAtUtc
                || row.ContentSha256 is null || row.ContentSha256.Length != 64
                || row.ContentSha256.Any(x => x is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
                || !Enum.IsDefined(row.Kind) || !previousRevisions.ContainsKey(row.MessageId)
                || row.Revision > previousRevisions[row.MessageId] || !heads.TryAdd(row.MessageId, row)) throw Unavailable();
            if (row.CommittedSequence > allocation.AfterSequence)
            {
                if (revisions[(int)(row.CommittedSequence - allocation.AfterSequence - 1)].Metadata != row) throw Unavailable();
            }
            else if (row.Revision >= revisions.First(x => x.Metadata.MessageId == row.MessageId).Metadata.Revision) throw Unavailable();
        }
        if (heads.Count != previousRevisions.Count) throw Unavailable();
        if (heads.Count != actualSelected.Length) throw Unavailable();
        var dispositions = new Dictionary<Guid, GroupWorkSourceDispositionRecord>();
        foreach (var row in actualSelected)
        {
            if (row is null || !Scoped(row.TenantId, row.CompanyId, row.BindingId, row.BatchId) || row.OperationId == Guid.Empty
                || !heads.TryGetValue(row.MessageId, out var head) || row.MessageRevision != head.Revision
                || !Enum.IsDefined(row.Outcome) || !dispositions.TryAdd(row.MessageId, row)) throw Unavailable();
        }
        var contributors = new Dictionary<Guid, GroupWorkDependencyManifest>(); var noteCount = 0;
        foreach (var receipt in actualReceipts)
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
            var own = actualSelected.Where(x => x.OperationId == receipt.OperationId).OrderBy(x => x.MessageId).ToArray();
            if (manifest.Scope != scope || manifest.BatchId != allocation.BatchId || manifest.OperationId != receipt.OperationId
                || manifest.AllocatedThroughSequence != allocation.AllocatedThroughSequence
                || own.Length != receipt.SelectedMessageCount || manifest.Sources.Count != own.Length
                || manifest.Sources.Where((source, index) => source.MessageId != own[index].MessageId || source.Revision != own[index].MessageRevision).Any()
                || !contributors.TryAdd(receipt.OperationId, manifest)) throw Unavailable();
            noteCount += receipt.NoteCount;
        }
        if (dispositions.Values.Any(x => !contributors.ContainsKey(x.OperationId))) throw Unavailable();
        var rows = actualRaw.OrderBy(x => x.CommittedSequence).ToArray();
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
        var orderedReceipts = actualReceipts.OrderBy(x => x.OperationId).ToArray();
        // Hash the frozen complete allocation/heads/receipts/selected/raw input.
        // No later enumerator, caller mutation or provider can replace an input.
        var frozenAllocation = allocation with { Revisions = Array.AsReadOnly(revisions) };
        var fingerprint = GroupWorkDependencyManifest.Fingerprint("aioffice-group-whole-batch-coverage-v1", new
        {
            Allocation = new
            {
                allocation.Scope,
                allocation.BatchId,
                allocation.OperationId,
                allocation.AfterSequence,
                allocation.AllocatedThroughSequence,
                allocation.ObservedCommittedThroughSequence,
                allocation.IsHistoricalBackfill,
                allocation.SourceVersion,
                allocation.DeletionGeneration,
                allocation.AccountVersion,
                allocation.ServiceId,
                allocation.CredentialEpoch,
                allocation.GrantVersion,
                allocation.AllocatedAtUtc,
                Revisions = revisions
            },
            CutoffHeads = actualHeads.OrderBy(x => x.MessageId).ToArray(),
            Receipts = orderedReceipts,
            Selected = actualSelected.OrderBy(x => x.MessageId).ToArray(),
            Raw = rows
        });
        return new(frozenAllocation, contributors.OrderBy(x => x.Key).Select(x => x.Value).ToArray(),
            orderedReceipts, rows.Length, noteCount, fingerprint);

        bool Scoped(Guid tenant, Guid company, Guid binding, Guid batch) => tenant == scope.TenantId && company == scope.CompanyId
            && binding == scope.SourceBindingId && batch == allocation.BatchId;
    }

    // This verifies sealed structural ledgers against every copied expectation.
    // SQL/current authorization and own-input dependency checks remain the
    // responsibility of the future owned terminal transaction.
    internal IReadOnlyList<GroupWorkEffectLedger> RequireOriginalEffects(IEnumerable<GroupWorkEffectLedger> input)
    {
        try
        {
            var effects = Freeze(input, FrozenGroupBatch.MaximumMessages, x => x);
            if (effects.Length != originalReceipts.Length) throw Unavailable();
            var byOperation = new Dictionary<Guid, GroupWorkEffectLedger>();
            foreach (var effect in effects)
            {
                if (effect.Scope != Scope || effect.BatchId != BatchId || !byOperation.TryAdd(effect.OperationId, effect)) throw Unavailable();
            }
            foreach (var receipt in originalReceipts)
            {
                if (!byOperation.TryGetValue(receipt.OperationId, out var effect)) throw Unavailable();
                GroupWorkEffectDigest.RequireUnchanged(receipt, effect);
            }
            return Array.AsReadOnly(effects.OrderBy(x => x.OperationId).ToArray());
        }
        catch (Exception) { throw Unavailable(); }
    }
    private static T[] Freeze<T>(IEnumerable<T> source, int maximum, Func<T, T> copy) where T : class
    {
        if (source is null) throw Unavailable();
        var rows = new List<T>(maximum);
        foreach (var row in source)
        {
            if (row is null || rows.Count == maximum) throw Unavailable();
            rows.Add(copy(row));
        }
        return rows.ToArray();
    }
    private static GroupWorkCommitReceiptRecord CopyReceipt(GroupWorkCommitReceiptRecord x)
    {
        if (x.DependencyManifest is { Length: > GroupWorkDependencyManifest.MaximumBytes }
            || x.ExpectedEffectSha256 is { Length: > GroupWorkEffectDigest.DigestBytes }) throw Unavailable();
        return new()
        {
            TenantId = x.TenantId,
            CompanyId = x.CompanyId,
            BindingId = x.BindingId,
            BatchId = x.BatchId,
            OperationId = x.OperationId,
            SourceSetSha256 = x.SourceSetSha256,
            DependencyManifestVersion = x.DependencyManifestVersion,
            DependencyManifest = x.DependencyManifest?.ToArray(),
            EffectLedgerVersion = x.EffectLedgerVersion,
            ExpectedEffectSha256 = x.ExpectedEffectSha256?.ToArray(),
            SelectedMessageCount = x.SelectedMessageCount,
            NoteCount = x.NoteCount,
            Outcome = x.Outcome,
            ServiceId = x.ServiceId,
            ClaimEpoch = x.ClaimEpoch,
            CredentialEpoch = x.CredentialEpoch,
            GrantVersion = x.GrantVersion,
            SourceVersion = x.SourceVersion,
            DeletionGeneration = x.DeletionGeneration,
            AccountVersion = x.AccountVersion,
            CommittedAtUtc = x.CommittedAtUtc
        };
    }
    private static InvalidOperationException Unavailable() => new("Whole group batch coverage is not available.");
}
