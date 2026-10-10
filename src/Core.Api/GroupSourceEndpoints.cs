using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

internal static class GroupSourceEndpoints
{
    private const string MessagePath = "/api/group-sources/{sourceId}/messages/{messageId}";
    private const string SourcesPath = "/api/group-sources";
    private const string MessagesPath = "/api/group-sources/{sourceId}/messages";

    internal static void MapGroupSourceReads(this WebApplication app, bool enabled, bool authenticationConfigured)
    {
        if (!enabled)
        {
            foreach (var path in new[] { SourcesPath, MessagesPath, MessagePath }) app.MapGet(path, () => Results.NotFound());
            return;
        }
        if (!authenticationConfigured)
        {
            foreach (var path in new[] { SourcesPath, MessagesPath, MessagePath })
                app.MapGet(path, () => Results.Json(new { error = "Group source access is not configured." }, statusCode: 503));
            return;
        }
        app.MapGet(MessagePath, async (string sourceId, string messageId, HttpRequest request,
            IRequestAuthorizationContextAccessor accessor, [FromServices] GroupSourceReader reader, CancellationToken cancellationToken) =>
        {
            if (!CanonicalId(sourceId, out var source) || !CanonicalId(messageId, out var message) || request.Query.Count != 0)
                return (IResult)Results.BadRequest(new { error = "Invalid group source request." });
            return await ReadAsync(accessor, token => reader.GetAsync(accessor.Current!.Context, source, message, token), cancellationToken);
        }).RequireAuthorization();

        app.MapGet(SourcesPath, async (HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] GroupSourceReader reader, CancellationToken cancellationToken) =>
        {
            if (request.Query.Any(x => x.Key is not ("offset" or "limit") || x.Value.Count != 1) ||
                !Number(request, "offset", 0, 0, 10000, out var offset) || !Number(request, "limit", 25, 1, 25, out var limit))
                return (IResult)Results.BadRequest(new { error = "Invalid group source request." });
            return await ReadAsync(accessor, token => reader.ListSourcesAsync(accessor.Current!.Context, (int)offset, (int)limit, token), cancellationToken);
        }).RequireAuthorization();
        app.MapGet(MessagesPath, async (string sourceId, HttpRequest request, IRequestAuthorizationContextAccessor accessor,
            [FromServices] GroupSourceReader reader, CancellationToken cancellationToken) =>
        {
            if (!CanonicalId(sourceId, out var source) || request.Query.Any(x => x.Key is not ("beforeSequence" or "limit") || x.Value.Count != 1) ||
                !Number(request, "beforeSequence", long.MaxValue, 1, long.MaxValue, out var before) || !Number(request, "limit", 25, 1, 25, out var limit))
                return (IResult)Results.BadRequest(new { error = "Invalid group source request." });
            return await ReadAsync(accessor, token => reader.ListMessagesAsync(accessor.Current!.Context, source, before, (int)limit, token), cancellationToken);
        }).RequireAuthorization();
    }

    private static async Task<IResult> ReadAsync<T>(IRequestAuthorizationContextAccessor accessor,
        Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        if (accessor.Current?.Context is null) return Results.Json(new { error = "Group source access is not available." }, statusCode: 403);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var view = await read(deadline.Token);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = "Group source access is not available." }, statusCode: 403); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Results.Json(new { error = "Group source content is unavailable." }, statusCode: 503); }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException or ArgumentException)
        { return Results.Json(new { error = "Group source content is unavailable." }, statusCode: 503); }
    }

    private static bool Number(HttpRequest request, string key, long fallback, long minimum, long maximum, out long number)
    {
        number = fallback;
        if (!request.Query.TryGetValue(key, out var value)) return true;
        var text = value.ToString();
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= minimum && number <= maximum &&
            number.ToString(CultureInfo.InvariantCulture) == text;
    }

    private static bool CanonicalId(string value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && id.ToString("D") == value;
}
