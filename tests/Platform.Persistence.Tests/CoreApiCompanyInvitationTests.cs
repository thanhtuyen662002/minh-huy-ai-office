using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CoreApiCompanyInvitationTests
{
    [Theory]
    [InlineData(false, false, true, 201)]
    [InlineData(true, false, true, 201)]
    [InlineData(false, true, true, 503)]
    [InlineData(false, false, true, 502)]
    [InlineData(false, false, false, 403)]
    public async Task Provisioning_response_is_private_once_and_failures_do_not_create_invitations(bool exists, bool failure, bool admin, int status)
    {
        var authority = MemberDirectoryFixture.Authority();
        var keycloak = new CompanyInvitationProvisioningTests.FakeKeycloak { Exists = exists, FailLookup = failure, FailCreate = status == 502 };
        await using var fixture = new InvitationApiFixture(authority, keycloak);
        await fixture.SeedAsync(admin ? "admin" : "viewer");
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());
        var response = await client.PostAsJsonAsync("/api/company/invitations", new { email = "member@example.com", roles = new[] { "viewer" } });
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        if (status == 201)
        {
            if (exists) Assert.DoesNotContain("temporaryPassword", body);
            else Assert.Contains(keycloak.Password!, body);
            var listing = await client.GetStringAsync("/api/company/invitations");
            Assert.DoesNotContain("temporaryPassword", listing);
            if (keycloak.Password is not null) Assert.DoesNotContain(keycloak.Password, listing);
        }
        else
        {
            if (keycloak.Password is not null) Assert.DoesNotContain(keycloak.Password, body);
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            Assert.Empty(scope.ServiceProvider.GetRequiredService<PlatformDbContext>().CompanyInvitations);
        }
        if (!admin) Assert.Equal(0, keycloak.Lookups);
    }

    [Fact]
    public async Task AdministratorApiCreatesListsRejectsDuplicateAndScopesRevoke()
    {
        var authority = MemberDirectoryFixture.Authority();
        await using var fixture = new InvitationApiFixture(authority);
        await fixture.SeedAsync("admin");
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());

        var createdResponse = await client.PostAsJsonAsync("/api/company/invitations", new { email = "Member@Example.com", roles = new[] { "viewer" } });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.True(createdResponse.Headers.CacheControl!.NoStore);
        var created = await createdResponse.Content.ReadFromJsonAsync<CompanyInvitationResult>();
        Assert.Equal("member@example.com", created!.Email);
        Assert.Equal($"/api/company/invitations/{created.Id:D}", createdResponse.Headers.Location!.OriginalString);

        var duplicate = await client.PostAsJsonAsync("/api/company/invitations", new { email = "member@example.com", roles = new[] { "viewer" } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("duplicate-invitation", await duplicate.Content.ReadAsStringAsync());
        var page = await client.GetFromJsonAsync<CompanyInvitationPage>("/api/company/invitations?offset=0&limit=25");
        Assert.Equal(created.Id, Assert.Single(page!.Items).Id);

        client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId);
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());
        var denied = await client.PostAsync($"/api/company/invitations/{created.Id:D}/revoke", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId);
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());
        var revoked = await client.PostAsync($"/api/company/invitations/{created.Id:D}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<CompanyInvitationPage>("/api/company/invitations"))!.Items);
    }

    [Fact]
    public async Task NonAdministratorApiIsDeniedByFreshStoredRole()
    {
        var authority = MemberDirectoryFixture.Authority();
        await using var fixture = new InvitationApiFixture(authority);
        await fixture.SeedAsync("viewer");
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());
        var response = await client.PostAsJsonAsync("/api/company/invitations", new { email = "member@example.com", roles = new[] { "viewer" } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<PlatformDbContext>().CompanyInvitations);
    }

    [Fact]
    public async Task CompanyDiscoveryAcceptsOnlyOneValidatedVerifiedEmailClaim()
    {
        var authority = MemberDirectoryFixture.Authority();
        await using var fixture = new InvitationApiFixture(authority);
        await fixture.SeedAsync("admin");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CompanyInvitationService>().CreateAsync(authority,
                new("member@example.com", ["viewer"]));
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", "member@example.com");
        client.DefaultRequestHeaders.Add("X-Test-Email-Verified", "false");
        Assert.Empty((await client.GetFromJsonAsync<CompanyDirectoryResponse>("/api/auth/companies"))!.Items);

        client.DefaultRequestHeaders.Remove("X-Test-Email-Verified");
        client.DefaultRequestHeaders.Add("X-Test-Email-Verified", "true");
        var companies = await client.GetFromJsonAsync<CompanyDirectoryResponse>("/api/auth/companies");
        Assert.Equal(authority.CompanyId, Assert.Single(companies!.Items).CompanyId);
        // The second discovery is a no-op because the opaque identity already exists.
        Assert.Equal(authority.CompanyId, Assert.Single((await client.GetFromJsonAsync<CompanyDirectoryResponse>("/api/auth/companies"))!.Items).CompanyId);
        await using var check = fixture.Factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Single(database.Users.Where(row => row.Subject == "invited-subject"));
        Assert.Equal(2, database.CompanyInvitationAudits.Count());
    }

    private sealed class InvitationApiFixture : IAsyncDisposable
    {
        private readonly string variable = "AIOFFICE294_" + Guid.NewGuid().ToString("N");
        public WebApplicationFactory<Program> Factory { get; }
        private readonly AuthorizationContext authority;

        public InvitationApiFixture(AuthorizationContext authority, IKeycloakAdminClient? keycloak = null)
        {
            this.authority = authority;
            Environment.SetEnvironmentVariable(variable, "synthetic-test-not-a-real-connection");
            var databaseName = "company-invitations-" + Guid.NewGuid().ToString("N");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.UseSetting("AIOffice:Authentication:Authority", "https://identity.example.invalid");
                builder.UseSetting("AIOffice:Authentication:Audience", "company-invitation-tests");
                builder.UseSetting("AIOffice:PlatformDatabase:ConnectionSecretRef", "secretref://env/" + variable);
                builder.ConfigureServices(services =>
                {
                    if (keycloak is not null)
                    {
                        services.RemoveAll<IKeycloakAdminClient>();
                        services.AddSingleton(keycloak);
                    }
                    services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = InvitationAuthenticationHandler.AuthenticationScheme;
                        options.DefaultChallengeScheme = InvitationAuthenticationHandler.AuthenticationScheme;
                    }).AddScheme<AuthenticationSchemeOptions, InvitationAuthenticationHandler>(InvitationAuthenticationHandler.AuthenticationScheme, _ => { });
                    services.RemoveAll<IAuthenticatedAuthorizationDirectory>();
                    services.AddScoped<IAuthenticatedAuthorizationDirectory>(_ => new ConstantAuthenticatedDirectory(
                        new(authority, ["admin"])));
                    services.RemoveAll<IAuthorizationDirectory>();
                    services.AddScoped<IAuthorizationDirectory, EfAuthorizationDirectory>();
                    services.RemoveAll<DbContextOptions<PlatformDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>();
                    services.RemoveAll<PlatformDbContext>();
                    services.AddDbContext<PlatformDbContext>(options => options.UseInMemoryDatabase(databaseName));
                    services.RemoveAll<IAuthenticatedCompanyDirectory>();
                    services.AddScoped<IAuthenticatedCompanyDirectory, EfAuthenticatedCompanyDirectory>();
                    var outbox = services.SingleOrDefault(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(MinhHuy.AIOffice.Core.Api.PilotTaskDispatchOutboxHostedService));
                    if (outbox is not null) services.Remove(outbox);
                });
            });
        }

        public async Task SeedAsync(string role)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            MemberDirectoryFixture.Seed(database, authority, role);
            await database.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private sealed class ConstantAuthenticatedDirectory(AuthenticatedAuthorizationEntry entry) : IAuthenticatedAuthorizationDirectory
    {
        public Task<AuthenticatedAuthorizationEntry?> ResolveAsync(string identityProvider, string subject, Guid companyId,
            CancellationToken cancellationToken = default) => Task.FromResult<AuthenticatedAuthorizationEntry?>(
                companyId == entry.Context.CompanyId ? entry : null);
    }

    private sealed record CompanyDirectoryResponse(AuthenticatedCompany[] Items);

    private sealed class InvitationAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationScheme = "CompanyInvitationTests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var email = Request.Headers["X-Test-Email"].FirstOrDefault() ?? "admin@example.com";
            var verified = Request.Headers["X-Test-Email-Verified"].FirstOrDefault() ?? "true";
            var identity = new ClaimsIdentity([
                new Claim(AuthenticationClaimTypes.IdentityProvider, "test-oidc"),
                new Claim(AuthenticationClaimTypes.Subject, email == "member@example.com" ? "invited-subject" : "test-subject"),
                new Claim("email", email), new Claim("email_verified", verified)
            ], AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme)));
        }
    }
}
