using Microsoft.AspNetCore.SignalR;

namespace MinhHuy.AIOffice.Core.Api.Realtime;

public interface ITaskStatusPublisher
{
    Task PublishTaskStatusAsync(Guid tenantId, Guid companyId, TaskStatusChanged message, CancellationToken cancellationToken = default);
    Task PublishStepStatusAsync(Guid tenantId, Guid companyId, TaskStepStatusChanged message, CancellationToken cancellationToken = default);
    Task PublishWorkerStatusAsync(Guid tenantId, Guid companyId, WorkerStatusChanged message, CancellationToken cancellationToken = default);
    Task PublishAttentionRequiredAsync(Guid tenantId, Guid companyId, TaskAttentionRequired message, CancellationToken cancellationToken = default);
}

public sealed class SignalRTaskStatusPublisher(IHubContext<TaskStatusHub, ITaskStatusClient> hubContext)
    : ITaskStatusPublisher
{
    public Task PublishTaskStatusAsync(Guid tenantId, Guid companyId, TaskStatusChanged message, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(message.TaskId);
        EnsureStatus(message.Status);
        return Company(tenantId, companyId).TaskStatusChanged(message).WaitAsync(cancellationToken);
    }

    public Task PublishStepStatusAsync(Guid tenantId, Guid companyId, TaskStepStatusChanged message, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(message.TaskId);
        if (message.StepId == Guid.Empty)
        {
            throw new ArgumentException("Step identifier is required for realtime publication.", nameof(message));
        }

        EnsureStatus(message.Status);
        return Company(tenantId, companyId).TaskStepStatusChanged(message).WaitAsync(cancellationToken);
    }

    public Task PublishWorkerStatusAsync(Guid tenantId, Guid companyId, WorkerStatusChanged message, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(message.TaskId);
        if (string.IsNullOrWhiteSpace(message.WorkerId))
        {
            throw new ArgumentException("Worker identifier is required for realtime publication.", nameof(message));
        }

        EnsureStatus(message.Status);
        return Company(tenantId, companyId).WorkerStatusChanged(message).WaitAsync(cancellationToken);
    }

    public Task PublishAttentionRequiredAsync(Guid tenantId, Guid companyId, TaskAttentionRequired message, CancellationToken cancellationToken = default)
    {
        EnsureTaskId(message.TaskId);
        if (string.IsNullOrWhiteSpace(message.Kind) || string.IsNullOrWhiteSpace(message.ReasonCode))
        {
            throw new ArgumentException("Attention kind and reason code are required for realtime publication.", nameof(message));
        }

        return Company(tenantId, companyId).AttentionRequired(message).WaitAsync(cancellationToken);
    }

    private ITaskStatusClient Company(Guid tenantId, Guid companyId)
    {
        if (tenantId == Guid.Empty || companyId == Guid.Empty)
        {
            throw new ArgumentException("Tenant and company identifiers are required for realtime publication.");
        }

        return hubContext.Clients.Group(TaskStatusRealtime.CompanyGroup(tenantId, companyId));
    }

    private static void EnsureTaskId(Guid taskId)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("Task identifier is required for realtime publication.");
        }
    }

    private static void EnsureStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Status is required for realtime publication.");
        }
    }
}
