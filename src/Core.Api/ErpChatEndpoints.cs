using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api;

/// <summary>
/// The chat API a web ERP calls server to server (signed with its client key), plus the administrator routes that create
/// clients and their keys. See docs/integration/ERP_CHAT_API.md.
/// </summary>
public static class ErpChatEndpoints
{
    public const string ApiPath = "/api/erp-chat/v1";
    public const string RateLimitPolicy = "erp-chat";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    /// <summary>At most 120 signed calls a minute per client id, before any database work.</summary>
    public static void AddErpChatRateLimit(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options) =>
        options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Request.Headers["X-AIOffice-Client"].ToString() is { Length: > 0 and <= 64 } client ? client : "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));

    public static void MapErpChat(this WebApplication app, bool configured, bool authenticated)
    {
        var api = app.MapGroup(ApiPath);
        var admin = app.MapGroup("/api/erp-chat/clients");
        if (!configured)
        {
            static IResult Unavailable() => Results.Json(new { code = "unavailable" }, statusCode: 503);
            api.MapPost("/messages", Unavailable);
            api.MapGet("/messages/{id:guid}", Unavailable);
            admin.MapGet("/", Unavailable);
            admin.MapPost("/", Unavailable);
            admin.MapPut("/{id:guid}", Unavailable);
            return;
        }

        api.MapPost("/messages", (HttpContext http, ErpChatService chat, PilotTaskSubmissionService submission) =>
            Signed(http, chat, async (client, body, token) =>
            {
                var request = Parse<ErpChatAskRequest>(body);
                try { return Results.Json(await chat.AskAsync(client, request, submission, token), Json, statusCode: 202); }
                catch (AiUsageLimitException limit) { return Results.Json(new { code = limit.Code, error = limit.Message }, statusCode: 429); }
                catch (UnauthorizedAccessException) { return Results.Json(new { code = "forbidden", error = "This client cannot ask now." }, statusCode: 403); }
            })).RequireRateLimiting(RateLimitPolicy);
        api.MapGet("/messages/{id:guid}", (Guid id, HttpContext http, ErpChatService chat, PilotTaskResultService results) =>
            Signed(http, chat, async (client, _, token) =>
                Results.Json(await chat.GetAsync(client, id, http.Request.Query["externalUserId"].ToString(), results, token), Json)))
            .RequireRateLimiting(RateLimitPolicy);

        if (!authenticated) return;
        admin.RequireAuthorization();
        admin.MapGet("/", (HttpRequest http, IRequestAuthorizationContextAccessor accessor, ErpChatService chat) =>
            Admin(http, accessor, (authority, token) => chat.ListClientsAsync(authority, token)));
        admin.MapPost("/", (HttpRequest http, IRequestAuthorizationContextAccessor accessor, ErpChatService chat) =>
            Admin(http, accessor, async (authority, token) =>
                await chat.CreateClientAsync(authority, Parse<ErpChatClientCreateRequest>(await ReadAsync(http, token)), token)))
            .RequireRateLimiting("customer-erp");
        admin.MapPut("/{id:guid}", (Guid id, HttpRequest http, IRequestAuthorizationContextAccessor accessor, ErpChatService chat) =>
            Admin(http, accessor, async (authority, token) =>
                (object?)await chat.UpdateClientAsync(authority, id, Parse<ErpChatClientUpdateRequest>(await ReadAsync(http, token)), token)
                    ?? new { updated = true }))
            .RequireRateLimiting("customer-erp");
    }

    /// <summary>Reads the body, verifies the signature headers over method, path, query and body, then runs the action.</summary>
    private static async Task<IResult> Signed(HttpContext http, ErpChatService chat,
        Func<ErpChatClientRecord, byte[], CancellationToken, Task<IResult>> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var headers = http.Request.Headers;
            if (!Guid.TryParseExact(headers["X-AIOffice-Client"].ToString(), "D", out var clientId)
                || !long.TryParse(headers["X-AIOffice-Timestamp"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
                || !Guid.TryParseExact(headers["X-AIOffice-Nonce"].ToString(), "D", out var nonce))
                return Results.Json(new { code = "unauthorized", error = "Signature headers are missing or malformed." }, statusCode: 401);
            var body = await ReadAsync(http.Request, deadline.Token);
            var client = await chat.AuthenticateAsync(new(clientId, timestamp, nonce, headers["X-AIOffice-Signature"].ToString().ToUpperInvariant()),
                http.Request.Method, http.Request.Path.Value + http.Request.QueryString.Value, body, deadline.Token);
            return await action(client, body, deadline.Token);
        }
        catch (ErpChatException error) { return Results.Json(new { code = error.Code, error = error.Message }, statusCode: error.Status); }
        catch (Exception error) when (error is ArgumentException or JsonException or DecoderFallbackException)
        { return Results.Json(new { code = "invalid_fields", error = "The request body is not valid." }, statusCode: 400); }
        catch (BadHttpRequestException) { return Results.Json(new { code = "too_large", error = "The request body is too large." }, statusCode: 413); }
        catch (TaskSubmissionIntentConflictException) { return Results.Json(new { code = "request_conflict", error = "This requestId was used for a different question." }, statusCode: 409); }
        catch (InvalidOperationException error) when (error.Message.Contains("idempotency key", StringComparison.OrdinalIgnoreCase))
        { return Results.Json(new { code = "request_conflict", error = "This requestId was used for a different question." }, statusCode: 409); }
        catch (Exception error) when (error is DbException or InvalidOperationException or DbUpdateException)
        { return Results.Json(new { code = "unavailable", error = "Try again shortly." }, statusCode: 503); }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        { return Results.Json(new { code = "unavailable", error = "Try again shortly." }, statusCode: 503); }
    }

    private static async Task<IResult> Admin<T>(HttpRequest http, IRequestAuthorizationContextAccessor accessor,
        Func<AuthorizationContext, CancellationToken, Task<T>> action)
    {
        if (accessor.Current is not { } current || !current.Roles.Contains("admin", StringComparer.Ordinal)) return Results.Forbid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        http.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            if (http.Query.Count != 0) throw new ArgumentException("Unexpected query.");
            return Results.Json(await action(current.Context, deadline.Token), Json);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "not_found" }); }
        catch (Exception error) when (error is ArgumentException or JsonException or DecoderFallbackException)
        { return Results.BadRequest(new { code = "invalid_fields" }); }
        catch (BadHttpRequestException) { return Results.Json(new { code = "invalid_fields" }, statusCode: 413); }
        catch (DbUpdateException) { return Results.Conflict(new { code = "conflict" }); }
        catch (Exception error) when (error is InvalidOperationException or DbException)
        { return Results.Json(new { code = "unavailable" }, statusCode: 503); }
        catch (OperationCanceledException) when (!http.HttpContext.RequestAborted.IsCancellationRequested)
        { return Results.Json(new { code = "unavailable" }, statusCode: 503); }
    }

    private static T Parse<T>(byte[] body) where T : class
    {
        _ = new UTF8Encoding(false, true).GetCharCount(body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
            .GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)) throw new ArgumentException("Invalid body.");
        return document.RootElement.Deserialize<T>(Json) ?? throw new ArgumentException("Missing body.");
    }

    private static async Task<byte[]> ReadAsync(HttpRequest http, CancellationToken token)
    {
        const int maximum = 32768;
        if (http.ContentLength > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Body.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > maximum) throw new BadHttpRequestException("Request is too large.", 413);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
