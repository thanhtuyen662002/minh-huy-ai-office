using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task ContextChunksSplitAtExactBudgetKeepEverySourceAndEveryHistoryWithoutEffects()
    {
        using var f = new Fixture(); var committed = new List<GroupIngressCommittedReceipt>();
        for (var i = 0; i < 4; i++) committed.Add(await f.CommitAsync(f.Payload($"message-{i}", $"event-{i}", $"Nguồn {i} 😀 \uFEFF " + new string('x', 3500))));
        var ids = committed.Select(x => x.MessageId).ToArray();
        var claim = await f.ClaimAsync(); var original = await f.Reader.ReadAsync(claim, ids);
        var preparation = GroupBatchSourcePreparation.Create(original);
        var first = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [ids[0]]));
        var brain = await ModelBrainAsync(f, claim, committed[0], first);
        var budget = GroupAutomaticModelContext.Create(first, brain, "existing-model", 64000).Manifest.UsedTokens;
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var chunks = GroupAutomaticContextChunks.Create(preparation, brain, "existing-model", budget);
        Assert.Equal(4, chunks.Count);
        Assert.Equal(preparation.Receipts.OrderBy(x => x.MessageId), chunks.SelectMany(x => x.Preparation.Receipts).OrderBy(x => x.MessageId));
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i]; Assert.Equal(i + 1, chunk.Ordinal);
            Assert.Same(claim, chunk.Preparation.Context.Handle); Assert.Same(original.Coverage, chunk.Preparation.Context.Coverage);
            Assert.Equal(original.AllocatedThroughSequence, chunk.Preparation.Context.AllocatedThroughSequence);
            var context = Assert.IsType<GroupAutomaticModelContext>(chunk.ModelContext);
            var request = context.CreateGatewayRequest(Guid.NewGuid());
            using var document = JsonDocument.Parse(request.Input);
            Assert.Equal(original.Items.Single(x => x.MessageId == ids[i]).Text,
                Assert.Single(document.RootElement.GetProperty("current_sources").EnumerateArray()).GetProperty("text").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("same_group_history").GetArrayLength());
            Assert.Equal(Encoding.UTF8.GetByteCount(request.Input) + Encoding.UTF8.GetByteCount(request.ResponseSchema!), context.Manifest.UsedTokens);
            Assert.InRange(context.Manifest.UsedTokens, 1, budget); Assert.True(context.RequiresModelTokenizerQualification);
        }
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<GroupAutomaticContextChunk>>(chunks).Clear());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ContextChunksKeepQuarantineMediaAndRecallReceiptsWithoutModelText()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        ids.Add((await f.CommitAsync(f.Payload("secret", "secret-event", "password=PRIVATE_SECRET_VALUE_7241"))).MessageId);
        ids.Add((await f.CommitAsync(f.Payload("media", "media-event", "", GroupSourceEventKind.Media))).MessageId);
        ids.Add((await f.CommitAsync(f.Payload("recall", "recall-event", "", GroupSourceEventKind.Recall))).MessageId);
        var claim = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids));
        Assert.Empty(preparation.Candidates); var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var chunk = Assert.Single(GroupAutomaticContextChunks.Create(preparation, brain, null, 0));
        Assert.Null(chunk.ModelContext); Assert.Equal(preparation.Receipts.OrderBy(x => x.MessageId), chunk.Preparation.Receipts.OrderBy(x => x.MessageId));
        var plan = GroupAutomaticNotePlan.Create(chunk.Preparation); Assert.Equal(2, plan.HostNotes.Count);
        Assert.Empty(plan.AiNotes); Assert.Equal(3, plan.SourceDispositions.Count);
    }

    [Fact]
    public async Task ContextChunksRefuseWholePlanWhenOneSourceCannotFitAndDoNotReleasePrefix()
    {
        using var f = new Fixture(); var small = await f.CommitAsync();
        var large = await f.CommitAsync(f.Payload("large", "large-event", new string('x', 8000)));
        var claim = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [small.MessageId, large.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var first = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [small.MessageId]));
        var budget = GroupAutomaticModelContext.Create(first, brain, "existing-model", 64000).Manifest.UsedTokens;
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticContextChunks.Create(preparation, brain, "existing-model", budget));
        Assert.Equal("Group automatic context chunks are not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
    }

    [Fact]
    public async Task ContextChunksRefuseReboundHandleEvenWhenNoProviderCandidatesExist()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: " "));
        var claim = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var other = new GroupBatchClaimHandle(claim.Receipt, claim.Authority);
        var brain = await BrainReader(f).ReadAsync(other, [], []);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticContextChunks.Create(preparation, brain, null, 0));
    }

    [Fact]
    public async Task ContextChunksCancellationRefusesBeforeConstructingModelInput()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupAutomaticContextChunks.Create(preparation, brain, "existing-model", 64000, canceled.Token));
        Assert.Throws<ArgumentNullException>(() => GroupAutomaticContextChunks.Create(null!, brain, "existing-model", 64000));
        Assert.Throws<ArgumentNullException>(() => GroupAutomaticContextChunks.Create(preparation, null!, "existing-model", 64000));
    }

    [Fact]
    public async Task ContextChunksBudgetIncludesExactArrayCommaAndCombinesAtBoundary()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await f.CommitAsync(f.Payload($"message-{i}", $"event-{i}", "Tra cứu tồn kho 😀"))).MessageId);
        var claim = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var firstTwo = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids.Take(2).ToArray()));
        var budget = GroupAutomaticModelContext.Create(firstTwo, brain, "existing-model", 64000).Manifest.UsedTokens;
        var exact = GroupAutomaticContextChunks.Create(preparation, brain, "existing-model", budget);
        Assert.Equal(new[] { 2, 1 }, exact.Select(x => x.Preparation.Candidates.Count));
        Assert.Equal(budget, exact[0].ModelContext!.Manifest.UsedTokens);
        var below = GroupAutomaticContextChunks.Create(preparation, brain, "existing-model", budget - 1);
        Assert.Equal(new[] { 1, 1, 1 }, below.Select(x => x.Preparation.Candidates.Count));
        Assert.Equal(3, below.SelectMany(x => x.Preparation.Candidates).Select(x => x.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task ContextChunksMixedHostAndModelSourcesKeepAllDispositionsExactlyOnce()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        ids.Add((await f.CommitAsync(f.Payload("media-first", "media-event", "", GroupSourceEventKind.Media))).MessageId);
        for (var i = 0; i < 3; i++) ids.Add((await f.CommitAsync(f.Payload($"message-{i}", $"event-{i}", "Tra cứu tồn kho 😀"))).MessageId);
        ids.Add((await f.CommitAsync(f.Payload("secret-last", "secret-event", "password=PRIVATE_SECRET_VALUE_7241"))).MessageId);
        var claim = await f.ClaimAsync(); var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var single = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [ids[1]]));
        var budget = GroupAutomaticModelContext.Create(single, brain, "existing-model", 64000).Manifest.UsedTokens;
        var chunks = GroupAutomaticContextChunks.Create(preparation, brain, "existing-model", budget);
        Assert.Equal(new[] { 2, 1, 2 }, chunks.Select(x => x.Preparation.Receipts.Count));
        Assert.Equal(5, chunks.SelectMany(x => x.Preparation.Receipts).Select(x => x.MessageId).Distinct().Count());
        Assert.Equal(2, chunks.Sum(x => GroupHostAttentionPlan.Create(x.Preparation).Notes.Count));
        foreach (var chunk in chunks)
        {
            Assert.DoesNotContain("PRIVATE_SECRET_VALUE_7241", chunk.ModelContext!.CreateGatewayRequest(Guid.NewGuid()).Input);
            Assert.DoesNotContain("PRIVATE_SECRET_VALUE_7241", chunk.ToString());
            Assert.Single(chunk.Preparation.Candidates);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64001)]
    [InlineData(-1)]
    public async Task ContextChunksDoNotTreatInvalidBudgetAsSplittableOverflow(int budget)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticContextChunks.Create(preparation, brain, "existing-model", budget));
        Assert.Null(error.InnerException); Assert.Equal("Group automatic context chunks are not available.", error.Message);
    }
}
