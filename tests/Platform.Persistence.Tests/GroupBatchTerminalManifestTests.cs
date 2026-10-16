using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupBatchTerminalManifestTests
{
    [Fact]
    public void CompleteFiveContributor500Raw100HeadInputHasCanonicalDetachedEncoding()
    {
        var f = new Fixture(); var terminal = f.Create(); var bytes = terminal.Write(); var original = bytes.ToArray();
        Assert.Equal(577, bytes.Length); Assert.Equal(f.Input.Allocation.Scope, terminal.Scope);
        Assert.Equal(500, terminal.RawCount); Assert.Equal(100, terminal.SelectedCount); Assert.Equal(0, terminal.NoteCount);
        Assert.Equal(5, terminal.Contributors.Count); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), terminal.Fingerprint);
        Assert.Equal(5, terminal.Contributors.Select(x => x.DependencySha256).Distinct().Count());
        var parsed = GroupBatchTerminalManifest.Read(bytes); bytes.AsSpan().Clear();
        Assert.Equal(original, parsed.Write()); var exported = parsed.Write(); exported.AsSpan().Clear();
        Assert.Equal(original, parsed.Write()); Assert.Equal(original, terminal.Write());
        Assert.DoesNotContain(terminal.Fingerprint, terminal.ToString());
        GroupBatchTerminalManifest.RequireUnchanged(f.Current, f.Operation, original);
    }

    [Fact]
    public void ContributorAndMetadataInputOrderingDoesNotChangeCanonicalBytes()
    {
        var f = new Fixture(); var before = f.Create().Write();
        f.Input.Receipts.Reverse(); f.Input.CutoffHeads.Reverse(); f.Input.Selected.Reverse(); f.Input.Raw.Reverse();
        var permuted = f.Current with { Coverage = f.Input.Require(), OriginalEffects = f.Effects.Reverse().ToArray() };
        Assert.Equal(before, GroupBatchTerminalManifest.Create(permuted, f.Operation).Write());
        var replay = GroupWholeBatchCoverage.Require(f.Input.Allocation with { WasAlreadyAllocated = true },
            f.Input.CutoffHeads, f.Input.Receipts, f.Input.Selected, f.Input.Raw);
        Assert.Equal(before, GroupBatchTerminalManifest.Create(permuted with { Coverage = replay }, f.Operation).Write());
    }

    [Fact]
    public void CallerMutationsCannotRewriteFrozenReceiptsOrOriginalEffects()
    {
        var f = new Fixture(); var before = f.Create().Write();
        foreach (var receipt in f.Input.Receipts)
        { receipt.TenantId = Guid.Empty; receipt.DependencyManifest!.AsSpan().Clear(); receipt.ExpectedEffectSha256!.AsSpan().Clear(); }
        f.Input.Revisions.Clear(); f.Input.CutoffHeads.Clear(); f.Input.Selected.Clear(); f.Input.Raw.Clear();
        Assert.Equal(before, f.Create().Write());
    }

    [Fact]
    public void EarlierFrozenAllocationAndExpectationArraysSurviveLaterInputEnumeratorMutations()
    {
        var f = new Fixture(); var before = f.Create().Write();
        var heads = new TrappedList<GroupPendingRevisionMetadata>(f.Input.CutoffHeads, () => f.Input.Revisions.Clear());
        var selected = new TrappedList<GroupWorkSourceDispositionRecord>(f.Input.Selected, () =>
        {
            foreach (var receipt in f.Input.Receipts)
            { receipt.ExpectedEffectSha256!.AsSpan().Clear(); receipt.DependencyManifest!.AsSpan().Clear(); }
        });
        var coverage = GroupWholeBatchCoverage.Require(f.Input.Allocation, heads, f.Input.Receipts, selected, f.Input.Raw);
        Assert.Equal(before, GroupBatchTerminalManifest.Create(f.Current with { Coverage = coverage }, f.Operation).Write());
    }

    [Fact]
    public void ActualEffectEnumerationUsesInclusiveBoundAndDisposesWithoutTrustingCollectionCount()
    {
        var f = new Fixture(); var valid = new TrappedList<GroupWorkEffectLedger>(f.Effects);
        Assert.Equal(f.Create().Write(), GroupBatchTerminalManifest.Create(f.Current with { OriginalEffects = valid }, f.Operation).Write());
        Assert.Equal(5, valid.Read); Assert.True(valid.Disposed);
        var oversized = new TrappedList<GroupWorkEffectLedger>(Enumerable.Repeat(f.Effects[0], 102));
        Refused(() => GroupBatchTerminalManifest.Create(f.Current with { OriginalEffects = oversized }, f.Operation));
        Assert.Equal(101, oversized.Read); Assert.True(oversized.Disposed);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("receipt")]
    [InlineData("expectation")]
    [InlineData("cutoff")]
    [InlineData("selected-and-raw")]
    public void WholeCoverageFingerprintRetainsEachOriginalMaterialCategory(string fault)
    {
        var f = new Fixture(); var before = f.Current.Coverage.Fingerprint;
        switch (fault)
        {
            case "manifest": f.Input.Receipts[0].DependencyManifest![130] ^= 1; break;
            case "receipt": f.Input.Receipts[0].GrantVersion++; break;
            case "expectation": f.Input.Receipts[0].ExpectedEffectSha256![0] ^= 1; break;
            case "cutoff":
                f.Input.CutoffHeads[0] = f.Input.CutoffHeads[0] with { Kind = GroupSourceEventKind.Edit };
                f.Input.Revisions[400] = f.Input.Revisions[400] with { Metadata = f.Input.CutoffHeads[0] }; break;
            case "selected-and-raw":
                f.Input.Selected[0].Outcome = GroupWorkSourceOutcome.ChangedAfterCutoff;
                foreach (var row in f.Input.Raw.Where(x => x.MessageId == f.Input.Selected[0].MessageId)) row.Outcome = GroupWorkSourceOutcome.ChangedAfterCutoff;
                break;
        }
        Assert.NotEqual(before, f.Input.Require().Fingerprint);
    }

    [Theory]
    [InlineData("observed-high-water")]
    [InlineData("source-version")]
    [InlineData("deletion-generation")]
    [InlineData("account-version")]
    [InlineData("service")]
    [InlineData("credential")]
    [InlineData("grant")]
    [InlineData("allocated-time")]
    [InlineData("raw-source-version")]
    [InlineData("raw-deletion-generation")]
    public void EveryOriginalAllocationAuthorityAndRawVersionRemainsBound(string fault)
    {
        var f = new Fixture(); var before = f.Create().Write(); var allocation = f.Input.Allocation;
        allocation = fault switch
        {
            "observed-high-water" => allocation with { ObservedCommittedThroughSequence = 502 },
            "source-version" => allocation with { SourceVersion = 2 },
            "deletion-generation" => allocation with { DeletionGeneration = 1 },
            "account-version" => allocation with { AccountVersion = 2 },
            "service" => allocation with { ServiceId = Guid.NewGuid() },
            "credential" => allocation with { CredentialEpoch = 2 },
            "grant" => allocation with { GrantVersion = 2 },
            "allocated-time" => allocation with { AllocatedAtUtc = allocation.AllocatedAtUtc.AddTicks(-1) },
            _ => allocation
        };
        if (fault == "raw-source-version") f.Input.Revisions[0] = f.Input.Revisions[0] with { SourceVersion = 2 };
        if (fault == "raw-deletion-generation") f.Input.Revisions[0] = f.Input.Revisions[0] with { DeletionGeneration = 1 };
        var changed = f.Current with
        {
            Coverage = GroupWholeBatchCoverage.Require(allocation, f.Input.CutoffHeads,
            f.Input.Receipts, f.Input.Selected, f.Input.Raw)
        };
        Assert.NotEqual(before, GroupBatchTerminalManifest.Create(changed, f.Operation).Write());
        Refused(() => GroupBatchTerminalManifest.RequireUnchanged(changed, f.Operation, before));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("null")]
    [InlineData("legacy")]
    [InlineData("changed-expectation")]
    [InlineData("changed-receipt")]
    public void TerminalEncodingRequiresEveryCopiedExpectedOriginalEffect(string fault)
    {
        var f = new Fixture(); var current = f.Current;
        switch (fault)
        {
            case "missing": current = current with { OriginalEffects = f.Effects[..4] }; break;
            case "duplicate": current = current with { OriginalEffects = [f.Effects[0], f.Effects[0], .. f.Effects[2..]] }; break;
            case "foreign": current = current with { OriginalEffects = new Fixture().Effects }; break;
            case "null": current = current with { OriginalEffects = null! }; break;
            case "legacy": f.Input.Receipts[0].EffectLedgerVersion = 0; f.Input.Receipts[0].ExpectedEffectSha256 = null; break;
            case "changed-expectation": f.Input.Receipts[0].ExpectedEffectSha256![0] ^= 1; break;
            case "changed-receipt": f.Input.Receipts[0].ServiceId = Guid.NewGuid(); break;
        }
        if (fault is "legacy" or "changed-expectation" or "changed-receipt") current = current with { Coverage = f.Input.Require() };
        Refused(() => GroupBatchTerminalManifest.Create(current, f.Operation));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("empty-scope")]
    [InlineData("empty-batch")]
    [InlineData("empty-allocation")]
    [InlineData("empty-operation")]
    [InlineData("negative-after")]
    [InlineData("bad-through")]
    [InlineData("committed-behind")]
    [InlineData("bad-ticks")]
    [InlineData("historical")]
    [InlineData("raw-count")]
    [InlineData("selected-count")]
    [InlineData("negative-notes")]
    [InlineData("large-notes")]
    [InlineData("zero-coverage")]
    [InlineData("zero-count")]
    [InlineData("wrong-count")]
    [InlineData("duplicate-contributor")]
    [InlineData("reordered-contributor")]
    [InlineData("zero-dependency")]
    [InlineData("zero-effect")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    public void ClosedCodecRefusesMalformedOrPartialMetadataBeforeReturning(string fault)
    {
        var bytes = new Fixture().Create().Write();
        switch (fault)
        {
            case "magic": bytes[0] ^= 1; break;
            case "version": bytes[7] = (byte)'2'; break;
            case "empty-scope": bytes.AsSpan(8, 16).Clear(); break;
            case "empty-batch": bytes.AsSpan(56, 16).Clear(); break;
            case "empty-allocation": bytes.AsSpan(72, 16).Clear(); break;
            case "empty-operation": bytes.AsSpan(88, 16).Clear(); break;
            case "negative-after": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(104), -1); break;
            case "bad-through": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(112), 0); break;
            case "committed-behind": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(120), 499); break;
            case "bad-ticks": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(128), long.MaxValue); break;
            case "historical": bytes[136] = 2; break;
            case "raw-count": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(137), 501); break;
            case "selected-count": bytes[139] = 101; break;
            case "negative-notes": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(140), -1); break;
            case "large-notes": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(140), 201); break;
            case "zero-coverage": bytes.AsSpan(144, 32).Clear(); break;
            case "zero-count": bytes[176] = 0; break;
            case "wrong-count": bytes[176] = 6; break;
            case "duplicate-contributor": bytes.AsSpan(177, 16).CopyTo(bytes.AsSpan(257, 16)); break;
            case "reordered-contributor":
                var first = bytes.AsSpan(177, 80).ToArray(); bytes.AsSpan(257, 80).CopyTo(bytes.AsSpan(177, 80)); first.CopyTo(bytes.AsSpan(257, 80)); break;
            case "zero-dependency": bytes.AsSpan(193, 32).Clear(); break;
            case "zero-effect": bytes.AsSpan(225, 32).Clear(); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing": bytes = [.. bytes, 1]; break;
        }
        Refused(() => GroupBatchTerminalManifest.Read(bytes));
    }

    [Fact]
    public void ShapeParsingDoesNotAuthenticateReplacedDigestScopeOrTerminalOperation()
    {
        var f = new Fixture(); var bytes = f.Create().Write(); bytes[193] ^= 1;
        _ = GroupBatchTerminalManifest.Read(bytes);
        Refused(() => GroupBatchTerminalManifest.RequireUnchanged(f.Current, f.Operation, bytes));
        Refused(() => GroupBatchTerminalManifest.RequireUnchanged(f.Current, Guid.NewGuid(), f.Create().Write()));
        Refused(() => GroupBatchTerminalManifest.Create(f.Current, Guid.Empty));
        Refused(() => GroupBatchTerminalManifest.Read(null));
        Refused(() => GroupBatchTerminalManifest.Read(new byte[GroupBatchTerminalManifest.MaximumBytes + 1]));
    }

    [Fact]
    public void InclusiveHundredContributorCarrierPreservesTheWhole4000NoteBound()
    {
        var bytes = new byte[GroupBatchTerminalManifest.MaximumBytes]; new Fixture().Create().Write().AsSpan(0, 177).CopyTo(bytes);
        bytes[176] = 100; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(140), 4000);
        for (var i = 0; i < 100; i++)
        {
            Guid.Parse((i + 1).ToString("X8", CultureInfo.InvariantCulture) + "-0000-0000-0000-000000000001").ToByteArray().CopyTo(bytes, 177 + i * 80);
            bytes.AsSpan(193 + i * 80, 32).Fill(1); bytes.AsSpan(225 + i * 80, 32).Fill(2);
        }
        var parsed = GroupBatchTerminalManifest.Read(bytes); Assert.Equal(100, parsed.Contributors.Count); Assert.Equal(4000, parsed.NoteCount);
        Assert.Equal(8177, parsed.Write().Length); // Shaped carrier only; no current SQL proof.
    }

    private static void Refused(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("Group terminal manifest is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("oversized")]
    [InlineData("lowercase")]
    [InlineData("nonhex")]
    [InlineData("unicode")]
    public void SourceSetScalarIsBoundedAndCanonicalBeforeLaterInputsOrFingerprintSerialization(string fault)
    {
        var f = new Fixture(); f.Input.Receipts[0].SourceSetSha256 = fault switch
        {
            "null" => null!,
            "empty" => "",
            "short" => new('A', 63),
            "long" => new('A', 65),
            "oversized" => new('A', 1_048_576),
            "lowercase" => new('a', 64),
            "nonhex" => new('G', 64),
            "unicode" => new('\uFF21', 64),
            _ => throw new InvalidOperationException()
        };
        var later = new TrappedList<GroupWorkSourceDispositionRecord>(f.Input.Selected,
            () => throw new Exception("PRIVATE_LATER_INPUT_WAS_ENUMERATED"));
        var error = Assert.Throws<InvalidOperationException>(() => GroupWholeBatchCoverage.Require(f.Input.Allocation,
            f.Input.CutoffHeads, f.Input.Receipts, later, f.Input.Raw));
        Assert.Equal("Whole group batch coverage is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, later.Read);
    }
    private sealed class TrappedList<T>(IEnumerable<T> input, Action? before = null) : IReadOnlyList<T>
    {
        internal int Read { get; private set; }
        internal bool Disposed { get; private set; }
        public int Count => throw new Exception("PRIVATE_COUNT");
        public T this[int index] => throw new Exception("PRIVATE_INDEXER");
        public IEnumerator<T> GetEnumerator()
        {
            before?.Invoke();
            try { foreach (var row in input) { Read++; yield return row; } }
            finally { Disposed = true; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Fixture
    {
        internal readonly GroupWholeBatchCoverageTests.Fixture Input = new();
        internal readonly GroupWorkEffectLedger[] Effects;
        internal readonly GroupWholeBatchDependencyVerdict.Current Current;
        internal readonly Guid Operation = Guid.NewGuid();
        internal Fixture()
        {
            var owner = Guid.NewGuid(); var claimOperation = Guid.NewGuid(); var claims = new List<GroupOriginalClaimProvenance>();
            var effects = new List<GroupWorkEffectLedger>();
            foreach (var work in Input.Receipts)
            {
                var own = Input.Selected.Where(x => x.OperationId == work.OperationId).OrderBy(x => x.MessageId).ToArray();
                work.SourceSetSha256 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\n",
                    own.Select(x => x.MessageId.ToString("D") + "/" + x.MessageRevision.ToString(CultureInfo.InvariantCulture))))));
                var claim = new GroupBatchClaimReceiptRecord
                {
                    TenantId = work.TenantId,
                    CompanyId = work.CompanyId,
                    BindingId = work.BindingId,
                    BatchId = work.BatchId,
                    Epoch = work.ClaimEpoch,
                    OwnerId = owner,
                    OperationId = claimOperation,
                    ServiceId = work.ServiceId,
                    CredentialEpoch = work.CredentialEpoch,
                    GrantVersion = work.GrantVersion,
                    SourceVersion = work.SourceVersion,
                    DeletionGeneration = work.DeletionGeneration,
                    AccountVersion = work.AccountVersion,
                    AuthoritySha256 = GroupWorkDependencyManifest.Read(work.DependencyManifest).AuthoritySha256,
                    RequestedLifetimeTicks = TimeSpan.FromSeconds(30).Ticks,
                    IssuedAtUtc = work.CommittedAtUtc.AddSeconds(-1),
                    ExpiresAtUtc = work.CommittedAtUtc.AddSeconds(29)
                };
                var graph = GroupWorkEffectLedger.Require(work, claim, false, own, [], [], [], [], []);
                GroupWorkEffectDigest.Stage(work, graph); effects.Add(graph); claims.Add(GroupOriginalClaimProvenance.Require(work, claim));
            }
            Effects = effects.ToArray(); Current = new(Input.Require(), claims.AsReadOnly(), Array.AsReadOnly(Effects));
        }
        internal GroupBatchTerminalManifest Create() => GroupBatchTerminalManifest.Create(Current, Operation);
    }
}
