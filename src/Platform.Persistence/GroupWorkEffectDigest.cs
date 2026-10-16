using System.Security.Cryptography;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Versioned expectation helper only. A caller must stage the expectation in
// the same owned SQL unit as effects and reread the complete original graph
// under current authority. This helper does not own SQL or authorize completion.
internal static class GroupWorkEffectDigest
{
    internal const int Version = 1;
    internal const int DigestBytes = 32;
    internal static void Stage(GroupWorkCommitReceiptRecord receipt, GroupWorkEffectLedger originalGraph)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(originalGraph);
        RequireIdentity(receipt, originalGraph);
        if (receipt.DependencyManifestVersion != GroupWorkDependencyManifest.Version || receipt.EffectLedgerVersion != 0
            || receipt.ExpectedEffectSha256 is not null) throw Unavailable();
        var digest = Convert.FromHexString(originalGraph.Fingerprint);
        // Mutate only after every check; replay cannot replace an expectation.
        receipt.ExpectedEffectSha256 = digest; receipt.EffectLedgerVersion = Version;
    }

    internal static bool HasExpectation(GroupWorkCommitReceiptRecord receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.EffectLedgerVersion == 0 && receipt.ExpectedEffectSha256 is null) return false;
        if (receipt.EffectLedgerVersion == Version && receipt.DependencyManifestVersion == GroupWorkDependencyManifest.Version
            && receipt.ExpectedEffectSha256?.Length == DigestBytes) return true;
        throw Unavailable();
    }

    internal static void RequireUnchanged(GroupWorkCommitReceiptRecord receipt, GroupWorkEffectLedger observedGraph)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(observedGraph);
        RequireIdentity(receipt, observedGraph);
        if (!HasExpectation(receipt) || !CryptographicOperations.FixedTimeEquals(receipt.ExpectedEffectSha256!,
            Convert.FromHexString(observedGraph.Fingerprint))) throw Unavailable();
    }
    private static void RequireIdentity(GroupWorkCommitReceiptRecord receipt, GroupWorkEffectLedger graph)
    {
        if (!graph.MatchesReceipt(receipt)) throw Unavailable();
    }
    private static InvalidOperationException Unavailable() => new("Group original effect expectation is not available.");
}
