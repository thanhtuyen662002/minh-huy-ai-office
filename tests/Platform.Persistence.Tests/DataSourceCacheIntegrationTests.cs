using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class DataSourceCacheIntegrationTests
{
    [Fact]
    public async Task Authentication_unavailable_data_source_response_is_not_cached()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/data-sources/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Authorized_data_source_list_is_not_cached()
    {
        var context = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(context, ["accounting"]));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/data-sources/");
        request.Headers.Add(AuthorizationHeaders.CompanyId, context.CompanyId.ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Forbidden_data_source_response_is_not_cached()
    {
        await using var factory = AuthenticatedFactory(null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/data-sources/");
        request.Headers.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Public_root_response_is_not_forced_to_no_store()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.NoStore);
    }

    private static WebApplicationFactory<Program> AuthenticatedFactory(AuthenticatedAuthorizationEntry? entry)
    {
        var databaseName = $"core-api-datasource-cache-{Guid.NewGuid():N}";
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
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.AuthenticationScheme, _ => { });
                services.RemoveAll<IAuthenticatedAuthorizationDirectory>();
                services.AddScoped<IAuthenticatedAuthorizationDirectory>(_ => new StubAuthenticatedAuthorizationDirectory(entry));
                services.RemoveAll<IAuthorizationDirectory>();
                services.AddScoped<IAuthorizationDirectory>(_ => new StubAuthorizationDirectory(entry));
                services.RemoveAll<DbContextOptions<PlatformDbContext>>();
                services.RemoveAll<PlatformDbContext>();
                services.AddDbContext<PlatformDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.AddScoped<DataSourceRegistryService>();
            });
        });
    }

    private sealed class StubAuthenticatedAuthorizationDirectory(AuthenticatedAuthorizationEntry? entry) : IAuthenticatedAuthorizationDirectory
    {
        public Task<AuthenticatedAuthorizationEntry?> ResolveAsync(
            string identityProvider,
            string subject,
            Guid companyId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(entry is not null && entry.Context.CompanyId == companyId ? entry : null);
    }

    private sealed class StubAuthorizationDirectory(AuthenticatedAuthorizationEntry? entry) : IAuthorizationDirectory
    {
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(
            AuthorizationContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(entry is not null && entry.Context == context
                ? new AuthorizationDirectoryEntry(entry.Context, entry.Roles)
                : null);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationScheme = "DeterministicDataSourceCacheTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([
                new Claim(AuthenticationClaimTypes.IdentityProvider, "test-oidc"),
                new Claim(AuthenticationClaimTypes.Subject, "test-subject")
            ], AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme)));
        }
    }
}
