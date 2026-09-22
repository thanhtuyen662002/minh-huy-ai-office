using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MinhHuy.AIOffice.Core.Api.Authorization;

namespace MinhHuy.AIOffice.Core.Api.Realtime;

public static class TaskStatusRealtime
{
    public const string HubPath = "/hubs/task-status";

    public static string CompanyGroup(Guid tenantId, Guid companyId)
    {
        if (tenantId == Guid.Empty || companyId == Guid.Empty)
        {
            throw new ArgumentException("Tenant and company identifiers are required for realtime group membership.");
        }

        return $"tenant:{tenantId:D}:company:{companyId:D}";
    }
}

public sealed record TaskStatusChanged(
    Guid TaskId,
    string Status,
    DateTimeOffset OccurredAtUtc);

public sealed record TaskStepStatusChanged(
    Guid TaskId,
    Guid StepId,
    string Status,
    DateTimeOffset OccurredAtUtc);

public sealed record WorkerStatusChanged(
    Guid TaskId,
    string WorkerId,
    string Status,
    DateTimeOffset OccurredAtUtc);

public sealed record TaskAttentionRequired(
    Guid TaskId,
    string Kind,
    string ReasonCode,
    DateTimeOffset OccurredAtUtc);

public interface ITaskStatusClient
{
    Task TaskStatusChanged(TaskStatusChanged message);
    Task TaskStepStatusChanged(TaskStepStatusChanged message);
    Task WorkerStatusChanged(WorkerStatusChanged message);
    Task AttentionRequired(TaskAttentionRequired message);
}

[Authorize]
public sealed class TaskStatusHub(IRequestAuthorizationContextAccessor authorizationContext)
    : Hub<ITaskStatusClient>
{
    public override async Task OnConnectedAsync()
    {
        var current = authorizationContext.Current?.Context;
        if (current is null)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            TaskStatusRealtime.CompanyGroup(current.TenantId, current.CompanyId));

        await base.OnConnectedAsync();
    }
}
