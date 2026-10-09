using System.Net;
using System.Net.Http.Json;
using System.Text;
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
    public async Task AdministratorApiGrantRemoveReplayAndFreshDirectoryKeepOtherRoles()
    {
        var scope = MemberDirectoryFixture.Authority(); var target = Guid.NewGuid();
        await using var fixture = new MemberApiFixture(scope);
        await SeedAdministratorFixture(fixture, scope, target);
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, scope.CompanyId.ToString());
        var path = $"/api/company/members/{target}/administrator";
        var request = new CompanyAdministratorRequest(Guid.NewGuid(), "1", true);
        var response = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var grant = await response.Content.ReadFromJsonAsync<CompanyAdministratorResult>();
        Assert.Equal(scope.CompanyId, grant!.CompanyId); Assert.Equal(target, grant.UserId); Assert.True(grant.IsAdministrator); Assert.Equal("2", grant.MembershipVersion);
        Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync());
        response = await client.PostAsJsonAsync(path, request); Assert.Equal(grant, await response.Content.ReadFromJsonAsync<CompanyAdministratorResult>());
        response = await client.PostAsJsonAsync(path, request with { OperationId = Guid.NewGuid() }); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        response = await client.PostAsJsonAsync(path, new CompanyAdministratorRequest(Guid.NewGuid(), "2", false)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response = await client.PostAsJsonAsync(path, request); Assert.Equal(grant, await response.Content.ReadFromJsonAsync<CompanyAdministratorResult>());
        using var page = JsonDocument.Parse(await client.GetStringAsync("/api/company/members?includeAccessVersion=true"));
        var member = page.RootElement.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("userId").GetGuid() == target);
        Assert.True(member.GetProperty("membershipActive").GetBoolean()); Assert.Equal("3", member.GetProperty("membershipVersion").GetString());
        Assert.Equal(new[] { "viewer" }, member.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        await using var check = fixture.Factory.Services.CreateAsyncScope();
        Assert.Equal(2, await check.ServiceProvider.GetRequiredService<PlatformDbContext>().CompanyAdministratorAudits.CountAsync());
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("ADMIN")]
    [InlineData("admin ")]
    public async Task AdministratorApiDoesNotTrustTokenRolesOrAlias(string role)
    {
        var scope = MemberDirectoryFixture.Authority(); var target = Guid.NewGuid(); await using var fixture = new MemberApiFixture(scope);
        await SeedAdministratorFixture(fixture, scope, target, role);
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, scope.CompanyId.ToString());
        var response = await client.PostAsJsonAsync($"/api/company/members/{target}/administrator", new CompanyAdministratorRequest(Guid.NewGuid(), "1", true));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"operationId\":\"00000000-0000-0000-0000-000000000000\",\"expectedVersion\":\"1\",\"isAdministrator\":true}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":1,\"isAdministrator\":true}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":\"1\",\"IsAdministrator\":true}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":\"1\",\"isAdministrator\":false,\"isAdministrator\":true}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":\"1\",\"isAdministrator\":true,\"roles\":[\"admin\"]}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":\"1\",\"isAdministrator\":true,\"tenantId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\"}")]
    public async Task AdministratorApiStrictBodyRejectsAuthorityAndDuplicateFields(string body)
    {
        var scope = MemberDirectoryFixture.Authority(); var target = Guid.NewGuid(); await using var fixture = new MemberApiFixture(scope);
        await SeedAdministratorFixture(fixture, scope, target);
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, scope.CompanyId.ToString());
        var response = await client.PostAsync($"/api/company/members/{target}/administrator", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        await using var check = fixture.Factory.Services.CreateAsyncScope(); Assert.Empty(check.ServiceProvider.GetRequiredService<PlatformDbContext>().CompanyAdministratorAudits);
    }

    [Fact]
    public async Task AdministratorApiBoundsRequestsAndProtectsSelfForeignAndCurrentCompany()
    {
        var scope = MemberDirectoryFixture.Authority(); var target = Guid.NewGuid(); await using var fixture = new MemberApiFixture(scope);
        await SeedAdministratorFixture(fixture, scope, target);
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, scope.CompanyId.ToString());
        var path = $"/api/company/members/{target}/administrator";
        var response = await client.PostAsync(path, new StringContent(new string(' ', 2049), Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        response = await client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "text/plain")); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        response = await client.PostAsJsonAsync($"/api/company/members/{scope.UserId}/administrator", new CompanyAdministratorRequest(Guid.NewGuid(), "1", false)); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        response = await client.PostAsJsonAsync($"/api/company/members/{Guid.NewGuid()}/administrator", new CompanyAdministratorRequest(Guid.NewGuid(), "1", true)); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());
        response = await client.PostAsJsonAsync(path, new CompanyAdministratorRequest(Guid.NewGuid(), "1", true)); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task AdministratorWithoutConfigurationIsUnavailable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => { builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", ""); });
        using var client = factory.CreateClient(); var response = await client.PostAsJsonAsync($"/api/company/members/{Guid.NewGuid()}/administrator", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }

    private static async Task SeedAdministratorFixture(MemberApiFixture fixture, Shared.Contracts.AuthorizationContext scope, Guid target, string role = "admin")
    {
        await using var services = fixture.Factory.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        MemberDirectoryFixture.Seed(db, scope, role); MemberDirectoryFixture.AddMember(db, scope, target, "Member", roles: ["viewer"]); await db.SaveChangesAsync();
    }
}
