using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record CustomerRequestItemInput(string Content, string Priority, string Kind);
public sealed record CustomerRequestEvidenceInput(string Quote, string SourceMessageId, string SenderName, DateTimeOffset MessageTimestamp);
public sealed record CustomerRequestInput(string IdempotencyKey, string SourceGroupExternalId, string SourceGroupName,
    string CustomerName, string ReportedBy, string Summary, DateTimeOffset SourceWindowFrom, DateTimeOffset SourceWindowTo,
    CustomerRequestItemInput[] Items, CustomerRequestEvidenceInput[] Evidence, string[] OpenQuestions,
    string? CustomerDesiredDueText = null);

public sealed record CustomerRequestReceipt(string Code, Guid Id, string Status);
public sealed record CustomerRequestStatusInput(string Code, string Status, string ActorName, string ActorExternalId, string SourceMessageId);
public sealed record CustomerRequestStatusReceipt(string Code, string Status, string PreviousStatus, bool Changed);
public sealed record CustomerRequestListItem(string Code, Guid Id, string Status, string Origin, string CustomerName,
    string Summary, string SourceGroupExternalId, string SourceGroupName, Guid? AssigneeUserId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RowVersion);
public sealed record CustomerRequestPage(IReadOnlyList<CustomerRequestListItem> Items, int Offset, int Limit, bool HasMore);
public sealed record CustomerRequestEventView(string EventType, string ActorType, Guid ActorId, string? OldValue, string? NewValue, DateTimeOffset Timestamp,
    string? ActorName = null);
public sealed record CustomerRequestDetail(CustomerRequestListItem Request, string ReportedBy, string? CustomerDesiredDueText,
    DateTimeOffset SourceWindowFrom, DateTimeOffset SourceWindowTo, IReadOnlyList<CustomerRequestItemInput> Items,
    IReadOnlyList<CustomerRequestEvidenceInput> Evidence, IReadOnlyList<string> OpenQuestions, IReadOnlyList<CustomerRequestEventView> Events);

// Presence matters: omitted assignee keeps the current value; explicit null unassigns.
public sealed record CustomerRequestPatch(string RowVersion, string? Status, Guid? AssigneeUserId, bool HasAssignee);
public sealed class CustomerRequestNotFoundException() : Exception("Customer request is unavailable.");
public sealed class CustomerRequestConflictException() : Exception("Customer request changed. Reload it before updating.");

public static class CustomerRequestJson
{
    public const int MaximumBodyBytes = 65536;
    public const int MaximumPatchBytes = 2048;
    public const int MaximumStatusBytes = 2048;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };

    public static CustomerRequestInput ReadInput(ReadOnlyMemory<byte> bytes)
    {
        using var document = Document(bytes, MaximumBodyBytes);
        var input = document.Deserialize<CustomerRequestInput>(Options) ?? throw Invalid();
        Text(input.IdempotencyKey, 200, opaque: true);
        Text(input.SourceGroupExternalId, 200, opaque: true);
        Text(input.SourceGroupName, 200); Text(input.CustomerName, 200); Text(input.ReportedBy, 200);
        Text(input.Summary, 8000);
        if (input.CustomerDesiredDueText is not null) Text(input.CustomerDesiredDueText, 1000);
        if (input.SourceWindowFrom.Offset != TimeSpan.Zero || input.SourceWindowTo.Offset != TimeSpan.Zero ||
            input.SourceWindowFrom == default || input.SourceWindowTo == default || input.SourceWindowFrom > input.SourceWindowTo ||
            input.Items is null || input.Items.Length is < 1 or > 50 || input.Evidence is null || input.Evidence.Length > 100 ||
            input.OpenQuestions is null || input.OpenQuestions.Length > 50) throw Invalid();
        foreach (var item in input.Items)
        {
            if (item is null || item.Priority is not ("high" or "medium" or "low") || item.Kind is not ("bug" or "feature" or "question" or "data")) throw Invalid();
            Text(item.Content, 4000);
        }
        foreach (var evidence in input.Evidence)
        {
            if (evidence is null || evidence.MessageTimestamp == default || evidence.MessageTimestamp.Offset != TimeSpan.Zero) throw Invalid();
            Text(evidence.Quote, 2000); Text(evidence.SourceMessageId, 200, opaque: true); Text(evidence.SenderName, 200);
        }
        foreach (var question in input.OpenQuestions) Text(question, 1000);
        return input;
    }

    public static CustomerRequestStatusInput ReadStatus(ReadOnlyMemory<byte> bytes)
    {
        using var document = Document(bytes, MaximumStatusBytes);
        var input = document.Deserialize<CustomerRequestStatusInput>(Options) ?? throw Invalid();
        Text(input.Code, 24, opaque: true);
        if (!IsStatus(input.Status)) throw Invalid();
        Text(input.ActorName, 200); Text(input.ActorExternalId, 200, opaque: true); Text(input.SourceMessageId, 200, opaque: true);
        return input;
    }

    public static CustomerRequestPatch ReadPatch(ReadOnlyMemory<byte> bytes)
    {
        using var document = Document(bytes, MaximumPatchBytes);
        var root = document.RootElement;
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("rowVersion" or "status" or "assigneeUserId")) throw Invalid();
        if (!root.TryGetProperty("rowVersion", out var version) || version.ValueKind != JsonValueKind.String) throw Invalid();
        var value = version.GetString()!;
        _ = Version(value);
        string? status = null;
        if (root.TryGetProperty("status", out var state))
        {
            if (state.ValueKind != JsonValueKind.String || !IsStatus(state.GetString())) throw Invalid();
            status = state.GetString();
        }
        Guid? assignee = null;
        var hasAssignee = root.TryGetProperty("assigneeUserId", out var user);
        if (hasAssignee && user.ValueKind != JsonValueKind.Null)
        {
            if (user.ValueKind != JsonValueKind.String || !user.TryGetGuid(out var id) || id == Guid.Empty) throw Invalid();
            assignee = id;
        }
        if (status is null && !hasAssignee) throw Invalid();
        return new(value, status, assignee, hasAssignee);
    }

    public static byte[] Version(string value)
    {
        if (value is null || value.Length != 12) throw Invalid();
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 8 && Convert.ToBase64String(bytes) == value ? bytes : throw Invalid();
        }
        catch (FormatException) { throw Invalid(); }
    }

    public static bool IsStatus(string? value) => value is "new" or "needs_clarification" or "in_progress" or "done" or "rejected";

    public static void Text(string? value, int maximum, bool opaque = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0') ||
            (opaque && (value.Trim() != value || value.Any(char.IsControl)))) throw Invalid();
        try { _ = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw Invalid(); }
    }

    private static JsonDocument Document(ReadOnlyMemory<byte> bytes, int maximum)
    {
        if (bytes.Length is < 1 || bytes.Length > maximum) throw Invalid();
        _ = Utf8.GetCharCount(bytes.Span);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            Unique(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static void Unique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                Unique(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) Unique(item);
    }

    private static ArgumentException Invalid() => new("Invalid customer request.");
}
