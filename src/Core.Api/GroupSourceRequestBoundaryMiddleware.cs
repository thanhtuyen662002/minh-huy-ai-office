namespace MinhHuy.AIOffice.Core.Api;

// Covers the initial portal identity-directory await as well as the endpoint.
// A handler-only deadline/error boundary starts after that private SQL work.
public sealed class GroupSourceRequestBoundaryMiddleware(RequestDelegate next)
{
    private static readonly PathString Path = new("/api/group-sources");

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(Path)) { await next(context); return; }
        var callerAbort = context.RequestAborted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerAbort);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        context.RequestAborted = deadline.Token;
        try { await next(context); }
        catch (OperationCanceledException) when (callerAbort.IsCancellationRequested) { throw; }
        catch (Exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = "Group source content is unavailable." }, callerAbort);
        }
        finally { context.RequestAborted = callerAbort; }
    }
}
