using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Detached immutable original evidence, not present authority or signed-prefix
// completion. The owned consumer must use the actual locked SQL reader and
// independently retain fresh authorization/latest-leaf/atomic-write fences.
internal sealed class GroupBatchHistoricalTerminalEvidence
{
    private readonly byte[] manifest;
    private GroupBatchHistoricalTerminalEvidence(GroupBatchHistoricalGraph graph, GroupBatchTerminalManifest value,
        DateTimeOffset committed)
    {
        OriginalGraph = graph; Scope = value.Scope; BatchId = value.BatchId; OperationId = value.OperationId;
        AfterSequence = value.AfterSequence; ThroughSequence = value.ThroughSequence; ManifestSha256 = value.Fingerprint;
        manifest = value.Write(); CommittedAtUtc = committed;
    }
    internal GroupScope Scope { get; }
    internal Guid BatchId { get; }
    internal Guid OperationId { get; }
    internal long AfterSequence { get; }
    internal long ThroughSequence { get; }
    internal string ManifestSha256 { get; }
    internal DateTimeOffset CommittedAtUtc { get; }
    internal GroupBatchHistoricalGraph OriginalGraph { get; }
    internal byte[] WriteManifest() => manifest.ToArray();
    public override string ToString() => "Group historical terminal evidence (private metadata).";

    internal static GroupBatchHistoricalTerminalEvidence Require(GroupBatchHistoricalGraph graph,
        GroupBatchClaimReceiptRecord original, GroupBatchTerminalReceiptRecord receipt, DateTimeOffset now)
    {
        GroupBatchHistoricalReceiptProof.Require(graph, original, receipt, receipt?.OperationId ?? Guid.Empty, now);
        return new(graph, GroupBatchTerminalManifest.Read(receipt!.Manifest), receipt.CommittedAtUtc);
    }
}
