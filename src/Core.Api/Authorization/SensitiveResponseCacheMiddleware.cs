namespace MinhHuy.AIOffice.Core.Api.Authorization;

public sealed class SensitiveResponseCacheMiddleware(RequestDelegate next)
{
    private static readonly PathString AuthorizationContextPath = new("/api/auth/context");
    private static readonly PathString BillingPlanPath = new("/api/billing/plan");
    private static readonly PathString DataSourcesPath = new("/api/data-sources");

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.Equals(AuthorizationContextPath)
            || context.Request.Path.Equals(BillingPlanPath)
            || context.Request.Path.StartsWithSegments(DataSourcesPath))
        {
            context.Response.OnStarting(static state =>
            {
                var response = (HttpResponse)state;
                response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            }, context.Response);
        }

        await next(context);
    }
}
