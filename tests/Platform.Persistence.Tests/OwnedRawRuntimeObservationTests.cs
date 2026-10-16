extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedRawRuntimeObservationTests
{
    [Fact]
    public async Task AdditionalObserverKeepsSupersededAndSelectedHeadRowsAndExactPrivateDigest()
    {
        using var f = new Fixture(); await f.SeedAsync(2);
        var first = await GroupAutomaticRawRuntimeProof.RequireAsync(f.Db, f.Scope, f.Operation, 2, default);
        Assert.Equal(first, await GroupAutomaticRawRuntimeProof.RequireAsync(f.Db, f.Scope, f.Operation, 2, default));
        Assert.Matches("^[0-9A-F]{64}$", first);
        GroupAutomaticRawRuntimeProof.RequireDetached(f.Db);
        Assert.Equal(0, await GroupNoteRuntimeProof.TargetRowsAsync(f.Db, f.Scope, Guid.NewGuid(), default));
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
    [InlineData("extra")]
    [InlineData("allocation-count")]
    [InlineData("allocation-sequence")]
    [InlineData("selected-outcome")]
    [InlineData("selected-batch")]
    public async Task AdditionalObserverRequiresRawGraphAlongsideOriginalSevenTables(string change)
    {
        using var f = new Fixture(); await f.SeedAsync(2);
        await GroupAutomaticRawRuntimeProof.RequireAsync(f.Db, f.Scope, f.Operation, 2, default);
        var graph = await GroupNoteRuntimeProof.CommitGraphDigestAsync(f.Db, f.Scope, f.Operation, default);
        var row = f.Rows[0];
        f.Db.GroupWorkRawDispositions.RemoveRange(f.Rows); await f.Db.SaveChangesAsync();
        if (change == "selected-batch") { f.Db.Remove(f.Selected); await f.Db.SaveChangesAsync(); }
        switch (change)
        {
            case "tenant": row.TenantId = Guid.NewGuid(); break;
            case "company": row.CompanyId = Guid.NewGuid(); break;
            case "binding": row.BindingId = Guid.NewGuid(); break;
            case "batch": row.BatchId = Guid.NewGuid(); break;
            case "sequence": row.CommittedSequence = 9; break;
            case "message": row.MessageId = Guid.NewGuid(); break;
            case "raw-revision": row.RawRevision = 9; break;
            case "head-revision": row.SelectedMessageRevision = 9; break;
            case "operation": row.OperationId = Guid.NewGuid(); break;
            case "outcome": row.Outcome = GroupWorkSourceOutcome.Work; break;
            case "relation": row.Relation = GroupWorkRawRelation.SelectedHead; break;
            case "allocation-count": f.Allocation.RawRevisionCount = 1; break;
            case "allocation-sequence": f.Allocated[0].Revision = 9; break;
            case "selected-outcome": f.Selected.Outcome = GroupWorkSourceOutcome.Work; break;
            case "selected-batch": f.Selected.BatchId = Guid.NewGuid(); break;
        }
        if (change != "missing") f.Db.Add(row);
        f.Db.Add(f.Rows[1]);
        if (change == "extra") f.Db.Add(f.Row(3, 2));
        if (change == "allocation-count") f.Db.Update(f.Allocation);
        if (change == "allocation-sequence") f.Db.Update(f.Allocated[0]);
        if (change == "selected-outcome") f.Db.Update(f.Selected);
        if (change == "selected-batch") f.Db.Add(f.Selected);
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GroupAutomaticRawRuntimeProof.RequireAsync(f.Db, f.Scope, f.Operation, 2, default));
        if (change is not ("selected-outcome" or "selected-batch"))
            Assert.Equal(graph, await GroupNoteRuntimeProof.CommitGraphDigestAsync(f.Db, f.Scope, f.Operation, default));
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public async Task AdditionalObserverHasAnActualBounded501RowSentinel(int count, bool allowed)
    {
        using var f = new Fixture(); await f.SeedAsync(count);
        if (allowed) Assert.Matches("^[0-9A-F]{64}$", await GroupAutomaticRawRuntimeProof.RequireAsync(f.Db, f.Scope, f.Operation, 500, default));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => GroupAutomaticRawRuntimeProof.RequireAsync(f.Db, f.Scope, f.Operation, 500, default));
    }

    [Fact]
    public async Task PartialRawEffectsAndUnchangedTrackedRowsCannotStandInForRollbackAndDetach()
    {
        using var f = new Fixture();
        await GroupAutomaticRawRuntimeProof.RequireEmptyAsync(f.Db, f.Scope, default);
        GroupAutomaticRawRuntimeProof.RequireDetached(f.Db);
        await f.SeedAsync(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GroupAutomaticRawRuntimeProof.RequireEmptyAsync(f.Db, f.Scope, default));
        f.Db.Attach(f.Rows[0]); Assert.Equal(EntityState.Unchanged, f.Db.Entry(f.Rows[0]).State);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticRawRuntimeProof.RequireDetached(f.Db));
        f.Db.ChangeTracker.Clear(); GroupAutomaticRawRuntimeProof.RequireDetached(f.Db);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly PlatformDbContext Db = new(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        internal readonly GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        internal readonly Guid Operation = Guid.NewGuid(), Batch = Guid.NewGuid(), Message = Guid.NewGuid();
        internal GroupBatchAllocationRecord Allocation = null!;
        internal GroupBatchAllocatedRevisionRecord[] Allocated = [];
        internal GroupWorkSourceDispositionRecord Selected = null!;
        internal GroupWorkRawDispositionRecord[] Rows = [];
        internal async Task SeedAsync(int count)
        {
            Allocation = new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                Id = Batch,
                RawRevisionCount = count,
                AfterSequence = 0,
                AllocatedThroughSequence = count
            };
            Selected = new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                BatchId = Batch,
                OperationId = Operation,
                MessageId = Message,
                MessageRevision = count,
                Outcome = GroupWorkSourceOutcome.NoWork
            };
            Allocated = Enumerable.Range(1, count).Select(index => new GroupBatchAllocatedRevisionRecord
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                BatchId = Batch,
                CommittedSequence = index,
                MessageId = Message,
                Revision = index
            }).ToArray();
            Rows = Enumerable.Range(1, count).Select(index => Row(index, count)).ToArray();
            Db.AddRange(Allocation, Selected, new GroupWorkCommitReceiptRecord
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                BatchId = Batch,
                OperationId = Operation,
                SelectedMessageCount = 1
            });
            Db.AddRange(Allocated); Db.AddRange(Rows); await Db.SaveChangesAsync(); Db.ChangeTracker.Clear();
        }
        internal GroupWorkRawDispositionRecord Row(int revision, int head) => new()
        {
            TenantId = Scope.TenantId,
            CompanyId = Scope.CompanyId,
            BindingId = Scope.SourceBindingId,
            BatchId = Batch,
            CommittedSequence = revision,
            MessageId = Message,
            RawRevision = revision,
            SelectedMessageRevision = head,
            OperationId = Operation,
            Outcome = GroupWorkSourceOutcome.NoWork,
            Relation = revision == head ? GroupWorkRawRelation.SelectedHead : GroupWorkRawRelation.SupersededBySelectedHead
        };
        public void Dispose() => Db.Dispose();
    }
}
