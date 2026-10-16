using System.Transactions;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    // Real closed plans, preparer and parser; structural permits and port
    // fakes below verify orchestration only, never committed SQL or a model.
    private static async Task<ChunkCoordinatorInput> ChunkCoordinatorInputAsync(Fixture f, bool work = false)
    {
        var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, new AttemptAdmissionGate(), f.Auth.Clock);
        var prepared = await issuer.PrepareAsync(plan, restored, sidecar);
        var permit = ExecutionGuardPermit(prepared); var wire = NoWorkResponse(permit);
        if (work) wire = wire with { Output = ProposalWire(Assert.Single(permit.Context.Preparation.Candidates)).ToJsonString() };
        var response = GroupModelAttemptResponse.Parse(permit, wire, f.Auth.Clock);
        Assert.Equal(GroupModelAttemptResponseDisposition.Parsed, response.Disposition);
        return new(plan, restored, sidecar, prepared, permit, response, GroupModelAttemptResult.Create(response, restored));
    }

    private sealed record ChunkCoordinatorInput(GroupAutomaticInputPlan Plan, GroupAutomaticInputPlan.RestoredChunk Chunk,
        GroupOriginalAdmissionSidecar Sidecar, GroupModelAttemptAdmissionPreparer.PreparedAttempt Prepared,
        GroupModelAttemptPermit Permit, GroupModelAttemptResponse Response, GroupModelAttemptResult Result);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalChunkRecoversAcceptedOutputBeforeBudgetSidecarOrCurrentProvider(bool work)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f, work);
        var operations = new ChunkCoordinatorOperations(input) { Accepted = input.Result };
        var outcome = await new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk);
        Assert.Equal(["read", "commit"], operations.Calls);
        Assert.Equal(input.Chunk.Original.OperationId, outcome.OperationId);
        Assert.Equal(GroupOriginalModelChunkDisposition.Committed, outcome.Disposition);
        Assert.True(outcome.AcceptedResultRecovered); Assert.False(outcome.ExtractionFailed); Assert.Null(outcome.RetryAtUtc);
        Assert.Equal(1, operations.Commits); Assert.Equal(0, operations.Failures);
    }

    [Theory]
    [InlineData("coordinator")]
    [InlineData("budget")]
    [InlineData("accepted-effect")]
    [InlineData("exhausted-effect")]
    public async Task OriginalChunkActualHostCompositionDoesNotCreateProviderBeforeRecoveryOrFinalSql(string path)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap); var providerFactories = 0;
        var operations = new GroupOriginalModelChunkOperations(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()), f.Keys,
            () => { providerFactories++; throw new Exception("CURRENT_PROVIDER_MUST_NOT_BE_CREATED"); });
        var original = GroupModelAttemptOriginalBinding.Create(input.Plan, input.Chunk); var reads = f.Keys.Reads;
        Task Action() => path switch
        {
            "budget" => operations.ReadBudgetAsync(input.Plan, input.Chunk, default),
            "accepted-effect" => operations.CommitAcceptedAsync(input.Result, original, default),
            "exhausted-effect" => operations.CommitExhaustedAsync(original, default),
            _ => new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(1, trap.Attempts); Assert.Equal(0, providerFactories); Assert.Equal(reads, f.Keys.Reads);
        // An intercepted SQL open is a call-path control, not SQL acceptance.
    }

    [Theory]
    [InlineData("non-sql")]
    [InlineData("dirty")]
    [InlineData("cancelled")]
    [InlineData("ambient")]
    [InlineData("foreign-worker")]
    public async Task OriginalChunkBudgetReaderPreservesExistingEntryFences(string fault)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f);
        var trap = new ModelFenceConnectionTrap(); using var sql = V3StoreDatabase(trap);
        var database = fault == "non-sql" ? f.Auth.Db : sql;
        if (fault == "dirty") database.Add(new GroupModelAttemptStartRecord());
        var worker = fault == "foreign-worker" ? f.Worker with { CompanyId = Guid.NewGuid() } : f.Worker;
        var store = new GroupModelAttemptJournalStore(database, worker, f.Auth.Clock,
            new(database, worker, f.Auth.Clock, f.Keys, new()), new(database, worker, f.Auth.Clock, f.Keys, new()));
        using var caller = new CancellationTokenSource(); if (fault == "cancelled") caller.Cancel();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        Task Action() => store.ReadOriginalBudgetAsync(input.Plan, input.Chunk, caller.Token);
        if (fault == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(Action);
        else if (fault == "foreign-worker") await Assert.ThrowsAsync<UnauthorizedAccessException>(Action);
        else await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(0, trap.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalChunkWaitOrExhaustionDoesNotReadSidecarOrAdmitModel(bool exhausted)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f);
        var retry = f.Auth.Clock.Current.AddSeconds(15);
        var operations = new ChunkCoordinatorOperations(input)
        { Budget = new(exhausted ? GroupModelAttemptDisposition.Exhausted : GroupModelAttemptDisposition.Wait, 3, exhausted ? null : retry) };
        var outcome = await new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk);
        Assert.Equal(exhausted ? ["read", "budget", "exhausted"] : ["read", "budget"], operations.Calls);
        Assert.Equal(exhausted ? GroupOriginalModelChunkDisposition.Committed : GroupOriginalModelChunkDisposition.Waiting, outcome.Disposition);
        Assert.Equal(exhausted ? null : retry, outcome.RetryAtUtc); Assert.Equal(exhausted, outcome.ExtractionFailed);
        Assert.False(outcome.AcceptedResultRecovered); Assert.Equal(input.Chunk.Original.OperationId, outcome.OperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalChunkPersistsParsedOutputBeforeEffectsForStableOriginalOperation(bool work)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f, work); var operations = new ChunkCoordinatorOperations(input);
        var outcome = await new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk);
        Assert.Equal(["read", "budget", "sidecar", "prepare", "start", "execute", "persist", "commit"], operations.Calls);
        Assert.Equal(GroupOriginalModelChunkDisposition.Committed, outcome.Disposition);
        Assert.False(outcome.AcceptedResultRecovered); Assert.False(outcome.ExtractionFailed);
        Assert.Equal(input.Chunk.Original.OperationId, outcome.OperationId); Assert.Equal(1, operations.Commits);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OriginalChunkLostStartReplyOrConcurrentBudgetNeverGetsSecondInvocation(int rawDisposition)
    {
        var disposition = (GroupModelAttemptDisposition)rawDisposition;
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f); var retry = f.Auth.Clock.Current.AddSeconds(30);
        var operations = new ChunkCoordinatorOperations(input)
        { Start = new(new(disposition, 1, disposition == GroupModelAttemptDisposition.Wait ? retry : null), null, true) };
        var outcome = await new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk);
        Assert.Equal(disposition == GroupModelAttemptDisposition.Exhausted
            ? ["read", "budget", "sidecar", "prepare", "start", "exhausted"] : ["read", "budget", "sidecar", "prepare", "start"], operations.Calls);
        Assert.Equal(disposition == GroupModelAttemptDisposition.Exhausted ? null
            : disposition == GroupModelAttemptDisposition.Wait ? retry : f.Auth.Clock.Current, outcome.RetryAtUtc);
        Assert.Equal(disposition == GroupModelAttemptDisposition.Exhausted, outcome.ExtractionFailed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OriginalChunkOnlyExistingTypedProviderOutcomeIsJournaledBeforeRetryOrAttention(bool transient)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f);
        var operations = new ChunkCoordinatorOperations(input)
        {
            ExecutionError = new GroupAdmittedProviderFailure(transient),
            AfterFailure = new(transient ? GroupModelAttemptDisposition.Wait : GroupModelAttemptDisposition.Exhausted, 1,
                transient ? f.Auth.Clock.Current.AddSeconds(15) : null)
        };
        var outcome = await new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk);
        Assert.Equal(transient ? ["read", "budget", "sidecar", "prepare", "start", "execute", "failure"]
            : ["read", "budget", "sidecar", "prepare", "start", "execute", "failure", "exhausted"], operations.Calls);
        Assert.Equal(transient ? GroupModelAttemptFailure.TransientProvider : GroupModelAttemptFailure.PermanentProvider, operations.RecordedFailure);
        Assert.Equal(!transient, outcome.ExtractionFailed); Assert.Equal(0, operations.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalChunkInvalidOutputAppendsBoundedFailureButExpiryOnlyRereadsDurableBudget(bool expired)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f); var operations = new ChunkCoordinatorOperations(input);
        if (expired) f.Auth.Clock.Current = input.Permit.Start.DeadlineUtc;
        operations.Response = GroupModelAttemptResponse.Parse(input.Permit, NoWorkResponse(input.Permit) with { Output = "{}" }, f.Auth.Clock);
        operations.AfterFailure = new(GroupModelAttemptDisposition.Wait, 1, f.Auth.Clock.Current.AddSeconds(15));
        var outcome = await new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk);
        Assert.Equal(["read", "budget", "sidecar", "prepare", "start", "execute", expired ? "budget" : "failure"], operations.Calls);
        Assert.Equal(expired ? null : GroupModelAttemptFailure.InvalidOutput, operations.RecordedFailure);
        Assert.Equal(GroupOriginalModelChunkDisposition.Waiting, outcome.Disposition); Assert.Equal(0, operations.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalChunkLocalFailureOrCancellationNeverBecomesProviderFailureOrAttention(bool cancellation)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f);
        Exception error = cancellation ? new OperationCanceledException() : new InvalidOperationException("local guard");
        var operations = new ChunkCoordinatorOperations(input) { ExecutionError = error };
        var observed = await Record.ExceptionAsync(() => new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk));
        Assert.Same(error, observed); Assert.Equal(["read", "budget", "sidecar", "prepare", "start", "execute"], operations.Calls);
        Assert.Equal(0, operations.Failures); Assert.Equal(0, operations.Commits);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("budget")]
    [InlineData("sidecar")]
    [InlineData("prepare")]
    [InlineData("start")]
    [InlineData("execute")]
    [InlineData("persist")]
    [InlineData("commit")]
    [InlineData("failure")]
    [InlineData("exhausted")]
    public async Task OriginalChunkCancellationAcrossEveryExternalAwaitNeverReportsCompletionOrCallsNextStep(string boundary)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f); using var caller = new CancellationTokenSource();
        var operations = new ChunkCoordinatorOperations(input) { AfterAwait = name => { if (name == boundary) caller.Cancel(); } };
        if (boundary == "failure") operations.ExecutionError = new GroupAdmittedProviderFailure(true);
        if (boundary == "exhausted") operations.Budget = new(GroupModelAttemptDisposition.Exhausted, 3, null);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(boundary, operations.Calls[^1]);
        Assert.Equal(1, operations.Calls.Count(x => x == boundary));
    }

    [Theory]
    [InlineData("persist")]
    [InlineData("commit")]
    [InlineData("failure")]
    [InlineData("exhausted")]
    public async Task OriginalChunkFailedDurableWriteNeverReportsSuccessOrInvokesProviderAgain(string boundary)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f); var operations = new ChunkCoordinatorOperations(input) { FailAt = boundary };
        if (boundary == "failure") operations.ExecutionError = new GroupAdmittedProviderFailure(true);
        if (boundary == "exhausted") operations.Budget = new(GroupModelAttemptDisposition.Exhausted, 3, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk));
        Assert.Equal(boundary, operations.Calls[^1]); Assert.InRange(operations.Calls.Count(x => x == "execute"), 0, 1);
        Assert.Equal(0, operations.Commits); Assert.Equal(0, operations.Failures);
    }

    [Fact]
    public async Task OriginalChunkMissingSidecarStopsBeforePreparation()
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f); var operations = new ChunkCoordinatorOperations(input) { Sidecar = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk));
        Assert.Equal(["read", "budget", "sidecar"], operations.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalChunkReplayPermitOrForeignIssuerCannotInvokeProvider(bool foreignIssuer)
    {
        using var f = new Fixture(); var input = await ChunkCoordinatorInputAsync(f);
        var permit = input.Permit;
        if (foreignIssuer)
        {
            var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, new AttemptAdmissionGate(), f.Auth.Clock);
            permit = ExecutionGuardPermit(await issuer.PrepareAsync(input.Plan, input.Chunk, input.Sidecar));
        }
        var operations = new ChunkCoordinatorOperations(input)
        { Start = new(new(GroupModelAttemptDisposition.Wait, 1, permit.Start.DeadlineUtc), permit, !foreignIssuer) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk));
        Assert.Equal(["read", "budget", "sidecar", "prepare", "start"], operations.Calls);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("response")]
    [InlineData("persisted")]
    public async Task OriginalChunkForeignContextCannotReachEffectWriter(string path)
    {
        using var f = new Fixture(); using var foreign = new Fixture();
        var input = await ChunkCoordinatorInputAsync(f); var other = await ChunkCoordinatorInputAsync(foreign);
        var operations = new ChunkCoordinatorOperations(input);
        if (path == "accepted") operations.Accepted = other.Result;
        if (path == "response") operations.Response = other.Response;
        if (path == "persisted") operations.Persisted = other.Result;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupOriginalModelChunkCoordinator(operations, f.Auth.Clock).ProcessAsync(input.Plan, input.Chunk));
        Assert.DoesNotContain("commit", operations.Calls); Assert.DoesNotContain("exhausted", operations.Calls);
    }

    private sealed class ChunkCoordinatorOperations(ChunkCoordinatorInput input) : IGroupOriginalModelChunkOperations
    {
        internal readonly List<string> Calls = [];
        internal GroupModelAttemptResult? Accepted;
        internal GroupModelAttemptResult Persisted = input.Result;
        internal GroupOriginalAdmissionSidecar? Sidecar = input.Sidecar;
        internal GroupModelAttemptResponse Response = input.Response;
        internal GroupModelAttemptBudgetDecision Budget = new(GroupModelAttemptDisposition.Available, 0, null);
        internal GroupModelAttemptBudgetDecision AfterFailure = new(GroupModelAttemptDisposition.Wait, 1, input.Permit.Start.StartedAtUtc.AddSeconds(15));
        internal GroupModelAttemptStartResult Start = new(new(GroupModelAttemptDisposition.Wait, 1, input.Permit.Start.DeadlineUtc), input.Permit, false);
        internal Exception? ExecutionError;
        internal Action<string>? AfterAwait;
        internal string? FailAt;
        internal GroupModelAttemptFailure? RecordedFailure;
        internal int Commits, Failures, BudgetReads;
        private async Task<T> Reply<T>(string name, T result)
        {
            Calls.Add(name); await Task.Yield(); AfterAwait?.Invoke(name);
            if (FailAt == name) throw new InvalidOperationException("owned durable write failed");
            return result;
        }
        public Task<GroupModelAttemptResult?> ReadAcceptedAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token) => Reply("read", Accepted);
        public Task<GroupModelAttemptBudgetDecision> ReadBudgetAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, CancellationToken token) => Reply("budget", BudgetReads++ == 0 ? Budget : AfterFailure);
        public Task<GroupOriginalAdmissionSidecar?> ReadSidecarAsync(GroupModelAttemptOriginalBinding original, CancellationToken token) => Reply("sidecar", Sidecar);
        public Task<GroupModelAttemptAdmissionPreparer.PreparedAttempt> PrepareAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, GroupOriginalAdmissionSidecar sidecar, CancellationToken token) => Reply("prepare", input.Prepared);
        public Task<GroupModelAttemptStartResult> StartAsync(GroupModelAttemptAdmissionPreparer.PreparedAttempt prepared, CancellationToken token) => Reply("start", Start);
        public async Task<GroupModelAttemptResponse> ExecuteAsync(GroupModelAttemptPermit permit, CancellationToken token)
        { var response = await Reply("execute", Response); if (ExecutionError is not null) throw ExecutionError; return response; }
        public async Task<GroupModelAttemptBudgetDecision> RecordFailureAsync(GroupModelAttemptPermit permit, GroupModelAttemptFailure failure, CancellationToken token)
        { var decision = await Reply("failure", AfterFailure); RecordedFailure = failure; return decision; }
        public Task<GroupModelAttemptResult> PersistAcceptedAsync(GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk, GroupModelAttemptResponse response, CancellationToken token) => Reply("persist", Persisted);
        public async Task CommitAcceptedAsync(GroupModelAttemptResult accepted, GroupModelAttemptOriginalBinding original, CancellationToken token)
        { Assert.Equal(input.Chunk.Original.OperationId, original.OperationId); await Reply("commit", true); Commits++; }
        public async Task CommitExhaustedAsync(GroupModelAttemptOriginalBinding original, CancellationToken token)
        { Assert.Equal(input.Chunk.Original.OperationId, original.OperationId); await Reply("exhausted", true); Failures++; }
    }
}
