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
    public async Task MemberAccessApiMutatesWithExactReplayAndSafeNoStoreResult()
    {
        var scope = MemberDirectoryFixture.Authority(); var target = Guid.NewGuid();
        await using var fixture = new MemberApiFixture(scope);
        await using (var services = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = services.ServiceProvider.GetRequiredService<PlatformDbContext>();
            MemberDirectoryFixture.Seed(db, scope); MemberDirectoryFixture.AddMember(db, scope, target, "Member", roles: ["viewer"]); await db.SaveChangesAsync();
        }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, scope.CompanyId.ToString());
        var path = $"/api/company/members/{target}/access";
        var request = new CompanyMembershipAccessRequest(Guid.NewGuid(), "1", false);
        var response = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var result = await response.Content.ReadFromJsonAsync<CompanyMembershipAccessResult>();
        Assert.Equal(scope.CompanyId, result!.CompanyId); Assert.Equal(target, result.UserId); Assert.False(result.MembershipActive); Assert.Equal("2", result.MembershipVersion);
        Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync());
        response = await client.PostAsJsonAsync(path, request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(result, await response.Content.ReadFromJsonAsync<CompanyMembershipAccessResult>());
        response = await client.PostAsJsonAsync(path, request with { OperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        response = await client.PostAsJsonAsync(path, new CompanyMembershipAccessRequest(Guid.NewGuid(), "2", true)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var accessPage = JsonDocument.Parse(await client.GetStringAsync("/api/company/members?includeAccessVersion=true"));
        var targetMember = accessPage.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("userId").GetGuid() == target);
        Assert.True(targetMember.GetProperty("membershipActive").GetBoolean()); Assert.Equal("3", targetMember.GetProperty("membershipVersion").GetString());
        using var legacyPage = JsonDocument.Parse(await client.GetStringAsync("/api/company/members"));
        Assert.All(legacyPage.RootElement.GetProperty("items").EnumerateArray(), item => Assert.False(item.TryGetProperty("membershipVersion", out _)));
        await using var check = fixture.Factory.Services.CreateAsyncScope();
        Assert.Equal(2, await check.ServiceProvider.GetRequiredService<PlatformDbContext>().CompanyMembershipAccessAudits.CountAsync());
    }
    [Theory]
    [InlineData("viewer")]
    [InlineData("ADMIN")]
    public async Task MemberAccessApiRejectsTokenOnlyAdminAuthority(string role)
    {
        var scope = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(scope);
        await using (var services = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = services.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, scope, role); await db.SaveChangesAsync();
        }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, scope.CompanyId.ToString());
        var response = await client.PostAsJsonAsync($"/api/company/members/{Guid.NewGuid()}/access", new CompanyMembershipAccessRequest(Guid.NewGuid(), "1", false));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"operationId\":\"00000000-0000-0000-0000-000000000000\",\"expectedVersion\":\"1\",\"isActive\":true}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":1,\"isActive\":true}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":\"1\",\"isActive\":true,\"roles\":[\"admin\"]}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":\"1\",\"isActive\":false,\"isActive\":true}")]
    [InlineData("{\"operationId\":\"d505f503-8939-4c14-9f61-ef9616b8fcbc\",\"expectedVersion\":\"1\",\"IsActive\":true}")]
    [InlineData("null")]
    public async Task MemberAccessApiRejectsMalformedOrAuthorityFields(string body)
    {
        var scope = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(scope);
        await using (var services = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = services.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, scope); await db.SaveChangesAsync();
        }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, scope.CompanyId.ToString());
        var response = await client.PostAsync($"/api/company/members/{Guid.NewGuid()}/access", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        await using var check = fixture.Factory.Services.CreateAsyncScope(); Assert.Empty(check.ServiceProvider.GetRequiredService<PlatformDbContext>().CompanyMembershipAccessAudits);
    }
    [Fact]
    public async Task MemberAccessApiBoundsBodyAndPreservesSelfAndForeignMemberships()
    {
        var scope = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(scope);
        await using (var services = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = services.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, scope); await db.SaveChangesAsync();
        }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, scope.CompanyId.ToString());
        var path = $"/api/company/members/{scope.UserId}/access";
        var response = await client.PostAsync(path, new StringContent(new string(' ', 2049), Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        response = await client.PostAsJsonAsync(path, new CompanyMembershipAccessRequest(Guid.NewGuid(), "1", false)); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        response = await client.PostAsJsonAsync($"/api/company/members/{Guid.NewGuid()}/access", new CompanyMembershipAccessRequest(Guid.NewGuid(), "1", false)); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        client.DefaultRequestHeaders.Remove(AuthorizationHeaders.CompanyId); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, Guid.NewGuid().ToString());
        response = await client.PostAsJsonAsync(path, new CompanyMembershipAccessRequest(Guid.NewGuid(), "1", false)); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }
    [Fact]
    public async Task MemberAccessWithoutConfigurationIsUnavailable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => { builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", ""); });
        using var client = factory.CreateClient(); var response = await client.PostAsJsonAsync($"/api/company/members/{Guid.NewGuid()}/access", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }
}
