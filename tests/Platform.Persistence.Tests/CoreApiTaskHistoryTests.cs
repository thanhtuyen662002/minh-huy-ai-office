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
    [Fact]
    public async Task TaskArchivePublishesOnlyOwnerAllowlistedDataAndRevocationDeniesBothReads()
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        Guid taskId; Guid otherId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, owner);
            var other = AuthorizationContext.Create(owner.TenantId, owner.CompanyId, Guid.NewGuid());
            MemberDirectoryFixture.AddMember(db, owner, other.UserId, "Other");
            taskId = TaskHistoryFixture.Add(db, owner, TaskExecutionStatus.Completed, checkpoint: TaskHistoryFixture.Checkpoint());
            otherId = TaskHistoryFixture.Add(db, other, request: TaskHistoryFixture.Request("PRIVATE_OTHER_QUESTION"));
            await db.SaveChangesAsync();
        }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, owner.CompanyId.ToString());
        client.DefaultRequestHeaders.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString());
        var response = await client.GetAsync("/api/tasks?offset=0&limit=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_", body);
        using var page = JsonDocument.Parse(body); var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(taskId, item.GetProperty("taskId").GetGuid());
        Assert.Equal(new[] { "taskId", "status", "createdAtUtc", "updatedAtUtc", "summary", "metadataUnavailable" }, item.EnumerateObject().Select(property => property.Name));
        var detail = await client.GetAsync($"/api/tasks/{taskId}/history"); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.True(detail.Headers.CacheControl!.NoStore); var detailBody = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PayloadJson", detailBody); Assert.DoesNotContain("PRIVATE_", detailBody); Assert.DoesNotContain("databaseName", detailBody);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{otherId}/history")).StatusCode);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            (await db.CompanyMemberships.SingleAsync(member => member.UserId == owner.UserId)).IsActive = false; await db.SaveChangesAsync();
        }
        foreach (var path in new[] { "/api/tasks", $"/api/tasks/{taskId}/history" })
        {
            var denied = await client.GetAsync(path); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl!.NoStore); Assert.DoesNotContain("answer", await denied.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("offset=01")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=garbage")]
    [InlineData("offset=0&offset=1")]
    [InlineData("ownerId=00000000-0000-0000-0000-000000000001")]
    [InlineData("tenantId=00000000-0000-0000-0000-000000000001")]
    public async Task InvalidArchivePageIsBoundedNoStore400(string query)
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        { var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); MemberDirectoryFixture.Seed(db, owner); await db.SaveChangesAsync(); }
        using var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, owner.CompanyId.ToString());
        var response = await client.GetAsync("/api/tasks?" + query); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore); Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/tasks")]
    [InlineData("/api/tasks/00000000-0000-0000-0000-000000000001/history")]
    public async Task UnconfiguredArchiveFailsClosedAndNoStore(string path)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        { builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", ""); });
        using var client = factory.CreateClient(); var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
    }
}
