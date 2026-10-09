using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>Read-only historical owner policy. Source grants admit new execution, not rediscovery of an owner's archive.</summary>
public sealed class TaskHistoryService(PlatformDbContext database, IAuthorizationDirectory directory)
{
    public const int MaximumOffset = 10000;
    public const int MaximumLimit = 100;

    public async Task<TaskHistoryPage> ListAsync(AuthorizationContext authority, int offset = 0, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (offset is < 0 or > MaximumOffset || limit is < 1 or > MaximumLimit) throw new ArgumentOutOfRangeException(nameof(offset));
        await RequireAuthorityAsync(authority, cancellationToken);
        var tasks = await OwnedTasks(authority).OrderByDescending(task => task.CreatedAtUtc).ThenByDescending(task => task.Id)
            .Skip(offset).Take(limit + 1)
            .ToArrayAsync(cancellationToken);
        var selected = tasks.Take(limit).ToArray();
        var requests = await RequestsAsync(authority, selected.Select(task => task.Id).ToArray(), cancellationToken);
        var items = selected.Select(task => Item(task, requests.GetValueOrDefault(task.Id))).ToArray();
        await RequireAuthorityAsync(authority, cancellationToken);
        return new(authority.CompanyId, items, offset, limit, tasks.Length > limit);
    }

    public async Task<TaskHistoryDetail?> GetAsync(AuthorizationContext authority, Guid taskId,
        CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Task identity is required.", nameof(taskId));
        await RequireAuthorityAsync(authority, cancellationToken);
        var task = await OwnedTasks(authority).Where(task => task.Id == taskId).SingleOrDefaultAsync(cancellationToken);
        if (task is null) { await RequireAuthorityAsync(authority, cancellationToken); return null; }
        var requests = await RequestsAsync(authority, [taskId], cancellationToken);
        var request = requests.GetValueOrDefault(taskId);
        var item = Item(task, request);
        TaskHistoryResult? result = null;
        if (item.Status == TaskExecutionStatus.Completed && !item.MetadataUnavailable)
        {
            var stepId = PilotTaskIdentity.ForStep(taskId);
            var query = Checkpoints().Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                && row.TaskId == taskId && row.StepId == stepId).OrderByDescending(row => row.Version);
            var row = await query.FirstOrDefaultAsync(cancellationToken);
            if (row is { Version: > 0 }) result = TaskHistoryMetadata.Result(
                TaskHistoryMetadata.Decode(row.Bytes, row.Text, TaskHistoryMetadata.MaximumCheckpointBytes), request);
        }
        await RequireAuthorityAsync(authority, cancellationToken);
        return new(authority.CompanyId, item, result, item.Status == TaskExecutionStatus.Completed && result is null);
    }

    private IQueryable<ArchiveTask> OwnedTasks(AuthorizationContext authority)
    {
        // Unknown persisted status must not make EF's enum converter invent or
        // reject a whole page. Preserve the bounded task row, label it unavailable.
        var tasks = database.Database.IsSqlServer()
            ? database.Database.SqlQuery<ArchiveTask>($"""
                SELECT TenantId,CompanyId,CreatedByUserId,Id,Status,CreatedAtUtc,UpdatedAtUtc FROM aioffice.Tasks
                """)
            : database.Tasks.AsNoTracking().Select(task => new ArchiveTask(task.TenantId, task.CompanyId,
                task.CreatedByUserId, task.Id, task.Status.ToString(), task.CreatedAtUtc, task.UpdatedAtUtc));
        return tasks.Where(task => task.TenantId == authority.TenantId && task.CompanyId == authority.CompanyId && task.CreatedByUserId == authority.UserId);
    }

    private async Task RequireAuthorityAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var current = await directory.ResolveAsync(authority, cancellationToken);
        if (current?.Context != authority) throw new UnauthorizedAccessException("An active company membership is required.");
    }

    private async Task<Dictionary<Guid, PilotTaskRequestEvent?>> RequestsAsync(AuthorizationContext authority, Guid[] ids,
        CancellationToken cancellationToken)
    {
        if (ids.Length == 0) return [];
        // Scope/id/first-event predicates are translated to SQL. CASE bounds the
        // materialized bytes; malformed nvarchar is never repaired by SqlClient.
        var query = database.Database.IsSqlServer()
            ? database.Database.SqlQuery<StoredRequest>($"""
                SELECT TenantId,CompanyId,TaskId,Sequence,EventType,
                  CASE WHEN DATALENGTH(PayloadJson) <= {TaskHistoryMetadata.MaximumRequestBytes}
                    THEN CONVERT(varbinary(max),PayloadJson) ELSE NULL END AS Bytes, CAST(NULL AS nvarchar(max)) AS Text
                FROM aioffice.TaskEvents
                """)
            : database.TaskEvents.AsNoTracking().Select(row => new StoredRequest(row.TenantId, row.CompanyId, row.TaskId,
                row.Sequence, row.EventType, null, row.PayloadJson));
        var rows = await query.Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
            && ids.Contains(row.TaskId) && row.Sequence == 1 && row.EventType == PilotTaskRequestEvent.EventType)
            .Take(MaximumLimit).ToArrayAsync(cancellationToken);
        return rows.ToDictionary(row => row.TaskId, row => row.EventType == PilotTaskRequestEvent.EventType
            ? TaskHistoryMetadata.Request(TaskHistoryMetadata.Decode(row.Bytes, row.Text, TaskHistoryMetadata.MaximumRequestBytes)) : null);
    }

    private IQueryable<StoredCheckpoint> Checkpoints() => database.Database.IsSqlServer()
        ? database.Database.SqlQuery<StoredCheckpoint>($"""
            SELECT TenantId,CompanyId,TaskId,StepId,Version,
              CASE WHEN DATALENGTH(PayloadJson) <= {TaskHistoryMetadata.MaximumCheckpointBytes}
                THEN CONVERT(varbinary(max),PayloadJson) ELSE NULL END AS Bytes, CAST(NULL AS nvarchar(max)) AS Text
            FROM aioffice.TaskCheckpoints
            """)
        : database.TaskCheckpoints.AsNoTracking().Select(row => new StoredCheckpoint(row.TenantId, row.CompanyId,
            row.TaskId, row.StepId, row.Version, null, row.PayloadJson));

    private static TaskHistoryItem Item(ArchiveTask task, PilotTaskRequestEvent? request)
    {
        var status = Enum.TryParse<TaskExecutionStatus>(task.Status, out var parsed) && Enum.IsDefined(parsed)
            && parsed.ToString() == task.Status ? parsed : (TaskExecutionStatus?)null;
        var malformed = status is null || task.CreatedAtUtc == default || task.UpdatedAtUtc < task.CreatedAtUtc || request is null;
        return new(task.Id, status, task.CreatedAtUtc, task.UpdatedAtUtc,
            request is null ? null : TaskHistoryMetadata.Summary(request.Question), malformed);
    }

    private sealed record ArchiveTask(Guid TenantId, Guid CompanyId, Guid CreatedByUserId, Guid Id, string Status,
        DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
    private sealed record StoredRequest(Guid TenantId, Guid CompanyId, Guid TaskId, long Sequence, string EventType, byte[]? Bytes, string? Text);
    private sealed record StoredCheckpoint(Guid TenantId, Guid CompanyId, Guid TaskId, Guid StepId, long Version, byte[]? Bytes, string? Text);
}
