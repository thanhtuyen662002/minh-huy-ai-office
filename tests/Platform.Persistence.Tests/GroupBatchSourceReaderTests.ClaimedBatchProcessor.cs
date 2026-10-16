using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimedBatchCreatesOnlyMissingOriginalThenReentersActualRecoveryBeforeReportingProgress(bool model)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f, hostOnly: !model); var ops = new ClaimedOperations(batch, model);
        var result = await new GroupClaimedBatchProcessor(ops).ProcessAsync(batch.Preparation.Context.Handle);
        Assert.Same(ops.After, result);
        Assert.Equal(model ? ["restore:1", "host", "model", "restore:2"] : new[] { "restore:1", "host", "restore:2" }, ops.Calls);
        // Internal structural protocol only; no actual SQL/effect/model.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimedBatchExistingCompletionOrWaitKeepsBothOriginalCreatorsCold(bool waiting)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); var ops = new ClaimedOperations(batch, true);
        ops.Before = waiting ? ops.After with { Disposition = GroupRestoredBatchDisposition.Waiting, RetryAtUtc = f.Auth.Clock.Current.AddSeconds(15) }
            : ops.After with { RecoveredTerminal = true };
        Assert.Same(ops.Before, await new GroupClaimedBatchProcessor(ops).ProcessAsync(batch.Preparation.Context.Handle));
        Assert.Equal(["restore:1"], ops.Calls);
    }

    [Theory]
    [InlineData("restore:1")]
    [InlineData("host")]
    [InlineData("model")]
    [InlineData("restore:2")]
    public async Task ClaimedBatchCancellationAcrossEachAwaitStopsWithoutFallbackOrCompletion(string boundary)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); using var caller = new CancellationTokenSource();
        var ops = new ClaimedOperations(batch, true) { AfterAwait = name => { if (name == boundary) caller.Cancel(); } };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GroupClaimedBatchProcessor(ops)
            .ProcessAsync(batch.Preparation.Context.Handle, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(boundary, ops.Calls[^1]);
    }

    [Theory]
    [InlineData("restore:1")]
    [InlineData("host")]
    [InlineData("model")]
    [InlineData("restore:2")]
    public async Task ClaimedBatchStoreFailureNeverBecomesSuccessfulProgressOrAnotherAttempt(string boundary)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); var ops = new ClaimedOperations(batch, true) { FailAt = boundary };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupClaimedBatchProcessor(ops).ProcessAsync(batch.Preparation.Context.Handle));
        Assert.Equal(boundary, ops.Calls[^1]);
    }

    [Theory]
    [InlineData("initial-scope")]
    [InlineData("initial-batch")]
    [InlineData("initial-enum")]
    [InlineData("initial-retry")]
    [InlineData("wait-missing")]
    [InlineData("wait-offset")]
    [InlineData("wait-terminal")]
    [InlineData("host-enum")]
    [InlineData("host-null")]
    [InlineData("model-missing-prep")]
    [InlineData("model-with-original")]
    [InlineData("available-missing-original")]
    [InlineData("available-with-prep")]
    [InlineData("model-unavailable")]
    [InlineData("final-missing")]
    [InlineData("final-scope")]
    [InlineData("final-retry")]
    public async Task ClaimedBatchMalformedOrForeignMetadataCannotAuthorizeCreationOrCompletion(string fault)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); var ops = new ClaimedOperations(batch, true);
        switch (fault)
        {
            case "initial-scope": ops.Before = ops.Before with { Scope = batch.Preparation.Scope with { CompanyId = Guid.NewGuid() } }; break;
            case "initial-batch": ops.Before = ops.Before with { BatchId = Guid.NewGuid() }; break;
            case "initial-enum": ops.Before = ops.Before with { Disposition = (GroupRestoredBatchDisposition)999 }; break;
            case "initial-retry": ops.Before = ops.Before with { RetryAtUtc = f.Auth.Clock.Current }; break;
            case "wait-missing": ops.Before = ops.Before with { Disposition = GroupRestoredBatchDisposition.Waiting }; break;
            case "wait-offset": ops.Before = ops.Before with { Disposition = GroupRestoredBatchDisposition.Waiting, RetryAtUtc = f.Auth.Clock.Current.ToOffset(TimeSpan.FromHours(7)) }; break;
            case "wait-terminal": ops.Before = ops.Before with { Disposition = GroupRestoredBatchDisposition.Waiting, RetryAtUtc = f.Auth.Clock.Current, RecoveredTerminal = true }; break;
            case "host-enum": ops.Host = ops.Host with { Disposition = (GroupOriginalCreationDisposition)999 }; break;
            case "host-null": ops.Host = null!; break;
            case "model-missing-prep": ops.Host = ops.Host with { Preparation = null }; break;
            case "model-with-original": ops.Host = ops.Host with { Original = batch.Original }; break;
            case "available-missing-original": ops.Host = new(GroupOriginalCreationDisposition.Available, null, null); break;
            case "available-with-prep": ops.Host = new(GroupOriginalCreationDisposition.Available, batch.Original, batch.Preparation); break;
            case "model-unavailable": ops.Model = ops.Host; break;
            case "final-missing": ops.After = ops.Before; break;
            case "final-scope": ops.After = ops.After with { Scope = batch.Preparation.Scope with { TenantId = Guid.NewGuid() } }; break;
            default: ops.After = ops.After with { RetryAtUtc = f.Auth.Clock.Current }; break;
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupClaimedBatchProcessor(ops).ProcessAsync(batch.Preparation.Context.Handle));
        Assert.Equal("Group claimed batch is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.True(ops.Calls.Count <= 4);
    }

    [Theory]
    [InlineData("processor")]
    [InlineData("host")]
    [InlineData("model")]
    public async Task ClaimedBatchFixedCompositionEntersActualSqlOwnerBeforeExistingGatewayFactoryOrPrivateKeys(string path)
    {
        using var f = new Fixture(); var batch = await RestoredProcessorBatchAsync(f); var trap = new ModelFenceConnectionTrap(); using var db = V3StoreDatabase(trap);
        var factories = 0; var reads = f.Keys.Reads;
        var ops = new GroupClaimedBatchOperations(db, f.Worker, f.Auth.Clock,
            new(db, f.Worker, f.Auth.Clock, f.Keys, new()), new(db, f.Worker, f.Auth.Clock, f.Keys, new()), f.Keys,
            () => { factories++; throw new InvalidOperationException("Must remain cold."); });
        var handle = batch.Preparation.Context.Handle;
        Task Action() => path switch
        {
            "host" => ops.CreateWithoutModelAsync(handle, default),
            "model" => ops.CreateModelAsync(handle, batch.Preparation, default),
            _ => new GroupClaimedBatchProcessor(ops).ProcessAsync(handle)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(1, trap.Attempts); Assert.Equal(0, factories); Assert.Equal(reads, f.Keys.Reads);
    }

    private sealed class ClaimedOperations : IGroupClaimedBatchOperations
    {
        internal readonly List<string> Calls = [];
        internal GroupRestoredBatchOutcome Before, After;
        internal GroupOriginalCreationResult Host, Model;
        internal Action<string>? AfterAwait;
        internal string? FailAt;
        private int restored;
        internal ClaimedOperations(GroupRestoredOriginalBatch batch, bool model)
        {
            Before = new(batch.Preparation.Scope, batch.Preparation.BatchId, GroupRestoredBatchDisposition.AwaitingOriginal, null, false);
            After = Before with { Disposition = GroupRestoredBatchDisposition.Completed };
            Model = new(GroupOriginalCreationDisposition.Available, batch.Original, null);
            Host = model ? new(GroupOriginalCreationDisposition.RequiresModel, null, batch.Preparation) : Model;
        }
        public async Task<GroupRestoredBatchOutcome> ProcessRestoredAsync(GroupBatchClaimHandle handle, CancellationToken token)
        { var ordinal = ++restored; await Boundary("restore:" + ordinal); return ordinal == 1 ? Before : After; }
        public async Task<GroupOriginalCreationResult> CreateWithoutModelAsync(GroupBatchClaimHandle handle, CancellationToken token)
        { await Boundary("host"); return Host; }
        public async Task<GroupOriginalCreationResult> CreateModelAsync(GroupBatchClaimHandle handle, GroupBatchSourcePreparation preparation, CancellationToken token)
        { await Boundary("model"); return Model; }
        private async Task Boundary(string name)
        { Calls.Add(name); await Task.Yield(); AfterAwait?.Invoke(name); if (FailAt == name) throw new InvalidOperationException("Synthetic store failure."); }
    }
}
