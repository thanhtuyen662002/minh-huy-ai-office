using System.Text.Json;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Pure metadata decoder for the test-only executable. Network access stays
// behind its original owned-resource guard and bounded fixed broker request.
public sealed record OwnedReferenceBrokerStatistics(long ack, long deliver, long consumers)
{
    public bool Matches(long expectedAck, long expectedDeliver, long expectedConsumers) =>
        ack == expectedAck && deliver == expectedDeliver && consumers == expectedConsumers;

    public static OwnedReferenceBrokerStatistics Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            if (body.Length is < 1 or > 32768) throw Unavailable();
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            Unique(root);
            var consumerCount = Counter(root, "consumers");
            if (!root.TryGetProperty("message_stats", out var statistics)) return new(0, 0, consumerCount);
            Unique(statistics);
            return new(Counter(statistics, "ack"), Counter(statistics, "deliver"), consumerCount);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw Unavailable(); }
    }
    private static long Counter(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        if (!value.TryGetInt64(out var number) || number < 0) throw Unavailable();
        return number;
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Unavailable();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!fields.Add(property.Name)) throw Unavailable();
    }
    private static InvalidOperationException Unavailable() => new("Owned reference statistics unavailable.");
}
