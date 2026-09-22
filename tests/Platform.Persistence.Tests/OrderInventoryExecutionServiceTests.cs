using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OrderInventoryExecutionServiceTests
{
    [Fact]
    public async Task Authorized_write_is_audited_before_writer_and_reconciled()
    {
        var fixture = Fixture.Create();
        var calls = 0;
        var result = await fixture.Service.ExecuteAsync(
            fixture.Preview, fixture.Authorization, [fixture.Permission],
            _ =>
            {
                calls++;
                var audit = Assert.Single(fixture.Audit.Entries);
                Assert.True(audit.Authorized);
                Assert.Equal("order-exec-001", audit.ExecutionId);
                return Task.FromResult(fixture.Result);
            },
            "order-exec-001");
        Assert.Equal(1, calls);
        Assert.Equal(OrderInventoryReconciliationState.Matched, result.ReconciliationState);
    }

    [Fact]
    public async Task Duplicate_idempotency_identity_executes_writer_once()
    {
        var fixture = Fixture.Create();
        var calls = 0;
        Task<OrderInventoryExecutionResult> Write(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(fixture.Result);
        }
        var results = await Task.WhenAll(
            fixture.Service.ExecuteAsync(fixture.Preview, fixture.Authorization, [fixture.Permission], Write),
            fixture.Service.ExecuteAsync(fixture.Preview, fixture.Authorization, [fixture.Permission], Write));
        Assert.Equal(1, calls);
        Assert.Equal(results[0], results[1]);
        Assert.Single(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Missing_write_permission_is_audited_and_never_writes()
    {
        var fixture = Fixture.Create();
        var executed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Preview, fixture.Authorization, [], _ =>
            {
                executed = true;
                return Task.FromResult(fixture.Result);
            }));
        Assert.False(executed);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.False(audit.Authorized);
    }

    [Fact]
    public async Task Cross_company_authority_is_rejected_before_audit_or_writer()
    {
        var fixture = Fixture.Create();
        var wrong = fixture.Authorization with { CompanyId = Guid.NewGuid() };
        var executed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Preview, wrong, [fixture.Permission], _ =>
            {
                executed = true;
                return Task.FromResult(fixture.Result);
            }));
        Assert.False(executed);
        Assert.Empty(fixture.Audit.Entries);
    }

    private sealed record Fixture(
        OrderInventoryExecutionService Service,
        RecordingAuditSink Audit,
        OrderInventoryPreview Preview,
        ToolAuthorizationRequest Authorization,
        ToolPermission Permission,
        OrderInventoryExecutionResult Result)
    {
        public static Fixture Create()
        {
            var tenant = Guid.NewGuid();
            var company = Guid.NewGuid();
            var dataSource = Guid.NewGuid();
            var user = Guid.NewGuid();
            var task = Guid.NewGuid();
            var preview = new OrderInventoryPreview(
                new OrderInventoryAuthority(tenant, company, dataSource, "order.inventory.execute", "erp-2026.09", "schema-42", "catalog-7"),
                "SO-001", "WH-A", [new OrderInventoryLine("SKU-1", 2m, "EA")], "audit-97");
            var resource = OrderInventoryExecutionService.ResourceFor(dataSource);
            var authorization = new ToolAuthorizationRequest(tenant, company, user, task, resource, OrderInventoryExecutionService.WriteAction, ToolRiskLevel.High);
            var permission = new ToolPermission(tenant, company, user, resource, OrderInventoryExecutionService.WriteAction, ToolRiskLevel.High);
            var result = new OrderInventoryExecutionResult(
                tenant, company, dataSource, preview.IdempotencyKey, "execution-1", "evidence-1", "erp-2026.09", "schema-42", "catalog-7",
                new Dictionary<string, decimal> { ["SKU-1"] = 2m }, OrderInventoryReconciliationState.Matched, preview.AuditCorrelationId);
            var audit = new RecordingAuditSink();
            var gate = new AuthorizedToolExecutionGate(new ToolAuthorizationPolicy(), new ToolExecutionAuditService(audit));
            return new Fixture(new OrderInventoryExecutionService(gate), audit, preview, authorization, permission, result);
        }
    }

    private sealed class RecordingAuditSink : IToolExecutionAuditSink
    {
        public List<ToolExecutionAuditEntry> Entries { get; } = [];
        public Task AppendAsync(ToolExecutionAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
