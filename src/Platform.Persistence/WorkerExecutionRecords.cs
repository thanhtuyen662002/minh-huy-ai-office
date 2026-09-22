using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class TaskStepExecutionRecord
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid TaskId { get; set; }

    public Guid StepId { get; set; }

    public required string IdempotencyKey { get; set; }

    public int Attempt { get; set; }

    public Guid? LeaseId { get; set; }

    public string? LeaseOwnerId { get; set; }

    public long LeaseFenceToken { get; set; }

    public DateTimeOffset? LeaseAcquiredAtUtc { get; set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    public WorkFailureClass? LastFailureClass { get; set; }

    public DateTimeOffset? DeadLetteredAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

public sealed class TaskDispatchRecord
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid TaskId { get; set; }

    public Guid StepId { get; set; }

    public Guid MessageId { get; set; }

    public required string IdempotencyKey { get; set; }

    public int Attempt { get; set; }

    public long? CheckpointVersion { get; set; }

    public WorkDispatchState State { get; set; } = WorkDispatchState.Pending;

    public DateTimeOffset AvailableAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; set; }

    public DateTimeOffset? DeadLetteredAtUtc { get; set; }
}
