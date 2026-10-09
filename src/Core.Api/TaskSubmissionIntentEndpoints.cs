using System.Globalization;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api;

internal static class TaskSubmissionIntentEndpoints
{
    private const string Path = "/api/tasks/intents";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    internal static void MapTaskSubmissionIntents(this WebApplication app)
    {
        app.MapPost(Path, (HttpRequest request, IRequestAuthorizationContextAccessor accessor, TaskSubmissionIntentService service,
            CancellationToken cancellationToken) => HandleAsync(accessor, cancellationToken, async (authority, token) =>
        {
            RequireNoQuery(request);
            var input = await ReadAsync<TaskSubmissionPrepareRequest>(request, token);
            return Results.Ok(await service.PrepareAsync(authority, input, token));
        })).RequireAuthorization();

        app.MapGet(Path, (HttpRequest request, IRequestAuthorizationContextAccessor accessor, TaskSubmissionIntentService service,
            CancellationToken cancellationToken) => HandleAsync(accessor, cancellationToken, async (authority, token) =>
        {
            if (request.Query.Any(pair => pair.Key is not ("offset" or "limit") || pair.Value.Count != 1))
                throw new ArgumentException("Invalid page selectors.");
            var offset = PageNumber(request, "offset", 0);
            var limit = PageNumber(request, "limit", TaskSubmissionIntentIdentity.MaximumPageSize);
            return Results.Ok(await service.ListAsync(authority, offset, limit, token));
        })).RequireAuthorization();

        app.MapGet(Path + "/{operationId}", (string operationId, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            TaskSubmissionIntentService service, CancellationToken cancellationToken) => HandleAsync(accessor, cancellationToken, async (authority, token) =>
        {
            RequireNoQuery(request);
            var id = Operation(operationId);
            var detail = await service.GetAsync(authority, id, token);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        })).RequireAuthorization();

        app.MapPost(Path + "/{operationId}/submit", (string operationId, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            TaskSubmissionIntentService service, CancellationToken cancellationToken) => HandleAsync(accessor, cancellationToken, async (authority, token) =>
        {
            RequireNoQuery(request);
            var id = Operation(operationId);
            var input = await ReadAsync<TaskSubmissionExecuteRequest>(request, token);
            var accepted = await service.ExecuteAsync(authority, id, input, token);
            return Results.Accepted($"/api/tasks/{accepted.TaskId:D}/history", accepted);
        })).RequireAuthorization();
    }

    internal static void MapUnavailableTaskSubmissionIntents(this WebApplication app)
    {
        static IResult Unavailable() => Results.Json(new { error = "Task submission is unavailable." }, statusCode: 503);
        app.MapPost(Path, Unavailable); app.MapGet(Path, Unavailable);
        app.MapGet(Path + "/{operationId}", Unavailable); app.MapPost(Path + "/{operationId}/submit", Unavailable);
    }

    private static async Task<IResult> HandleAsync(IRequestAuthorizationContextAccessor accessor, CancellationToken cancellationToken,
        Func<AuthorizationContext, CancellationToken, Task<IResult>> action)
    {
        var authority = accessor.Current?.Context;
        if (authority is null) return Results.Forbid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await action(authority, deadline.Token); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (TaskSubmissionIntentNotFoundException) { return Results.NotFound(); }
        catch (TaskSubmissionIntentConflictException error)
        { return Results.Conflict(new { error = "Task submission conflicts with current state.", code = error.Code }); }
        catch (BadHttpRequestException error)
        { return Results.Json(new { error = "Invalid task submission request." }, statusCode: error.StatusCode == 413 ? 413 : 400); }
        catch (Exception error) when (error is ArgumentException or JsonException or DecoderFallbackException)
        { return Results.BadRequest(new { error = "Invalid task submission request." }); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Results.Json(new { error = "Task submission is unavailable. Reconcile the same operation." }, statusCode: 503); }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Results.Json(new { error = "Task submission is unavailable. Reconcile the same operation." }, statusCode: 503); }
    }

    private static Guid Operation(string text)
    {
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty) throw new ArgumentException("Invalid operation selector.");
        return id;
    }

    private static int PageNumber(HttpRequest request, string key, int fallback)
    {
        if (!request.Query.TryGetValue(key, out var value)) return fallback;
        var text = value.ToString();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result.ToString(CultureInfo.InvariantCulture) != text) throw new ArgumentException("Invalid page selector.");
        return result;
    }

    private static void RequireNoQuery(HttpRequest request)
    {
        if (request.Query.Count != 0) throw new ArgumentException("Unexpected query selectors.");
    }

    private static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
    {
        const int maximum = 32768;
        if (!request.HasJsonContentType()) throw new ArgumentException("JSON is required.");
        if (request.ContentLength > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        var bytes = new byte[maximum + 1]; var length = 0;
        while (length <= maximum)
        {
            var read = await request.Body.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > maximum) throw new BadHttpRequestException("Request is too large.", 413);
        try
        {
            _ = StrictUtf8.GetCharCount(bytes.AsSpan(0, length));
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
                throw new ArgumentException("Invalid request properties.");
            return document.RootElement.Deserialize<T>(Json) ?? throw new ArgumentException("Missing request.");
        }
        catch (InvalidOperationException error) { throw new ArgumentException("Invalid JSON text.", error); }
    }
}
