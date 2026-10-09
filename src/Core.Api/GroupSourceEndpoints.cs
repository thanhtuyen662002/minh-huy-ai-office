using Microsoft.AspNetCore.Mvc;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

internal static class GroupSourceEndpoints
{
    private const string MessagePath = "/api/group-sources/{sourceId}/messages/{messageId}";

    internal static void MapGroupSourceReads(this WebApplication app, bool enabled, bool authenticationConfigured)
    {
        if (!enabled) { app.MapGet(MessagePath, () => Results.NotFound()); return; }
        if (!authenticationConfigured)
        {
            app.MapGet(MessagePath, () => Results.Json(new { error = "Group source access is not configured." }, statusCode: 503));
            return;
        }
        app.MapGet(MessagePath, async (string sourceId, string messageId, HttpRequest request,
            IRequestAuthorizationContextAccessor accessor, [FromServices] GroupSourceReader reader, CancellationToken cancellationToken) =>
        {
            if (!CanonicalId(sourceId, out var source) || !CanonicalId(messageId, out var message) || request.Query.Count != 0)
                return (IResult)Results.BadRequest(new { error = "Invalid group source request." });
            var authority = accessor.Current?.Context;
            if (authority is null) return Results.Json(new { error = "Group source access is not available." }, statusCode: 403);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var view = await reader.GetAsync(authority, source, message, deadline.Token);
                return view is null ? Results.NotFound() : Results.Ok(view);
            }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "Group source access is not available." }, statusCode: 403); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Results.Json(new { error = "Group source content is unavailable." }, statusCode: 503); }
            catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException or ArgumentException)
            { return Results.Json(new { error = "Group source content is unavailable." }, statusCode: 503); }
        }).RequireAuthorization();
    }

    private static bool CanonicalId(string value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && id.ToString("D") == value;
}
