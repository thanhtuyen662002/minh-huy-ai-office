using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    public async Task RawAccountingCoversCompleteMaximumAndRetainsTheUnallocatedSuffix(int count)
    {
        using var f = new Fixture(); GroupIngressCommittedReceipt? source = null;
        for (var index = 0; index < count; index++) source = await f.CommitAsync(f.Payload(eventId: "raw-" + index,
            kind: index == 0 ? GroupSourceEventKind.NewText : GroupSourceEventKind.Edit));
        var allocation = await f.AllocateAsync(); var claim = await f.ClaimAllocatedAsync(allocation.BatchId);
        var context = await f.Reader.ReadAsync(claim, [source!.MessageId]);
        var outcome = count == 500 ? GroupWorkSourceOutcome.NoWork : GroupWorkSourceOutcome.ChangedAfterCutoff;
        var rows = GroupWorkRawAccounting.Build(context, allocation,
            context.Items.ToDictionary(x => x.MessageId, x => (x.Revision, outcome)), Guid.NewGuid());
        Assert.Equal(500, rows.Length); Assert.Equal(Enumerable.Range(1, 500).Select(x => (long)x), rows.Select(x => x.CommittedSequence));
        Assert.All(rows, row => Assert.Equal(500, row.SelectedMessageRevision));
        Assert.Equal(499, rows.Count(x => x.Relation == GroupWorkRawRelation.SupersededBySelectedHead));
        Assert.Single(rows, x => x.Relation == GroupWorkRawRelation.SelectedHead);
        var state = await f.Auth.Db.GroupSourceStates.SingleAsync();
        Assert.Equal(count, state.CommittedSequence); Assert.Equal(500, state.ScheduledThroughSequence);
        Assert.Empty(await f.Auth.Db.GroupWorkRawDispositions.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task RawAccountingRetainsSupersededRevisionAndLeavesUnselectedMessageUnaccounted()
    {
        using var f = new Fixture(); var first = await f.CommitAsync();
        var edited = await f.CommitAsync(f.Payload(eventId: "edited", text: "current", kind: GroupSourceEventKind.Edit));
        var other = await f.CommitAsync(f.Payload(messageId: "other", eventId: "other"));
        var allocation = await f.AllocateAsync(); var claim = await f.ClaimAllocatedAsync(allocation.BatchId);
        var context = await f.Reader.ReadAsync(claim, [first.MessageId]);
        var operation = Guid.NewGuid();
        var rows = GroupWorkRawAccounting.Build(context, allocation,
            context.Items.ToDictionary(x => x.MessageId, x => (x.Revision, GroupWorkSourceOutcome.NoWork)), operation);
        Assert.Equal(new long[] { 1, 2 }, rows.Select(x => x.CommittedSequence));
        Assert.Equal(new long[] { 1, 2 }, rows.Select(x => x.RawRevision));
        Assert.All(rows, row =>
        {
            Assert.Equal(first.MessageId, row.MessageId); Assert.Equal(edited.Revision, row.SelectedMessageRevision);
            Assert.Equal(operation, row.OperationId); Assert.Equal(GroupWorkSourceOutcome.NoWork, row.Outcome);
            Assert.Equal(context.BatchId, row.BatchId); Assert.Equal(context.Scope.TenantId, row.TenantId);
            Assert.Equal(context.Scope.CompanyId, row.CompanyId); Assert.Equal(context.Scope.SourceBindingId, row.BindingId);
        });
        Assert.Equal(GroupWorkRawRelation.SupersededBySelectedHead, rows[0].Relation);
        Assert.Equal(GroupWorkRawRelation.SelectedHead, rows[1].Relation);
        Assert.DoesNotContain(rows, row => row.MessageId == other.MessageId);
        Assert.Equal(3, allocation.Revisions.Count);
        Assert.Empty(await f.Auth.Db.GroupWorkRawDispositions.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("scope")]
    [InlineData("cutoff")]
    [InlineData("range")]
    [InlineData("sequence")]
    [InlineData("raw-scope")]
    [InlineData("raw-message")]
    [InlineData("raw-revision")]
    [InlineData("selected-revision")]
    [InlineData("selected-outcome")]
    [InlineData("operation")]
    public async Task RawAccountingRefusesAlteredTrustedAllocationOrSelectionWithoutEffects(string change)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var allocation = await f.AllocateAsync();
        var claim = await f.ClaimAllocatedAsync(allocation.BatchId); var context = await f.Reader.ReadAsync(claim, [source.MessageId]);
        var selected = context.Items.ToDictionary(x => x.MessageId, x => (Revision: x.Revision, Outcome: GroupWorkSourceOutcome.NoWork));
        var operation = Guid.NewGuid(); var row = allocation.Revisions[0];
        switch (change)
        {
            case "batch": allocation = allocation with { BatchId = Guid.NewGuid() }; break;
            case "scope": allocation = allocation with { Scope = allocation.Scope with { CompanyId = Guid.NewGuid() } }; break;
            case "cutoff": allocation = allocation with { AllocatedThroughSequence = 2 }; break;
            case "range": allocation = allocation with { AfterSequence = 1 }; break;
            case "sequence": row = row with { Metadata = row.Metadata with { CommittedSequence = 2 } }; break;
            case "raw-scope": row = row with { Metadata = row.Metadata with { Scope = row.Metadata.Scope with { SourceBindingId = Guid.NewGuid() } } }; break;
            case "raw-message": row = row with { Metadata = row.Metadata with { MessageId = Guid.NewGuid() } }; break;
            case "raw-revision": row = row with { Metadata = row.Metadata with { Revision = 0 } }; break;
            case "selected-revision": selected[source.MessageId] = (2, GroupWorkSourceOutcome.NoWork); break;
            case "selected-outcome": selected[source.MessageId] = (1, (GroupWorkSourceOutcome)99); break;
            case "operation": operation = Guid.Empty; break;
        }
        allocation = allocation with { Revisions = Array.AsReadOnly(new[] { row }) };
        var error = Assert.Throws<InvalidOperationException>(() => GroupWorkRawAccounting.Build(context, allocation, selected, operation));
        Assert.Equal("Group raw source accounting is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Empty(await f.Auth.Db.GroupWorkRawDispositions.ToArrayAsync()); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("batch")]
    [InlineData("sequence")]
    [InlineData("message")]
    [InlineData("raw-revision")]
    [InlineData("head-revision")]
    [InlineData("operation")]
    [InlineData("outcome")]
    [InlineData("relation")]
    [InlineData("missing")]
    public async Task RawReplayRefusesEveryAlteredOriginalFieldOrMissingRow(string change)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var allocation = await f.AllocateAsync();
        var claim = await f.ClaimAllocatedAsync(allocation.BatchId); var context = await f.Reader.ReadAsync(claim, [source.MessageId]);
        var operation = Guid.NewGuid(); var selected = context.Items.ToDictionary(x => x.MessageId, x => (x.Revision, GroupWorkSourceOutcome.NoWork));
        var expected = GroupWorkRawAccounting.Build(context, allocation, selected, operation);
        var row = GroupWorkRawAccounting.Build(context, allocation, selected, operation)[0];
        f.Auth.Db.Add(row); await f.Auth.Db.SaveChangesAsync(); f.Auth.Db.Entry(row).State = EntityState.Detached;
        await GroupWorkRawAccounting.RequireReplayAsync(f.Auth.Db, context.Scope, operation, expected, default);
        // Simulate corrupted/operator-modified metadata; SQL runtime UPDATE is denied.
        switch (change)
        {
            case "tenant": row.TenantId = Guid.NewGuid(); break;
            case "company": row.CompanyId = Guid.NewGuid(); break;
            case "binding": row.BindingId = Guid.NewGuid(); break;
            case "batch": row.BatchId = Guid.NewGuid(); break;
            case "sequence": row.CommittedSequence++; break;
            case "message": row.MessageId = Guid.NewGuid(); break;
            case "raw-revision": row.RawRevision++; break;
            case "head-revision": row.SelectedMessageRevision++; break;
            case "operation": row.OperationId = Guid.NewGuid(); break;
            case "outcome": row.Outcome = GroupWorkSourceOutcome.Work; break;
            case "relation": row.Relation = GroupWorkRawRelation.SupersededBySelectedHead; break;
        }
        f.Auth.Db.RemoveRange(await f.Auth.Db.GroupWorkRawDispositions.ToArrayAsync()); await f.Auth.Db.SaveChangesAsync();
        f.Auth.Db.ChangeTracker.Clear();
        if (change != "missing") { f.Auth.Db.Add(row); await f.Auth.Db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => GroupWorkRawAccounting.RequireReplayAsync(f.Auth.Db, context.Scope, operation, expected, default));
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }
}
