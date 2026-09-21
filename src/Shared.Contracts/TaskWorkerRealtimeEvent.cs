namespace MinhHuy.AIOffice.Shared.Contracts;

public enum TaskWorkerEventKind
{
    TaskStatusChanged,
    StepStatusChanged,
    WorkerStatusChanged,
    ApprovalRequired,
    Blocked
}

public enum WorkerExecutionStatus
{
    Available,
    Busy,
    Waiting,
    Failed
}

/// <summary>
/// Authoritative, tenant-scoped event emitted from durable runtime state for realtime consumers.
/// This contract carries identifiers and status only; it must never contain credentials, connection strings,
/// prompts, tool payloads, or other secret-bearing execution context.
/// </summary>
public sealed record TaskWorkerRealtimeEvent(
    Guid EventId,
    int ContractVersion,
    TaskWorkerEventKind Kind,
    Guid TenantId,
    Guid CompanyId,
    Guid TaskId,
    Guid? StepId,
    string? WorkerId,
    TaskExecutionStatus? TaskStatus,
    TaskStepStatus? StepStatus,
    WorkerExecutionStatus? WorkerStatus,
    long Sequence,
    DateTimeOffset OccurredAtUtc)
{
    public const int CurrentContractVersion = 1;

    public static TaskWorkerRealtimeEvent Create(
        Guid eventId,
        TaskWorkerEventKind kind,
        Guid tenantId,
        Guid companyId,
        Guid taskId,
        Guid? stepId,
        string? workerId,
        TaskExecutionStatus? taskStatus,
        TaskStepStatus? stepStatus,
        WorkerExecutionStatus? workerStatus,
        long sequence,
        DateTimeOffset occurredAtUtc)
    {
        EnsureNonEmpty(eventId, nameof(eventId));
        EnsureNonEmpty(tenantId, nameof(tenantId));
        EnsureNonEmpty(companyId, nameof(companyId));
        EnsureNonEmpty(taskId, nameof(taskId));
        if (stepId == Guid.Empty) throw new ArgumentException("Step identifier must be non-empty when present.", nameof(stepId));
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be at least 1.");
        if (workerId is not null && string.IsNullOrWhiteSpace(workerId)) throw new ArgumentException("Worker identifier cannot be blank.", nameof(workerId));

        ValidateShape(kind, stepId, workerId, taskStatus, stepStatus, workerStatus);

        return new(eventId, CurrentContractVersion, kind, tenantId, companyId, taskId, stepId,
            workerId, taskStatus, stepStatus, workerStatus, sequence, occurredAtUtc);
    }

    public bool IsInScope(Guid tenantId, Guid companyId) => TenantId == tenantId && CompanyId == companyId;

    private static void ValidateShape(
        TaskWorkerEventKind kind,
        Guid? stepId,
        string? workerId,
        TaskExecutionStatus? taskStatus,
        TaskStepStatus? stepStatus,
        WorkerExecutionStatus? workerStatus)
    {
        var valid = kind switch
        {
            TaskWorkerEventKind.TaskStatusChanged => taskStatus is not null && stepId is null && stepStatus is null && workerStatus is null,
            TaskWorkerEventKind.StepStatusChanged => stepId is not null && stepStatus is not null && taskStatus is null && workerStatus is null,
            TaskWorkerEventKind.WorkerStatusChanged => !string.IsNullOrWhiteSpace(workerId) && workerStatus is not null && taskStatus is null && stepStatus is null,
            TaskWorkerEventKind.ApprovalRequired => taskStatus == TaskExecutionStatus.PermissionRequired && workerStatus is null,
            TaskWorkerEventKind.Blocked => taskStatus == TaskExecutionStatus.Blocked && workerStatus is null,
            _ => false
        };

        if (!valid)
        {
            throw new ArgumentException("Realtime event payload does not match its event kind.");
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier must be non-empty.", parameterName);
    }
}
