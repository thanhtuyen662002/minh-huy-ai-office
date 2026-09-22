using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaxInvoiceImportExecutionServiceTests
{
    [Fact]
    public async Task Authorized_import_is_audited_before_connector_and_validated()
    {
        var fixture = Fixture.Create();
        var calls = 0;

        var result = await fixture.Service.ExecuteAsync(
            fixture.Request, fixture.Authorization, [fixture.Permission],
            (request, _) =>
            {
                calls++;
                var audit = Assert.Single(fixture.Audit.Entries);
                Assert.True(audit.Authorized);
                Assert.Equal("invoice-exec-001", audit.ExecutionId);
                Assert.Equal(fixture.Request.SecretReference, request.SecretReference);
                return Task.FromResult(fixture.Result);
            },
            "invoice-exec-001");

        Assert.Equal(1, calls);
        Assert.Equal(fixture.Result, result);
    }

    [Fact]
    public async Task Equivalent_normalized_identity_executes_connector_once()
    {
        var fixture = Fixture.Create();
        var equivalent = fixture.Request with
        {
            Authority = fixture.Request.Authority with { Provider = " TAX-PROVIDER " },
            ExternalDocumentId = " inv-001 "
        };
        var calls = 0;

        Task<TaxInvoiceImportResult> Import(TaxInvoiceImportRequest request, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(fixture.Result);
        }

        var first = fixture.Service.ExecuteAsync(fixture.Request, fixture.Authorization, [fixture.Permission], Import);
        var second = fixture.Service.ExecuteAsync(equivalent, fixture.Authorization, [fixture.Permission], Import);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(results[0], results[1]);
        Assert.Single(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Missing_permission_is_audited_and_never_calls_connector()
    {
        var fixture = Fixture.Create();
        var executed = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Request, fixture.Authorization, [],
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(fixture.Result);
            }));

        Assert.False(executed);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.False(audit.Authorized);
    }

    [Fact]
    public async Task Cross_company_authority_is_rejected_before_audit_or_connector()
    {
        var fixture = Fixture.Create();
        var wrong = fixture.Authorization with { CompanyId = Guid.NewGuid() };
        var executed = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            fixture.Request, wrong, [fixture.Permission],
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(fixture.Result);
            }));

        Assert.False(executed);
        Assert.Empty(fixture.Audit.Entries);
    }

    private sealed record Fixture(
        TaxInvoiceImportExecutionService Service,
        RecordingAuditSink Audit,
        TaxInvoiceImportRequest Request,
        ToolAuthorizationRequest Authorization,
        ToolPermission Permission,
        TaxInvoiceImportResult Result)
    {
        public static Fixture Create()
        {
            var tenantId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var dataSourceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var authority = new TaxInvoiceImportAuthority(
                tenantId, companyId, dataSourceId, "tax-provider", TaxInvoiceImportExecutionService.ImportAction,
                "erp-2026.09", "schema-42", "v1");
            var request = new TaxInvoiceImportRequest(
                authority, "INV-001", DateTimeOffset.Parse("2026-09-22T00:00:00Z"), "VND", 100m, 10m,
                "secretref://tenant/tax-provider", "audit-001");
            var resource = TaxInvoiceImportExecutionService.ResourceFor(dataSourceId);
            var authorization = new ToolAuthorizationRequest(
                tenantId, companyId, userId, taskId, resource, TaxInvoiceImportExecutionService.ImportAction, ToolRiskLevel.High);
            var permission = new ToolPermission(
                tenantId, companyId, userId, resource, TaxInvoiceImportExecutionService.ImportAction, ToolRiskLevel.High);
            var result = new TaxInvoiceImportResult(
                tenantId, companyId, dataSourceId, authority.Provider, request.ExternalDocumentId, authority.ContractVersion,
                "evidence://invoice/INV-001", request.AuditCorrelationId);
            var audit = new RecordingAuditSink();
            var gate = new AuthorizedToolExecutionGate(new ToolAuthorizationPolicy(), new ToolExecutionAuditService(audit));
            return new Fixture(new TaxInvoiceImportExecutionService(gate), audit, request, authorization, permission, result);
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
