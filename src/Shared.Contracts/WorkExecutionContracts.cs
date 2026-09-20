namespace MinhHuy.AIOffice.Shared.Contracts;

public enum WorkFailureClass
{
    Transient,
    Permanent,
    Authorization,
    Validation
}

public enum RetryDisposition
{
    Retry,
    DeadLetter
}

public enum WorkDispatchState
{
    Pending,
    Published,
    Acknowledged,
    DeadLettered
}

public enum WorkDeliveryOutcome
{
    Completed,
    AlreadyCompleted,
    Failed
}

public enum BrokerSettlement
{
    Acknowledge,
    Requeue,
    DeadLetter
}

public static class WorkDeliverySettlement
{
    public static BrokerSettlement Resolve(
        WorkDeliveryOutcome outcome,
        WorkFailureClass? failureClass,
        int attempt,
        int maxAttempts)
    {
        if (outcome is WorkDeliveryOutcome.Completed or WorkDeliveryOutcome.AlreadyCompleted)
        {
            if (failureClass is not null)
            {
                throw new ArgumentException(
                    "Successful delivery outcomes cannot carry a failure class.",
                    nameof(failureClass));
            }

            return BrokerSettlement.Acknowledge;
        }

        if (failureClass is null)
        {
            throw new ArgumentException(
                "Failed delivery outcomes require a failure class.",
                nameof(failureClass));
        }

        return WorkRetryPolicy.Classify(failureClass.Value, attempt, maxAttempts) switch
        {
            RetryDisposition.Retry => BrokerSettlement.Requeue,
            RetryDisposition.DeadLetter => BrokerSettlement.DeadLetter,
            _ => throw new InvalidOperationException("Unknown retry disposition.")
        };
    }
}

public static class WorkDispatchTransitions
{
    public static bool CanTransition(WorkDispatchState current, WorkDispatchState next)
    {
        if (current == next)
        {
            return true;
        }

        return current switch
        {
            WorkDispatchState.Pending => next is WorkDispatchState.Published
                or WorkDispatchState.DeadLettered,
            WorkDispatchState.Published => next is WorkDispatchState.Acknowledged
                or WorkDispatchState.DeadLettered,
            WorkDispatchState.Acknowledged => false,
            WorkDispatchState.DeadLettered => false,
            _ => false
        };
    }
}

public sealed record WorkDispatchEnvelope(
    Guid MessageId,
    Guid TenantId,
    Guid CompanyId,
    Guid TaskId,
    Guid StepId,
    string IdempotencyKey,
    int Attempt,
    long? CheckpointVersion,
    DateTimeOffset EnqueuedAtUtc)
{
    public static WorkDispatchEnvelope Create(
        Guid messageId,
        Guid tenantId,
        Guid companyId,
        Guid taskId,
        Guid stepId,
        int attempt,
        long? checkpointVersion,
        DateTimeOffset enqueuedAtUtc)
    {
        EnsureNonEmpty(messageId, nameof(messageId));
        EnsureNonEmpty(tenantId, nameof(tenantId));
        EnsureNonEmpty(companyId, nameof(companyId));
        EnsureNonEmpty(taskId, nameof(taskId));
        EnsureNonEmpty(stepId, nameof(stepId));

        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must be at least 1.");
        }

        if (checkpointVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkpointVersion),
                "Checkpoint version cannot be negative.");
        }

        return new(
            messageId,
            tenantId,
            companyId,
            taskId,
            stepId,
            WorkIdempotencyKey.ForStep(tenantId, companyId, taskId, stepId),
            attempt,
            checkpointVersion,
            enqueuedAtUtc);
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must be non-empty.", parameterName);
        }
    }
}

public static class WorkIdempotencyKey
{
    public static string ForStep(
        Guid tenantId,
        Guid companyId,
        Guid taskId,
        Guid stepId)
    {
        EnsureNonEmpty(tenantId, nameof(tenantId));
        EnsureNonEmpty(companyId, nameof(companyId));
        EnsureNonEmpty(taskId, nameof(taskId));
        EnsureNonEmpty(stepId, nameof(stepId));

        return $"v1:{tenantId:N}:{companyId:N}:{taskId:N}:{stepId:N}";
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must be non-empty.", parameterName);
        }
    }
}

public static class WorkRetryPolicy
{
    public static RetryDisposition Classify(
        WorkFailureClass failureClass,
        int attempt,
        int maxAttempts)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        return failureClass == WorkFailureClass.Transient && attempt < maxAttempts
            ? RetryDisposition.Retry
            : RetryDisposition.DeadLetter;
    }
}

public sealed record WorkLeaseSnapshot(
    Guid LeaseId,
    string OwnerId,
    long FenceToken,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

    public bool Authorizes(
        Guid leaseId,
        string ownerId,
        long fenceToken,
        DateTimeOffset nowUtc)
    {
        return !IsExpired(nowUtc)
            && leaseId == LeaseId
            && string.Equals(ownerId, OwnerId, StringComparison.Ordinal)
            && fenceToken == FenceToken;
    }
}
