using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class CoreApiDataSourceAuthorizationIntegrationTests
{
    [Theory]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("ADMIN", HttpStatusCode.Forbidden)]
    public async Task MemberDirectoryUsesFreshServerRoleAndSafeDto(string role, HttpStatusCode expected)
    {
        var authority = MemberDirectoryFixture.Authority();
        await using var fixture = new MemberApiFixture(authority);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, authority, role);
            MemberDirectoryFixture.AddMember(db, MemberDirectoryFixture.Authority(), Guid.NewGuid(), "FOREIGN", roles: ["admin"]); await db.SaveChangesAsync();
        }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());
        var response = await client.GetAsync("/api/company/members?offset=0&limit=25");
        Assert.Equal(expected, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_", body); Assert.DoesNotContain("FOREIGN", body);
        if (expected == HttpStatusCode.OK)
        {
            using var parsed = JsonDocument.Parse(body); var item = Assert.Single(parsed.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(new[] { "userId", "displayName", "userActive", "membershipActive", "roles" }, item.EnumerateObject().Select(property => property.Name));
            Assert.Equal("admin", Assert.Single(item.GetProperty("roles").EnumerateArray()).GetString());
        }
        client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());
        response = await client.GetAsync("/api/company/members"); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }
    [Fact]
    public async Task IssuedAdminSessionCannotReadMembersAfterRoleRevocation()
    {
        var authority = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(authority);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, authority); await db.SaveChangesAsync(); }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/company/members")).StatusCode);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); db.RoleAssignments.RemoveRange(await db.RoleAssignments.ToArrayAsync()); await db.SaveChangesAsync(); }
        var denied = await client.GetAsync("/api/company/members"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.True(denied.Headers.CacheControl!.NoStore);
    }
    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=1001")]
    [InlineData("limit=101")]
    [InlineData("limit=garbage")]
    public async Task InvalidMemberPaginationReturnsBoundedNoStoreError(string query)
    {
        var authority = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(authority);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, authority); await db.SaveChangesAsync(); }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());
        var response = await client.GetAsync("/api/company/members?" + query); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }
    [Fact]
    public async Task UnconfiguredMemberDirectoryFailsClosedAndNoStore()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => { builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", ""); });
        using var client = factory.CreateClient(); var response = await client.GetAsync("/api/company/members"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }
    private sealed class MemberApiFixture : IAsyncDisposable
    {
        private readonly string variable = "AIOFFICE254_" + Guid.NewGuid().ToString("N");
        public WebApplicationFactory<Program> Factory { get; }
        public MemberApiFixture(AuthorizationContext authority)
        {
            Environment.SetEnvironmentVariable(variable, "synthetic-test-not-a-real-connection");
            Factory = AuthenticatedFactory(new(authority, ["admin"]), "secretref://env/" + variable, useRealDirectory: true);
        }
        public async ValueTask DisposeAsync() { await Factory.DisposeAsync(); Environment.SetEnvironmentVariable(variable, null); }
    }
}
