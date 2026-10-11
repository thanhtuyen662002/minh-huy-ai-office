using System.Collections;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupContiguousTerminalFrontierTests
{
    private readonly GroupScope scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 5, 2)]
    public void FrontierAdvancesOnlyThroughEveryContiguousTerminalPrefix(int completed, long expected, int advanced)
    {
        var rows = Rows(); var terminals = rows.Take(completed).Select(Done).ToArray();
        var result = GroupContiguousTerminalFrontier.Require(scope, 0, 5, 6, false, rows, terminals);
        Assert.Equal(expected, result.ThroughSequence); Assert.Equal(advanced, result.AdvancedIntervals);
        Assert.Equal(expected < 5, result.HasScheduledBacklog); Assert.True(result.HasUnscheduledBacklog);
        Assert.False(result.NeedsContinuation); Assert.False(result.HasCoverageGap);
    }

    [Fact]
    public void LaterCompletedBatchCannotSkipEarlierPendingBatchAndPermutedInputsStayDeterministic()
    {
        var rows = Rows(); var later = Done(rows[1]);
        var pending = GroupContiguousTerminalFrontier.Require(scope, 0, 5, 5, false, rows.Reverse(), [later]);
        Assert.Equal(0, pending.ThroughSequence); Assert.True(pending.HasScheduledBacklog);
        var closed = GroupContiguousTerminalFrontier.Require(scope, 0, 5, 5, false, rows.Reverse(), [later, Done(rows[0])]);
        Assert.Equal(5, closed.ThroughSequence); Assert.False(closed.HasScheduledBacklog);
    }

    [Fact]
    public void UnscheduledAndZeroMessageGapRemainVisibleWithoutBlockingAlreadyKnownContiguousWork()
    {
        var empty = GroupContiguousTerminalFrontier.Require(scope, 0, 0, 1, true, [], []);
        Assert.Equal(0, empty.ThroughSequence); Assert.True(empty.HasUnscheduledBacklog); Assert.True(empty.HasCoverageGap);
        var rows = Rows(); var known = GroupContiguousTerminalFrontier.Require(scope, 0, 5, 5, true, rows, rows.Select(Done));
        Assert.Equal(5, known.ThroughSequence); Assert.True(known.HasCoverageGap);
        Assert.False(known.HasScheduledBacklog); Assert.False(known.HasUnscheduledBacklog);
    }

    [Fact]
    public void InclusiveHundredWindowRequiresExplicitContinuationAndCannotClaimUnqueriedSuffix()
    {
        var rows = Enumerable.Range(0, 100).Select(i => new GroupContiguousTerminalFrontier.Allocation(scope, Guid.NewGuid(), i, i + 1, 1)).ToArray();
        var result = GroupContiguousTerminalFrontier.Require(scope, 0, 101, 101, false, rows, rows.Select(Done));
        Assert.Equal(100, result.ThroughSequence); Assert.True(result.HasScheduledBacklog); Assert.True(result.NeedsContinuation);
        var blocked = GroupContiguousTerminalFrontier.Require(scope, 0, 101, 101, false, rows, rows.Where((_, index) => index != 57).Select(Done));
        Assert.Equal(57, blocked.ThroughSequence); Assert.False(blocked.NeedsContinuation);
        var next = new GroupContiguousTerminalFrontier.Allocation(scope, Guid.NewGuid(), 100, 101, 1);
        var last = GroupContiguousTerminalFrontier.Require(scope, result.ThroughSequence, 101, 101, false, [next], [Done(next)]);
        Assert.Equal(101, last.ThroughSequence); Assert.False(last.HasScheduledBacklog); Assert.False(last.NeedsContinuation);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("empty-id")]
    [InlineData("duplicate-id")]
    [InlineData("missing-first")]
    [InlineData("overlap")]
    [InlineData("gap")]
    [InlineData("zero-raw")]
    [InlineData("large-raw")]
    [InlineData("count-mismatch")]
    [InlineData("short-window")]
    [InlineData("beyond-schedule")]
    [InlineData("null-row")]
    public void AllocationInputsCannotHideAHoleOverlapMissingPrefixOrTruncatedWindow(string fault)
    {
        var rows = Rows();
        switch (fault)
        {
            case "scope": rows[0] = rows[0] with { Scope = scope with { CompanyId = Guid.NewGuid() } }; break;
            case "empty-id": rows[0] = rows[0] with { BatchId = Guid.Empty }; break;
            case "duplicate-id": rows[1] = rows[1] with { BatchId = rows[0].BatchId }; break;
            case "missing-first": rows = rows[1..]; break;
            case "overlap": rows[1] = rows[1] with { AfterSequence = 1, RawCount = 4 }; break;
            case "gap": rows[1] = rows[1] with { AfterSequence = 3, RawCount = 2 }; break;
            case "zero-raw": rows[0] = rows[0] with { RawCount = 0 }; break;
            case "large-raw": rows[0] = rows[0] with { RawCount = 501, ThroughSequence = 501 }; break;
            case "count-mismatch": rows[0] = rows[0] with { RawCount = 1 }; break;
            case "short-window": rows = rows[..1]; break;
            case "beyond-schedule": rows[1] = rows[1] with { ThroughSequence = 6, RawCount = 4 }; break;
            case "null-row": rows[0] = null!; break;
        }
        Refused(() => GroupContiguousTerminalFrontier.Require(scope, 0, 5, 5, false, rows, []));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("unknown-batch")]
    [InlineData("empty-operation")]
    [InlineData("duplicate-batch")]
    [InlineData("duplicate-operation")]
    [InlineData("legacy")]
    [InlineData("future-version")]
    [InlineData("null-hash")]
    [InlineData("short-hash")]
    [InlineData("lowercase")]
    [InlineData("placeholder")]
    [InlineData("wrong-after")]
    [InlineData("wrong-through")]
    [InlineData("null-row")]
    public void TerminalInputsMustMatchExactScopedAllocationAndClosedVersionedCarrier(string fault)
    {
        var rows = Rows(); var terminal = rows.Select(Done).ToArray();
        switch (fault)
        {
            case "scope": terminal[0] = terminal[0] with { Scope = scope with { SourceBindingId = Guid.NewGuid() } }; break;
            case "unknown-batch": terminal[0] = terminal[0] with { BatchId = Guid.NewGuid() }; break;
            case "empty-operation": terminal[0] = terminal[0] with { OperationId = Guid.Empty }; break;
            case "duplicate-batch": terminal[1] = terminal[0]; break;
            case "duplicate-operation": terminal[1] = terminal[1] with { OperationId = terminal[0].OperationId }; break;
            case "legacy": terminal[0] = terminal[0] with { ManifestVersion = 0 }; break;
            case "future-version": terminal[0] = terminal[0] with { ManifestVersion = 2 }; break;
            case "null-hash": terminal[0] = terminal[0] with { ManifestSha256 = null! }; break;
            case "short-hash": terminal[0] = terminal[0] with { ManifestSha256 = new('A', 63) }; break;
            case "lowercase": terminal[0] = terminal[0] with { ManifestSha256 = new('a', 64) }; break;
            case "placeholder": terminal[0] = terminal[0] with { ManifestSha256 = new('0', 64) }; break;
            case "wrong-after": terminal[0] = terminal[0] with { AfterSequence = 1 }; break;
            case "wrong-through": terminal[0] = terminal[0] with { ThroughSequence = 1 }; break;
            case "null-row": terminal[0] = null!; break;
        }
        Refused(() => GroupContiguousTerminalFrontier.Require(scope, 0, 5, 5, false, rows, terminal));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(2, 1, 2)]
    [InlineData(0, 2, 1)]
    public void HighWaterMarkersCannotSubstituteForAContiguousTerminalCursor(long current, long scheduled, long committed) =>
        Refused(() => GroupContiguousTerminalFrontier.Require(scope, current, scheduled, committed, false, [], []));

    [Fact]
    public void UpperSequenceBoundaryDoesNotOverflowAndRawFiveHundredIsInclusive()
    {
        var row = new GroupContiguousTerminalFrontier.Allocation(scope, Guid.NewGuid(), long.MaxValue - 500, long.MaxValue, 500);
        var result = GroupContiguousTerminalFrontier.Require(scope, row.AfterSequence, long.MaxValue, long.MaxValue, false, [row], [Done(row)]);
        Assert.Equal(long.MaxValue, result.ThroughSequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualEnumerationRefusesHundredAndOneAndDisposesWithoutTrustingCount(bool terminalOverflow)
    {
        var rows = Enumerable.Range(0, 101).Select(i => new GroupContiguousTerminalFrontier.Allocation(scope, Guid.NewGuid(), i, i + 1, 1)).ToArray();
        var allocations = new DeceptiveList<GroupContiguousTerminalFrontier.Allocation>(rows);
        var terminals = new DeceptiveList<GroupContiguousTerminalFrontier.Terminal>(rows.Select(Done).ToArray());
        Refused(() => GroupContiguousTerminalFrontier.Require(scope, 0, 101, 101, false,
            terminalOverflow ? rows[..100] : allocations, terminalOverflow ? terminals : []));
        var observed = terminalOverflow ? (IObserved)terminals : allocations;
        Assert.Equal(101, observed.Read); Assert.True(observed.Disposed); Assert.Equal(0, observed.CountAccesses);
    }

    [Fact]
    public void EnumerationFailureIsFixedAndCancellationPrecedesEnumeration()
    {
        IEnumerable<GroupContiguousTerminalFrontier.Allocation> Fault() { yield return Rows()[0]; throw new Exception("PRIVATE_PROVIDER_DETAIL"); }
        Refused(() => GroupContiguousTerminalFrontier.Require(scope, 0, 5, 5, false, Fault(), []));
        Assert.Throws<OperationCanceledException>(() => GroupContiguousTerminalFrontier.Require(scope, 0, 5, 5, false,
            null!, null!, new CancellationToken(true)));
    }

    private GroupContiguousTerminalFrontier.Allocation[] Rows() => [new(scope, Guid.NewGuid(), 0, 2, 2), new(scope, Guid.NewGuid(), 2, 5, 3)];
    private static GroupContiguousTerminalFrontier.Terminal Done(GroupContiguousTerminalFrontier.Allocation row) =>
        new(row.Scope, row.BatchId, Guid.NewGuid(), 1, new('A', 64), row.AfterSequence, row.ThroughSequence);
    private static void Refused(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("Contiguous group terminal frontier is not available.", error.Message); Assert.Null(error.InnerException);
    }
    private interface IObserved { int Read { get; } bool Disposed { get; } int CountAccesses { get; } }
    private sealed class DeceptiveList<T>(T[] rows) : IReadOnlyList<T>, IObserved
    {
        public int Read { get; private set; }
        public bool Disposed { get; private set; }
        public int CountAccesses { get; private set; }
        public int Count { get { CountAccesses++; return 0; } }
        public T this[int index] => throw new Exception("PRIVATE_INDEXER");
        public IEnumerator<T> GetEnumerator()
        {
            try { foreach (var row in rows) { Read++; yield return row; } }
            finally { Disposed = true; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
