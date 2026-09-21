using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using Platform.Persistence;

namespace Platform.Persistence.Tests;

public sealed class TrustedToolAuthorizationRequestFactoryTests
{
    [Fact]
    public async Task CreateAsync_derives_user_from_durable_task_and_uses_explicit_tool_scope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Tasks.Add(new TaskRecord
        {
            TenantId = tenantId,
            CompanyId = companyId,
            Id = taskId,
            CreatedByUserId = userId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var factory = new TrustedToolAuthorizationRequestFactory(db);
        var request = await factory.CreateAsync(
            tenantId,
            companyId,
            taskId,
            new TrustedToolExecutionMetadata("erp.invoice", "post", ToolRiskLevel.High));

        Assert.Equal(tenantId, request.TenantId);
        Assert.Equal(companyId, request.CompanyId);
        Assert.Equal(taskId, request.TaskId);
        Assert.Equal(userId, request.UserId);
        Assert.Equal("erp.invoice", request.Resource);
        Assert.Equal("post", request.Action);
        Assert.Equal(ToolRiskLevel.High, request.Risk);
    }

    [Fact]
    public async Task CreateAsync_fails_closed_when_task_is_not_in_selected_company()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        db.Tasks.Add(new TaskRecord
        {
            TenantId = tenantId,
            CompanyId = Guid.NewGuid(),
            Id = taskId,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var factory = new TrustedToolAuthorizationRequestFactory(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => factory.CreateAsync(
            tenantId,
            companyId,
            taskId,
            new TrustedToolExecutionMetadata("erp.invoice", "post", ToolRiskLevel.Low)));
    }

    [Fact]
    public async Task CreateAsync_fails_closed_when_explicit_tool_scope_is_missing()
    {
        await using var db = CreateDb();
        var factory = new TrustedToolAuthorizationRequestFactory(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => factory.CreateAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new TrustedToolExecutionMetadata(" ", "post", ToolRiskLevel.Low)));
    }

    private static PlatformDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new PlatformDbContext(options);
    }
}
