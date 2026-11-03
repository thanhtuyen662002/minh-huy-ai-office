using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// A short, secret-free reason the worker gives for a failed step (for example "AI provider returned
/// HTTP 404"). It rides on the failed step's status event so the task owner sees why an answer failed.
/// </summary>
public static class TaskFailureDetail
{
    public const int MaxLength = 300;

    public static string? Normalize(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        var builder = new StringBuilder(Math.Min(detail.Length, MaxLength));
        var space = false;
        foreach (var character in detail.Trim())
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                if (!space && builder.Length > 0) builder.Append(' ');
                space = true;
                continue;
            }
            space = false;
            builder.Append(character);
            if (builder.Length >= MaxLength) break;
        }
        var text = builder.ToString().Trim();
        if (text.Length >= MaxLength) text = text[..(MaxLength - 1)] + "…";
        return text.Length == 0 ? null : text;
    }

    /// <summary>The detail on the newest status event of the step, when that event carries one.</summary>
    public static async Task<string?> ReadAsync(PlatformDbContext db, Guid tenantId, Guid companyId, Guid taskId, Guid stepId,
        CancellationToken cancellationToken)
    {
        var payload = await db.TaskEvents.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.CompanyId == companyId && item.TaskId == taskId
                && item.StepId == stepId && item.EventType == "step.status.changed")
            .OrderByDescending(item => item.Sequence)
            .Select(item => item.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                ? Normalize(detail.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }
}
