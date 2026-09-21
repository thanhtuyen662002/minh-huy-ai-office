extern alias RuntimeWorker;

using Microsoft.EntityFrameworkCore;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class PersistentWorkDeliveryHandlerTests
{
    [Fact]
    public async Task CompletedDelivery_PersistsAcknowledgementAndCheckpointBeforeReturning()
    {
        await using var db = NewDb();
        var (execution, dispatch, envelope) = SeedPublishedDispatch(db);
        var handler = new PersistentWorkDeliveryHandler(db, new StubExecutor(new WorkStepExecutionResult(WorkDeliveryOutcome.Completed, null, 3, 7, "{\"cursor\":7}")));
        var result = await handler.HandleAsync(envelope, CancellationToken.None);
        Assert.Equal(WorkDeliveryOutcome.Completed, result.Outcome);
        Assert.Equal(WorkDispatchState.Acknowledged, dispatch.State);
        Assert.NotNull(dispatch.AcknowledgedAtUtc);
        Assert.Null(execution.LeaseId);
        var checkpoint = await db.TaskCheckpoints.SingleAsync();
        Assert.Equal(7, checkpoint.Version);
        Assert.Equal("{\"cursor\":7}", checkpoint.PayloadJson);
    }

    [Fact]
    public async Task TransientFailure_PersistsRetryDispatchBeforeReturningRetryEnvelope()
    {
        await using var db = NewDb();
        var (execution, original, envelope) = SeedPublishedDispatch(db);
        var handler = new PersistentWorkDeliveryHandler(db, new StubExecutor(new WorkStepExecutionResult(WorkDeliveryOutcome.Failed, WorkFailureClass.Transient, 3, 4, "{}")));
        var result = await handler.HandleAsync(envelope, CancellationToken.None);
        Assert.NotNull(result.DurableRetryEnvelope);
        Assert.Equal(WorkDispatchState.Published, original.State);
        Assert.NotNull(execution.NextAttemptAtUtc);
        var retry = await db.TaskDispatches.SingleAsync(x => x.MessageId == result.DurableRetryEnvelope!.MessageId);
        Assert.Equal(WorkDispatchState.Published, retry.State);
        Assert.Equal(envelope.Attempt + 1, retry.Attempt);
        Assert.Equal(4, retry.CheckpointVersion);
        Assert.Equal(envelope.IdempotencyKey, retry.IdempotencyKey);
    }

    [Fact]
    public async Task OriginalRedelivery_AfterRetryPublishFailure_RecoversDurableRetryWithoutReexecuting()
    {
        await using var db = NewDb();
        var (_, original, envelope) = SeedPublishedDispatch(db);
        var executor = new CountingExecutor(new WorkStepExecutionResult(WorkDeliveryOutcome.Failed, WorkFailureClass.Transient, 3, 4, "{}"));
        var handler = new PersistentWorkDeliveryHandler(db, executor);
        var first = await handler.HandleAsync(envelope, CancellationToken.None);
        Assert.NotNull(first.DurableRetryEnvelope);
        Assert.Equal(1, executor.CallCount);
        var recovered = await handler.HandleAsync(envelope, CancellationToken.None);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(WorkDeliveryOutcome.Failed, recovered.Outcome);
        Assert.Equal(WorkFailureClass.Transient, recovered.FailureClass);
        Assert.Equal(first.DurableRetryEnvelope, recovered.DurableRetryEnvelope);
        Assert.Equal(WorkDispatchState.Published, original.State);
        Assert.Equal(2, await db.TaskDispatches.CountAsync());
    }

    [Fact]
    public async Task PermanentFailure_PersistsDeadLetterBeforeReturning()
    {
        await using var db = NewDb();
        var (execution, dispatch, envelope) = SeedPublishedDispatch(db);
        var handler = new PersistentWorkDeliveryHandler(db, new StubExecutor(new WorkStepExecutionResult(WorkDeliveryOutcome.Failed, WorkFailureClass.Permanent, 3)));
        var result = await handler.HandleAsync(envelope, CancellationToken.None);
        Assert.Equal(WorkDeliveryOutcome.Failed, result.Outcome);
        Assert.Equal(WorkDispatchState.DeadLettered, dispatch.State);
        Assert.NotNull(execution.DeadLetteredAtUtc);
        Assert.Null(result.DurableRetryEnvelope);
    }

    private static PlatformDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        return new PlatformDbContext(options);
    }

    private static (TaskStepExecutionRecord Execution, TaskDispatchRecord Dispatch, WorkDispatchEnvelope Envelope) SeedPublishedDispatch(PlatformDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var execution = WorkerExecutionStateMachine.Initialize(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
        var dispatch = WorkerExecutionStateMachine.CreateDispatch(execution, Guid.NewGuid(), 2, now, now);
        WorkerExecutionStateMachine.TransitionDispatch(dispatch, WorkDispatchState.Published, now);
        db.TaskStepExecutions.Add(execution);
        db.TaskDispatches.Add(dispatch);
        db.SaveChanges();
        var envelope = WorkDispatchEnvelope.Create(dispatch.MessageId, dispatch.TenantId, dispatch.CompanyId, dispatch.TaskId, dispatch.StepId, dispatch.Attempt, dispatch.CheckpointVersion, now);
        return (execution, dispatch, envelope);
    }

    private sealed class StubExecutor(WorkStepExecutionResult result) : IWorkStepExecutor
    {
        public Task<WorkStepExecutionResult> ExecuteAsync(WorkDispatchEnvelope envelope, WorkLeaseSnapshot lease, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class CountingExecutor(WorkStepExecutionResult result) : IWorkStepExecutor
    {
        public int CallCount { get; private set; }
        public Task<WorkStepExecutionResult> ExecuteAsync(WorkDispatchEnvelope envelope, WorkLeaseSnapshot lease, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
