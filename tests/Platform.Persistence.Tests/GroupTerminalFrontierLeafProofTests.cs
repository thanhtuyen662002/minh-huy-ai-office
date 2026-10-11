using System.Security.Cryptography;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupTerminalFrontierLeafProofTests
{
    [Fact]
    public void EmptyGenesisAllowsOnlyAbsentOrExactScopedZeroCursorWithoutInventedTerminalPrefix()
    {
        var scope = Scope(); Assert.Null(GroupTerminalFrontierLeafProof.Require(scope, [], null, null, null));
        var zero = Cursor(scope); Assert.Null(GroupTerminalFrontierLeafProof.Require(scope, [], zero, null, null));
        foreach (var change in new Action<GroupTerminalFrontierStateRecord>[] { x => x.Version = 1, x => x.ThroughSequence = 4,
            x => x.LastTerminalBatchId = Guid.NewGuid(), x => x.TenantId = Guid.NewGuid(), x => x.CompanyId = Guid.NewGuid(),
            x => x.BindingId = Guid.NewGuid(), x => x.UpdatedAtUtc = x.UpdatedAtUtc.ToOffset(TimeSpan.FromHours(7)) })
        { zero = Cursor(scope); change(zero); Private(() => GroupTerminalFrontierLeafProof.Require(scope, [], zero, null, null)); }
        // A genuine later terminal batch ID alone cannot authenticate (0,4].
        zero = Cursor(scope); zero.ThroughSequence = 4; zero.Version = 1; zero.LastTerminalBatchId = Guid.NewGuid();
        Private(() => GroupTerminalFrontierLeafProof.Require(scope, [], zero, null, null));
    }

    [Fact]
    public void GenuineLatestRootAndSuccessorBindActualCursorAndRetainedPredecessorKeys()
    {
        var value = Value(); using var old = Key(); using var current = new GroupSourceKeyMaterial("rotated-key", new byte[32]);
        var first = GroupTerminalAdvanceIntegrity.Sign(value, old); var root = GroupTerminalAdvanceRowProof.Stage(first, null);
        var initial = GroupTerminalFrontierLeafProof.Require(value.Scope, [root], Cursor(first), old, null)!;
        Assert.Equal(first.Fingerprint, initial.Fingerprint);
        var next = GroupTerminalAdvanceIntegrity.Sign(Next(first, current.KeyId), current); var leaf = GroupTerminalAdvanceRowProof.Stage(next, first);
        var final = GroupTerminalFrontierLeafProof.Require(value.Scope, [leaf, root], Cursor(next), current, old)!;
        Assert.Equal(next.Fingerprint, final.Fingerprint);
        leaf.Envelope[0] ^= 1; root.Envelope[0] ^= 1; Assert.Equal(next.Write(), final.Write());
    }

    [Theory]
    [InlineData("stale-cursor")]
    [InlineData("version")]
    [InlineData("through")]
    [InlineData("batch")]
    [InlineData("utc")]
    [InlineData("absent-cursor")]
    [InlineData("absent-predecessor")]
    [InlineData("wrong-order")]
    [InlineData("missing-key")]
    [InlineData("missing-old-key")]
    [InlineData("forged-newer")]
    public void StaleRolledBackSkippedOrForgedNewerSnapshotsCannotAcceptAnAuthenticOldLeaf(string fault)
    {
        var value = Value(); using var key = Key(); var first = GroupTerminalAdvanceIntegrity.Sign(value, key);
        var next = GroupTerminalAdvanceIntegrity.Sign(Next(first, key.KeyId), key);
        var root = GroupTerminalAdvanceRowProof.Stage(first, null); var leaf = GroupTerminalAdvanceRowProof.Stage(next, first);
        GroupTerminalAdvanceRecord[] rows = [leaf, root]; GroupTerminalFrontierStateRecord? cursor = Cursor(next);
        GroupSourceKeyMaterial? latestKey = key, previousKey = key;
        switch (fault)
        {
            case "stale-cursor": cursor = Cursor(first); break;
            case "version": cursor.Version = 1; break;
            case "through": cursor.ThroughSequence = 2; break;
            case "batch": cursor.LastTerminalBatchId = first.Metadata.BatchId; break;
            case "utc": cursor.UpdatedAtUtc = cursor.UpdatedAtUtc.AddTicks(1); break;
            case "absent-cursor": cursor = null; break;
            case "absent-predecessor": rows = [leaf]; break;
            case "wrong-order": rows = [root, leaf]; break;
            case "missing-key": latestKey = null; break;
            case "missing-old-key": previousKey = null; break;
            case "forged-newer": leaf.Envelope[8] ^= 1; leaf.Fingerprint = SHA256.HashData(leaf.Envelope); cursor = Cursor(first); break;
        }
        Private(() => GroupTerminalFrontierLeafProof.Require(value.Scope, rows, cursor, latestKey, previousKey));
    }

    [Fact]
    public void EveryReliedUponPredecessorProjectionMustMatchItsAuthenticatedEnvelope()
    {
        var value = Value(); using var key = Key(); var first = GroupTerminalAdvanceIntegrity.Sign(value, key);
        var next = GroupTerminalAdvanceIntegrity.Sign(Next(first, key.KeyId), key);
        foreach (var change in new Action<GroupTerminalAdvanceRecord>[] { x => x.TenantId = Guid.NewGuid(), x => x.CompanyId = Guid.NewGuid(),
            x => x.BindingId = Guid.NewGuid(), x => x.Version++, x => x.ThroughSequence++, x => x.AdvancedAtUtc = x.AdvancedAtUtc.AddTicks(1),
            x => x.AdvancedAtUtc = x.AdvancedAtUtc.ToOffset(TimeSpan.FromHours(7)), x => x.EnvelopeVersion++, x => x.KeyId = "other",
            x => x.Fingerprint[0] ^= 1, x => x.Envelope[8] ^= 1, x => x.Envelope = new byte[1_000_000], x => x.Fingerprint = new byte[1_000_000] })
        {
            var root = GroupTerminalAdvanceRowProof.Stage(first, null); var leaf = GroupTerminalAdvanceRowProof.Stage(next, first); change(root);
            Private(() => GroupTerminalFrontierLeafProof.Require(value.Scope, [leaf, root], Cursor(next), key, key));
        }
    }

    [Fact]
    public void BoundsNullsUnexpectedRootInputsAndCancellationCannotBypassActualEnumeration()
    {
        var value = Value(); using var key = Key(); var first = GroupTerminalAdvanceIntegrity.Sign(value, key);
        var root = GroupTerminalAdvanceRowProof.Stage(first, null);
        Private(() => GroupTerminalFrontierLeafProof.Require(value.Scope, [root, root, root], Cursor(first), key, key));
        Private(() => GroupTerminalFrontierLeafProof.Require(value.Scope, [root, root], Cursor(first), key, key));
        Private(() => GroupTerminalFrontierLeafProof.Require(value.Scope, [null!], null, key, null));
        Private(() => GroupTerminalFrontierLeafProof.Require(value.Scope, null!, null, null, null));
        Private(() => GroupTerminalFrontierLeafProof.Require(value.Scope, [], null, key, null));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() => GroupTerminalFrontierLeafProof.Require(value.Scope, [root], Cursor(first), key, null, cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        IEnumerable<GroupTerminalAdvanceRecord> Interrupted()
        { yield return root; throw new InvalidOperationException("Private iterator failure must be masked."); }
        // The ordinary iterator limit and disposal contract applies; no Count,
        // indexer or CopyTo is used to replace actual rows.
        Private(() => GroupTerminalFrontierLeafProof.Require(value.Scope, Interrupted(), Cursor(first), key, null));
        using var during = new CancellationTokenSource(); var disposed = false;
        IEnumerable<GroupTerminalAdvanceRecord> Canceled()
        { try { yield return root; during.Cancel(); yield return root; } finally { disposed = true; } }
        var canceled = Assert.ThrowsAny<OperationCanceledException>(() => GroupTerminalFrontierLeafProof.Require(value.Scope, Canceled(), Cursor(first), key, null, during.Token));
        Assert.Equal(during.Token, canceled.CancellationToken); Assert.True(disposed);
    }

    private static void Private(Action action)
    { var error = Assert.Throws<InvalidOperationException>(action); Assert.Equal("Group terminal frontier leaf is not available.", error.Message); Assert.Null(error.InnerException); }
    private static GroupScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static GroupSourceKeyMaterial Key() => new("owned-key", Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    private static GroupTerminalAdvanceMetadata Value() => new(Scope(), Guid.NewGuid(), Guid.NewGuid(), 0, 1, 0, 2,
        new byte[32], SHA256.HashData([42]), new(2026, 10, 11, 6, 0, 0, TimeSpan.Zero), new(2026, 10, 11, 6, 1, 0, TimeSpan.Zero),
        Guid.NewGuid(), 1, 1, 1, 0, 1, SHA256.HashData([99]), "owned-key");
    private static GroupTerminalAdvanceMetadata Next(GroupTerminalAdvanceIntegrity root, string key) => root.Metadata with
    {
        BatchId = Guid.NewGuid(),
        TerminalOperationId = Guid.NewGuid(),
        PreviousVersion = 1,
        Version = 2,
        AfterSequence = 2,
        ThroughSequence = 4,
        PreviousFingerprint = Convert.FromHexString(root.Fingerprint),
        AdvancedAtUtc = root.Metadata.AdvancedAtUtc.AddMinutes(1),
        KeyId = key
    };
    private static GroupTerminalFrontierStateRecord Cursor(GroupScope scope) => new()
    { TenantId = scope.TenantId, CompanyId = scope.CompanyId, BindingId = scope.SourceBindingId, UpdatedAtUtc = DateTimeOffset.UnixEpoch };
    private static GroupTerminalFrontierStateRecord Cursor(GroupTerminalAdvanceIntegrity leaf)
    {
        var value = leaf.Metadata; var row = Cursor(value.Scope); row.Version = value.Version; row.ThroughSequence = value.ThroughSequence;
        row.LastTerminalBatchId = value.BatchId; row.UpdatedAtUtc = value.AdvancedAtUtc; return row;
    }
}
