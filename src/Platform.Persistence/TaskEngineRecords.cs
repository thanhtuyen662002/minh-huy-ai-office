using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class TaskRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? ConversationId { get; set; }
    public TaskExecutionStatus Status { get; set; } = TaskExecutionStatus.Pending;
    public string? WaitReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TaskStepRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TaskId { get; set; }
    public Guid Id { get; set; }
    public required string StepKey { get; set; }
    public TaskStepStatus Status { get; set; } = TaskStepStatus.Pending;
    public int Attempt { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TaskDependencyRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TaskId { get; set; }
    public Guid StepId { get; set; }
    public Guid DependsOnStepId { get; set; }
}

public sealed class TaskEventRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TaskId { get; set; }
    public long Sequence { get; set; }
    public Guid? StepId { get; set; }
    public required string EventType { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class TaskCheckpointRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TaskId { get; set; }
    public Guid StepId { get; set; }
    public long Version { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
