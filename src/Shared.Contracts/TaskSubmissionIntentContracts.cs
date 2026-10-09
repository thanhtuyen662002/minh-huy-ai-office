using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MinhHuy.AIOffice.Shared.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskSubmissionPrepareRequest(
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] Guid DataSourceId,
    [property: JsonRequired] string Question)
{
    public void Validate()
    {
        TaskSubmissionIntentIdentity.RequireOperation(OperationId);
        new CustomerPilotTaskRequest(DataSourceId, Question).Validate();
        // Strict encoding must precede hashing. An unpaired surrogate cannot
        // become a different, executable request through replacement encoding.
        try { _ = TaskSubmissionIntentIdentity.StrictUtf8.GetByteCount(Question); }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("Question must contain valid Unicode scalar text.", nameof(Question), error);
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskSubmissionExecuteRequest([property: JsonRequired] string InputFingerprint)
{
    public void Validate()
    {
        if (!TaskSubmissionIntentIdentity.IsFingerprint(InputFingerprint))
            throw new ArgumentException("A canonical input fingerprint is required.", nameof(InputFingerprint));
    }
}

public enum TaskSubmissionIntentState
{
    Prepared,
    Accepted,
    Expired,
    Unavailable
}

public sealed record TaskSubmissionAcceptedReceipt(
    Guid CompanyId,
    Guid OperationId,
    Guid DataSourceId,
    string InputFingerprint,
    Guid TaskId,
    Guid StepId,
    Guid MessageId,
    TaskExecutionStatus Status,
    WorkDispatchState DispatchState,
    DateTimeOffset CreatedAtUtc);

public sealed record TaskSubmissionIntentDetail(
    Guid CompanyId,
    Guid OperationId,
    TaskSubmissionIntentState State,
    Guid? DataSourceId,
    string? Question,
    string? InputFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    TaskSubmissionAcceptedReceipt? Accepted);

public sealed record TaskSubmissionIntentPage(
    Guid CompanyId,
    IReadOnlyList<TaskSubmissionIntentDetail> Items,
    int Offset,
    int Limit,
    bool HasMore);

/// <summary>
/// Binds one immutable input to an operation without changing the pilot's
/// existing authority-scoped task/step/message identity algorithm.
/// </summary>
public static class TaskSubmissionIntentIdentity
{
    public const int InputVersion = 1;
    public const int MaximumActiveIntents = 100;
    public const int MaximumPageSize = 25;
    public const int MaximumOffset = 10000;
    public const int MaximumAttempts = 3;
    public static readonly TimeSpan PreparationLifetime = TimeSpan.FromHours(24);
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string Domain = "aioffice-task-intent-v1";

    public static void RequireOperation(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("An operation identity is required.", nameof(operationId));
    }

    public static string IdempotencyKey(Guid operationId)
    {
        RequireOperation(operationId);
        return "web-intent-v1-" + operationId.ToString("N");
    }

    public static string Fingerprint(TaskSubmissionPrepareRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var prefix = Encoding.ASCII.GetBytes(Domain + "\0" + request.DataSourceId.ToString("N"));
        var question = StrictUtf8.GetBytes(request.Question);
        var bytes = new byte[prefix.Length + 8 + question.Length];
        prefix.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(prefix.Length, 4), MaximumAttempts);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(prefix.Length + 4, 4), (uint)question.Length);
        question.CopyTo(bytes, prefix.Length + 8);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static bool IsFingerprint(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
