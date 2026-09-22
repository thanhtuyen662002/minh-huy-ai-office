using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api.Realtime;

public sealed class TaskWorkerRealtimeSignalRBridge(ITaskStatusPublisher publisher)
    : ITaskWorkerRealtimeEventPublisher
{
    public Task PublishAsync(
        TaskWorkerRealtimeEvent realtimeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeEvent);

        return realtimeEvent.Kind switch
        {
            TaskWorkerEventKind.TaskStatusChanged => publisher.PublishTaskStatusAsync(
                realtimeEvent.TenantId,
                realtimeEvent.CompanyId,
                new TaskStatusChanged(
                    realtimeEvent.TaskId,
                    Require(realtimeEvent.TaskStatus, nameof(realtimeEvent.TaskStatus)).ToString(),
                    realtimeEvent.OccurredAtUtc),
                cancellationToken),
            TaskWorkerEventKind.StepStatusChanged => publisher.PublishStepStatusAsync(
                realtimeEvent.TenantId,
                realtimeEvent.CompanyId,
                new TaskStepStatusChanged(
                    realtimeEvent.TaskId,
                    Require(realtimeEvent.StepId, nameof(realtimeEvent.StepId)),
                    Require(realtimeEvent.StepStatus, nameof(realtimeEvent.StepStatus)).ToString(),
                    realtimeEvent.OccurredAtUtc),
                cancellationToken),
            TaskWorkerEventKind.WorkerStatusChanged => publisher.PublishWorkerStatusAsync(
                realtimeEvent.TenantId,
                realtimeEvent.CompanyId,
                new WorkerStatusChanged(
                    realtimeEvent.TaskId,
                    RequireWorkerId(realtimeEvent.WorkerId),
                    Require(realtimeEvent.WorkerStatus, nameof(realtimeEvent.WorkerStatus)).ToString(),
                    realtimeEvent.OccurredAtUtc),
                cancellationToken),
            TaskWorkerEventKind.ApprovalRequired => publisher.PublishAttentionRequiredAsync(
                realtimeEvent.TenantId,
                realtimeEvent.CompanyId,
                new TaskAttentionRequired(
                    realtimeEvent.TaskId,
                    "approval",
                    "permission-required",
                    realtimeEvent.OccurredAtUtc),
                cancellationToken),
            TaskWorkerEventKind.Blocked => publisher.PublishAttentionRequiredAsync(
                realtimeEvent.TenantId,
                realtimeEvent.CompanyId,
                new TaskAttentionRequired(
                    realtimeEvent.TaskId,
                    "blocked",
                    "task-blocked",
                    realtimeEvent.OccurredAtUtc),
                cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported realtime event kind '{realtimeEvent.Kind}'.")
        };
    }

    private static T Require<T>(T? value, string parameterName) where T : struct =>
        value ?? throw new InvalidOperationException($"Realtime event requires '{parameterName}'.");

    private static string RequireWorkerId(string? workerId) =>
        !string.IsNullOrWhiteSpace(workerId)
            ? workerId
            : throw new InvalidOperationException("Realtime worker event requires a worker identifier.");
}
