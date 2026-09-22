using MinhHuy.AiOffice.Shared.Contracts.Erp;
using MinhHuy.AIOffice.Platform.Persistence;
using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class AccountingPostingExecutionServiceTests
{
    [Fact]
    public async Task Authorized_write_is_audited_before_post_and_reconciled()
    {
        var fixture = Fixture.Create();
        var calls = 0;

        var result = await fixture.Service.ExecuteAsync(
            fixture.Preview,
            fixture.Catalog,
            fixture.Authorization,
            [fixture.Permission],
            _ =>
            {
                calls++;
                var audit = Assert.Single(fixture.Audit.Entries);
                Assert.True(audit.Authorized);
                Assert.Equal("posting-exec-001", audit.ExecutionId);
                return Task.FromResult(fixture.Result);
            },
            "posting-exec-001");

        Assert.Equal(1, calls);
        Assert.Equal(AccountingReconciliationState.Balanced, result.ReconciliationState);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_executes_writer_once()
    {
        var fixture = Fixture.Create();
        var calls = 0;

        Task<AccountingPostingExecutionResult> Post(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(fixture.Result);
        }

        var first = fixture.Service.ExecuteAsync(
            fixture.Preview, fixture.Catalog, fixture.Authorization, [fixture.Permission], Post);
        var second = fixture.Service.ExecuteAsync(
            fixture.Preview, fixture.Catalog, fixture.Authorization, [fixture.Permission], Post);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(results[0], results[1]);
        Assert.Single(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Missing_write_permission_is_audited_and_never_posts()
    {
        var fixture = Fixture.Create();
        var executed = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Preview,
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
    public async Task Cross_company_authority_is_rejected_before_audit_or_post()
    {
        var fixture = Fixture.Create();
        var wrongAuthority = fixture.Authorization with { CompanyId = Guid.NewGuid() };
        var executed = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Preview,
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
    public async Task Writer_cannot_claim_balanced_when_erp_totals_mismatch_preview()
    {
        var fixture = Fixture.Create();
        var falseBalanced = fixture.Result with { ActualCredit = 99m };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecuteAsync(
            fixture.Preview,
            fixture.Catalog,
            fixture.Authorization,
            [fixture.Permission],
            _ => Task.FromResult(falseBalanced)));
    }

    private sealed record Fixture(
        AccountingPostingExecutionService Service,
        RecordingAuditSink Audit,
        AccountingPostingPreview Preview,
        ErpCatalog Catalog,
        ToolAuthorizationRequest Authorization,
        ToolPermission Permission,
        AccountingPostingExecutionResult Result)
    {
        public static Fixture Create()
        {
            var tenantId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            const string dataSourceId = "erp-main";
            var resource = AccountingPostingExecutionService.ResourceFor(dataSourceId);
            var audit = new RecordingAuditSink();
            var gate = new AuthorizedToolExecutionGate(new ToolAuthorizationPolicy(), new ToolExecutionAuditService(audit));
            var catalog = new ErpCatalog(
                tenantId.ToString("D"), companyId.ToString("D"), dataSourceId, "minh-huy-erp", "2026.09", 12,
                [new ErpCatalogItem(ErpCatalogItemKind.DatabaseObject, "dbo.GL", "12", "Table:dbo.GL")],
                [new ErpCapability("accounting.post", "1")],
                [new ErpFeature("accounting.posting", "1", ["accounting.post"])]);
            var preview = new AccountingPostingPreview(
                catalog.TenantId, catalog.CompanyId, dataSourceId, catalog.InstalledVersion, catalog.SchemaSnapshotVersion,
                "accounting.post", "post:invoice:2026-0001",
                [new AccountingPostingLine("111", 100m, 0m, "Cash"), new AccountingPostingLine("511", 0m, 100m, "Revenue")]);
            var authorization = new ToolAuthorizationRequest(
                tenantId, companyId, userId, taskId, resource, AccountingPostingExecutionService.WriteAction, ToolRiskLevel.High);
            var permission = new ToolPermission(
                tenantId, companyId, userId, resource, AccountingPostingExecutionService.WriteAction, ToolRiskLevel.High);
            var result = new AccountingPostingExecutionResult(
                catalog.TenantId, catalog.CompanyId, dataSourceId, preview.IdempotencyKey, "posting-001",
                [new AccountingEvidence("dbo.GL", "gl:001", "Journal entry 001")],
                100m, 100m, AccountingReconciliationState.Balanced);

            return new Fixture(new AccountingPostingExecutionService(gate), audit, preview, catalog, authorization, permission, result);
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
