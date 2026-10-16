namespace MinhHuy.AIOffice.Platform.Persistence;

// Non-authorizing, ephemeral metadata observation. The owner must still perform
// its own current claim/permissions/activity/write fences. No plaintext or keys.
internal sealed class GroupDependencyMetadataInspection
{
    internal GroupDependencyMetadataInspection(GroupBatchClaimFenceVerdict verdict, bool? matches, string? snapshotSha256)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        Verdict = verdict;
        // An authentic expiry observation always wins over a computed comparison.
        // Preserve the exact typed verdict; this class never retires the claim.
        if (verdict is GroupBatchClaimFenceVerdict.Expired) return;
        if (verdict is not GroupBatchClaimFenceVerdict.Current || matches is null
            || snapshotSha256 is not { Length: 64 } || snapshotSha256.AsSpan().ContainsAnyExcept("0123456789ABCDEF"))
            throw new InvalidOperationException("Group dependency metadata is unavailable.");
        Matches = matches; SnapshotSha256 = snapshotSha256;
    }

    internal GroupBatchClaimFenceVerdict Verdict { get; }
    internal bool? Matches { get; }
    internal string? SnapshotSha256 { get; }
    public override string ToString() => "Group dependency inspection (private metadata, no authority).";

    // Pure comparison/hash projections. Only the fixed reader paths validate
    // and authorize current SQL metadata before invoking these helpers.
    internal static (bool Matches, string SnapshotSha256) CompareSource(GroupWorkDependencyManifest manifest,
        GroupBatchCoverageSnapshot coverage, GroupBatchSourceSnapshot[] snapshots)
    {
        var sources = snapshots.Select(snapshot => new GroupWorkDependencyManifest.Source(
            snapshot.Head.MessageId, snapshot.Head.Revision, GroupWorkDependencyManifest.SourceFingerprint(snapshot))).ToArray();
        var coverageSha256 = coverage.Fingerprint();
        var matches = coverageSha256 == manifest.CoverageSha256 && sources.SequenceEqual(manifest.Sources);
        var hash = GroupWorkDependencyManifest.Fingerprint("aioffice-group-current-source-metadata-v1", new
        {
            manifest.Scope,
            manifest.BatchId,
            manifest.OperationId,
            manifest.AllocatedThroughSequence,
            manifest.AuthoritySha256,
            CoverageSha256 = coverageSha256,
            Sources = sources
        });
        return (matches, hash);
    }

    internal static (bool Matches, string SnapshotSha256) CompareBrain(GroupWorkDependencyManifest manifest,
        GroupBrainSnapshot[] snapshots)
    {
        var dependencies = snapshots.Select(snapshot => new GroupWorkDependencyManifest.Brain(snapshot.Kind,
            snapshot.RecordId, snapshot.Revision.Revision, GroupWorkDependencyManifest.BrainFingerprint(snapshot))).ToArray();
        var matches = dependencies.SequenceEqual(manifest.Dependencies);
        var hash = GroupWorkDependencyManifest.Fingerprint("aioffice-group-current-brain-metadata-v1", new
        {
            manifest.Scope,
            manifest.BatchId,
            manifest.OperationId,
            manifest.AllocatedThroughSequence,
            manifest.AuthoritySha256,
            Dependencies = dependencies
        });
        return (matches, hash);
    }
}
