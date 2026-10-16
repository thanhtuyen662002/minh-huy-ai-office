using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

// A broker hint identifies an already committed SQL outbox/revision. It grants
// no service capability, portal identity, destination or source-content access.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupIngressDispatchReference(int Version, GroupScope Source,
    Guid EventId, Guid MessageId, long Revision, long CommittedSequence)
{
    public const string MessageType = "aioffice-group-ingress-committed-v1";
    public const int MaximumBytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    public void Validate()
    {
        if (Version != 1 || Source is null || EventId == Guid.Empty || MessageId == Guid.Empty || Revision <= 0 || CommittedSequence <= 0)
            throw Refused();
        Source.Validate();
    }

    public byte[] ToBytes()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (bytes.Length > MaximumBytes) throw Refused();
        return bytes;
    }

    public static GroupIngressDispatchReference Parse(ReadOnlySpan<byte> body, string? messageType, string? messageId, string? contentType)
    {
        try
        {
            if (body.Length is < 1 or > MaximumBytes || messageType != MessageType || contentType != "application/json" ||
                !Guid.TryParseExact(messageId, "N", out var eventId) || eventId == Guid.Empty || messageId != eventId.ToString("N")) throw Refused();
            _ = StrictUtf8.GetCharCount(body);
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 4 });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName &&
                    (objects.Count == 0 || !objects.Peek().Add(reader.GetString()!))) throw Refused();
            }
            var reference = JsonSerializer.Deserialize<GroupIngressDispatchReference>(body, Options) ?? throw Refused();
            reference.Validate();
            if (reference.EventId != eventId) throw Refused();
            return reference;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            throw Refused();
        }
    }

    private static InvalidOperationException Refused() => new("Group dispatch reference is not available.");
}

public interface IGroupIngressReferencePublisher
{
    Task PublishAsync(GroupIngressDispatchReference reference, CancellationToken cancellationToken);
}
