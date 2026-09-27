using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

public sealed record WorkStepExecutionResult(
    WorkDeliveryOutcome Outcome,
    WorkFailureClass? FailureClass,
    int MaxAttempts,
    long? DurableCheckpointVersion = null,
    string? CheckpointPayloadJson = null,
    CustomerAiCreditSettlementRequest? CreditSettlement = null);

/// <summary>
/// Trusted runtime evidence produced by an authorized worker when a task consumes
/// reserved AI credits. The release callback must perform an idempotent, durable
/// reservation release keyed by <see cref="SettlementId"/>.
/// </summary>
public sealed record CustomerAiCreditSettlementRequest(
    string SettlementId,
    CustomerAiCreditReservationDecision Reservation,
    long SettledAiCredits,
    Func<CustomerAiCreditSettlementEvidence, CancellationToken, Task> ReleaseReservation);

public interface IWorkStepExecutor
{
    Task<WorkStepExecutionResult> ExecuteAsync(
        WorkDispatchEnvelope envelope,
        WorkLeaseSnapshot lease,
        CancellationToken cancellationToken);
}

public sealed class PersistentWorkDeliveryHandler(
    PlatformDbContext dbContext,
    IWorkStepExecutor executor,
    CustomerAiCreditSettlementPersistenceService? settlementPersistence = null) : IWorkDeliveryHandler
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

        // A transient failure is committed before the retry is published. If publisher confirm fails,
        // RabbitMQ redelivers the original message. Recover the already-durable retry instead of
        // executing the original attempt again and risking duplicate side effects.
        if (execution.Attempt > envelope.Attempt)
        {
            var retryDispatch = await dbContext.TaskDispatches
                .Where(x => x.TenantId == envelope.TenantId
                    && x.CompanyId == envelope.CompanyId
                    && x.TaskId == envelope.TaskId
                    && x.StepId == envelope.StepId
                    && x.Attempt == execution.Attempt
                    && x.State == WorkDispatchState.Published)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Stale delivery has no durable retry dispatch to recover.");
            var recoveredRetryEnvelope = WorkDispatchEnvelope.Create(
                retryDispatch.MessageId,
                retryDispatch.TenantId,
                retryDispatch.CompanyId,
                retryDispatch.TaskId,
                retryDispatch.StepId,
                retryDispatch.Attempt,
                retryDispatch.CheckpointVersion,
                retryDispatch.PublishedAtUtc ?? retryDispatch.CreatedAtUtc);
            return new WorkDeliveryResult(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Transient,
                retryDispatch.Attempt,
                retryDispatch.CheckpointVersion,
                recoveredRetryEnvelope);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var lease = WorkerExecutionStateMachine.AcquireLease(
            execution,
            dispatch,
            Guid.NewGuid(),
            $"rabbitmq:{envelope.MessageId:N}",
            nowUtc,
            LeaseDuration);
        await MarkRunningAsync(envelope, nowUtc, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await executor.ExecuteAsync(envelope, lease, cancellationToken);
        ValidateExecutorResult(result, envelope);

        nowUtc = DateTimeOffset.UtcNow;
        WorkDispatchEnvelope? retryEnvelope = null;
        if (result.Outcome is WorkDeliveryOutcome.Completed or WorkDeliveryOutcome.AlreadyCompleted)
        {
            // Settlement is deliberately completed before the durable task completion
            // transition. RabbitMQ acknowledgement happens only after this handler
            // returns, so a persistence/release failure leaves the delivery retryable.
            if (result.CreditSettlement is not null)
            {
                if (settlementPersistence is null)
                {
                    throw new InvalidOperationException(
                        "AI credit settlement was returned by the executor, but durable settlement persistence is not configured.");
                }

                await settlementPersistence.SettleAndReleaseAsync(
                    result.CreditSettlement.SettlementId,
                    result.CreditSettlement.Reservation,
                    result.CreditSettlement.SettledAiCredits,
                    result.CreditSettlement.ReleaseReservation,
                    cancellationToken);
            }

            WorkerExecutionStateMachine.CompleteLease(execution, lease, nowUtc);
            WorkerExecutionStateMachine.TransitionDispatch(dispatch, WorkDispatchState.Acknowledged, nowUtc);
            await PersistCheckpointAsync(envelope, result, nowUtc, cancellationToken);
            await MarkTerminalAsync(envelope, success: true, result.FailureClass, nowUtc, cancellationToken);
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

            await MarkTerminalAsync(
                envelope,
                success: false,
                result.FailureClass,
                nowUtc,
                cancellationToken);
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

    private async Task MarkRunningAsync(
        WorkDispatchEnvelope envelope,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks.SingleOrDefaultAsync(
            item => item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.Id == envelope.TaskId,
            cancellationToken);
        var step = await dbContext.TaskSteps.SingleOrDefaultAsync(
            item => item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.TaskId == envelope.TaskId
                && item.Id == envelope.StepId,
            cancellationToken);

        if (task is null || step is null)
        {
            throw new InvalidOperationException("Durable task projection is missing for the worker dispatch.");
        }

        if (task.Status == TaskExecutionStatus.Pending)
        {
            task.Status = TaskExecutionStatus.Running;
        }
        else if (task.Status is TaskExecutionStatus.Failed or TaskExecutionStatus.Completed)
        {
            throw new InvalidOperationException("A terminal task cannot acquire a worker lease.");
        }

        if (step.Status is TaskStepStatus.Pending or TaskStepStatus.Ready)
        {
            step.Status = TaskStepStatus.Running;
        }

        task.UpdatedAtUtc = nowUtc;
        step.Attempt = envelope.Attempt;
        step.UpdatedAtUtc = nowUtc;
    }

    private async Task MarkTerminalAsync(
        WorkDispatchEnvelope envelope,
        bool success,
        WorkFailureClass? failureClass,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks.SingleOrDefaultAsync(
            item => item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.Id == envelope.TaskId,
            cancellationToken);
        var step = await dbContext.TaskSteps.SingleOrDefaultAsync(
            item => item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.TaskId == envelope.TaskId
                && item.Id == envelope.StepId,
            cancellationToken);

        if (task is null || step is null)
        {
            throw new InvalidOperationException("Durable task projection is missing for the worker dispatch.");
        }

        var nextTaskStatus = success
            ? TaskExecutionStatus.Completed
            : failureClass == WorkFailureClass.Transient
                ? TaskExecutionStatus.Running
                : TaskExecutionStatus.Failed;
        var nextStepStatus = success
            ? TaskStepStatus.Completed
            : failureClass == WorkFailureClass.Transient
                ? TaskStepStatus.Ready
                : TaskStepStatus.Failed;

        if (task.Status != nextTaskStatus)
        {
            if (!TaskExecutionTransitions.CanTransition(task.Status, nextTaskStatus))
            {
                throw new InvalidOperationException(
                    $"Invalid durable task transition from {task.Status} to {nextTaskStatus}.");
            }

            task.Status = nextTaskStatus;
        }

        step.Status = nextStepStatus;
        task.UpdatedAtUtc = nowUtc;
        step.UpdatedAtUtc = nowUtc;
        await AppendStatusEventAsync(
            envelope,
            step.Id,
            "step.status.changed",
            nextStepStatus.ToString(),
            nowUtc,
            cancellationToken);
        await AppendStatusEventAsync(
            envelope,
            stepId: null,
            "task.status.changed",
            nextTaskStatus.ToString(),
            nowUtc,
            cancellationToken);
    }

    private async Task AppendStatusEventAsync(
        WorkDispatchEnvelope envelope,
        Guid? stepId,
        string eventType,
        string status,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var latestSequence = await dbContext.TaskEvents
            .Where(item => item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.TaskId == envelope.TaskId)
            .Select(item => (long?)item.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        var trackedLatestSequence = dbContext.ChangeTracker
            .Entries<TaskEventRecord>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .Where(item => item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.TaskId == envelope.TaskId)
            .Select(item => (long?)item.Sequence)
            .Max() ?? 0;
        latestSequence = Math.Max(latestSequence, trackedLatestSequence);

        dbContext.TaskEvents.Add(new TaskEventRecord
        {
            TenantId = envelope.TenantId,
            CompanyId = envelope.CompanyId,
            TaskId = envelope.TaskId,
            StepId = stepId,
            Sequence = checked(latestSequence + 1),
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(new { status }),
            OccurredAtUtc = occurredAtUtc
        });
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

        if (result.CreditSettlement is not null
            && result.Outcome is not (WorkDeliveryOutcome.Completed or WorkDeliveryOutcome.AlreadyCompleted))
        {
            throw new InvalidOperationException("AI credit settlement evidence is only valid for a successful execution outcome.");
        }

        _ = WorkDeliverySettlement.Resolve(result.Outcome, result.FailureClass, envelope.Attempt, result.MaxAttempts);
    }
}
