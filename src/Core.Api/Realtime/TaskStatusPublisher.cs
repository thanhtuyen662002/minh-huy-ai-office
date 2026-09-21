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
    public Task PublishTaskStatusAsync(Guid tenantId, Guid companyId, TaskStatusChanged message, CancellationToken cancellationToken = default) =>
        Company(tenantId, companyId).TaskStatusChanged(message);

    public Task PublishStepStatusAsync(Guid tenantId, Guid companyId, TaskStepStatusChanged message, CancellationToken cancellationToken = default) =>
        Company(tenantId, companyId).TaskStepStatusChanged(message);

    public Task PublishWorkerStatusAsync(Guid tenantId, Guid companyId, WorkerStatusChanged message, CancellationToken cancellationToken = default) =>
        Company(tenantId, companyId).WorkerStatusChanged(message);

    public Task PublishAttentionRequiredAsync(Guid tenantId, Guid companyId, TaskAttentionRequired message, CancellationToken cancellationToken = default) =>
        Company(tenantId, companyId).AttentionRequired(message);

    private ITaskStatusClient Company(Guid tenantId, Guid companyId)
    {
        if (tenantId == Guid.Empty || companyId == Guid.Empty)
        {
            throw new ArgumentException("Tenant and company identifiers are required for realtime publication.");
        }

        return hubContext.Clients.Group(TaskStatusRealtime.CompanyGroup(tenantId, companyId));
    }
}
