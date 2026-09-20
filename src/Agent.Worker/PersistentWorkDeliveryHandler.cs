using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

public sealed record WorkStepExecutionResult(
    WorkDeliveryOutcome Outcome,
    WorkFailureClass? FailureClass,
    int MaxAttempts,
    long? DurableCheckpointVersion = null,
    string? CheckpointPayloadJson = null);

public interface IWorkStepExecutor
{
    Task<WorkStepExecutionResult> ExecuteAsync(
        WorkDispatchEnvelope envelope,
        WorkLeaseSnapshot lease,
        CancellationToken cancellationToken);
}

public sealed class PersistentWorkDeliveryHandler(
    PlatformDbContext dbContext,
    IWorkStepExecutor executor) : IWorkDeliveryHandler
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public async Task<WorkDeliveryResult> HandleAsync(
        WorkDispatchEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var execution = await dbContext.TaskStepExecutions.SingleOrDefaultAsync(
            x => x.TenantId == envelope.TenantId
                && x.CompanyId == envelope.CompanyId
                && x.TaskId == envelope.TaskId
                && x.StepId == envelope.StepId,
            cancellationToken) ?? throw new InvalidOperationException("Worker execution state was not found.");

        var dispatch = await dbContext.TaskDispatches.SingleOrDefaultAsync(
            x => x.TenantId == envelope.TenantId
                && x.CompanyId == envelope.CompanyId
                && x.TaskId == envelope.TaskId
                && x.StepId == envelope.StepId
                && x.MessageId == envelope.MessageId,
            cancellationToken) ?? throw new InvalidOperationException("Worker dispatch state was not found.");

        EnsureEnvelopeMatchesDurableState(envelope, execution, dispatch);

        if (dispatch.State == WorkDispatchState.Acknowledged)
        {
            return new WorkDeliveryResult(WorkDeliveryOutcome.AlreadyCompleted, null, Math.Max(1, envelope.Attempt));
        }

        if (dispatch.State == WorkDispatchState.DeadLettered)
        {
            return new WorkDeliveryResult(WorkDeliveryOutcome.Failed, execution.LastFailureClass ?? WorkFailureClass.Permanent, Math.Max(1, envelope.Attempt));
        }

        if (dispatch.State != WorkDispatchState.Published)
        {
            throw new InvalidOperationException("Only durably published dispatches may be consumed.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var lease = WorkerExecutionStateMachine.AcquireLease(
            execution,
            dispatch,
            Guid.NewGuid(),
            $"rabbitmq:{envelope.MessageId:N}",
            nowUtc,
            LeaseDuration);
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await executor.ExecuteAsync(envelope, lease, cancellationToken);
        ValidateExecutorResult(result, envelope);

        nowUtc = DateTimeOffset.UtcNow;
        WorkDispatchEnvelope? retryEnvelope = null;
        if (result.Outcome is WorkDeliveryOutcome.Completed or WorkDeliveryOutcome.AlreadyCompleted)
        {
            WorkerExecutionStateMachine.CompleteLease(execution, lease, nowUtc);
            WorkerExecutionStateMachine.TransitionDispatch(dispatch, WorkDispatchState.Acknowledged, nowUtc);
            await PersistCheckpointAsync(envelope, result, nowUtc, cancellationToken);
        }
        else
        {
            var retryAt = nowUtc.Add(RetryDelay);
            var disposition = WorkerExecutionStateMachine.RecordFailure(
                execution,
                lease,
                result.FailureClass!.Value,
                result.MaxAttempts,
                nowUtc,
                retryAt);

            await PersistCheckpointAsync(envelope, result, nowUtc, cancellationToken);
            if (disposition == RetryDisposition.Retry)
            {
                var retryDispatch = WorkerExecutionStateMachine.CreateDispatch(
                    execution,
                    Guid.NewGuid(),
                    result.DurableCheckpointVersion ?? envelope.CheckpointVersion,
                    retryAt,
                    nowUtc);
                WorkerExecutionStateMachine.TransitionDispatch(retryDispatch, WorkDispatchState.Published, nowUtc);
                dbContext.TaskDispatches.Add(retryDispatch);
                retryEnvelope = WorkDispatchEnvelope.Create(
                    retryDispatch.MessageId,
                    retryDispatch.TenantId,
                    retryDispatch.CompanyId,
                    retryDispatch.TaskId,
                    retryDispatch.StepId,
                    retryDispatch.Attempt,
                    retryDispatch.CheckpointVersion,
                    nowUtc);
            }
            else
            {
                WorkerExecutionStateMachine.TransitionDispatch(dispatch, WorkDispatchState.DeadLettered, nowUtc);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new WorkDeliveryResult(
            result.Outcome,
            result.FailureClass,
            result.MaxAttempts,
            result.DurableCheckpointVersion,
            retryEnvelope);
    }

    private async Task PersistCheckpointAsync(
        WorkDispatchEnvelope envelope,
        WorkStepExecutionResult result,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (result.DurableCheckpointVersion is not { } version)
        {
            return;
        }

        if (version < (envelope.CheckpointVersion ?? 0))
        {
            throw new InvalidOperationException("Executor returned a regressed durable checkpoint.");
        }

        var exists = await dbContext.TaskCheckpoints.AnyAsync(
            x => x.TenantId == envelope.TenantId
                && x.CompanyId == envelope.CompanyId
                && x.TaskId == envelope.TaskId
                && x.StepId == envelope.StepId
                && x.Version == version,
            cancellationToken);
        if (!exists)
        {
            dbContext.TaskCheckpoints.Add(new TaskCheckpointRecord
            {
                TenantId = envelope.TenantId,
                CompanyId = envelope.CompanyId,
                TaskId = envelope.TaskId,
                StepId = envelope.StepId,
                Version = version,
                PayloadJson = result.CheckpointPayloadJson ?? "{}",
                CreatedAtUtc = nowUtc
            });
        }
    }

    private static void EnsureEnvelopeMatchesDurableState(
        WorkDispatchEnvelope envelope,
        TaskStepExecutionRecord execution,
        TaskDispatchRecord dispatch)
    {
        if (!string.Equals(envelope.IdempotencyKey, execution.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(envelope.IdempotencyKey, dispatch.IdempotencyKey, StringComparison.Ordinal)
            || envelope.Attempt != dispatch.Attempt
            || envelope.CheckpointVersion != dispatch.CheckpointVersion)
        {
            throw new InvalidOperationException("Broker envelope does not match durable dispatch state.");
        }
    }

    private static void ValidateExecutorResult(WorkStepExecutionResult result, WorkDispatchEnvelope envelope)
    {
        if (result.MaxAttempts < envelope.Attempt)
        {
            throw new InvalidOperationException("Executor retry budget cannot be below the current attempt.");
        }

        _ = WorkDeliverySettlement.Resolve(result.Outcome, result.FailureClass, envelope.Attempt, result.MaxAttempts);
    }
}
