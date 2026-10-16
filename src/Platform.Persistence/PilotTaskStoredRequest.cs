using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class PilotTaskStoredRequest
{
    internal static async Task<PilotTaskRequestEvent?> ReadAsync(PlatformDbContext database, AuthorizationContext authority,
        Guid taskId, CancellationToken cancellationToken)
    {
        var stepId = PilotTaskIdentity.ForStep(taskId);
        var query = database.Database.IsSqlServer()
            ? database.Database.SqlQuery<StoredRequest>($"""
                SELECT TenantId,CompanyId,TaskId,StepId,Sequence,EventType,
                  CASE WHEN DATALENGTH(PayloadJson)<={TaskHistoryMetadata.MaximumRequestBytes}
                    THEN CONVERT(varbinary(max),PayloadJson) ELSE NULL END AS Bytes, CAST(NULL AS nvarchar(max)) AS Text
                FROM aioffice.TaskEvents
                """)
            : database.TaskEvents.AsNoTracking().Select(row => new StoredRequest(row.TenantId, row.CompanyId, row.TaskId,
                row.StepId, row.Sequence, row.EventType, null, row.PayloadJson));
        var stored = await query.SingleOrDefaultAsync(row => row.TenantId == authority.TenantId
            && row.CompanyId == authority.CompanyId && row.TaskId == taskId && row.StepId == stepId
            && row.Sequence == 1 && row.EventType == PilotTaskRequestEvent.EventType, cancellationToken);
        if (stored?.EventType != PilotTaskRequestEvent.EventType) return null;
        return TaskHistoryMetadata.Request(TaskHistoryMetadata.Decode(stored.Bytes, stored.Text, TaskHistoryMetadata.MaximumRequestBytes));
    }

    internal static bool Matches(PilotTaskRequestEvent stored, PilotTaskSubmissionRequest requested) =>
        StringComparer.Ordinal.Equals(stored.IdempotencyKey, requested.IdempotencyKey)
        && stored.DataSourceId == requested.DataSourceId && StringComparer.Ordinal.Equals(stored.Question, requested.Question)
        && stored.MaxAttempts == requested.MaxAttempts;

    private sealed record StoredRequest(Guid TenantId, Guid CompanyId, Guid TaskId, Guid? StepId, long Sequence,
        string EventType, byte[]? Bytes, string? Text);
}
