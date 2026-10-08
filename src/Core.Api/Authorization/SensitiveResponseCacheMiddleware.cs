namespace MinhHuy.AIOffice.Core.Api.Authorization;

public sealed class SensitiveResponseCacheMiddleware(RequestDelegate next)
{
    private static readonly PathString AuthorizationContextPath = new("/api/auth/context");
    private static readonly PathString CompanyDirectoryPath = new("/api/auth/companies");
    private static readonly PathString BillingPlanPath = new("/api/billing/plan");
    private static readonly PathString DataSourcesPath = new("/api/data-sources");
    private static readonly PathString AuditPath = new("/api/audit");
    private static readonly PathString SlaStatusPath = new("/api/sla/status");
    private static readonly PathString TasksPath = new("/api/tasks");
    private static readonly PathString CompanyPath = new("/api/company");

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.Equals(AuthorizationContextPath)
            || context.Request.Path.StartsWithSegments(CompanyDirectoryPath)
            || context.Request.Path.Equals(BillingPlanPath)
            || context.Request.Path.StartsWithSegments(DataSourcesPath)
            || context.Request.Path.StartsWithSegments(AuditPath)
            || context.Request.Path.Equals(SlaStatusPath)
            || context.Request.Path.StartsWithSegments(TasksPath)
            || context.Request.Path.StartsWithSegments(CompanyPath))
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        await next(context);
    }
}
