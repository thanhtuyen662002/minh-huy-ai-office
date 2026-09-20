using System.Net;
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
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CoreApiDataSourceAuthorizationIntegrationTests
{
    [Theory]
    [InlineData("/api/data-sources/")]
    [InlineData("/api/data-sources/00000000-0000-0000-0000-000000000001/connection-test")]
    public async Task Data_source_endpoints_fail_closed_when_authentication_is_not_configured(string path)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AIOffice:Authentication:Authority"] = null,
                    ["AIOffice:Authentication:Audience"] = null
                })));
        using var client = factory.CreateClient();

        var response = path.EndsWith("connection-test", StringComparison.Ordinal)
            ? await client.PostAsync(path, null)
            : await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/data-sources/")]
    [InlineData("/api/data-sources/00000000-0000-0000-0000-000000000001/connection-test")]
    public async Task Cross_company_or_inactive_membership_fails_before_data_source_access(string path)
    {
        await using var factory = AuthenticatedFactory(null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            path.EndsWith("connection-test", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get,
            path);
        request.Headers.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());
        request.Headers.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static WebApplicationFactory<Program> AuthenticatedFactory(AuthenticatedAuthorizationEntry? entry)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
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

    private sealed class StubAuthorizationDirectory(AuthenticatedAuthorizationEntry? entry)
        : IAuthenticatedAuthorizationDirectory
    {
        public Task<AuthenticatedAuthorizationEntry?> ResolveAsync(
            string identityProvider,
            string subject,
            Guid companyId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(entry);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationScheme = "DeterministicDataSourceTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(AuthenticationClaimTypes.IdentityProvider, "test-oidc"),
                    new Claim(AuthenticationClaimTypes.Subject, "test-subject")
                ],
                AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme)));
        }
    }
}
