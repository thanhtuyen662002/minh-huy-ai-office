using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

internal static class GroupListenerEndpoints
{
    internal const string Path = "/internal/group-ingress/listener";

    internal static void MapGroupListener(this WebApplication app, bool enabled)
    {
        if (!enabled) { app.MapPost(Path, () => Results.NotFound()); return; }
        app.MapPost(Path, async (HttpRequest request, [FromServices] GroupServiceAuthenticator authentication,
            [FromServices] GroupListenerStore store, CancellationToken cancellationToken) =>
        {
            byte[]? body = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (request.Query.Count != 0) throw new ArgumentException();
                var signature = GroupIngressEndpoints.Signature(request);
                body = await GroupIngressEndpoints.ReadBodyAsync(request, GroupServiceAuthenticator.MaximumListenerBodyBytes, deadline.Token);
                var verified = await authentication.AuthenticateListenerAsync(signature, body, deadline.Token);
                return (IResult)Results.Ok(await store.ApplyAsync(verified, deadline.Token));
            }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "Group service access is not available." }, statusCode: 403); }
            catch (GroupListenerConflictException) { return Results.Json(new { error = "Listener command identity conflicts with a committed receipt." }, statusCode: 409); }
            catch (BadHttpRequestException error) { return Results.Json(new { error = "Invalid listener request." }, statusCode: error.StatusCode); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid listener request." }); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Results.Json(new { error = "Listener is unavailable. Reconcile the same command." }, statusCode: 503); }
            catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException)
            { return Results.Json(new { error = "Listener is unavailable." }, statusCode: 503); }
            finally { if (body is not null) CryptographicOperations.ZeroMemory(body); }
        });
    }
}
