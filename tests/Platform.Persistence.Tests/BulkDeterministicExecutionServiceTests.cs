using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class BulkDeterministicExecutionServiceTests
{
    [Fact]
    public async Task Authorized_item_is_audited_before_writer_and_validated()
    {
        var fixture = Fixture.Create();
        var calls = 0;
        var result = await fixture.Service.ExecuteItemAsync(fixture.Plan, fixture.Item, fixture.Authorization, [fixture.Permission], _ =>
        {
            calls++;
            var audit = Assert.Single(fixture.Audit.Entries);
            Assert.True(audit.Authorized);
            Assert.Equal("bulk-exec-001", audit.ExecutionId);
            return Task.FromResult(fixture.Result);
        }, "bulk-exec-001");
        Assert.Equal(1, calls);
        Assert.Equal(fixture.Result, result);
    }

    [Fact]
    public async Task Duplicate_exact_item_identity_executes_writer_once()
    {
        var fixture = Fixture.Create();
        var calls = 0;
        Task<BulkExecutionItemResult> Write(CancellationToken _) { Interlocked.Increment(ref calls); return Task.FromResult(fixture.Result); }
        var results = await Task.WhenAll(
            fixture.Service.ExecuteItemAsync(fixture.Plan, fixture.Item, fixture.Authorization, [fixture.Permission], Write),
            fixture.Service.ExecuteItemAsync(fixture.Plan, fixture.Item, fixture.Authorization, [fixture.Permission], Write));
        Assert.Equal(1, calls);
        Assert.Equal(results[0], results[1]);
        Assert.Single(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Missing_permission_is_audited_and_never_writes()
    {
        var fixture = Fixture.Create();
        var executed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteItemAsync(fixture.Plan, fixture.Item, fixture.Authorization, [], _ =>
        { executed = true; return Task.FromResult(fixture.Result); }));
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
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteItemAsync(fixture.Plan, fixture.Item, wrong, [fixture.Permission], _ =>
        { executed = true; return Task.FromResult(fixture.Result); }));
        Assert.False(executed);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Result_with_wrong_evidence_authority_fails_closed()
    {
        var fixture = Fixture.Create();
        var wrong = fixture.Result with { DataSourceId = Guid.NewGuid() };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteItemAsync(
            fixture.Plan, fixture.Item, fixture.Authorization, [fixture.Permission], _ => Task.FromResult(wrong)));
    }

    [Fact]
    public async Task Writer_cannot_report_success_when_actual_erp_state_mismatches_preview()
    {
        var fixture = Fixture.Create();
        var mismatch = fixture.Result with
        {
            ActualResultFingerprint = "unexpected-erp-state",
            ReconciliationState = BulkReconciliationState.Mismatch
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecuteItemAsync(
            fixture.Plan, fixture.Item, fixture.Authorization, [fixture.Permission], _ => Task.FromResult(mismatch)));
    }

    private sealed record Fixture(BulkDeterministicExecutionService Service, RecordingAuditSink Audit, BulkExecutionPlan Plan,
        BulkExecutionItem Item, ToolAuthorizationRequest Authorization, ToolPermission Permission, BulkExecutionItemResult Result)
    {
        public static Fixture Create()
        {
            var tenant = Guid.NewGuid(); var company = Guid.NewGuid(); var dataSource = Guid.NewGuid(); var user = Guid.NewGuid(); var task = Guid.NewGuid();
            var authority = new BulkExecutionAuthority(tenant, company, dataSource, "erp.bulk.execute", "workflow-7", "erp-2026.09", "schema-42", "catalog-12", "secret-ref:erp-prod");
            var item = new BulkExecutionItem("ITEM-001", "order.post", "payload-a", "evidence-preview-a", "actual-1");
            var plan = new BulkExecutionPlan(authority, [item], "audit-103");
            var resource = BulkDeterministicExecutionService.ResourceFor(dataSource);
            var authorization = new ToolAuthorizationRequest(tenant, company, user, task, resource, BulkDeterministicExecutionService.WriteAction, ToolRiskLevel.High);
            var permission = new ToolPermission(tenant, company, user, resource, BulkDeterministicExecutionService.WriteAction, ToolRiskLevel.High);
            var result = new BulkExecutionItemResult(tenant, company, dataSource, item.GetExecutionIdentity(authority), BulkExecutionItemState.Succeeded,
                "evidence-write-1", "actual-1", BulkReconciliationState.Matched, plan.AuditCorrelationId);
            var audit = new RecordingAuditSink();
            var gate = new AuthorizedToolExecutionGate(new ToolAuthorizationPolicy(), new ToolExecutionAuditService(audit));
            return new Fixture(new BulkDeterministicExecutionService(gate), audit, plan, item, authorization, permission, result);
        }
    }

    private sealed class RecordingAuditSink : IToolExecutionAuditSink
    {
        public List<ToolExecutionAuditEntry> Entries { get; } = [];
        public Task AppendAsync(ToolExecutionAuditEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry); return Task.CompletedTask; }
    }
}
