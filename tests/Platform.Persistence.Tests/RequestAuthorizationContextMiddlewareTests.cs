using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class RequestAuthorizationContextMiddlewareTests
{
    [Fact]
    public async Task Authenticated_request_uses_principal_and_validated_company_only()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var directory = new RecordingDirectory(new AuthenticatedAuthorizationEntry(
            AuthorizationContext.Create(tenantId, companyId, userId), ["accountant"]));
        var accessor = new RequestAuthorizationContextAccessor();
        var context = AuthenticatedContext("oidc", "subject-1");
        context.Request.Headers[AuthorizationHeaders.CompanyId] = companyId.ToString();
        context.Request.Headers["X-AIOffice-Tenant-Id"] = Guid.NewGuid().ToString();
        context.Request.Headers["X-AIOffice-User-Id"] = Guid.NewGuid().ToString();
        var nextCalled = false;
        var middleware = new RequestAuthorizationContextMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, directory, accessor);

        Assert.True(nextCalled);
        Assert.Equal(("oidc", "subject-1", companyId), directory.LastRequest);
        Assert.Equal(tenantId, accessor.Current!.Context.TenantId);
        Assert.Equal(userId, accessor.Current.Context.UserId);
    }

    [Theory]
    [InlineData(null, "subject")]
    [InlineData("oidc", null)]
    public async Task Missing_trusted_identity_claim_fails_closed(string? provider, string? subject)
    {
        var context = AuthenticatedContext(provider, subject);
        context.Request.Headers[AuthorizationHeaders.CompanyId] = Guid.NewGuid().ToString();
        var nextCalled = false;
        var middleware = new RequestAuthorizationContextMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, new RecordingDirectory(null), new RequestAuthorizationContextAccessor());

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Malformed_company_scope_fails_closed_without_directory_lookup()
    {
        var context = AuthenticatedContext("oidc", "subject");
        context.Request.Headers[AuthorizationHeaders.CompanyId] = "not-a-guid";
        var directory = new RecordingDirectory(null);
        var middleware = new RequestAuthorizationContextMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, directory, new RequestAuthorizationContextAccessor());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Null(directory.LastRequest);
    }

    [Fact]
    public async Task Cross_company_or_inactive_membership_fails_closed()
    {
        var context = AuthenticatedContext("oidc", "subject");
        context.Request.Headers[AuthorizationHeaders.CompanyId] = Guid.NewGuid().ToString();
        var nextCalled = false;
        var middleware = new RequestAuthorizationContextMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, new RecordingDirectory(null), new RequestAuthorizationContextAccessor());

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static DefaultHttpContext AuthenticatedContext(string? provider, string? subject)
    {
        var claims = new List<Claim>();
        if (provider is not null) claims.Add(new Claim(AuthenticationClaimTypes.IdentityProvider, provider));
        if (subject is not null) claims.Add(new Claim(AuthenticationClaimTypes.Subject, subject));
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            Response = { Body = new MemoryStream() }
        };
    }

    private sealed class RecordingDirectory(AuthenticatedAuthorizationEntry? result) : IAuthenticatedAuthorizationDirectory
    {
        public (string Provider, string Subject, Guid CompanyId)? LastRequest { get; private set; }

        public Task<AuthenticatedAuthorizationEntry?> ResolveAsync(string identityProvider, string subject, Guid companyId, CancellationToken cancellationToken = default)
        {
            LastRequest = (identityProvider, subject, companyId);
            return Task.FromResult(result);
        }
    }
}
