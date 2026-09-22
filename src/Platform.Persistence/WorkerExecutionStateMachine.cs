using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public static class WorkerExecutionStateMachine
{
    public static TaskStepExecutionRecord Initialize(
        Guid tenantId,
        Guid companyId,
        Guid taskId,
        Guid stepId,
        DateTimeOffset nowUtc)
    {
        return new TaskStepExecutionRecord
        {
            TenantId = tenantId,
            CompanyId = companyId,
            TaskId = taskId,
            StepId = stepId,
            IdempotencyKey = WorkIdempotencyKey.ForStep(
                tenantId,
                companyId,
                taskId,
                stepId),
            UpdatedAtUtc = nowUtc
        };
    }

    public static TaskDispatchRecord CreateDispatch(
        TaskStepExecutionRecord execution,
        Guid messageId,
        long? checkpointVersion,
        DateTimeOffset availableAtUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("Message id must be non-empty.", nameof(messageId));
        }

        if (checkpointVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpointVersion));
        }

        if (execution.DeadLetteredAtUtc is not null)
        {
            throw new InvalidOperationException("Dead-lettered execution cannot be dispatched again.");
        }

        if (execution.LeaseId is not null
            && execution.LeaseExpiresAtUtc is { } leaseExpiry
            && leaseExpiry > nowUtc)
        {
            throw new InvalidOperationException("Active execution lease prevents another dispatch.");
        }

        if (execution.NextAttemptAtUtc is { } nextAttemptAt
            && availableAtUtc < nextAttemptAt)
        {
            throw new InvalidOperationException("Dispatch cannot run before the retry window.");
        }

        var attempt = checked(execution.Attempt + 1);
        execution.Attempt = attempt;
        execution.NextAttemptAtUtc = null;
        execution.UpdatedAtUtc = nowUtc;

        return new TaskDispatchRecord
        {
            TenantId = execution.TenantId,
            CompanyId = execution.CompanyId,
            TaskId = execution.TaskId,
            StepId = execution.StepId,
            MessageId = messageId,
            IdempotencyKey = execution.IdempotencyKey,
            Attempt = attempt,
            CheckpointVersion = checkpointVersion,
            State = WorkDispatchState.Pending,
            AvailableAtUtc = availableAtUtc,
            CreatedAtUtc = nowUtc
        };
    }

    public static WorkLeaseSnapshot AcquireLease(
        TaskStepExecutionRecord execution,
        TaskDispatchRecord dispatch,
        Guid leaseId,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(dispatch);

        if (leaseId == Guid.Empty)
        {
            throw new ArgumentException("Lease id must be non-empty.", nameof(leaseId));
        }

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException("Lease owner must be present.", nameof(ownerId));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        EnsureMatchingExecution(execution, dispatch);

        if (dispatch.State != WorkDispatchState.Published)
        {
            throw new InvalidOperationException("Only published dispatches can acquire a worker lease.");
        }

        if (dispatch.Attempt != execution.Attempt)
        {
            throw new InvalidOperationException("Stale dispatch attempt cannot acquire a worker lease.");
        }

        if (execution.LeaseId is not null
            && execution.LeaseExpiresAtUtc is { } currentExpiry
            && currentExpiry > nowUtc)
        {
            throw new InvalidOperationException("Execution already has an active lease.");
        }

        execution.LeaseFenceToken = checked(execution.LeaseFenceToken + 1);
        execution.LeaseId = leaseId;
        execution.LeaseOwnerId = ownerId;
        execution.LeaseAcquiredAtUtc = nowUtc;
        execution.LeaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        execution.UpdatedAtUtc = nowUtc;

        return CurrentLease(execution);
    }

    public static WorkLeaseSnapshot RenewLease(
        TaskStepExecutionRecord execution,
        WorkLeaseSnapshot lease,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(lease);

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        EnsureAuthorized(execution, lease, nowUtc);
        execution.LeaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        execution.UpdatedAtUtc = nowUtc;

        return CurrentLease(execution);
    }

    public static RetryDisposition RecordFailure(
        TaskStepExecutionRecord execution,
        WorkLeaseSnapshot lease,
        WorkFailureClass failureClass,
        int maxAttempts,
        DateTimeOffset nowUtc,
        DateTimeOffset? nextAttemptAtUtc)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(lease);

        EnsureAuthorized(execution, lease, nowUtc);
        var disposition = WorkRetryPolicy.Classify(
            failureClass,
            execution.Attempt,
            maxAttempts);

        execution.LastFailureClass = failureClass;
        execution.UpdatedAtUtc = nowUtc;

        if (disposition == RetryDisposition.Retry)
        {
            if (nextAttemptAtUtc is null || nextAttemptAtUtc <= nowUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nextAttemptAtUtc),
                    "Retry must be scheduled in the future.");
            }

            execution.NextAttemptAtUtc = nextAttemptAtUtc;
            execution.DeadLetteredAtUtc = null;
        }
        else
        {
            execution.NextAttemptAtUtc = null;
            execution.DeadLetteredAtUtc = nowUtc;
        }

        ClearLease(execution);
        return disposition;
    }

    public static void CompleteLease(
        TaskStepExecutionRecord execution,
        WorkLeaseSnapshot lease,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(lease);

        EnsureAuthorized(execution, lease, nowUtc);
        execution.NextAttemptAtUtc = null;
        execution.LastFailureClass = null;
        execution.UpdatedAtUtc = nowUtc;
        ClearLease(execution);
    }

    public static void TransitionDispatch(
        TaskDispatchRecord dispatch,
        WorkDispatchState next,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        if (!WorkDispatchTransitions.CanTransition(dispatch.State, next))
        {
            throw new InvalidOperationException(
                $"Invalid dispatch transition from {dispatch.State} to {next}.");
        }

        dispatch.State = next;

        switch (next)
        {
            case WorkDispatchState.Published:
                dispatch.PublishedAtUtc ??= nowUtc;
                break;
            case WorkDispatchState.Acknowledged:
                dispatch.AcknowledgedAtUtc ??= nowUtc;
                break;
            case WorkDispatchState.DeadLettered:
                dispatch.DeadLetteredAtUtc ??= nowUtc;
                break;
        }
    }

    private static void EnsureMatchingExecution(
        TaskStepExecutionRecord execution,
        TaskDispatchRecord dispatch)
    {
        if (dispatch.TenantId != execution.TenantId
            || dispatch.CompanyId != execution.CompanyId
            || dispatch.TaskId != execution.TaskId
            || dispatch.StepId != execution.StepId
            || !string.Equals(
                dispatch.IdempotencyKey,
                execution.IdempotencyKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Dispatch does not belong to this execution.");
        }
    }

    private static void EnsureAuthorized(
        TaskStepExecutionRecord execution,
        WorkLeaseSnapshot lease,
        DateTimeOffset nowUtc)
    {
        var current = CurrentLease(execution);
        if (!current.Authorizes(
                lease.LeaseId,
                lease.OwnerId,
                lease.FenceToken,
                nowUtc))
        {
            throw new InvalidOperationException("Worker lease is stale, expired or owned elsewhere.");
        }
    }

    private static WorkLeaseSnapshot CurrentLease(TaskStepExecutionRecord execution)
    {
        if (execution.LeaseId is not { } leaseId
            || execution.LeaseExpiresAtUtc is not { } expiresAt
            || string.IsNullOrWhiteSpace(execution.LeaseOwnerId))
        {
            throw new InvalidOperationException("Execution does not have a complete lease.");
        }

        return new WorkLeaseSnapshot(
            leaseId,
            execution.LeaseOwnerId,
            execution.LeaseFenceToken,
            expiresAt);
    }

    private static void ClearLease(TaskStepExecutionRecord execution)
    {
        execution.LeaseId = null;
        execution.LeaseOwnerId = null;
        execution.LeaseAcquiredAtUtc = null;
        execution.LeaseExpiresAtUtc = null;
    }
}
