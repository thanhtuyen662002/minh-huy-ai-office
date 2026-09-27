namespace MinhHuy.AIOffice.Core.Api.Authorization;

public sealed class SensitiveResponseCacheMiddleware(RequestDelegate next)
{
    private static readonly PathString AuthorizationContextPath = new("/api/auth/context");
    private static readonly PathString BillingPlanPath = new("/api/billing/plan");
    private static readonly PathString DataSourcesPath = new("/api/data-sources");
    private static readonly PathString AuditPath = new("/api/audit");
    private static readonly PathString SlaStatusPath = new("/api/sla/status");
    private static readonly PathString TasksPath = new("/api/tasks");

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.Equals(AuthorizationContextPath)
            || context.Request.Path.Equals(BillingPlanPath)
            || context.Request.Path.StartsWithSegments(DataSourcesPath)
            || context.Request.Path.StartsWithSegments(AuditPath)
            || context.Request.Path.Equals(SlaStatusPath)
            || context.Request.Path.StartsWithSegments(TasksPath))
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        await next(context);
    }
}
