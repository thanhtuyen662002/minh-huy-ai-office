using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static async Task<GroupRestoredOriginalBatch> RestoredProcessorBatchAsync(Fixture f, bool multiple = false, bool hostOnly = false)
    {
        if (multiple)
        {
            var (original, chunks) = await OriginalAttemptInputAsync(f); var handle = chunks[0].Brain.Handle;
            var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, original.Dependencies.Sources.Select(x => x.MessageId).ToArray()));
            return new(original, preparation, chunks);
        }
        var (prep, brain, selection) = await IssuedDisclosure(f, hostOnly ? "password=OWNED_HOST_ONLY_FIXTURE" : "Tra cứu tồn kho");
        var plan = GroupAutomaticInputPlan.CreateV3(prep, brain, selection, PlanProfile, hostOnly ? null : "model", hostOnly ? 0 : 64000, []);
        return new(plan, prep, plan.RestoreChunks(prep, brain, PlanProfile, []));
    }

    [Fact]
    public async Task RestoredBatchProcessorRecoversTerminalBeforePrivateOrModelContext()
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); var operations = new RestoredProcessorOperations(batch, f.Auth.Clock.Current);
        operations.Prior = operations.Advanced;
        var result = await new GroupRestoredBatchProcessor(operations).ProcessClaimedAsync(batch.Preparation.Context.Handle);
        Assert.Equal(["probe"], operations.Calls); Assert.Equal(GroupRestoredBatchDisposition.Completed, result.Disposition);
        Assert.True(result.RecoveredTerminal); Assert.Null(result.RetryAtUtc);
    }

    [Fact]
    public async Task RestoredBatchProcessorMissingOriginalDoesNotReselectInvokeModelOrManufactureCompletion()
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); var operations = new RestoredProcessorOperations(batch, f.Auth.Clock.Current) { Batch = null };
        var result = await new GroupRestoredBatchProcessor(operations).ProcessClaimedAsync(batch.Preparation.Context.Handle);
        Assert.Equal(["probe", "restore"], operations.Calls); Assert.Equal(GroupRestoredBatchDisposition.AwaitingOriginal, result.Disposition);
        Assert.False(result.RecoveredTerminal); Assert.Null(result.RetryAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredBatchProcessorCommitsEveryOriginalChunkBeforeTerminalThenFrontier(bool multiple)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f, multiple); var operations = new RestoredProcessorOperations(batch, f.Auth.Clock.Current);
        var result = await new GroupRestoredBatchProcessor(operations).ProcessClaimedAsync(batch.Preparation.Context.Handle);
        Assert.Equal(new[] { "probe", "restore" }.Concat(batch.Chunks.Select(x => "chunk:" + x.Original.Ordinal)).Concat(["terminal", "advance"]), operations.Calls);
        Assert.Equal(GroupRestoredBatchDisposition.Completed, result.Disposition); Assert.False(result.RecoveredTerminal);
        Assert.Equal(batch.Preparation.Scope, result.Scope); Assert.Equal(batch.Preparation.BatchId, result.BatchId);
    }

    [Fact]
    public async Task RestoredBatchProcessorWaitingChunkStopsLaterWorkAndBothCompletionStores()
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f, true); var retry = f.Auth.Clock.Current.AddSeconds(15);
        var operations = new RestoredProcessorOperations(batch, f.Auth.Clock.Current) { WaitOrdinal = 2, Retry = retry };
        var result = await new GroupRestoredBatchProcessor(operations).ProcessClaimedAsync(batch.Preparation.Context.Handle);
        Assert.Equal(["probe", "restore", "chunk:1", "chunk:2"], operations.Calls);
        Assert.Equal(GroupRestoredBatchDisposition.Waiting, result.Disposition); Assert.Equal(retry, result.RetryAtUtc);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("restore")]
    [InlineData("chunk:1")]
    [InlineData("terminal")]
    [InlineData("advance")]
    public async Task RestoredBatchProcessorCancellationAcrossEachAwaitNeverReportsCompletionOrCallsNextStage(string boundary)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); using var caller = new CancellationTokenSource();
        var operations = new RestoredProcessorOperations(batch, f.Auth.Clock.Current) { AfterAwait = name => { if (name == boundary) caller.Cancel(); } };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GroupRestoredBatchProcessor(operations)
            .ProcessClaimedAsync(batch.Preparation.Context.Handle, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(boundary, operations.Calls[^1]);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("restore")]
    [InlineData("chunk:1")]
    [InlineData("terminal")]
    [InlineData("advance")]
    public async Task RestoredBatchProcessorAnyStoreFailurePropagatesWithoutFallbackOrFalseCompletion(string boundary)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); var operations = new RestoredProcessorOperations(batch, f.Auth.Clock.Current) { FailAt = boundary };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupRestoredBatchProcessor(operations).ProcessClaimedAsync(batch.Preparation.Context.Handle));
        Assert.Equal(boundary, operations.Calls[^1]);
        if (boundary == "terminal") Assert.DoesNotContain("advance", operations.Calls);
        // A retry after a lost terminal reply begins with the independent
        // terminal path; this fake establishes protocol, not real SQL replay.
        operations.Calls.Clear(); operations.FailAt = null; operations.Prior = operations.Advanced;
        var recovered = await new GroupRestoredBatchProcessor(operations).ProcessClaimedAsync(batch.Preparation.Context.Handle);
        Assert.Equal(["probe"], operations.Calls); Assert.True(recovered.RecoveredTerminal);
    }

    [Theory]
    [InlineData("prior-scope")]
    [InlineData("prior-batch")]
    [InlineData("chunk-operation")]
    [InlineData("wait-without-time")]
    [InlineData("wait-offset")]
    [InlineData("committed-with-retry")]
    [InlineData("terminal-scope")]
    [InlineData("terminal-operation")]
    [InlineData("advance-batch")]
    [InlineData("advance-version")]
    public async Task RestoredBatchProcessorMismatchedProgressCannotQualifyAnotherOperationOrCompletion(string fault)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); var operations = new RestoredProcessorOperations(batch, f.Auth.Clock.Current);
        switch (fault)
        {
            case "prior-scope": operations.Prior = operations.Advanced with { Scope = batch.Preparation.Scope with { CompanyId = Guid.NewGuid() } }; break;
            case "prior-batch": operations.Prior = operations.Advanced with { BatchId = Guid.NewGuid() }; break;
            case "chunk-operation": operations.WrongOperation = true; break;
            case "wait-without-time": operations.WaitOrdinal = 1; operations.Retry = null; break;
            case "wait-offset": operations.WaitOrdinal = 1; operations.Retry = f.Auth.Clock.Current.ToOffset(TimeSpan.FromHours(7)); break;
            case "committed-with-retry": operations.CommittedRetry = true; break;
            case "terminal-scope": operations.Terminal = operations.Terminal with { Scope = batch.Preparation.Scope with { TenantId = Guid.NewGuid() } }; break;
            case "terminal-operation": operations.Terminal = operations.Terminal with { OperationId = Guid.NewGuid() }; break;
            case "advance-batch": operations.Advanced = operations.Advanced with { BatchId = Guid.NewGuid() }; break;
            default: operations.Advanced = operations.Advanced with { Version = 0 }; break;
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupRestoredBatchProcessor(operations).ProcessClaimedAsync(batch.Preparation.Context.Handle));
        Assert.Equal("Group restored batch is not available.", error.Message); Assert.Null(error.InnerException);
        if (fault.StartsWith("terminal-", StringComparison.Ordinal)) Assert.DoesNotContain("advance", operations.Calls);
    }

    [Theory]
    [InlineData("processor")]
    [InlineData("restore")]
    [InlineData("host")]
    [InlineData("terminal")]
    [InlineData("advance")]
    public async Task RestoredBatchActualHostCompositionKeepsProviderFactoryLazyBeforeEachSqlOwner(string path)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f, hostOnly: true); var chunk = Assert.Single(batch.Chunks);
        Assert.False(chunk.Original.RequiresModel); var trap = new ModelFenceConnectionTrap(); using var db = V3StoreDatabase(trap);
        var factories = 0; var reads = f.Keys.Reads;
        var operations = new GroupRestoredBatchOperations(db, f.Worker, f.Auth.Clock,
            new(db, f.Worker, f.Auth.Clock, f.Keys, new()), new(db, f.Worker, f.Auth.Clock, f.Keys, new()), f.Keys,
            () => { factories++; throw new InvalidOperationException("CURRENT_MODEL_MUST_NOT_INITIALIZE"); });
        Task Action() => path switch
        {
            "restore" => operations.RestoreAsync(batch.Preparation.Context.Handle, default),
            "host" => operations.ProcessChunkAsync(batch, chunk, default),
            "terminal" => operations.CommitTerminalAsync(batch, default),
            "advance" => operations.AdvanceAsync(batch.Preparation.Scope, batch.Preparation.BatchId, default),
            _ => new GroupRestoredBatchProcessor(operations).ProcessClaimedAsync(batch.Preparation.Context.Handle)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(1, trap.Attempts); Assert.Equal(0, factories); Assert.Equal(reads, f.Keys.Reads);
        // Actual call-path guards, never positive SQL/effect/model evidence.
    }

    private sealed class RestoredProcessorOperations(GroupRestoredOriginalBatch input, DateTimeOffset now) : IGroupRestoredBatchOperations
    {
        internal readonly List<string> Calls = [];
        internal GroupRestoredOriginalBatch? Batch = input;
        internal GroupTerminalFrontierAdvanceResult? Prior;
        internal GroupTerminalFrontierAdvanceResult Advanced = new(input.Preparation.Scope, input.Preparation.BatchId, 1,
            input.Preparation.Context.AllocatedThroughSequence, new('A', 64), now, false);
        internal GroupBatchTerminalCommitResult Terminal = new(input.Preparation.Scope, input.Preparation.BatchId, input.Preparation.BatchId,
            new('A', 64), 0, input.Preparation.Context.AllocatedThroughSequence, input.Preparation.Receipts.Count, input.Preparation.Receipts.Count,
            input.Chunks.Count, 0, now, false);
        internal int? WaitOrdinal;
        internal DateTimeOffset? Retry = now.AddSeconds(15);
        internal bool WrongOperation, CommittedRetry;
        internal string? FailAt;
        internal Action<string>? AfterAwait;
        private async Task<T> Reply<T>(string name, T result)
        {
            Calls.Add(name); await Task.Yield(); AfterAwait?.Invoke(name);
            if (FailAt == name) throw new InvalidOperationException("owned store failed");
            return result;
        }
        public Task<GroupTerminalFrontierAdvanceResult?> TryAdvanceCommittedAsync(GroupBatchClaimHandle handle, CancellationToken token) => Reply("probe", Prior);
        public Task<GroupRestoredOriginalBatch?> RestoreAsync(GroupBatchClaimHandle handle, CancellationToken token) => Reply("restore", Batch);
        public Task<GroupRestoredChunkProgress> ProcessChunkAsync(GroupRestoredOriginalBatch batch, GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token) =>
            Reply("chunk:" + chunk.Original.Ordinal, new GroupRestoredChunkProgress(WrongOperation ? Guid.NewGuid() : chunk.Original.OperationId,
                WaitOrdinal != chunk.Original.Ordinal, WaitOrdinal == chunk.Original.Ordinal || CommittedRetry ? Retry : null));
        public Task<GroupBatchTerminalCommitResult> CommitTerminalAsync(GroupRestoredOriginalBatch batch, CancellationToken token) => Reply("terminal", Terminal);
        public Task<GroupTerminalFrontierAdvanceResult> AdvanceAsync(GroupScope scope, Guid batch, CancellationToken token) => Reply("advance", Advanced);
    }
}
