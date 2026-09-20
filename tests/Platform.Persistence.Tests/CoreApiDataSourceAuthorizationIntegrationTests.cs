using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
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
        using var request = new HttpRequestMessage(path.EndsWith("connection-test", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, path);
        request.Headers.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());
        request.Headers.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Authorized_list_is_company_scoped_and_never_exposes_secret_reference()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var context = AuthorizationContext.Create(tenantId, companyId, userId);
        var entry = new AuthenticatedAuthorizationEntry(context, ["accounting"]);
        await using var factory = AuthenticatedFactory(entry);

        await SeedDataSourcesAsync(factory,
            DataSource(tenantId, companyId, "visible", "env://TOP_SECRET_VISIBLE"),
            DataSource(tenantId, otherCompanyId, "other-company", "env://TOP_SECRET_OTHER"));

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/data-sources/");
        request.Headers.Add(AuthorizationHeaders.CompanyId, companyId.ToString());
        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("visible", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("other-company", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP_SECRET_VISIBLE", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionSecretReference", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authorized_connection_test_cannot_cross_company_scope_even_with_known_data_source_id()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var otherCompanyDataSource = DataSource(tenantId, otherCompanyId, "other-company", "env://SHOULD_NEVER_RESOLVE");
        var context = AuthorizationContext.Create(tenantId, companyId, userId);
        var entry = new AuthenticatedAuthorizationEntry(context, ["accounting"]);
        await using var factory = AuthenticatedFactory(entry);

        await SeedDataSourcesAsync(factory, otherCompanyDataSource);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/data-sources/{otherCompanyDataSource.Id}/connection-test");
        request.Headers.Add(AuthorizationHeaders.CompanyId, companyId.ToString());
        request.Headers.Add("X-AIOffice-Tenant-Id", tenantId.ToString());
        request.Headers.Add("X-AIOffice-User-Id", userId.ToString());
        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("not_found", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHOULD_NEVER_RESOLVE", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionSecretReference", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", payload, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplicationFactory<Program> AuthenticatedFactory(AuthenticatedAuthorizationEntry? entry)
    {
        var databaseName = $"core-api-datasource-{Guid.NewGuid():N}";
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
            });
        });
    }

    private static async Task SeedDataSourcesAsync(WebApplicationFactory<Program> factory, params DataSourceRecord[] records)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        dbContext.DataSources.AddRange(records);
        await dbContext.SaveChangesAsync();
    }

    private static DataSourceRecord DataSource(Guid tenantId, Guid companyId, string logicalName, string secretReference)
        => new()
        {
            TenantId = tenantId,
            CompanyId = companyId,
            Id = Guid.NewGuid(),
            LogicalName = logicalName,
            Kind = "sqlserver",
            Environment = "test",
            Purpose = "acceptance",
            ConnectionSecretReference = secretReference,
            AllowRead = true,
            AllowWrite = false,
            MaxConcurrency = 1,
            IsEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

    private sealed class StubAuthenticatedAuthorizationDirectory(AuthenticatedAuthorizationEntry? entry) : IAuthenticatedAuthorizationDirectory
    {
        public Task<AuthenticatedAuthorizationEntry?> ResolveAsync(string identityProvider, string subject, Guid companyId, CancellationToken cancellationToken = default)
            => Task.FromResult(entry is not null && entry.Context.CompanyId == companyId ? entry : null);
    }

    private sealed class StubAuthorizationDirectory(AuthenticatedAuthorizationEntry? entry) : IAuthorizationDirectory
    {
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
        {
            if (entry is null
                || entry.Context.TenantId != context.TenantId
                || entry.Context.CompanyId != context.CompanyId
                || entry.Context.UserId != context.UserId)
            {
                return Task.FromResult<AuthorizationDirectoryEntry?>(null);
            }

            return Task.FromResult<AuthorizationDirectoryEntry?>(new AuthorizationDirectoryEntry(entry.Context, entry.Roles));
        }
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationScheme = "DeterministicDataSourceTest";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([
                new Claim(AuthenticationClaimTypes.IdentityProvider, "test-oidc"),
                new Claim(AuthenticationClaimTypes.Subject, "test-subject")
            ], AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme)));
        }
    }
}
