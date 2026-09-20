using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class WorkerExecutionStateMachineTests
{
    [Fact]
    public void CreateDispatch_PreservesIdempotencyAndIncrementsAttempt()
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);

        var first = WorkerExecutionStateMachine.CreateDispatch(
            execution, Guid.NewGuid(), null, now, now);
        var second = WorkerExecutionStateMachine.CreateDispatch(
            execution, Guid.NewGuid(), 2, now, now);

        Assert.Equal(1, first.Attempt);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Fact]
    public void AcquireLease_ReclaimAfterExpiryAdvancesFenceToken()
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);
        var dispatch = PublishedDispatch(execution, now);

        var first = WorkerExecutionStateMachine.AcquireLease(
            execution, dispatch, Guid.NewGuid(), "worker-a", now, TimeSpan.FromSeconds(30));

        var reclaimedAt = first.ExpiresAtUtc;
        var second = WorkerExecutionStateMachine.AcquireLease(
            execution, dispatch, Guid.NewGuid(), "worker-b", reclaimedAt, TimeSpan.FromSeconds(30));

        Assert.Equal(first.FenceToken + 1, second.FenceToken);
        Assert.False(first.Authorizes(first.LeaseId, first.OwnerId, first.FenceToken, reclaimedAt));
        Assert.True(second.Authorizes(second.LeaseId, second.OwnerId, second.FenceToken, reclaimedAt));
    }

    [Fact]
    public void RecordFailure_RetriesTransientAndDeadLettersPermanent()
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);
        var dispatch = PublishedDispatch(execution, now);
        var lease = WorkerExecutionStateMachine.AcquireLease(
            execution, dispatch, Guid.NewGuid(), "worker-a", now, TimeSpan.FromMinutes(1));

        var retryAt = now.AddMinutes(2);
        var disposition = WorkerExecutionStateMachine.RecordFailure(
            execution, lease, WorkFailureClass.Transient, 3, now, retryAt);

        Assert.Equal(RetryDisposition.Retry, disposition);
        Assert.Equal(retryAt, execution.NextAttemptAtUtc);
        Assert.Null(execution.LeaseId);

        var retryDispatch = WorkerExecutionStateMachine.CreateDispatch(
            execution, Guid.NewGuid(), null, retryAt, retryAt);
        WorkerExecutionStateMachine.TransitionDispatch(
            retryDispatch, WorkDispatchState.Published, retryAt);
        var retryLease = WorkerExecutionStateMachine.AcquireLease(
            execution, retryDispatch, Guid.NewGuid(), "worker-b", retryAt, TimeSpan.FromMinutes(1));

        var deadLetter = WorkerExecutionStateMachine.RecordFailure(
            execution, retryLease, WorkFailureClass.Permanent, 3, retryAt, null);

        Assert.Equal(RetryDisposition.DeadLetter, deadLetter);
        Assert.Equal(retryAt, execution.DeadLetteredAtUtc);
        Assert.Null(execution.NextAttemptAtUtc);
    }

    [Fact]
    public void StaleLeaseCannotCompleteAfterReclaim()
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);
        var dispatch = PublishedDispatch(execution, now);
        var first = WorkerExecutionStateMachine.AcquireLease(
            execution, dispatch, Guid.NewGuid(), "worker-a", now, TimeSpan.FromSeconds(10));

        var reclaimedAt = first.ExpiresAtUtc;
        _ = WorkerExecutionStateMachine.AcquireLease(
            execution, dispatch, Guid.NewGuid(), "worker-b", reclaimedAt, TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            WorkerExecutionStateMachine.CompleteLease(execution, first, reclaimedAt));
    }

    [Fact]
    public void AcknowledgedDispatchIsTerminal()
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);
        var dispatch = WorkerExecutionStateMachine.CreateDispatch(
            execution, Guid.NewGuid(), null, now, now);

        WorkerExecutionStateMachine.TransitionDispatch(dispatch, WorkDispatchState.Published, now);
        WorkerExecutionStateMachine.TransitionDispatch(
            dispatch, WorkDispatchState.Acknowledged, now.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() =>
            WorkerExecutionStateMachine.TransitionDispatch(
                dispatch, WorkDispatchState.DeadLettered, now.AddSeconds(2)));
    }

    private static TaskStepExecutionRecord NewExecution(DateTimeOffset now)
    {
        return WorkerExecutionStateMachine.Initialize(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
    }

    private static TaskDispatchRecord PublishedDispatch(
        TaskStepExecutionRecord execution,
        DateTimeOffset now)
    {
        var dispatch = WorkerExecutionStateMachine.CreateDispatch(
            execution, Guid.NewGuid(), null, now, now);
        WorkerExecutionStateMachine.TransitionDispatch(dispatch, WorkDispatchState.Published, now);
        return dispatch;
    }
}
