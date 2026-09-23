using MinhHuy.AIOffice.Platform.Persistence;
using Platform.Persistence;
using Xunit;

namespace Platform.Persistence.Tests;

public sealed class CustomerAuditProjectionTests
{
    [Fact]
    public async Task ListAsync_OrdersDeterministicallyAndPaginates()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.Parse("2026-09-23T12:00:00Z");
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var newestId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var entries = new[]
        {
            Entry(secondId, tenantId, companyId, userId, occurredAt),
            Entry(newestId, tenantId, companyId, userId, occurredAt.AddMinutes(1)),
            Entry(firstId, tenantId, companyId, userId, occurredAt)
        };
        var projection = new CustomerAuditProjection(new StubStore(entries));

        var page = await projection.ListAsync(new CustomerAuditQuery(tenantId, companyId, userId, Offset: 1, Limit: 2));

        Assert.Equal(new[] { firstId, secondId }, page.Select(item => item.AuditId));
        Assert.All(page, item => Assert.DoesNotContain("secret", item.Resource, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListAsync_FailsClosedWhenStoreLeaksAnotherAuthority()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var leaked = Entry(Guid.NewGuid(), tenantId, Guid.NewGuid(), userId, DateTimeOffset.UtcNow);
        var projection = new CustomerAuditProjection(new StubStore([leaked]));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            projection.ListAsync(new CustomerAuditQuery(tenantId, companyId, userId)));
    }

    [Fact]
    public async Task ListAsync_RejectsIncompleteAuthorityAndInvalidPaging()
    {
        var projection = new CustomerAuditProjection(new StubStore([]));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            projection.ListAsync(new CustomerAuditQuery(Guid.Empty, Guid.NewGuid(), Guid.NewGuid())));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            projection.ListAsync(new CustomerAuditQuery(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Limit: 101)));
    }

    private static ToolExecutionAuditEntry Entry(Guid auditId, Guid tenantId, Guid companyId, Guid userId, DateTimeOffset occurredAt) =>
        new(auditId, tenantId, companyId, userId, Guid.NewGuid(), "accounting.invoice", "read", ToolRiskLevel.ReadOnly, true, "allowed", occurredAt, "exec-1");

    private sealed class StubStore(IReadOnlyList<ToolExecutionAuditEntry> entries) : ICustomerAuditStore
    {
        public Task<IReadOnlyList<ToolExecutionAuditEntry>> ListAsync(Guid tenantId, Guid companyId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(entries);
    }
}
