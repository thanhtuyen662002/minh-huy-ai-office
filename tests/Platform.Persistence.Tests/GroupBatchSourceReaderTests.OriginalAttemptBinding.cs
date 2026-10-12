using System.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task OriginalAttemptBindingRetainsWholeOriginalPlanForEachRestoredChunkWithoutEffects()
    {
        using var f = new Fixture(); var (plan, chunks) = await OriginalAttemptInputAsync(f);
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        Assert.Equal(3, chunks.Count);
        foreach (var chunk in chunks)
        {
            var binding = GroupModelAttemptOriginalBinding.Create(plan, chunk);
            Assert.Equal(plan.Encode(), binding.Plan.Encode()); Assert.Same(chunk.Current.ModelContext, binding.Context);
            Assert.Equal(chunk.Original.OperationId, binding.OperationId);
            Assert.Equal(3, binding.Plan.Dependencies.Sources.Count);
            Assert.Single(binding.Context.Preparation.Context.Items);
            Assert.DoesNotContain("Tồn kho", binding.ToString());
        }
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("operation")]
    [InlineData("input")]
    [InlineData("members")]
    [InlineData("host-kind")]
    [InlineData("current-ordinal")]
    [InlineData("other-chunk")]
    [InlineData("rebound-brain")]
    [InlineData("preparation")]
    public async Task OriginalAttemptBindingRejectsSplicedOriginalOrRestoredCarrier(string fault)
    {
        using var f = new Fixture(); var (plan, chunks) = await OriginalAttemptInputAsync(f);
        var selected = chunks[0]; var original = selected.Original; var current = selected.Current; var brain = selected.Brain;
        switch (fault)
        {
            case "ordinal": original = new(2, original.OperationId, true, original.InputSha256, original.Members.ToArray()); break;
            case "operation": original = new(1, Guid.NewGuid(), true, original.InputSha256, original.Members.ToArray()); break;
            case "input": original = new(1, original.OperationId, true, new string('B', 64), original.Members.ToArray()); break;
            case "members": original = new(1, original.OperationId, true, original.InputSha256, chunks[1].Original.Members.ToArray()); break;
            case "host-kind": original = new(1, original.OperationId, false, original.InputSha256, original.Members.ToArray()); break;
            case "current-ordinal": current = new(2, current.Preparation, current.ModelContext); break;
            case "other-chunk": current = new(1, chunks[1].Current.Preparation, chunks[1].Current.ModelContext); break;
            case "rebound-brain": brain = new(brain.Handle, brain.Snapshots.ToArray(), brain.Items.ToArray()); break;
            case "preparation":
                current = new(1, GroupBatchSourcePreparation.Create(current.Preparation.Context), current.ModelContext); break;
        }
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var error = Assert.Throws<InvalidOperationException>(() => GroupModelAttemptOriginalBinding.Create(plan, new(original, current, brain)));
        Assert.Equal("Group original model attempt binding is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
    }

    [Fact]
    public async Task OriginalAttemptBindingRefusesModelFreeChunkCancellationAndNullCarriers()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: "password=private-binding-test-7124"));
        var handle = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(handle, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, null, 0, []);
        var chunk = Assert.Single(plan.RestoreChunks(preparation, brain, PlanProfile, []));
        Assert.Null(chunk.Current.ModelContext);
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptOriginalBinding.Create(plan, chunk));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupModelAttemptOriginalBinding.Create(plan, chunk, canceled.Token));
        Assert.Throws<ArgumentNullException>(() => GroupModelAttemptOriginalBinding.Create(null!, chunk));
        Assert.Throws<ArgumentNullException>(() => GroupModelAttemptOriginalBinding.Create(plan, null!));
    }

    [Fact]
    public async Task OriginalAttemptBindingPreservesPublishedHistoryOrderAndRefusesDifferentProfile()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var handle = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [source.MessageId]));
        var selected = await PlanGlossariesAsync(f, handle, source);
        var brain = await BrainReader(f).ReadAsync(handle, [], selected);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []);
        var chunk = Assert.Single(plan.RestoreChunks(preparation, brain, PlanProfile, []));
        var binding = GroupModelAttemptOriginalBinding.Create(plan, chunk);
        Assert.Equal(selected, binding.Plan.HistoryOrder.Select(x => x.RecordId));
        var otherProfile = GroupAutomaticInputPlan.Create(preparation, brain, new string('B', 64), "model", 64000, []);
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptOriginalBinding.Create(otherProfile, chunk));
        var reordered = await BrainReader(f).ReadAsync(handle, [], selected.Reverse().ToArray());
        var input = GroupAutomaticModelContext.Create(preparation, reordered, "model", 64000);
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptOriginalBinding.Create(plan,
            new(chunk.Original, new(1, preparation, input), reordered)));
    }

    [Fact]
    public async Task OriginalAttemptJournalAndLockedPlanReadRefuseInMemoryBeforePretendingPersistence()
    {
        using var f = new Fixture(); var (plan, chunks) = await OriginalAttemptInputAsync(f);
        var chunk = chunks[0]; var context = chunk.Current.ModelContext!;
        var journal = new GroupModelAttemptJournalStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var store = new GroupAutomaticInputPlanStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryStartOriginalAsync(plan, chunk, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequireOriginalUnchangedLockedAsync(
            context.Preparation.Context.Handle, plan, default));
        Assert.Empty(await f.Auth.Db.GroupModelAttemptStarts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupModelAttemptFailures.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupAutomaticInputPlans.AsNoTracking().ToArrayAsync());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task OriginalAttemptJournalCancellationAndLockedReadWithoutOwnedUnitNeverConnect()
    {
        using var f = new Fixture(); var (plan, chunks) = await OriginalAttemptInputAsync(f);
        var chunk = chunks[0]; var trap = new ModelFenceConnectionTrap(); var keys = f.Keys.Reads;
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false")
            .AddInterceptors(trap).Options);
        var sources = new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var brain = new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var journal = new GroupModelAttemptJournalStore(database, f.Worker, f.Auth.Clock, sources, brain);
        var store = new GroupAutomaticInputPlanStore(database, f.Worker, f.Auth.Clock, sources, brain);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.TryStartOriginalAsync(plan, chunk, Guid.NewGuid(), canceled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryStartOriginalAsync(plan, chunk, Guid.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequireOriginalUnchangedLockedAsync(chunk.Brain.Handle, plan, default));
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.False(database.ChangeTracker.HasChanges());
    }

    private static async Task<(GroupAutomaticInputPlan Plan, IReadOnlyList<GroupAutomaticInputPlan.RestoredChunk> Chunks)>
        OriginalAttemptInputAsync(Fixture f)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await f.CommitAsync(f.Payload($"original-attempt-{i}", $"original-attempt-event-{i}", "Tồn kho 😀 " + new string('x', 3000)))).MessageId);
        var handle = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, ids));
        var brain = await BrainReader(f).ReadAsync(handle, [], []);
        var first = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [ids[0]]));
        var budget = GroupAutomaticModelContext.Create(first, brain, "model", 64000).Manifest.UsedTokens;
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", budget, []);
        return (plan, plan.RestoreChunks(preparation, brain, PlanProfile, []));
    }
}
