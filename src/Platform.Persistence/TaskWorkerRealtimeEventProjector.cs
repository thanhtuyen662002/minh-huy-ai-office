using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class TaskWorkerRealtimeEventProjector
{
    public TaskWorkerRealtimeEvent Project(TaskEventRecord durableEvent)
    {
        ArgumentNullException.ThrowIfNull(durableEvent);
        EnsureScope(durableEvent);

        var payload = ParsePayload(durableEvent.PayloadJson);
        return durableEvent.EventType switch
        {
            "task.status.changed" => Create(durableEvent, TaskWorkerEventKind.TaskStatusChanged,
                taskStatus: ReadEnum<TaskExecutionStatus>(payload, "status")),
            "step.status.changed" => Create(durableEvent, TaskWorkerEventKind.StepStatusChanged,
                stepId: RequireStepId(durableEvent), stepStatus: ReadEnum<TaskStepStatus>(payload, "status")),
            "worker.status.changed" => Create(durableEvent, TaskWorkerEventKind.WorkerStatusChanged,
                workerId: ReadRequiredString(payload, "workerId"), workerStatus: ReadEnum<WorkerExecutionStatus>(payload, "status")),
            "task.approval.required" => Create(durableEvent, TaskWorkerEventKind.ApprovalRequired,
                taskStatus: TaskExecutionStatus.PermissionRequired, stepId: durableEvent.StepId),
            "task.blocked" => Create(durableEvent, TaskWorkerEventKind.Blocked,
                taskStatus: TaskExecutionStatus.Blocked, stepId: durableEvent.StepId),
            _ => throw new InvalidOperationException($"Unsupported durable task event type '{durableEvent.EventType}'.")
        };
    }

    private static TaskWorkerRealtimeEvent Create(
        TaskEventRecord source,
        TaskWorkerEventKind kind,
        Guid? stepId = null,
        string? workerId = null,
        TaskExecutionStatus? taskStatus = null,
        TaskStepStatus? stepStatus = null,
        WorkerExecutionStatus? workerStatus = null)
    {
        var eventId = DeterministicEventId(source.TenantId, source.CompanyId, source.TaskId, source.Sequence);
        return TaskWorkerRealtimeEvent.Create(eventId, kind, source.TenantId, source.CompanyId, source.TaskId,
            stepId, workerId, taskStatus, stepStatus, workerStatus, source.Sequence, source.OccurredAtUtc);
    }

    private static void EnsureScope(TaskEventRecord source)
    {
        if (source.TenantId == Guid.Empty || source.CompanyId == Guid.Empty || source.TaskId == Guid.Empty)
            throw new InvalidOperationException("Durable task event must have tenant, company, and task scope before projection.");
        if (source.Sequence < 1)
            throw new InvalidOperationException("Durable task event sequence must be positive before projection.");
        if (string.IsNullOrWhiteSpace(source.EventType))
            throw new InvalidOperationException("Durable task event type is required before projection.");
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Durable task event payload is not valid JSON.", exception);
        }
    }

    private static T ReadEnum<T>(JsonElement payload, string propertyName) where T : struct, Enum
    {
        var value = ReadRequiredString(payload, propertyName);
        if (!Enum.TryParse<T>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new InvalidOperationException($"Durable task event '{propertyName}' is unsupported.");
        return parsed;
    }

    private static string ReadRequiredString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidOperationException($"Durable task event requires non-empty '{propertyName}'.");
        return property.GetString()!;
    }

    private static Guid RequireStepId(TaskEventRecord source) =>
        source.StepId is { } stepId && stepId != Guid.Empty
            ? stepId
            : throw new InvalidOperationException("Step status event requires a durable step identifier.");

    private static Guid DeterministicEventId(Guid tenantId, Guid companyId, Guid taskId, long sequence)
    {
        Span<byte> bytes = stackalloc byte[16];
        tenantId.TryWriteBytes(bytes);
        Span<byte> company = stackalloc byte[16];
        companyId.TryWriteBytes(company);
        Span<byte> task = stackalloc byte[16];
        taskId.TryWriteBytes(task);
        for (var index = 0; index < 16; index++) bytes[index] ^= company[index] ^= task[index];
        var sequenceBytes = BitConverter.GetBytes(sequence);
        for (var index = 0; index < sequenceBytes.Length; index++) bytes[index] ^= sequenceBytes[index];
        return new Guid(bytes);
    }
}
