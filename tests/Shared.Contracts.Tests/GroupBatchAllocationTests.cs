using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class GroupBatchAllocationTests
{
    private static readonly GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly DateTimeOffset Time = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static GroupPendingRevisionMetadata Row(long sequence, Guid? id = null, bool historical = false) =>
        new(Scope, id ?? Guid.NewGuid(), sequence, sequence, new string('A', 64), GroupSourceEventKind.Edit,
            Time.AddSeconds(sequence % 2000), historical);

    [Fact]
    public void One_message_with_501_revisions_allocates_500_and_preserves_the_unallocated_sentinel()
    {
        var id = Guid.NewGuid();
        var candidates = Enumerable.Range(1, 501).Select(sequence => Row(sequence, id)).ToArray();
        var plan = GroupBatchAllocationPrefix.Select(Scope, 0, 700, candidates);
        Assert.Equal(500, plan.Rows.Count);
        Assert.Equal(500, plan.AllocatedThrough);
        Assert.Single(plan.Rows.Select(row => row.MessageId).Distinct());
        Assert.True(plan.HasUnallocatedSuffix);
        Assert.Equal(candidates[500], plan.FirstUnallocated);
        Assert.Equal(700, plan.ObservedCommittedThrough);
        Assert.False(plan.IsHistoricalBackfill);
    }

    [Fact]
    public void Stops_before_101st_distinct_id_but_keeps_all_previous_edits()
    {
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray();
        var candidates = Enumerable.Range(1, 202).Select(sequence => Row(sequence, ids[(sequence - 1) / 2])).ToList();
        var plan = GroupBatchAllocationPrefix.Select(Scope, 0, 202, candidates);
        Assert.Equal(200, plan.Rows.Count);
        Assert.Equal(100, plan.Rows.Select(row => row.MessageId).Distinct().Count());
        Assert.Equal(201, plan.FirstUnallocated!.CommittedSequence);
    }

    [Fact]
    public void Homogeneous_historical_boundary_is_preserved_without_turning_backfill_into_a_live_trigger()
    {
        var candidates = new[] { Row(1, historical: true), Row(2, historical: true), Row(3), Row(4) };
        var first = GroupBatchAllocationPrefix.Select(Scope, 0, 4, candidates);
        Assert.True(first.IsHistoricalBackfill);
        Assert.Equal(2, first.AllocatedThrough);
        Assert.Equal(candidates[2], first.FirstUnallocated);
        var next = GroupBatchAllocationPrefix.Select(Scope, 2, 4, candidates[2..]);
        Assert.False(next.IsHistoricalBackfill);
        Assert.Equal(4, next.AllocatedThrough);
        Assert.False(next.HasUnallocatedSuffix);
        Assert.Null(next.FirstUnallocated);
    }

    [Fact]
    public void Repeated_prefixes_cover_all_1200_revisions_exactly_once_with_small_bounded_reads()
    {
        var all = Enumerable.Range(1, 1200).Select(sequence => Row(sequence, historical: sequence <= 333)).ToArray();
        var selected = new List<long>();
        long after = 0;
        while (after < all.Length)
        {
            var candidates = all.Where(row => row.CommittedSequence > after).Take(501).ToArray();
            var plan = GroupBatchAllocationPrefix.Select(Scope, after, all.Length, candidates);
            Assert.InRange(plan.Rows.Count, 1, 500);
            Assert.InRange(plan.Rows.Select(row => row.MessageId).Distinct().Count(), 1, 100);
            selected.AddRange(plan.Rows.Select(row => row.CommittedSequence));
            after = plan.AllocatedThrough;
        }
        Assert.Equal(all.Select(row => row.CommittedSequence), selected);
        Assert.Equal(selected.Count, selected.Distinct().Count());
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("unsorted")]
    [InlineData("missing-sentinel")]
    [InlineData("wrong-source")]
    [InlineData("duplicate-revision")]
    [InlineData("wrong-digest")]
    [InlineData("bad-kind")]
    [InlineData("non-utc")]
    public void Invalid_metadata_cannot_be_sorted_or_silently_skipped(string failure)
    {
        var rows = new[] { Row(1), Row(2), Row(3) };
        rows = failure switch
        {
            "gap" => new[] { rows[0], rows[2] },
            "unsorted" => new[] { rows[1], rows[0], rows[2] },
            "missing-sentinel" => rows[..2],
            "wrong-source" => new[] { rows[0] with { Scope = Scope with { CompanyId = Guid.NewGuid() } }, rows[1], rows[2] },
            "duplicate-revision" => new[] { rows[0], rows[1] with { MessageId = rows[0].MessageId, Revision = rows[0].Revision }, rows[2] },
            "wrong-digest" => new[] { rows[0] with { ContentSha256 = new string('a', 64) }, rows[1], rows[2] },
            "bad-kind" => new[] { rows[0] with { Kind = (GroupSourceEventKind)99 }, rows[1], rows[2] },
            "non-utc" => new[] { rows[0] with { CommittedAtUtc = Time.ToOffset(TimeSpan.FromHours(7)) }, rows[1], rows[2] },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidOperationException>(() => GroupBatchAllocationPrefix.Select(Scope, 0, 3, rows));
    }

    [Fact]
    public void Input_copies_cannot_mutate_the_selected_receipt_and_near_long_max_is_valid()
    {
        var rows = new[] { Row(long.MaxValue - 1), Row(long.MaxValue) };
        var plan = GroupBatchAllocationPrefix.Select(Scope, long.MaxValue - 2, long.MaxValue, rows);
        rows[0] = rows[0] with { MessageId = Guid.NewGuid() };
        Assert.NotEqual(rows[0], plan.Rows[0]);
        Assert.Equal(long.MaxValue, plan.AllocatedThrough);
        Assert.False(plan.HasUnallocatedSuffix);
    }
}
