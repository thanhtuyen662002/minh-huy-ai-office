using System.Security.Claims;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Core.Api.Authorization;

public static class AuthenticationClaimTypes
{
    public const string IdentityProvider = "idp";
    public const string Subject = "sub";
}

public static class AuthorizationHeaders
{
    public const string CompanyId = "X-AIOffice-Company-Id";
}

public sealed record RequestAuthorizationContext(
    AuthorizationContext Context,
    IReadOnlyList<string> Roles);

public interface IRequestAuthorizationContextAccessor
{
    RequestAuthorizationContext? Current { get; set; }
}

public sealed class RequestAuthorizationContextAccessor : IRequestAuthorizationContextAccessor
{
    public RequestAuthorizationContext? Current { get; set; }
}

public sealed class RequestAuthorizationContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        IAuthenticatedAuthorizationDirectory authorizationDirectory,
        IRequestAuthorizationContextAccessor accessor)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            await next(httpContext);
            return;
        }

        var provider = httpContext.User.FindFirstValue(AuthenticationClaimTypes.IdentityProvider);
        var subject = httpContext.User.FindFirstValue(AuthenticationClaimTypes.Subject)
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var companyHeader = httpContext.Request.Headers[AuthorizationHeaders.CompanyId].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(provider)
            || string.IsNullOrWhiteSpace(subject)
            || !Guid.TryParse(companyHeader, out var companyId)
            || companyId == Guid.Empty)
        {
            await RejectAsync(httpContext);
            return;
        }

        var entry = await authorizationDirectory.ResolveAsync(
            provider,
            subject,
            companyId,
            httpContext.RequestAborted);

        if (entry is null)
        {
            await RejectAsync(httpContext);
            return;
        }

        accessor.Current = new RequestAuthorizationContext(entry.Context, entry.Roles);
        await next(httpContext);
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Authenticated identity is not authorized for the requested company."
        });
    }
}
