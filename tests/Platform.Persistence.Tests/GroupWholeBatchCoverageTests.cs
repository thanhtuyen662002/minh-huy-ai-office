using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupWholeBatchCoverageTests
{
    [Fact]
    public void ActualEnumerationOfEveryInputIsUsedWithoutCountIndexerOrCopyTo()
    {
        var f = new Fixture();
        var result = GroupWholeBatchCoverage.Require(
            f.Allocation with { Revisions = new MisleadingList<GroupAllocatedRevision>(f.Revisions, -1) },
            new MisleadingList<GroupPendingRevisionMetadata>(f.CutoffHeads, -1),
            new MisleadingList<GroupWorkCommitReceiptRecord>(f.Receipts, -1),
            new MisleadingList<GroupWorkSourceDispositionRecord>(f.Selected, -1),
            new MisleadingList<GroupWorkRawDispositionRecord>(f.Raw, -1));
        Assert.Equal(500, result.RawCount); Assert.Equal(5, result.Manifests.Count);
    }

    [Theory]
    [InlineData("allocation", false)]
    [InlineData("allocation", true)]
    [InlineData("cutoff", false)]
    [InlineData("cutoff", true)]
    [InlineData("receipts", false)]
    [InlineData("receipts", true)]
    [InlineData("selected", false)]
    [InlineData("selected", true)]
    [InlineData("raw", false)]
    [InlineData("raw", true)]
    public void ReportedCompleteCountsCannotHideMissingActualContributions(string kind, bool partial)
    {
        var f = new Fixture(); var allocation = f.Allocation;
        IReadOnlyList<GroupPendingRevisionMetadata> cutoff = f.CutoffHeads;
        IReadOnlyList<GroupWorkCommitReceiptRecord> receipts = f.Receipts;
        IReadOnlyList<GroupWorkSourceDispositionRecord> selected = f.Selected;
        IReadOnlyList<GroupWorkRawDispositionRecord> raw = f.Raw;
        switch (kind)
        {
            case "allocation": allocation = allocation with { Revisions = new MisleadingList<GroupAllocatedRevision>(f.Revisions.Take(partial ? 499 : 0), 500) }; break;
            case "cutoff": cutoff = new MisleadingList<GroupPendingRevisionMetadata>(f.CutoffHeads.Take(partial ? 99 : 0), 100); break;
            case "receipts": receipts = new MisleadingList<GroupWorkCommitReceiptRecord>(f.Receipts.Take(partial ? 4 : 0), 5); break;
            case "selected": selected = new MisleadingList<GroupWorkSourceDispositionRecord>(f.Selected.Take(partial ? 99 : 0), 100); break;
            case "raw": raw = new MisleadingList<GroupWorkRawDispositionRecord>(f.Raw.Take(partial ? 499 : 0), 500); break;
            default: throw new InvalidOperationException();
        }
        Assert.Throws<InvalidOperationException>(() => GroupWholeBatchCoverage.Require(allocation, cutoff, receipts, selected, raw));
    }

    [Theory]
    [InlineData("allocation")]
    [InlineData("cutoff")]
    [InlineData("receipts")]
    [InlineData("selected")]
    [InlineData("raw")]
    public void ActualOversizedEnumerationRefusesAtTheFirstExcessElement(string kind)
    {
        var f = new Fixture(); var maximum = kind is "allocation" or "raw" ? 500 : 100; var enumerated = 0;
        IEnumerable<T> Excess<T>(T value)
        {
            for (var i = 0; i < maximum + 2; i++)
            {
                enumerated++; if (enumerated > maximum + 1) throw new Exception("Unbounded enumeration.");
                yield return value;
            }
        }
        var allocation = f.Allocation;
        IReadOnlyList<GroupPendingRevisionMetadata> cutoff = f.CutoffHeads;
        IReadOnlyList<GroupWorkCommitReceiptRecord> receipts = f.Receipts;
        IReadOnlyList<GroupWorkSourceDispositionRecord> selected = f.Selected;
        IReadOnlyList<GroupWorkRawDispositionRecord> raw = f.Raw;
        switch (kind)
        {
            case "allocation": allocation = allocation with { Revisions = new MisleadingList<GroupAllocatedRevision>(Excess(f.Revisions[0]), 1) }; break;
            case "cutoff": cutoff = new MisleadingList<GroupPendingRevisionMetadata>(Excess(f.CutoffHeads[0]), 1); break;
            case "receipts": receipts = new MisleadingList<GroupWorkCommitReceiptRecord>(Excess(f.Receipts[0]), 1); break;
            case "selected": selected = new MisleadingList<GroupWorkSourceDispositionRecord>(Excess(f.Selected[0]), 1); break;
            case "raw": raw = new MisleadingList<GroupWorkRawDispositionRecord>(Excess(f.Raw[0]), 1); break;
            default: throw new InvalidOperationException();
        }
        Assert.Throws<InvalidOperationException>(() => GroupWholeBatchCoverage.Require(allocation, cutoff, receipts, selected, raw));
        Assert.Equal(maximum + 1, enumerated);
    }

    [Fact]
    public void ReceiptBlobAndMutableRowFieldsAreCopiedBeforeOtherInputEnumeratorsRun()
    {
        var f = new Fixture();
        var selected = new MisleadingList<GroupWorkSourceDispositionRecord>(f.Selected, -1, () =>
        {
            foreach (var receipt in f.Receipts) { receipt.TenantId = Guid.Empty; receipt.DependencyManifest!.AsSpan().Clear(); }
        });
        var raw = new MisleadingList<GroupWorkRawDispositionRecord>(f.Raw, -1, () =>
        {
            foreach (var row in f.Selected) row.MessageId = Guid.Empty;
        });
        var result = GroupWholeBatchCoverage.Require(f.Allocation, f.CutoffHeads, f.Receipts, selected, raw);
        Assert.Equal(500, result.RawCount);
        Assert.All(result.Manifests, x => Assert.Equal(f.Allocation.Scope, x.Scope));
    }

    [Fact]
    public void All500RawRowsAnd100HeadsRequireEveryOneOfFiveContributingChunks()
    {
        var f = new Fixture(); var result = f.Require();
        Assert.Equal(500, result.RawCount); Assert.Equal(5, result.Manifests.Count); Assert.Equal(0, result.NoteCount);
        Assert.Equal(100, result.Manifests.Sum(x => x.Sources.Count));
        Assert.All(result.Manifests, x => Assert.Equal(500, x.AllocatedThroughSequence));
        Assert.Equal(501, f.Allocation.ObservedCommittedThroughSequence); // Pending suffix is not completion input.
        var ordered = result.Manifests.Select(x => x.OperationId).ToArray();
        f.Receipts.Reverse(); f.Selected.Reverse(); f.Raw.Reverse();
        Assert.Equal(ordered, f.Require().Manifests.Select(x => x.OperationId));
        var original = result.Manifests[0].Sources[0].SnapshotSha256;
        foreach (var receipt in f.Receipts) receipt.DependencyManifest!.AsSpan().Fill(0);
        Assert.Equal(original, result.Manifests[0].Sources[0].SnapshotSha256);
    }

    [Fact]
    public void EveryChunkHasItsOwn40NoteBoundAndLargerWholeResultsRemainIntact()
    {
        var f = new Fixture();
        foreach (var receipt in f.Receipts.Take(2)) { receipt.NoteCount = 20; receipt.Outcome = GroupWorkCommitOutcome.Notes; }
        Assert.Equal(40, f.Require().NoteCount);
        f.Receipts[2].NoteCount = 1; f.Receipts[2].Outcome = GroupWorkCommitOutcome.Attention;
        Assert.Equal(41, f.Require().NoteCount);
        f.Receipts[2].NoteCount = 41;
        Assert.Throws<InvalidOperationException>(() => f.Require());
    }

    [Theory]
    [InlineData("partial-raw")]
    [InlineData("empty-raw")]
    [InlineData("duplicate-raw")]
    [InlineData("raw-scope")]
    [InlineData("raw-batch")]
    [InlineData("raw-message")]
    [InlineData("raw-revision")]
    [InlineData("raw-head")]
    [InlineData("raw-operation")]
    [InlineData("raw-outcome")]
    [InlineData("raw-relation")]
    [InlineData("missing-chunk")]
    [InlineData("duplicate-chunk")]
    [InlineData("chunk-scope")]
    [InlineData("chunk-batch")]
    [InlineData("chunk-operation")]
    [InlineData("chunk-count")]
    [InlineData("chunk-time")]
    [InlineData("legacy-manifest")]
    [InlineData("missing-manifest")]
    [InlineData("manifest-operation")]
    [InlineData("manifest-batch")]
    [InlineData("manifest-scope")]
    [InlineData("manifest-cutoff")]
    [InlineData("manifest-head")]
    [InlineData("manifest-selection")]
    [InlineData("missing-head")]
    [InlineData("duplicate-head")]
    [InlineData("head-scope")]
    [InlineData("head-operation")]
    [InlineData("head-revision")]
    [InlineData("head-outcome")]
    [InlineData("allocation-duplicate")]
    [InlineData("allocation-gap")]
    [InlineData("allocation-foreign")]
    [InlineData("allocation-history")]
    [InlineData("allocation-version")]
    [InlineData("allocation-sha")]
    [InlineData("cutoff-missing")]
    [InlineData("cutoff-duplicate")]
    [InlineData("cutoff-null")]
    [InlineData("cutoff-scope")]
    [InlineData("cutoff-extra")]
    [InlineData("cutoff-revision")]
    [InlineData("cutoff-sequence")]
    [InlineData("cutoff-time")]
    [InlineData("cutoff-sha")]
    [InlineData("cutoff-kind")]
    [InlineData("cutoff-row-mismatch")]
    public void PartialForeignOrConflictingContributionsCannotQualifyWholeCoverage(string fault)
    {
        var f = new Fixture();
        switch (fault)
        {
            case "partial-raw": f.Raw.RemoveAt(0); break;
            case "empty-raw": f.Raw.Clear(); break;
            case "duplicate-raw": f.Raw[1] = f.Raw[0]; break;
            case "raw-scope": f.Raw[0].TenantId = Guid.NewGuid(); break;
            case "raw-batch": f.Raw[0].BatchId = Guid.NewGuid(); break;
            case "raw-message": f.Raw[0].MessageId = Guid.NewGuid(); break;
            case "raw-revision": f.Raw[0].RawRevision++; break;
            case "raw-head": f.Raw[0].SelectedMessageRevision--; break;
            case "raw-operation": f.Raw[0].OperationId = Guid.NewGuid(); break;
            case "raw-outcome": f.Raw[0].Outcome = GroupWorkSourceOutcome.Work; break;
            case "raw-relation": f.Raw[0].Relation = GroupWorkRawRelation.SelectedHead; break;
            case "missing-chunk": f.Receipts.RemoveAt(0); break;
            case "duplicate-chunk": f.Receipts[1] = f.Receipts[0]; break;
            case "chunk-scope": f.Receipts[0].CompanyId = Guid.NewGuid(); break;
            case "chunk-batch": f.Receipts[0].BatchId = Guid.NewGuid(); break;
            case "chunk-operation": f.Receipts[0].OperationId = Guid.NewGuid(); break;
            case "chunk-count": f.Receipts[0].SelectedMessageCount--; break;
            case "chunk-time": f.Receipts[0].CommittedAtUtc = f.Allocation.AllocatedAtUtc.AddTicks(-1); break;
            case "legacy-manifest": f.Receipts[0].DependencyManifestVersion = 0; f.Receipts[0].DependencyManifest = null; break;
            case "missing-manifest": f.Receipts[0].DependencyManifest = null; break;
            case "manifest-operation": f.Receipts[0].DependencyManifest![72] ^= 1; break;
            case "manifest-batch": f.Receipts[0].DependencyManifest![56] ^= 1; break;
            case "manifest-scope": f.Receipts[0].DependencyManifest![8] ^= 1; break;
            case "manifest-cutoff": f.Receipts[0].DependencyManifest![88] ^= 1; break;
            case "manifest-head": f.Receipts[0].DependencyManifest![178] ^= 1; break;
            case "manifest-selection": f.Receipts[0].DependencyManifest![162] ^= 1; break;
            case "missing-head": f.Selected.RemoveAt(0); break;
            case "duplicate-head": f.Selected[1] = f.Selected[0]; break;
            case "head-scope": f.Selected[0].BindingId = Guid.NewGuid(); break;
            case "head-operation": f.Selected[0].OperationId = Guid.NewGuid(); break;
            case "head-revision": f.Selected[0].MessageRevision--; break;
            case "head-outcome": f.Selected[0].Outcome = (GroupWorkSourceOutcome)99; break;
            case "allocation-duplicate": f.Revisions[1] = f.Revisions[0]; break;
            case "allocation-gap": f.Revisions[0] = f.Revisions[0] with { Metadata = f.Revisions[0].Metadata with { CommittedSequence = 2 } }; break;
            case "allocation-foreign": f.Revisions[0] = f.Revisions[0] with { Metadata = f.Revisions[0].Metadata with { Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) } }; break;
            case "allocation-history": f.Revisions[0] = f.Revisions[0] with { Metadata = f.Revisions[0].Metadata with { IsHistoricalBackfill = true } }; break;
            case "allocation-version": f.Revisions[0] = f.Revisions[0] with { SourceVersion = 0 }; break;
            case "allocation-sha": f.Revisions[0] = f.Revisions[0] with { Metadata = f.Revisions[0].Metadata with { ContentSha256 = "invalid" } }; break;
            case "cutoff-missing": f.CutoffHeads.RemoveAt(0); break;
            case "cutoff-duplicate": f.CutoffHeads[1] = f.CutoffHeads[0]; break;
            case "cutoff-null": f.CutoffHeads[0] = null!; break;
            case "cutoff-scope": f.CutoffHeads[0] = f.CutoffHeads[0] with { Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) }; break;
            case "cutoff-extra": f.CutoffHeads.Add(f.CutoffHeads[0] with { MessageId = Guid.NewGuid() }); break;
            case "cutoff-revision": f.CutoffHeads[0] = f.CutoffHeads[0] with { Revision = 6 }; break;
            case "cutoff-sequence": f.CutoffHeads[0] = f.CutoffHeads[0] with { CommittedSequence = 501 }; break;
            case "cutoff-time": f.CutoffHeads[0] = f.CutoffHeads[0] with { CommittedAtUtc = f.Allocation.AllocatedAtUtc.AddTicks(1) }; break;
            case "cutoff-sha": f.CutoffHeads[0] = f.CutoffHeads[0] with { ContentSha256 = new('a', 64) }; break;
            case "cutoff-kind": f.CutoffHeads[0] = f.CutoffHeads[0] with { Kind = (GroupSourceEventKind)99 }; break;
            case "cutoff-row-mismatch": f.CutoffHeads[0] = f.CutoffHeads[0] with { ContentSha256 = new('B', 64) }; break;
            default: throw new InvalidOperationException();
        }
        var error = Assert.Throws<InvalidOperationException>(() => f.Require());
        Assert.Null(error.InnerException);
    }

    internal sealed class Fixture
    {
        internal readonly List<GroupAllocatedRevision> Revisions = [];
        internal readonly List<GroupPendingRevisionMetadata> CutoffHeads = [];
        internal readonly List<GroupWorkCommitReceiptRecord> Receipts = [];
        internal readonly List<GroupWorkSourceDispositionRecord> Selected = [];
        internal readonly List<GroupWorkRawDispositionRecord> Raw = [];
        internal readonly GroupBatchAllocationReceipt Allocation;
        internal Fixture()
        {
            var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var batch = Guid.NewGuid(); var service = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow; var messages = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).Order().ToArray();
            for (var index = 0; index < 500; index++) Revisions.Add(new(new(scope, messages[index % 100], index / 100 + 1,
                index + 1, new('A', 64), GroupSourceEventKind.NewText, now.AddTicks(-1), false), 1, 0));
            CutoffHeads.AddRange(Revisions.Skip(400).Select(x => x.Metadata));
            Allocation = new(scope, batch, Guid.NewGuid(), 0, 500, 501, false, 1, 0, 1, service, 1, 1, now, Revisions, false);
            for (var chunk = 0; chunk < 5; chunk++)
            {
                var operation = Guid.NewGuid(); var own = messages.Skip(chunk * 20).Take(20).ToArray();
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
                writer.Write("AIOGDEP1"u8);
                foreach (var id in new[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId, batch, operation }) writer.Write(id.ToByteArray());
                writer.Write(500L); writer.Write(Enumerable.Repeat((byte)1, 64).ToArray()); writer.Write((byte)20); writer.Write((byte)0);
                foreach (var message in own) { writer.Write(message.ToByteArray()); writer.Write(5L); writer.Write(Enumerable.Repeat((byte)1, 32).ToArray()); }
                writer.Flush();
                Receipts.Add(new()
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    BatchId = batch,
                    OperationId = operation,
                    SelectedMessageCount = 20,
                    NoteCount = 0,
                    Outcome = GroupWorkCommitOutcome.NoWork,
                    ServiceId = service,
                    ClaimEpoch = 1,
                    CredentialEpoch = 1,
                    GrantVersion = 1,
                    SourceVersion = 1,
                    DeletionGeneration = 0,
                    AccountVersion = 1,
                    CommittedAtUtc = now,
                    DependencyManifestVersion = 1,
                    DependencyManifest = stream.ToArray()
                });
                Selected.AddRange(own.Select(message => new GroupWorkSourceDispositionRecord
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    BatchId = batch,
                    MessageId = message,
                    MessageRevision = 5,
                    OperationId = operation,
                    Outcome = GroupWorkSourceOutcome.NoWork
                }));
            }
            foreach (var revision in Revisions)
            {
                var value = revision.Metadata; var head = Selected.Single(x => x.MessageId == value.MessageId);
                Raw.Add(new()
                {
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    BindingId = scope.SourceBindingId,
                    BatchId = batch,
                    CommittedSequence = value.CommittedSequence,
                    MessageId = value.MessageId,
                    RawRevision = value.Revision,
                    SelectedMessageRevision = 5,
                    OperationId = head.OperationId,
                    Outcome = head.Outcome,
                    Relation = value.Revision == 5 ? GroupWorkRawRelation.SelectedHead : GroupWorkRawRelation.SupersededBySelectedHead
                });
            }
        }
        internal GroupWholeBatchCoverage Require() => GroupWholeBatchCoverage.Require(Allocation, CutoffHeads, Receipts, Selected, Raw);
    }

    private sealed class MisleadingList<T>(IEnumerable<T> actual, int reported, Action? before = null)
        : IReadOnlyList<T>, ICollection<T>
    {
        public int Count => reported < 0 ? throw new Exception("Count accessed.") : reported;
        public T this[int index] => throw new Exception("Indexer accessed.");
        public bool IsReadOnly => true;
        public IEnumerator<T> GetEnumerator() { before?.Invoke(); return actual.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void CopyTo(T[] array, int arrayIndex) => throw new Exception("CopyTo accessed.");
        public void Add(T item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Remove(T item) => throw new NotSupportedException();
        public bool Contains(T item) => throw new NotSupportedException();
    }
}
