using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
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

    [Theory]
    [InlineData(null)]
    [InlineData("viewer")]
    [InlineData("ADMIN")]
    [InlineData("unrecognized-role")]
    public async Task CallerAdminClaim_DoesNotGrantRegistryManagementWithoutDirectoryAdmin(string? role)
    {
        var context = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string[] roles = role is null ? [] : [role];
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(context, roles));
        var original = DataSource(context.TenantId, context.CompanyId, "original", "secretref://env/original");
        await SeedDataSourcesAsync(factory, original);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, context.CompanyId.ToString());
        var request = new DataSourceRegistryWriteRequest(
            "changed", "changed-kind", "changed-environment", "changed-purpose", "secretref://env/denied-rotation",
            AllowRead: false, AllowWrite: true, MaxConcurrency: 16, IsEnabled: false);

        var create = await client.PostAsJsonAsync("/api/data-sources/", request);
        var update = await client.PutAsJsonAsync($"/api/data-sources/{original.Id}", request);
        var metadata = await client.PutAsJsonAsync($"/api/data-sources/{original.Id}/metadata", new DataSourceMetadataWriteRequest("metadata-denied", "denied", 7, false));

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, metadata.StatusCode);
        Assert.DoesNotContain("denied-rotation", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("denied-rotation", await update.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/data-sources/")).StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var persisted = verificationScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Equal(1, await persisted.DataSources.CountAsync());
        Assert.Equivalent(original, await persisted.DataSources.AsNoTracking().SingleAsync(), strict: true);
    }

    [Fact]
    public async Task DirectoryAdmin_CanCreateAndUpdatePersistedSourceWithoutExposingReference()
    {
        var context = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(context, ["admin"]));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, context.CompanyId.ToString());
        var request = new DataSourceRegistryWriteRequest(
            "created", "sql-server", "test", "acceptance", "secretref://env/original",
            AllowRead: true, AllowWrite: false, MaxConcurrency: 2);

        var create = await client.PostAsJsonAsync("/api/data-sources/", request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetGuid();
        var update = await client.PutAsJsonAsync($"/api/data-sources/{id}", request with
        {
            LogicalName = "updated",
            ConnectionSecretReference = "secretref://env/rotated",
            AllowRead = false,
            AllowWrite = true,
            MaxConcurrency = 8,
            IsEnabled = false
        });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.DoesNotContain("secretref://", await update.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .DataSources.AsNoTracking().SingleAsync();
        Assert.Equal("updated", persisted.LogicalName);
        Assert.Equal("secretref://env/rotated", persisted.ConnectionSecretReference);
        Assert.False(persisted.AllowRead);
        Assert.True(persisted.AllowWrite);
        Assert.False(persisted.IsEnabled);
        Assert.Equal(8, persisted.MaxConcurrency);
    }

    [Theory]
    [InlineData("logicalName")]
    [InlineData("purpose")]
    [InlineData("maxConcurrency")]
    [InlineData("isEnabled")]
    [InlineData("kind")]
    [InlineData("environment")]
    [InlineData("allowRead")]
    [InlineData("allowWrite")]
    [InlineData("connectionSecretReference")]
    [InlineData("tenantId")]
    [InlineData("companyId")]
    [InlineData("id")]
    public async Task MetadataContract_RejectsMissingOrUnknownFieldsWithoutMutation(string field)
    {
        var context = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(context, ["admin"]));
        var original = DataSource(context.TenantId, context.CompanyId, "original", "secretref://env/fixture-original");
        await SeedDataSourcesAsync(factory, original);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, context.CompanyId.ToString());
        var payload = new Dictionary<string, object?> { ["logicalName"] = "changed", ["purpose"] = "changed", ["maxConcurrency"] = 7, ["isEnabled"] = false };
        if (!payload.Remove(field)) payload[field] = "FIXTURE-UNTRUSTED";
        var response = await client.PutAsJsonAsync($"/api/data-sources/{original.Id}/metadata", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("FIXTURE-UNTRUSTED", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equivalent(original, await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().DataSources.AsNoTracking().SingleAsync(), strict: true);
    }

    [Fact]
    public async Task MetadataContract_RetainsConcurrentProtectedValuesAndScopesTheId()
    {
        var context = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(context, ["admin"]));
        var original = DataSource(context.TenantId, context.CompanyId, "original", "secretref://env/fixture-original");
        var foreign = DataSource(context.TenantId, Guid.NewGuid(), "foreign", "secretref://env/fixture-foreign");
        await SeedDataSourcesAsync(factory, original, foreign);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, context.CompanyId.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/data-sources/")).StatusCode);
        await using (var manager = factory.Services.CreateAsyncScope())
        {
            var db = manager.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = await db.DataSources.SingleAsync(source => source.Id == original.Id);
            row.Kind = "Postgres"; row.Environment = "Production"; row.AllowRead = false; row.AllowWrite = true;
            row.ConnectionSecretReference = "secretref://env/fixture-rotated";
            await db.SaveChangesAsync();
        }
        var request = new DataSourceMetadataWriteRequest("renamed", "changed purpose", 7, false);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/data-sources/{foreign.Id}/metadata", request)).StatusCode);
        var response = await client.PutAsJsonAsync($"/api/data-sources/{original.Id}/metadata", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secretref", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionSecretReference", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("Postgres", document.RootElement.GetProperty("kind").GetString());
        Assert.False(document.RootElement.GetProperty("allowRead").GetBoolean());
        await using var verify = factory.Services.CreateAsyncScope();
        var stored = await verify.ServiceProvider.GetRequiredService<PlatformDbContext>().DataSources.AsNoTracking().SingleAsync(row => row.Id == original.Id);
        Assert.Equal("renamed", stored.LogicalName);
        Assert.Equal("secretref://env/fixture-rotated", stored.ConnectionSecretReference);
    }

    [Theory]
    [InlineData("logicalName", " ")]
    [InlineData("purpose", " ")]
    [InlineData("maxConcurrency", 0)]
    [InlineData("maxConcurrency", 1025)]
    [InlineData("maxConcurrency", "7")]
    public async Task MetadataContract_RejectsInvalidValues(string field, object value)
    {
        var context = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(context, ["admin"]));
        var original = DataSource(context.TenantId, context.CompanyId, "original", "secretref://env/fixture-original");
        await SeedDataSourcesAsync(factory, original);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, context.CompanyId.ToString());
        var payload = new Dictionary<string, object?> { ["logicalName"] = "changed", ["purpose"] = "changed", ["maxConcurrency"] = 7, ["isEnabled"] = false };
        payload[field] = value;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/data-sources/{original.Id}/metadata", payload)).StatusCode);
    }

    [Fact]
    public async Task MetadataContract_FailsClosedWhenAuthenticationUnavailable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AIOffice:Authentication:Authority"] = null, ["AIOffice:Authentication:Audience"] = null })));
        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/data-sources/{Guid.NewGuid()}/metadata", new DataSourceMetadataWriteRequest("name", "purpose", 1, false));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task MetadataContract_CrossTenantAndMissingIdsReturnNotFoundWithoutMutation()
    {
        var context = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(context, ["admin"]));
        var foreign = DataSource(Guid.NewGuid(), context.CompanyId, "foreign-tenant", "secretref://env/foreign-tenant");
        await SeedDataSourcesAsync(factory, foreign);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, context.CompanyId.ToString());
        var request = new DataSourceMetadataWriteRequest("denied", "denied", 1, false);
        foreach (var id in new[] { foreign.Id, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/data-sources/{id}/metadata", request)).StatusCode);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equivalent(foreign, await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().DataSources.AsNoTracking().SingleAsync(), strict: true);
    }

    [Fact]
    public async Task MetadataContract_RequiresActiveDirectoryEntryAndMapsDuplicateNameToConflict()
    {
        await using (var deniedFactory = AuthenticatedFactory(null))
        {
            using var deniedClient = deniedFactory.CreateClient();
            deniedClient.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Forbidden, (await deniedClient.PutAsJsonAsync($"/api/data-sources/{Guid.NewGuid()}/metadata", new DataSourceMetadataWriteRequest("denied", "denied", 1, false))).StatusCode);
        }
        var context = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var factory = AuthenticatedFactory(new AuthenticatedAuthorizationEntry(context, ["admin"]));
        var first = DataSource(context.TenantId, context.CompanyId, "first", "secretref://env/first");
        var second = DataSource(context.TenantId, context.CompanyId, "second", "secretref://env/second");
        await SeedDataSourcesAsync(factory, first, second);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, context.CompanyId.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/data-sources/{first.Id}/metadata", new DataSourceMetadataWriteRequest("second", "changed", 1, false))).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().DataSources.AsNoTracking().SingleAsync(row => row.Id == first.Id);
        Assert.Equivalent(first, stored, strict: true);
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
                services.AddScoped<DataSourceRegistryService>();
                services.AddScoped<IDataSourceConnectionProbe, SqlDataSourceConnectionProbe>();
                services.AddScoped<DataSourceConnectionTestService>();
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
                new Claim(AuthenticationClaimTypes.Subject, "test-subject"),
                new Claim(ClaimTypes.Role, "admin")
            ], AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme)));
        }
    }
}
