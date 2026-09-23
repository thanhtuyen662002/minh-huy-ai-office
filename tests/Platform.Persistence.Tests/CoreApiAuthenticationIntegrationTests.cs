extern alias CoreApi;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CoreApi::MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;
using CoreApiProgram = CoreApi::Program;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CoreApiAuthenticationIntegrationTests
{
    [Fact]
    public async Task Protected_context_endpoint_fails_closed_when_authentication_is_not_configured()
    {
        await using var factory = new WebApplicationFactory<CoreApiProgram>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AIOffice:Authentication:Authority"] = null,
                    ["AIOffice:Authentication:Audience"] = null
                })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/context");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Billing_plan_endpoint_fails_closed_when_authentication_is_not_configured()
    {
        await using var factory = new WebApplicationFactory<CoreApiProgram>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AIOffice:Authentication:Authority"] = null,
                    ["AIOffice:Authentication:Audience"] = null
                })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/billing/plan");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Authenticated_billing_plan_uses_server_derived_company_and_returns_not_found_when_source_has_no_plan()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(
            AuthorizationContext.Create(tenantId, companyId, userId), ["accountant"]));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/billing/plan");
        request.Headers.Add(AuthorizationHeaders.CompanyId, companyId.ToString());
        request.Headers.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Test_auth_fixture_derives_context_server_side_and_ignores_spoofed_identity_headers()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(
            AuthorizationContext.Create(tenantId, companyId, userId), ["accountant"]));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/context");
        request.Headers.Add(AuthorizationHeaders.CompanyId, companyId.ToString());
        request.Headers.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadFromJsonAsync<AuthorizationContextResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        Assert.NotNull(payload);
        Assert.Equal(tenantId, payload.TenantId);
        Assert.Equal(companyId, payload.CompanyId);
        Assert.Equal(userId, payload.UserId);
        Assert.Contains("accountant", payload.Roles);
    }

    [Fact]
    public async Task Authenticated_cross_company_request_fails_closed()
    {
        await using var factory = AuthenticatedFactory(null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/context");
        request.Headers.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoStore(response);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.NoStore);
    }

    private static WebApplicationFactory<CoreApiProgram> AuthenticatedFactory(AuthenticatedAuthorizationEntry? entry)
    {
        return new WebApplicationFactory<CoreApiProgram>().WithWebHostBuilder(builder =>
        {
            // Use host settings because Program reads authentication configuration while the
            // WebApplicationBuilder is being constructed, before test ConfigureAppConfiguration callbacks run.
            builder.UseSetting("AIOffice:Authentication:Authority", "https://identity.example.invalid");
            builder.UseSetting("AIOffice:Authentication:Audience", "minh-huy-ai-office-tests");
            builder.ConfigureServices(services =>
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.AuthenticationScheme;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.AuthenticationScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.AuthenticationScheme,
                        _ => { });
                services.RemoveAll<IAuthenticatedAuthorizationDirectory>();
                services.AddScoped<IAuthenticatedAuthorizationDirectory>(_ => new StubAuthorizationDirectory(entry));
            });
        });
    }

    private sealed record AuthorizationContextResponse(Guid TenantId, Guid CompanyId, Guid UserId, string[] Roles);

    private sealed class StubAuthorizationDirectory(AuthenticatedAuthorizationEntry? entry)
        : IAuthenticatedAuthorizationDirectory
    {
        public Task<AuthenticatedAuthorizationEntry?> ResolveAsync(
            string identityProvider,
            string subject,
            Guid companyId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("test-oidc", identityProvider);
            Assert.Equal("test-subject", subject);
            return Task.FromResult(entry);
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationScheme = "DeterministicTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(AuthenticationClaimTypes.IdentityProvider, "test-oidc"),
                    new Claim(AuthenticationClaimTypes.Subject, "test-subject")
                ],
                AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AuthenticationScheme)));
        }
    }
}
