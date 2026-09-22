using MinhHuy.AiOffice.Shared.Contracts.Erp;
using MinhHuy.AIOffice.Platform.Persistence;
using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class AccountingInvestigationExecutionServiceTests
{
    [Fact]
    public async Task Authorized_read_is_audited_before_deterministic_investigation()
    {
        var fixture = Fixture.Create();
        var executed = false;

        var result = await fixture.Service.ExecuteAsync(
            fixture.Request,
            fixture.Catalog,
            fixture.Authorization,
            [fixture.Permission],
            _ =>
            {
                executed = true;
                var audit = Assert.Single(fixture.Audit.Entries);
                Assert.True(audit.Authorized);
                Assert.Equal("investigation-001", audit.ExecutionId);
                return Task.FromResult(fixture.Result);
            },
            "investigation-001");

        Assert.True(executed);
        Assert.Equal("audit-001", result.AuditCorrelationId);
        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task Missing_permission_fails_closed_and_never_runs_investigation()
    {
        var fixture = Fixture.Create();
        var executed = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Request,
            fixture.Catalog,
            fixture.Authorization,
            [],
            _ =>
            {
                executed = true;
                return Task.FromResult(fixture.Result);
            }));

        Assert.False(executed);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.False(audit.Authorized);
        Assert.Equal("no_permission", audit.DecisionReason);
    }

    [Fact]
    public async Task Cross_company_task_authority_is_rejected_before_audit_or_execution()
    {
        var fixture = Fixture.Create();
        var wrongAuthority = fixture.Authorization with { CompanyId = Guid.NewGuid() };
        var executed = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Request,
            fixture.Catalog,
            wrongAuthority,
            [fixture.Permission],
            _ =>
            {
                executed = true;
                return Task.FromResult(fixture.Result);
            }));

        Assert.False(executed);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Write_or_wrong_data_source_scope_is_rejected_before_execution()
    {
        var fixture = Fixture.Create();
        var wrongScope = fixture.Authorization with { Action = "post.write" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Request,
            fixture.Catalog,
            wrongScope,
            [fixture.Permission],
            _ => Task.FromResult(fixture.Result)));

        Assert.Empty(fixture.Audit.Entries);
    }

    private sealed record Fixture(
        AccountingInvestigationExecutionService Service,
        RecordingAuditSink Audit,
        AccountingInvestigationRequest Request,
        ErpCatalog Catalog,
        ToolAuthorizationRequest Authorization,
        ToolPermission Permission,
        AccountingInvestigationResult Result)
    {
        public static Fixture Create()
        {
            var tenantId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            const string dataSourceId = "erp-main";
            var resource = AccountingInvestigationExecutionService.ResourceFor(dataSourceId);
            var audit = new RecordingAuditSink();
            var gate = new AuthorizedToolExecutionGate(
                new ToolAuthorizationPolicy(),
                new ToolExecutionAuditService(audit));
            var request = new AccountingInvestigationRequest(
                tenantId.ToString("D"),
                companyId.ToString("D"),
                dataSourceId,
                "What is the current inventory balance?",
                ["inventory.read"]);
            var catalog = new ErpCatalog(
                request.TenantId,
                request.CompanyId,
                dataSourceId,
                "minh-huy-erp",
                "2026.09",
                12,
                [new ErpCatalogItem(ErpCatalogItemKind.DatabaseObject, "dbo.Inventory", "12", "Table:dbo.Inventory")],
                [new ErpCapability("inventory.read", "1")],
                [new ErpFeature("inventory.balance", "1", ["inventory.read"])]);
            var authorization = new ToolAuthorizationRequest(
                tenantId,
                companyId,
                userId,
                taskId,
                resource,
                AccountingInvestigationExecutionService.ReadAction,
                ToolRiskLevel.Low);
            var permission = new ToolPermission(
                tenantId,
                companyId,
                userId,
                resource,
                AccountingInvestigationExecutionService.ReadAction,
                ToolRiskLevel.Low);
            var result = new AccountingInvestigationResult(
                request.TenantId,
                request.CompanyId,
                dataSourceId,
                "Inventory evidence confirms the balance.",
                [new AccountingEvidence("dbo.Inventory", "inventory:42", "Balance row 42")],
                "audit-001");

            return new Fixture(
                new AccountingInvestigationExecutionService(gate),
                audit,
                request,
                catalog,
                authorization,
                permission,
                result);
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
