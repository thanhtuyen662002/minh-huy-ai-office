using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class TaskHistoryMetadata
{
    internal const int MaximumRequestBytes = 65536;
    // The worker uses default JSON escaping: each legitimate UTF16 answer
    // unit can occupy six stored characters, plus bounded catalog metadata.
    internal const int MaximumCheckpointBytes = 1048576;
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    internal static string? Decode(byte[]? bytes, string? text, int maximumBytes)
    {
        try
        {
            if (bytes is not null)
                return bytes.Length is > 0 && bytes.Length <= maximumBytes && bytes.Length % 2 == 0
                    ? StrictUtf16.GetString(bytes) : null;
            if (text is null || text.Length == 0 || text.Length > maximumBytes / 2) return null;
            _ = StrictUtf16.GetByteCount(text);
            return text;
        }
        catch (EncoderFallbackException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    internal static PilotTaskRequestEvent? Request(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = Document(json);
            var root = doc.RootElement;
            if (!Fields(root, "idempotencyKey", "dataSourceId", "question", "maxAttempts")) return null;
            var value = new PilotTaskRequestEvent(root.GetProperty("idempotencyKey").GetString()!,
                root.GetProperty("dataSourceId").GetGuid(), root.GetProperty("question").GetString()!,
                root.GetProperty("maxAttempts").GetInt32());
            value.Validate();
            if (!Canonical(value.Question, CustomerPilotTaskRequest.MaximumQuestionLength)
                || !Canonical(value.IdempotencyKey, PilotTaskSubmissionRequest.MaximumIdempotencyKeyLength)) return null;
            return value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { return null; }
    }

    internal static string Summary(string question)
    {
        const int maximum = 240;
        if (question.Length <= maximum) return question;
        var length = char.IsHighSurrogate(question[maximum - 1]) ? maximum - 1 : maximum;
        return question[..length] + "…";
    }

    internal static TaskHistoryResult? Result(string? json, PilotTaskRequestEvent? request)
    {
        if (json is null || request is null) return null;
        try
        {
            using var doc = Document(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var status = root.GetProperty("status").GetString();
            if (root.GetProperty("dataSourceId").GetGuid() != request.DataSourceId
                || root.GetProperty("questionLength").GetInt32() != request.Question.Length
                || !Canonical(root.GetProperty("logicalName").GetString(), 200)) return null;
            if (status == "connected")
            {
                if (!Fields(root, "status", "dataSourceId", "logicalName", "questionLength", "aiCredits", "evidence")
                    || root.GetProperty("aiCredits").GetInt32() != 0
                    || root.GetProperty("evidence").GetString() != "read-only-connection-probe") return null;
                return new("connection", null, "read-only-connection-probe", null, null, null, null, null);
            }
            if (status != "completed" || !Fields(root, "status", "dataSourceId", "logicalName", "questionLength",
                    "answer", "provider", "model", "usage", "billingStatus", "erpEvidence", "evidence")) return null;
            var answer = root.GetProperty("answer").GetString();
            var provider = root.GetProperty("provider").GetString();
            var model = root.GetProperty("model").GetString();
            if (!Canonical(answer, 64000, allowWhitespace: true) || !Canonical(provider, 200) || !Canonical(model, 200)
                || root.GetProperty("billingStatus").GetString() != "usage-observed-not-settled"
                || root.GetProperty("evidence").GetString() != "ai-provider-reasoning-after-bounded-read-only-erp-catalog") return null;
            var usage = root.GetProperty("usage");
            if (!Fields(usage, "inputTokens", "outputTokens", "totalTokens")) return null;
            var input = usage.GetProperty("inputTokens").GetInt32();
            var output = usage.GetProperty("outputTokens").GetInt32();
            var total = usage.GetProperty("totalTokens").GetInt32();
            if (input < 0 || output < 0 || (long)input + output != total) return null;
            var erp = root.GetProperty("erpEvidence");
            if (!Fields(erp, "databaseName", "tableCount", "sampledTableCount", "topTables")
                || !Canonical(erp.GetProperty("databaseName").GetString(), 128)
                || erp.GetProperty("tableCount").GetInt32() < 0) return null;
            var tables = erp.GetProperty("topTables");
            if (tables.ValueKind != JsonValueKind.Array || tables.GetArrayLength() > 20
                || erp.GetProperty("sampledTableCount").GetInt32() != tables.GetArrayLength()
                || erp.GetProperty("tableCount").GetInt32() < tables.GetArrayLength()) return null;
            foreach (var table in tables.EnumerateArray())
                if (!Fields(table, "schema", "table", "approximateRowCount")
                    || !Canonical(table.GetProperty("schema").GetString(), 128)
                    || !Canonical(table.GetProperty("table").GetString(), 128)
                    || table.GetProperty("approximateRowCount").GetInt64() < 0) return null;
            return new("answer", answer, "ai-provider-reasoning-after-bounded-read-only-erp-catalog",
                provider, model, input, output, total);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return null; }
    }

    private static JsonDocument Document(string json) => JsonDocument.Parse(json,
        new JsonDocumentOptions { MaxDepth = 12, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });

    private static bool Fields(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool Canonical(string? value, int maximum, bool allowWhitespace = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || (!allowWhitespace && value.Trim() != value)) return false;
        try { _ = StrictUtf16.GetByteCount(value); } catch (EncoderFallbackException) { return false; }
        return !value.Any(character => char.IsControl(character) && !(allowWhitespace && character is '\r' or '\n' or '\t'));
    }
}
