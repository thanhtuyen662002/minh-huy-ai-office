namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record ToolExecutionAuditEntry(
    Guid AuditId,
    Guid TenantId,
    Guid CompanyId,
    Guid UserId,
    Guid TaskId,
    string Resource,
    string Action,
    ToolRiskLevel Risk,
    bool Authorized,
    string DecisionReason,
    DateTimeOffset OccurredAtUtc,
    string? ExecutionId = null);

/// <summary>
/// Append-only boundary for recording authorization and execution evidence.
/// Implementations may persist to durable storage, but must never expose update/delete
/// operations through this contract. Secret values and tool payloads do not belong here.
/// </summary>
public interface IToolExecutionAuditSink
{
    Task AppendAsync(ToolExecutionAuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed class ToolExecutionAuditService(IToolExecutionAuditSink sink)
{
    public Task RecordAsync(
        ToolAuthorizationRequest request,
        ToolAuthorizationDecision decision,
        DateTimeOffset occurredAtUtc,
        string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);

        if (request.TenantId == Guid.Empty || request.CompanyId == Guid.Empty ||
            request.UserId == Guid.Empty || request.TaskId == Guid.Empty)
        {
            throw new ArgumentException("Audit authority must be complete.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Resource) || string.IsNullOrWhiteSpace(request.Action))
        {
            throw new ArgumentException("Audit scope must be complete.", nameof(request));
        }

        var entry = new ToolExecutionAuditEntry(
            Guid.NewGuid(), request.TenantId, request.CompanyId, request.UserId, request.TaskId,
            request.Resource, request.Action, request.Risk, decision.Allowed, decision.Reason,
            occurredAtUtc, executionId);

        return sink.AppendAsync(entry, cancellationToken);
    }
}
