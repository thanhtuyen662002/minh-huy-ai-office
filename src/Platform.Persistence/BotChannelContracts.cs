using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record BotGroup(string ExternalId, string DisplayName);
public sealed record BotCustomerGroup(string ExternalId, string DisplayName, string CustomerName);
public sealed record BotChannelUpdate(string ExpectedVersion, bool Enabled, bool DryRun, string[] SummarySchedule,
    string? TechGroupExternalId, BotCustomerGroup[] CustomerGroups);
public sealed record BotAiUpdate(string ExpectedVersion, string BaseUrl, string Model, string? ApiKey = null, bool ClearApiKey = false);
public sealed record BotHeartbeat(string Channel, string LoginState, BotGroup[] Groups, DateTimeOffset? LastSummaryAt = null,
    string? LastError = null, string? LoginQrContentType = null, string? LoginQrBase64 = null);
public sealed record BotCommandInput(string Kind);
public sealed record BotCommandAck(string Channel);
public sealed record BotCommandView(Guid Id, string Kind, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record BotStatusView(string LoginState, BotGroup[] Groups, DateTimeOffset? LastSummaryAt, string? LastError, DateTimeOffset ReceivedAt);
public sealed record BotChannelView(string Channel, bool Enabled, bool DryRun, string[] SummarySchedule, string TimeZone,
    string? TechGroupExternalId, BotCustomerGroup[] CustomerGroups, string ConfigVersion, BotStatusView? Status);
public sealed record BotAiView(string BaseUrl, string Model, bool ApiKeyConfigured, string Version);
public sealed record BotAdminView(bool Enabled, BotChannelView[] Channels, BotAiView Ai);
public sealed record BotLoginQr(string ContentType, string Base64, DateTimeOffset ExpiresAt);
public sealed record BotConfigView(BotChannelView Config, BotAiConfig Ai, BotCommandView[] Commands);
public sealed record BotAiConfig(string BaseUrl, string Model, string? ApiKey);
public sealed record BotAuditView(Guid Id, string? Channel, Guid ActorUserId, string Field, string? Before, string? After, DateTimeOffset Timestamp);
public sealed class BotChannelNotFoundException() : Exception("Bot channel is unavailable.");
public sealed class BotChannelConflictException() : Exception("Bot settings changed. Reload before updating.");

public static class BotChannelJson
{
    public const int MaximumBodyBytes = 524288;
    public const string TimeZone = "Asia/Ho_Chi_Minh";
    public static readonly string[] Channels = ["zalo", "whatsapp"];
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };

    public static T Read<T>(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBodyBytes) throw Invalid();
        _ = new UTF8Encoding(false, true).GetCharCount(bytes.Span);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        Unique(document.RootElement);
        return document.Deserialize<T>(Options) ?? throw Invalid();
    }

    public static string Channel(string channel) => Channels.Contains(channel, StringComparer.Ordinal) ? channel : throw Invalid();
    public static long Version(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) &&
        result >= 0 && result.ToString(CultureInfo.InvariantCulture) == value ? result : throw Invalid();
    public static string[] Schedule(string[] schedule)
    {
        if (schedule is null || schedule.Length > 1440 || schedule.Any(x => x is null || x.Length != 5 ||
            !TimeOnly.TryParseExact(x, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) throw Invalid();
        return schedule.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static string BaseUrl(string value)
    {
        CustomerRequestJson.Text(value, 1000, opaque: true);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrEmpty(uri.Host)) throw Invalid();
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static BotHeartbeat Heartbeat(BotHeartbeat input, DateTimeOffset now)
    {
        Channel(input.Channel);
        if (input.LoginState is not ("logged_out" or "awaiting_qr" or "logged_in" or "error") || input.Groups is null || input.Groups.Length > 500 ||
            input.LastSummaryAt is { } summary && (summary == default || summary.Offset != TimeSpan.Zero || summary > now.AddMinutes(5))) throw Invalid();
        var groups = input.Groups.Select(group =>
        {
            if (group is null) throw Invalid();
            CustomerRequestJson.Text(group.ExternalId, 200, opaque: true);
            return new BotGroup(group.ExternalId, Display(group.DisplayName));
        }).OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToArray();
        if (groups.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() != groups.Length) throw Invalid();
        // Arbitrary error text can contain keys, headers and upstream bodies. Store only a bounded server-owned message.
        if (input.LastError?.Length > 8000) throw Invalid();
        var error = string.IsNullOrWhiteSpace(input.LastError) ? null : "Bot báo lỗi. Kiểm tra nhật ký đã che thông tin nhạy cảm trên máy chạy bot.";
        _ = QrBytes(input.LoginQrContentType, input.LoginQrBase64);
        if (input.LoginQrBase64 is not null && input.LoginState != "awaiting_qr") throw Invalid();
        return input with { Groups = groups, LastError = error };
    }

    public static byte[]? QrBytes(string? contentType, string? base64)
    {
        if (base64 is null && contentType is null) return null;
        if (contentType != "image/png" || base64 is null || base64.Length > 87384) throw Invalid();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw Invalid(); }
        // Only PNG raster images; forbid SVG/HTML and oversized dimensions/decompression payloads.
        if (bytes.Length is < 45 or > 65536 || Convert.ToBase64String(bytes) != base64 ||
            !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) || !bytes.AsSpan(bytes.Length - 8, 4).SequenceEqual("IEND"u8)) throw Invalid();
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 1 or > 2048 || height is < 1 or > 2048) throw Invalid();
        var offset = 8; var hasImage = false;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12) throw Invalid();
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > bytes.Length - offset - 12) throw Invalid();
            var count = (int)length; var type = bytes.AsSpan(offset + 4, 4);
            if (offset == 8 && (!type.SequenceEqual("IHDR"u8) || count != 13)) throw Invalid();
            if (offset != 8 && type.SequenceEqual("IHDR"u8)) throw Invalid();
            if (Crc(bytes.AsSpan(offset + 4, count + 4)) != System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + count, 4))) throw Invalid();
            if (type.SequenceEqual("IDAT"u8)) hasImage = true;
            offset += count + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                if (count != 0 || !hasImage || offset != bytes.Length) throw Invalid();
                return bytes;
            }
        }
        throw Invalid();
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var value = uint.MaxValue;
        foreach (var b in bytes)
        {
            value ^= b;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~value;
    }

    private static string Display(string value)
    {
        if (value is null || value.Length > 1000) throw Invalid();
        var clean = new string(value.Where(c => !char.IsControl(c) && c is not ('\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or '\u2066' or '\u2067' or '\u2068' or '\u2069')).Take(200).ToArray()).Trim();
        CustomerRequestJson.Text(clean, 200);
        return clean;
    }

    private static void Unique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in node.EnumerateObject()) { if (!names.Add(field.Name)) throw Invalid(); Unique(field.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Unique(item);
    }
    private static ArgumentException Invalid() => new("Invalid bot channel request.");
}
