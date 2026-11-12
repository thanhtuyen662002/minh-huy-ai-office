using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api;

/// <summary>Administrator routes for AI spend: prices, billed customers with their limits, and the monthly report.</summary>
public static class AiUsageEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    public static void MapAiUsage(this WebApplication app, bool configured, bool authenticated)
    {
        var routes = app.MapGroup("/api/ai-usage");
        if (!configured || !authenticated)
        {
            static IResult Unavailable() => Results.Json(new { code = "unavailable" }, statusCode: 503);
            routes.MapGet("/", Unavailable);
            routes.MapGet("/settings", Unavailable);
            routes.MapPost("/prices", Unavailable);
            routes.MapPost("/accounts", Unavailable);
            routes.MapPut("/accounts/{id:guid}", Unavailable);
            return;
        }
        routes.RequireAuthorization();
        routes.MapGet("/", (HttpRequest http, IRequestAuthorizationContextAccessor accessor, AiUsageService service) =>
            Handle(http, accessor, (authority, token) =>
            {
                var (year, month) = Month(http);
                return service.ReportAsync(authority, year, month, token);
            }, query: ["month"]));
        routes.MapGet("/settings", (HttpRequest http, IRequestAuthorizationContextAccessor accessor, AiUsageService service) =>
            Handle(http, accessor, (authority, token) => service.GetSettingsAsync(authority, token)));
        routes.MapPost("/prices", (HttpRequest http, IRequestAuthorizationContextAccessor accessor, AiUsageService service) =>
            Handle(http, accessor, async (authority, token) =>
                await service.SavePriceAsync(authority, await ReadAsync<AiPriceSaveRequest>(http, token), token)))
            .RequireRateLimiting("customer-erp");
        routes.MapPost("/accounts", (HttpRequest http, IRequestAuthorizationContextAccessor accessor, AiUsageService service) =>
            Handle(http, accessor, async (authority, token) =>
                await service.SaveAccountAsync(authority, await ReadAsync<AiBillingAccountSaveRequest>(http, token), token: token)))
            .RequireRateLimiting("customer-erp");
        routes.MapPut("/accounts/{id:guid}", (Guid id, HttpRequest http, IRequestAuthorizationContextAccessor accessor, AiUsageService service) =>
            Handle(http, accessor, async (authority, token) =>
                await service.SaveAccountAsync(authority, await ReadAsync<AiBillingAccountSaveRequest>(http, token), id, token)))
            .RequireRateLimiting("customer-erp");
    }

    /// <summary>The refusal a question gets when a rate limit or the customer's budget stops it.</summary>
    internal static IResult Refused(AiUsageLimitException limit) =>
        Results.Json(new { error = limit.Message, code = limit.Code }, statusCode: StatusCodes.Status429TooManyRequests);

    private static (int Year, int Month) Month(HttpRequest http)
    {
        var text = http.Query["month"].ToString();
        if (text.Length == 0)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(AiBillingCalendar.Offset);
            return (now.Year, now.Month);
        }
        return DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            ? (month.Year, month.Month) : throw new ArgumentException("Invalid month.");
    }

    private static async Task<IResult> Handle<T>(HttpRequest http, IRequestAuthorizationContextAccessor accessor,
        Func<AuthorizationContext, CancellationToken, Task<T>> action, string[]? query = null)
    {
        if (accessor.Current is not { } current || !current.Roles.Contains("admin", StringComparer.Ordinal)) return Results.Forbid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (http.Query.Any(pair => query?.Contains(pair.Key, StringComparer.Ordinal) != true || pair.Value.Count != 1))
                throw new ArgumentException("Unexpected query.");
            return Results.Json(await action(current.Context, deadline.Token), Json);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "not_found" }); }
        catch (Exception error) when (error is ArgumentException or JsonException or DecoderFallbackException)
        { return Results.BadRequest(new { code = "invalid_fields" }); }
        catch (BadHttpRequestException error) { return Results.Json(new { code = "invalid_fields" }, statusCode: error.StatusCode == 413 ? 413 : 400); }
        catch (DbUpdateException) { return Results.Conflict(new { code = "conflict" }); }
        catch (Exception error) when (error is InvalidOperationException or DbException)
        { return Results.Json(new { code = "unavailable" }, statusCode: 503); }
        catch (OperationCanceledException) when (!http.HttpContext.RequestAborted.IsCancellationRequested)
        { return Results.Json(new { code = "unavailable" }, statusCode: 503); }
    }

    private static async Task<T> ReadAsync<T>(HttpRequest http, CancellationToken token) where T : class
    {
        const int maximum = 65536;
        if (!http.HasJsonContentType()) throw new ArgumentException("JSON is required.");
        if (http.ContentLength > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        var bytes = new byte[maximum + 1]; var length = 0;
        while (length <= maximum)
        {
            var read = await http.Body.ReadAsync(bytes.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        if (length > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        _ = new UTF8Encoding(false, true).GetCharCount(bytes.AsSpan(0, length));
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
            .GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)) throw new ArgumentException("Invalid body.");
        return document.RootElement.Deserialize<T>(Json) ?? throw new ArgumentException("Missing body.");
    }
}
