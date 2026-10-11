using System.Net;
using System.Text;
using System.Text.Json;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>Host-owned byte/schema profile. This does not validate business facts or model token budgets.</summary>
public static class StructuredResponsesPolicy
{
    public const int InputByteLimit = 128_000;
    public const int SchemaByteLimit = 32_000;
    public const int OutputByteLimit = 64_000;
    public const int EntityByteLimit = 262_144;
    public const int JsonDepthLimit = 32;
    public const int SchemaDepthLimit = 10;
    public const int OperationLimit = 4;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static HttpClient CreateProductionClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    { Timeout = TimeSpan.FromSeconds(120) };

    internal static void ValidateOptions(OpenAiCompatibleResponsesOptions options)
    {
        var endpoint = options.Endpoint;
        if (!endpoint.IsAbsoluteUri
            || (endpoint.Scheme != Uri.UriSchemeHttps
                && !(options.AllowInsecureLoopback && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !Canonical(options.Model, 256) || !Canonical(options.ProviderId, 128)
            || string.IsNullOrWhiteSpace(options.AuthorizationHeader)
            || options.AuthorizationHeader.Length > 4096 || options.AuthorizationHeader.Any(char.IsControl)
            || options.MaxOutputTokens is < 1 or > 32_768
            || options.StructuredTimeout <= TimeSpan.Zero || options.StructuredTimeout > TimeSpan.FromSeconds(60))
        {
            throw new AiProviderExecutionException("AI structured configuration is invalid.", false);
        }
    }

    internal static bool Canonical(string value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl) && ValidUnicode(value);

    private static bool ValidUnicode(string value)
    {
        try { StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static byte[] BoundedText(string value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || StrictUtf8.GetByteCount(value) > limit)
        {
            throw new InvalidOperationException("Structured text exceeds its profile.");
        }
        return StrictUtf8.GetBytes(value);
    }

    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        // Check decoded names and every escaped UTF16 string before DOM property lookup can choose a duplicate.
        var reader = new Utf8JsonReader(bytes.Span, new JsonReaderOptions { MaxDepth = JsonDepthLimit });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                VerifyEscapedUnicode(reader.ValueSpan);
                var decoded = reader.GetString()!;
                StrictUtf8.GetByteCount(decoded);
                if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(decoded))
                    throw new JsonException("Duplicate JSON name.");
            }
        }
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = JsonDepthLimit });
    }

    private static void VerifyEscapedUnicode(ReadOnlySpan<byte> raw)
    {
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] != (byte)'\\') continue;
            if (++index >= raw.Length) throw new JsonException();
            if (raw[index] != (byte)'u') continue;
            var code = ReadHex(raw, index + 1);
            index += 4;
            if (code is >= 0xDC00 and <= 0xDFFF) throw new JsonException("Unpaired Unicode.");
            if (code is not (>= 0xD800 and <= 0xDBFF)) continue;
            if (index + 6 >= raw.Length || raw[index + 1] != (byte)'\\' || raw[index + 2] != (byte)'u')
                throw new JsonException("Unpaired Unicode.");
            var low = ReadHex(raw, index + 3);
            if (low is < 0xDC00 or > 0xDFFF) throw new JsonException("Unpaired Unicode.");
            index += 6;
        }
    }

    private static int ReadHex(ReadOnlySpan<byte> raw, int start)
    {
        if (start + 4 > raw.Length) throw new JsonException();
        var value = 0;
        foreach (var character in raw.Slice(start, 4))
        {
            var digit = character switch
            {
                >= (byte)'0' and <= (byte)'9' => character - '0',
                >= (byte)'a' and <= (byte)'f' => character - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => character - 'A' + 10,
                _ => -1
            };
            if (digit < 0) throw new JsonException();
            value = (value << 4) | digit;
        }
        return value;
    }

    internal static void ValidateSchema(JsonElement schema)
    {
        var totalEnumValues = 0;
        ValidateSchemaNode(schema, 1, ref totalEnumValues);
    }

    private static void ValidateSchemaNode(JsonElement schema, int depth, ref int totalEnumValues)
    {
        if (schema.ValueKind != JsonValueKind.Object || depth > SchemaDepthLimit)
            throw new JsonException("Schema shape is unsupported.");
        foreach (var field in schema.EnumerateObject())
        {
            if (field.Name is not ("type" or "properties" or "required" or "additionalProperties"
                or "items" or "enum" or "description" or "title")) throw new JsonException("Unsupported schema keyword.");
            if (field.Name is "description" or "title" && field.Value.ValueKind != JsonValueKind.String)
                throw new JsonException();
        }
        var type = SchemaType(schema, out _);
        if (depth == 1 && (type != "object" || schema.GetProperty("type").ValueKind != JsonValueKind.String)) throw new JsonException("Schema root must be an object.");
        if (type == "object")
        {
            if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object
                || !schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array
                || !schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False
                || schema.TryGetProperty("items", out _)) throw new JsonException();
            var names = properties.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
            var requiredNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in required.EnumerateArray())
                if (item.ValueKind != JsonValueKind.String || !requiredNames.Add(item.GetString()!)) throw new JsonException();
            if (!names.SetEquals(requiredNames)) throw new JsonException("All properties must be required.");
            foreach (var property in properties.EnumerateObject()) ValidateSchemaNode(property.Value, depth + 1, ref totalEnumValues);
        }
        else if (type == "array")
        {
            if (!schema.TryGetProperty("items", out var items)) throw new JsonException();
            ValidateSchemaNode(items, depth + 1, ref totalEnumValues);
        }
        if (type != "object" && (schema.TryGetProperty("properties", out _) || schema.TryGetProperty("required", out _)
            || schema.TryGetProperty("additionalProperties", out _))) throw new JsonException();
        if (type != "array" && schema.TryGetProperty("items", out _)) throw new JsonException();
        if (schema.TryGetProperty("enum", out var values))
        {
            if (type is "object" or "array" || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
                throw new JsonException();
            totalEnumValues = checked(totalEnumValues + values.GetArrayLength());
            if (totalEnumValues > 1000) throw new JsonException("Schema enum profile exceeded.");
            if (type == "string" && values.GetArrayLength() > 250)
            {
                var characters = 0;
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String) characters = checked(characters + value.GetString()!.Length);
                }
                if (characters > 15000) throw new JsonException("Schema string enum profile exceeded.");
            }
            var unique = new List<JsonElement>();
            foreach (var value in values.EnumerateArray())
            {
                if (!MatchesType(schema, value) || unique.Any(previous => JsonElement.DeepEquals(previous, value))) throw new JsonException();
                unique.Add(value);
            }
        }
    }

    private static string SchemaType(JsonElement schema, out bool nullable)
    {
        nullable = false;
        if (!schema.TryGetProperty("type", out var field)) throw new JsonException();
        string? type;
        if (field.ValueKind == JsonValueKind.String) type = field.GetString();
        else if (field.ValueKind == JsonValueKind.Array && field.GetArrayLength() == 2)
        {
            var values = field.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null).ToArray();
            if (values.Count(value => value == "null") != 1) throw new JsonException();
            nullable = true;
            type = values.Single(value => value != "null");
        }
        else throw new JsonException();
        if (type is not ("object" or "array" or "string" or "integer" or "number" or "boolean" or "null")) throw new JsonException();
        return type;
    }

    private static bool MatchesType(JsonElement schema, JsonElement value)
    {
        var type = SchemaType(schema, out var nullable);
        if (nullable && value.ValueKind == JsonValueKind.Null) return true;
        return type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
    }

    internal static void ValidateOutput(JsonElement schema, JsonElement value)
    {
        if (!MatchesType(schema, value)) throw new JsonException("Output type does not match schema.");
        if (schema.TryGetProperty("enum", out var choices)
            && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value))) throw new JsonException();
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.GetProperty("properties");
            if (value.EnumerateObject().Count() != properties.EnumerateObject().Count()) throw new JsonException();
            foreach (var property in properties.EnumerateObject())
            {
                if (!value.TryGetProperty(property.Name, out var actual)) throw new JsonException();
                ValidateOutput(property.Value, actual);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateOutput(schema.GetProperty("items"), item);
    }
}
