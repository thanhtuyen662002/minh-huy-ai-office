using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Core.Api.Authorization;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class CoreApiDataSourceAuthorizationIntegrationTests
{
    [Fact]
    public async Task IdentityCompanyDirectoryRequiresNoCompanySelectorAndPublishesOnlyActiveOwnChoices()
    {
        var authority = MemberDirectoryFixture.Authority(); await using var factory = AuthenticatedFactory(new(authority, ["admin"]));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, authority);
            var user = db.Users.Local.Single(); user.IdentityProvider = "test-oidc"; user.Subject = "test-subject";
            MemberDirectoryFixture.Seed(db, MemberDirectoryFixture.Authority()); db.Companies.Local.Last().Name = "PRIVATE_FOREIGN_COMPANY";
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        // Forged selector/identity/roles cannot widen this pre-selection endpoint.
        client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-AIOffice-Subject", "PRIVATE_FOREIGN_SUBJECT");
        var response = await client.GetAsync("/api/auth/companies?tenantId=" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_", body); Assert.DoesNotContain("test-subject", body);
        using var parsed = JsonDocument.Parse(body); var item = Assert.Single(parsed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "companyId", "companyName" }, item.EnumerateObject().Select(property => property.Name));
        Assert.Equal(authority.CompanyId, item.GetProperty("companyId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/auth/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/company/members")).StatusCode);
        client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/auth/context")).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            (await db.CompanyMemberships.SingleAsync(row => row.CompanyId == authority.CompanyId)).IsActive = false; await db.SaveChangesAsync();
        }
        using var revoked = JsonDocument.Parse(await (await client.GetAsync("/api/auth/companies")).Content.ReadAsStringAsync());
        Assert.Empty(revoked.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task IdentityCompanyDirectoryDeniesMalformedCatalogWithoutPrivateDiagnostics()
    {
        var authority = MemberDirectoryFixture.Authority(); await using var factory = AuthenticatedFactory(new(authority, ["admin"]));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, authority);
            var user = db.Users.Local.Single(); user.IdentityProvider = "test-oidc"; user.Subject = "test-subject";
            db.Companies.Local.Single().Name = "PRIVATE_INVALID_NAME\ud800"; await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient(); var response = await client.GetAsync("/api/auth/companies");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain("PRIVATE_", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/auth/companies")]
    [InlineData("/api/auth/companies/")]
    [InlineData("/API/AUTH/COMPANIES/")]
    public async Task UnconfiguredIdentityCompanyDirectoryFailsClosedWithNoStore(string path)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", "");
        });
        using var client = factory.CreateClient(); var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("/api/auth/companies/")]
    [InlineData("/API/AUTH/COMPANIES/")]
    public async Task RoutedDirectoryAliasesKeepSuccessAndDenialPrivate(string path)
    {
        var authority = MemberDirectoryFixture.Authority(); await using var factory = AuthenticatedFactory(new(authority, ["admin"]));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, authority);
            var user = db.Users.Local.Single(); user.IdentityProvider = "test-oidc"; user.Subject = "test-subject"; await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient(); var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); (await db.Companies.SingleAsync()).Name = "\nPRIVATE"; await db.SaveChangesAsync();
        }
        response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }
}
