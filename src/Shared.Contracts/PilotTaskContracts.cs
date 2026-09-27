using System.Security.Cryptography;
using System.Text;

namespace MinhHuy.AIOffice.Shared.Contracts;

/// <summary>
/// Customer-facing input for the first read-only pilot task. Tenant, company, user and
/// idempotency authority are deliberately absent: the API obtains those values from the
/// authenticated request context and the idempotency header.
/// </summary>
public sealed record CustomerPilotTaskRequest(
    Guid DataSourceId,
    string Question)
{
    public const int MaximumQuestionLength = 4000;

    public void Validate()
    {
        if (DataSourceId == Guid.Empty)
        {
            throw new ArgumentException("Data source identity is required.", nameof(DataSourceId));
        }

        if (string.IsNullOrWhiteSpace(Question)
            || !string.Equals(Question, Question.Trim(), StringComparison.Ordinal)
            || Question.Length > MaximumQuestionLength
            || Question.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Question must be canonical text of at most {MaximumQuestionLength} characters.",
                nameof(Question));
        }
    }
}

/// <summary>
/// The deliberately small task request used by the first executable pilot slice. The request
/// contains no tenant, company or user fields: those values must come from the authenticated
/// server-side <see cref="AuthorizationContext"/>.
/// </summary>
public sealed record PilotTaskSubmissionRequest(
    string IdempotencyKey,
    Guid DataSourceId,
    string Question,
    int MaxAttempts = 3)
{
    public const int MaximumIdempotencyKeyLength = 200;
    public const int MaximumAttempts = 16;

    public void Validate()
    {
        ValidateIdempotencyKey(IdempotencyKey);
        new CustomerPilotTaskRequest(DataSourceId, Question).Validate();

        if (MaxAttempts is < 1 or > MaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttempts),
                $"Max attempts must be between 1 and {MaximumAttempts}.");
        }
    }

    public static void ValidateIdempotencyKey(string value)
    {
        const string parameterName = nameof(IdempotencyKey);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > MaximumIdempotencyKeyLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must be canonical text of at most {MaximumIdempotencyKeyLength} characters.",
                parameterName);
        }
    }
}

/// <summary>
/// Durable request evidence stored in the task event stream. It is intentionally separate from
/// the HTTP request so replay compares the original server-accepted values rather than trusting a
/// later caller payload.
/// </summary>
public sealed record PilotTaskRequestEvent(
    string IdempotencyKey,
    Guid DataSourceId,
    string Question,
    int MaxAttempts)
{
    public const string EventType = "pilot.task.requested";

    public void Validate()
    {
        new PilotTaskSubmissionRequest(IdempotencyKey, DataSourceId, Question, MaxAttempts).Validate();
    }
}

public sealed record PilotTaskSubmissionAccepted(
    Guid TaskId,
    Guid StepId,
    string IdempotencyKey,
    TaskExecutionStatus TaskStatus,
    WorkDispatchState DispatchState,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Stable API result for a newly accepted or idempotently replayed pilot task. MessageId is
/// deterministic for the task step and is the identity preserved by the worker outbox publisher.
/// </summary>
public sealed record CustomerPilotTaskSubmissionResult(
    Guid TaskId,
    Guid StepId,
    Guid MessageId,
    TaskExecutionStatus Status)
{
    public string? IdempotencyKey { get; init; }

    public WorkDispatchState DispatchState { get; init; } = WorkDispatchState.Pending;

    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// Projection returned to an authorized task owner. Result payloads are opaque JSON produced by
/// the trusted worker and are never selected by browser authority fields.
/// </summary>
public sealed record PilotTaskStatusSnapshot(
    Guid TaskId,
    Guid StepId,
    TaskExecutionStatus TaskStatus,
    TaskStepStatus StepStatus,
    WorkDispatchState DispatchState,
    int Attempt,
    DateTimeOffset UpdatedAtUtc,
    string? ResultPayloadJson,
    string? FailureReason);

/// <summary>
/// Stable identities for the pilot's one read-only step. Deterministic identities let an HTTP
/// retry find the original durable task without adding a process-local idempotency cache or
/// trusting a caller-selected task identifier.
/// </summary>
public static class PilotTaskIdentity
{
    public const string StepKey = "pilot.readonly-data-source-v1";

    public static Guid ForTask(AuthorizationContext authority, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority = AuthorizationContext.Create(authority.TenantId, authority.CompanyId, authority.UserId);
        // Validate only the key here. The data-source/question fields are deliberately not part of
        // the durable identity; reusing a key with a different request is detected by the event.
        PilotTaskSubmissionRequest.ValidateIdempotencyKey(idempotencyKey);
        return FromParts(
            "pilot-task-v1",
            authority.TenantId.ToString("N"),
            authority.CompanyId.ToString("N"),
            authority.UserId.ToString("N"),
            idempotencyKey);
    }

    public static Guid ForStep(Guid taskId)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("Task identity is required.", nameof(taskId));
        }

        return FromParts("pilot-step-v1", taskId.ToString("N"), StepKey);
    }

    public static Guid ForMessage(Guid taskId, Guid stepId)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("Task identity is required.", nameof(taskId));
        }

        if (stepId == Guid.Empty)
        {
            throw new ArgumentException("Step identity is required.", nameof(stepId));
        }

        return FromParts("pilot-message-v1", taskId.ToString("N"), stepId.ToString("N"), "1");
    }

    private static Guid FromParts(params string[] parts)
    {
        var payload = string.Join('\u001f', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        // Set RFC 4122 version/variant bits so the deterministic values remain recognizable as
        // UUIDs while retaining all 122 bits of identity entropy needed by this boundary.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var value = new Guid(hash.AsSpan(0, 16));
        return value == Guid.Empty ? Guid.Parse("00000000-0000-5000-8000-000000000001") : value;
    }
}
